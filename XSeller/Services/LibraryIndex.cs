using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;
using System.Text.Json;
using XSeller.Models;

namespace XSeller.Services;

public enum SortColumn { Name, Path, Size, Modified, Type }

/// <summary>
/// Everything-style in-memory index of every file under the configured roots.
/// <list type="bullet">
/// <item>Startup: a persisted cache (%DOCUMENTS%\XSeller\index-cache.bin) is loaded
/// in the background so results appear immediately; a full rescan then runs and
/// atomically replaces the snapshot.</item>
/// <item>Live: one FileSystemWatcher per root applies incremental add/remove/rename
/// (no full rescans per change, so whole-drive roots stay cheap).</item>
/// <item>Search: AND of space-separated tokens, case-insensitive substring over the
/// lower-cased full path (+ title for protected packages). Runs on a background
/// thread over a pre-sorted flat array snapshot, in parallel, and honours cancellation
/// (~15 ms for 500k entries on an 8-core box in a synthetic benchmark).</item>
/// </list>
/// Protected packages (.shammvid + .shammmeta) are indexed from their metadata
/// exactly as before; .shammmeta/.shammkey/.shammvid files are never listed as plain.
/// </summary>
public sealed class LibraryIndex : IDisposable
{
    private static readonly string[] VideoExts =
        [".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".ts", ".flv", ".3gp"];
    private static readonly string[] ImageExts =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic"];
    private static readonly string[] PdfExts = [".pdf"];
    private static readonly string[] ExeExts = [".exe"];

    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Config.Msi",
    };

    /// <summary>The OS folder (C:\Windows) — skipped by default, but NOT other folders named "Windows".</summary>
    private static readonly string WinDir = SafeWinDir();
    private static string SafeWinDir()
    {
        try { return Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\', '/'); }
        catch { return ""; }
    }

    /// <summary>
    /// "Scan everything": no folder/attribute skipping at all (hidden, system, dot-folders,
    /// C:\Windows included). Only reparse points are skipped to avoid junction loops.
    /// </summary>
    public bool ScanEverything { get; set; }

    private const int CacheMagic = 0x58534958; // "XISX"
    private const int CacheVersion = 2;
    public static string CachePath => Path.Combine(FirstRunBootstrap.DocsRoot, "index-cache.bin");

    private ConcurrentDictionary<string, LibraryItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _rootsLock = new();
    private readonly object _scanLock = new();
    private readonly List<string> _roots = new();

    private volatile LibraryItem[]? _snapshot;
    private int _dirty = 1;
    private readonly System.Threading.Timer _changeTimer;
    private int _changePending;
    private int _cacheDirty;

    /// <summary>Raised (throttled to ~4/s) whenever the index content changes.</summary>
    public event Action? Changed;

    /// <summary>True while the background (re)scan runs.</summary>
    public bool IsScanning { get; private set; }

    public LibraryIndex()
    {
        _changeTimer = new System.Threading.Timer(_ => FlushChanged(), null, 250, 250);
    }

    public int Count => _items.Count;

    public IReadOnlyList<string> Roots
    {
        get { lock (_rootsLock) return _roots.ToList(); }
    }

    // ---------------------------------------------------------------- startup

    /// <summary>Register roots, load cache, then rescan everything in the background.</summary>
    public void Start(IEnumerable<string> roots)
    {
        foreach (var r in roots) AddRootInternal(r);
        Task.Run(() =>
        {
            try { LoadCache(); } catch { /* corrupt cache: ignore, rescan rebuilds */ }
            MarkChanged();
            FullRescan();
        });
    }

    /// <summary>Register a folder at runtime. Returns false if already present / invalid.</summary>
    public bool AddRoot(string path)
    {
        if (!AddRootInternal(path, out var full)) return false;
        Task.Run(() =>
        {
            lock (_scanLock) ScanInto(_items, full!);
            _cacheDirty = 1;
            MarkChanged();
            SaveCacheSafe();
        });
        return true;
    }

    private bool AddRootInternal(string path) => AddRootInternal(path, out _);

    private bool AddRootInternal(string? path, out string? full)
    {
        full = null;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { full = Path.GetFullPath(path); } catch { return false; }
        if (!Directory.Exists(full))
        {
            try { Directory.CreateDirectory(full); } catch { return false; }
        }
        lock (_rootsLock)
        {
            var f = full;
            if (_roots.Any(r => string.Equals(r, f, StringComparison.OrdinalIgnoreCase))) return false;
            _roots.Add(full);
        }
        AttachWatcher(full);
        return true;
    }

    /// <summary>Unregister a folder, drop its watcher and rows not covered by another root.</summary>
    public bool RemoveRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { path = Path.GetFullPath(path); } catch { return false; }
        List<string> remaining;
        lock (_rootsLock)
        {
            var idx = _roots.FindIndex(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            path = _roots[idx];
            _roots.RemoveAt(idx);
            remaining = _roots.ToList();
        }
        DetachWatchersFor(path);
        var prefix = WithSep(path);
        foreach (var kv in _items)
        {
            var fp = kv.Value.FilePath;
            if (!fp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (remaining.Any(r => fp.StartsWith(WithSep(r), StringComparison.OrdinalIgnoreCase))) continue;
            _items.TryRemove(kv.Key, out _);
        }
        _cacheDirty = 1;
        MarkChanged();
        Task.Run(SaveCacheSafe);
        return true;
    }

    /// <summary>Full rescan of all roots into a fresh dictionary, then atomic swap.</summary>
    public void FullRescan()
    {
        lock (_scanLock)
        {
            IsScanning = true;
            MarkChanged();
            try
            {
                var fresh = new ConcurrentDictionary<string, LibraryItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in Roots) ScanInto(fresh, root);
                _items = fresh;
                _cacheDirty = 1;
            }
            finally
            {
                IsScanning = false;
                MarkChanged();
            }
        }
        SaveCacheSafe();
    }

    // ---------------------------------------------------------------- external (phone) sources

    /// <summary>Replace all rows from one external source (e.g. a phone indexed over MTP).</summary>
    public void SetExternal(string sourcePrefix, IEnumerable<LibraryItem> items)
    {
        RemoveExternal(sourcePrefix);
        foreach (var it in items) _items[it.FilePath] = it;
        MarkChanged();
    }

    public void RemoveExternal(string sourcePrefix)
    {
        foreach (var kv in _items)
            if (kv.Key.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)) _items.TryRemove(kv.Key, out _);
        MarkChanged();
    }

    public static LibraryItem MakePhoneItem(string deviceId, string phoneName, string remotePath, long size, DateTime modUtc)
    {
        var display = $"[{phoneName}]{remotePath}";
        return new LibraryItem
        {
            Id = "mtp:" + display.ToLowerInvariant(),
            Title = Path.GetFileNameWithoutExtension(remotePath),
            FilePath = display,
            MtpDeviceId = deviceId,
            MtpPath = remotePath,
            IsEncrypted = false,
            Kind = ClassifyPlain(remotePath),
            SizeBytes = size,
            ModifiedUtc = modUtc,
            IndexedAtUtc = DateTime.UtcNow,
            SearchBlob = (display + " phone").ToLowerInvariant(),
        };
    }

    // ---------------------------------------------------------------- scanning

    private readonly record struct RawEntry(string Path, long Size, DateTime ModifiedUtc);

    private EnumerationOptions ScanOptions(bool everything) => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = everything ? FileAttributes.ReparsePoint : FileAttributes.ReparsePoint | FileAttributes.System,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    private static bool ShouldRecurseDefault(ref FileSystemEntry e)
    {
        var name = e.FileName;
        if (name.Length > 0 && name[0] == '.') return false;
        if (SkipDirNames.Contains(name.ToString())) return false;
        if (WinDir.Length > 0 && name.Equals("Windows", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.ToFullPath().TrimEnd('\\', '/'), WinDir, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private void ScanInto(ConcurrentDictionary<string, LibraryItem> target, string root)
    {
        if (!Directory.Exists(root)) return;
        var metas = new List<string>();
        // A root the user explicitly picked inside a normally-skipped area (e.g. C:\Windows\Fonts or a
        // hidden folder) is scanned without skipping, as is everything in "Scan everything" mode.
        var everything = ScanEverything || IsInsideSkippedArea(root);
        try
        {
            var en = new FileSystemEnumerable<RawEntry>(root,
                (ref FileSystemEntry e) => new RawEntry(e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcDateTime),
                ScanOptions(everything))
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
                ShouldRecursePredicate = everything ? (ref FileSystemEntry e) => true : ShouldRecurseDefault,
            };
            foreach (var raw in en)
            {
                var ext = Path.GetExtension(raw.Path);
                if (ext.Equals(".shammmeta", StringComparison.OrdinalIgnoreCase)) { metas.Add(raw.Path); continue; }
                if (IsPackagePart(ext)) continue;
                var item = MakePlain(raw.Path, raw.Size, raw.ModifiedUtc);
                target[item.FilePath] = item;
            }
        }
        catch { /* root vanished mid-scan */ }

        foreach (var m in metas)
        {
            try
            {
                var it = MakeEncrypted(m);
                if (it != null) target[it.FilePath] = it;
            }
            catch { /* skip bad package */ }
        }
    }

    private static bool IsPackagePart(string ext) =>
        ext.Equals(".shammvid", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".shammkey", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".shammmeta", StringComparison.OrdinalIgnoreCase);

    internal static MediaKind ClassifyPlain(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return MediaKind.Other;
        if (VideoExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return MediaKind.Video;
        if (PdfExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return MediaKind.Pdf;
        if (ImageExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return MediaKind.Image;
        if (ExeExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return MediaKind.Exe;
        return MediaKind.Other;
    }

    private static LibraryItem MakePlain(string path, long size, DateTime modUtc) => new()
    {
        Id = "plain:" + path.ToLowerInvariant(),
        Title = Path.GetFileNameWithoutExtension(path),
        FilePath = path,
        IsEncrypted = false,
        Kind = ClassifyPlain(path),
        SizeBytes = size,
        ModifiedUtc = modUtc,
        IndexedAtUtc = DateTime.UtcNow,
        SearchBlob = path.ToLowerInvariant(),
    };

    private static LibraryItem? MakeEncrypted(string metaPath)
    {
        var json = File.ReadAllText(metaPath);
        var meta = JsonSerializer.Deserialize<VideoMetadata>(json);
        if (meta == null || string.IsNullOrWhiteSpace(meta.VideoId)) return null;
        var dir = Path.GetDirectoryName(metaPath)!;
        var vidPath = Path.Combine(dir, $"{meta.VideoId}.shammvid");
        var fi = new FileInfo(vidPath);
        if (!fi.Exists) return null;
        var item = new LibraryItem
        {
            Id = meta.VideoId,
            Title = string.IsNullOrWhiteSpace(meta.Title) ? meta.VideoId : meta.Title,
            FilePath = vidPath,
            MetaPath = metaPath,
            IsEncrypted = true,
            Kind = MediaKind.Protected,
            TokenPrice = meta.TokenPrice,
            IsVertical = meta.IsVertical,
            SizeBytes = fi.Length,
            ModifiedUtc = fi.LastWriteTimeUtc,
            IvBase64 = meta.IvBase64,
            WrappedContentKeyForShopsBase64 = meta.WrappedContentKeyForShopsBase64,
            IndexedAtUtc = DateTime.UtcNow,
        };
        item.SearchBlob = $"{vidPath}|{item.Title} protected drm".ToLowerInvariant();
        return item;
    }

    // ---------------------------------------------------------------- live updates

    private void AttachWatcher(string root)
    {
        try
        {
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            w.Created += (_, e) => Enqueue(() => OnUpsert(e.FullPath));
            w.Changed += (_, e) => Enqueue(() => OnUpsert(e.FullPath));
            w.Deleted += (_, e) => Enqueue(() => OnDelete(e.FullPath));
            w.Renamed += (_, e) => Enqueue(() => { OnDelete(e.OldFullPath); OnUpsert(e.FullPath); });
            w.Error += (_, _) => Task.Run(() => { lock (_scanLock) ScanInto(_items, root); MarkChanged(); });
            w.EnableRaisingEvents = true;
            lock (_watchers) _watchers.Add(w);
        }
        catch { /* no watch permission */ }
    }

    private readonly BlockingCollection<Action> _fsQueue = new();
    private Task? _fsWorker;

    private void Enqueue(Action a)
    {
        if (_fsQueue.IsAddingCompleted) return;
        _fsWorker ??= Task.Factory.StartNew(() =>
        {
            foreach (var act in _fsQueue.GetConsumingEnumerable())
            {
                try { act(); } catch { }
            }
        }, TaskCreationOptions.LongRunning);
        try { _fsQueue.Add(a); } catch { }
    }

    private void OnUpsert(string path)
    {
        if (Directory.Exists(path))
        {
            ScanInto(_items, path); // new / moved-in folder
        }
        else if (File.Exists(path))
        {
            var ext = Path.GetExtension(path);
            if (ext.Equals(".shammmeta", StringComparison.OrdinalIgnoreCase))
            {
                var it = MakeEncrypted(path);
                if (it != null) _items[it.FilePath] = it;
            }
            else if (ext.Equals(".shammvid", StringComparison.OrdinalIgnoreCase))
            {
                // Refresh the matching protected row if its meta already exists.
                if (_items.TryGetValue(path, out var existing) && existing.MetaPath != null)
                {
                    var it = MakeEncrypted(existing.MetaPath);
                    if (it != null) _items[it.FilePath] = it;
                }
                else
                {
                    var dir = Path.GetDirectoryName(path)!;
                    var meta = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".shammmeta");
                    if (File.Exists(meta)) { var it = MakeEncrypted(meta); if (it != null) _items[it.FilePath] = it; }
                }
            }
            else if (!IsPackagePart(ext))
            {
                var fi = new FileInfo(path);
                if (fi.Exists && !IsSkippedLive(path))
                    _items[path] = MakePlain(path, fi.Length, fi.LastWriteTimeUtc);
            }
        }
        else return;
        _cacheDirty = 1;
        MarkChanged();
    }

    private static bool IsInsideSkippedArea(string root)
    {
        var r = root.TrimEnd('\\', '/');
        if (WinDir.Length > 0 && (r.Equals(WinDir, StringComparison.OrdinalIgnoreCase) ||
                                  r.StartsWith(WinDir + "\\", StringComparison.OrdinalIgnoreCase))) return true;
        try
        {
            var attr = File.GetAttributes(root);
            if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) return true;
        }
        catch { }
        return IsSkippedPath(Path.Combine(root, "x"));
    }

    private bool IsSkippedLive(string path)
    {
        if (ScanEverything) return false;
        foreach (var r in Roots)
            if (path.StartsWith(WithSep(r), StringComparison.OrdinalIgnoreCase) && IsInsideSkippedArea(r)) return false;
        if (WinDir.Length > 0 && path.StartsWith(WinDir + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        return IsSkippedPath(path);
    }

    private static bool IsSkippedPath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        foreach (var part in dir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            if (SkipDirNames.Contains(part) || part[0] == '.') return true;
        return false;
    }

    private void OnDelete(string path)
    {
        var removed = _items.TryRemove(path, out _);
        if (path.EndsWith(".shammmeta", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var kv in _items)
                if (string.Equals(kv.Value.MetaPath, path, StringComparison.OrdinalIgnoreCase))
                    removed |= _items.TryRemove(kv.Key, out _);
        }
        // A folder was deleted: drop everything under it.
        var prefix = WithSep(path);
        foreach (var kv in _items)
            if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                removed |= _items.TryRemove(kv.Key, out _);
        if (removed) { _cacheDirty = 1; MarkChanged(); }
    }

    private void DetachWatchersFor(string root)
    {
        lock (_watchers)
        {
            for (var i = _watchers.Count - 1; i >= 0; i--)
            {
                var w = _watchers[i];
                if (!string.Equals(w.Path, root, StringComparison.OrdinalIgnoreCase)) continue;
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
                _watchers.RemoveAt(i);
            }
        }
    }

    private static string WithSep(string p) =>
        p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar;

    private void MarkChanged()
    {
        Interlocked.Exchange(ref _dirty, 1);
        Interlocked.Exchange(ref _changePending, 1);
    }

    private long _lastCacheSaveTicks = Environment.TickCount64;
    private int _tick;
    private void FlushChanged()
    {
        // While a big scan runs, refresh the UI at most once a second (each refresh re-sorts the snapshot).
        if (IsScanning && (++_tick % 4) != 0) return;
        if (Interlocked.Exchange(ref _changePending, 0) == 1)
        {
            try { Changed?.Invoke(); } catch { }
        }
        // Persist live changes at most every 30 s.
        if (_cacheDirty == 1 && !IsScanning && Environment.TickCount64 - _lastCacheSaveTicks > 30_000)
        {
            _lastCacheSaveTicks = Environment.TickCount64;
            Task.Run(SaveCacheSafe);
        }
    }

    private LibraryItem[] Snapshot()
    {
        var snap = _snapshot;
        if (snap != null && Volatile.Read(ref _dirty) == 0) return snap;
        Interlocked.Exchange(ref _dirty, 0);
        snap = _items.Values.ToArray();
        _snapshot = snap;
        return snap;
    }

    // ---------------------------------------------------------------- search

    /// <summary>
    /// Everything-style search: tokens AND-ed, case-insensitive substring over the
    /// full path (and title for protected). Optional kind filter, sorted, cancellable.
    /// Call from a background thread.
    /// </summary>
    public List<LibraryItem> Search(string? query, Func<LibraryItem, bool>? filter,
        SortColumn sort, bool descending, CancellationToken ct)
    {
        // Pre-sorted snapshot (cached per sort order until the index changes), so a
        // keystroke is a single ordered parallel filter pass with no per-query sort.
        var all = SortedSnapshot(sort, descending, ct);
        var tokens = (query ?? "").ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 && filter == null) return new List<LibraryItem>(all);

        const int chunk = 16_384;
        var chunks = (all.Length + chunk - 1) / chunk;
        var parts = new List<LibraryItem>[chunks];
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, c =>
        {
            var local = new List<LibraryItem>();
            var end = Math.Min(all.Length, (c + 1) * chunk);
            for (var i = c * chunk; i < end; i++)
            {
                var it = all[i];
                var blob = it.SearchBlob;
                var ok = true;
                for (var t = 0; t < tokens.Length; t++)
                {
                    if (blob.IndexOf(tokens[t], StringComparison.Ordinal) < 0) { ok = false; break; }
                }
                if (ok && (filter == null || filter(it))) local.Add(it);
            }
            parts[c] = local;
        });
        ct.ThrowIfCancellationRequested();
        var result = new List<LibraryItem>(parts.Sum(p => p.Count));
        foreach (var p in parts) result.AddRange(p);
        return result;
    }

    private readonly object _sortLock = new();
    private LibraryItem[]? _sortedFor;
    private (SortColumn, bool) _sortedKey;
    private LibraryItem[]? _sorted;

    private LibraryItem[] SortedSnapshot(SortColumn sort, bool descending, CancellationToken ct)
    {
        var snap = Snapshot();
        lock (_sortLock)
        {
            if (_sorted != null && ReferenceEquals(_sortedFor, snap) && _sortedKey == (sort, descending))
                return _sorted;
        }
        Comparison<LibraryItem> cmp = sort switch
        {
            SortColumn.Path => (a, b) => string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase),
            SortColumn.Size => (a, b) => a.SizeBytes.CompareTo(b.SizeBytes),
            SortColumn.Modified => (a, b) => a.ModifiedUtc.CompareTo(b.ModifiedUtc),
            SortColumn.Type => (a, b) =>
            {
                var c = a.Kind.CompareTo(b.Kind);
                return c != 0 ? c : string.Compare(a.FormatLabel, b.FormatLabel, StringComparison.OrdinalIgnoreCase);
            },
            _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        if (descending) { var inner = cmp; cmp = (a, b) => inner(b, a); }
        var sorted = (LibraryItem[])snap.Clone();
        Array.Sort(sorted, cmp);
        ct.ThrowIfCancellationRequested();
        lock (_sortLock)
        {
            _sorted = sorted;
            _sortedFor = snap;
            _sortedKey = (sort, descending);
        }
        return sorted;
    }

    /// <summary>Back-compat simple search (Phase 1 signature).</summary>
    public List<LibraryItem> Search(string? query, int max = 500) =>
        Search(query, null, SortColumn.Name, false, CancellationToken.None).Take(max).ToList();

    // ---------------------------------------------------------------- cache

    /// <summary>
    /// Binary cache of plain entries (path, size, mtime). Protected packages are not
    /// cached — key material stays only in the .shammmeta on disk and is re-read by the
    /// rescan that follows every launch.
    /// </summary>
    private void LoadCache()
    {
        if (!File.Exists(CachePath)) return;
        using var fs = new FileStream(CachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var br = new BinaryReader(fs, System.Text.Encoding.UTF8);
        if (br.ReadInt32() != CacheMagic || br.ReadInt32() != CacheVersion) return;
        var n = br.ReadInt32();
        var roots = Roots.Select(WithSep).ToArray();
        for (var i = 0; i < n; i++)
        {
            var path = br.ReadString();
            var size = br.ReadInt64();
            var mod = new DateTime(br.ReadInt64(), DateTimeKind.Utc);
            if (!roots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase))) continue;
            _items.TryAdd(path, MakePlain(path, size, mod));
        }
    }

    private readonly object _saveLock = new();
    private void SaveCacheSafe()
    {
        try
        {
            lock (_saveLock)
            {
                if (Interlocked.Exchange(ref _cacheDirty, 0) == 0) return;
                Directory.CreateDirectory(FirstRunBootstrap.DocsRoot);
                var items = _items.Values.Where(i => !i.IsEncrypted && !i.IsOnPhone).ToArray();
                var tmp = CachePath + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var bw = new BinaryWriter(fs, System.Text.Encoding.UTF8))
                {
                    bw.Write(CacheMagic);
                    bw.Write(CacheVersion);
                    bw.Write(items.Length);
                    foreach (var it in items)
                    {
                        bw.Write(it.FilePath);
                        bw.Write(it.SizeBytes);
                        bw.Write(it.ModifiedUtc.Ticks);
                    }
                }
                File.Move(tmp, CachePath, overwrite: true);
            }
        }
        catch { /* disk full / ACL — cache is optional */ }
    }

    public void Dispose()
    {
        _changeTimer.Dispose();
        lock (_watchers)
        {
            foreach (var w in _watchers) { try { w.Dispose(); } catch { } }
            _watchers.Clear();
        }
        _fsQueue.CompleteAdding();
        SaveCacheSafe();
    }
}
