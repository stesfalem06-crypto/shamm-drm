using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using XSeller.Models;

namespace XSeller.Services;

/// <summary>
/// Everything-style in-memory index of encrypted DRM packages plus plain
/// video / PDF / image / exe files. FileSystemWatcher keeps it fresh; Search()
/// is pure RAM substring matching (no disk I/O on each keystroke). Scans run
/// off the UI thread and are capped so a huge Downloads folder cannot freeze
/// or crash the shop app.
/// </summary>
public sealed class LibraryIndex : IDisposable
{
    private static readonly string[] VideoExts =
        [".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".ts", ".flv", ".3gp"];

    private static readonly string[] ImageExts =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic"];

    private static readonly string[] PdfExts = [".pdf"];

    private static readonly string[] ExeExts = [".exe"];

    private const int MaxFilesPerRoot = 4000;
    private const int MaxDepth = 6;
    private const long MinVideoBytes = 1024;
    private const long MinOtherBytes = 1;

    private readonly ConcurrentDictionary<string, LibraryItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _rebuildLock = new();
    private readonly List<string> _roots = new();

    public event Action? Changed;

    public int Count => _items.Count;

    public IReadOnlyList<string> Roots
    {
        get { lock (_rebuildLock) return _roots.ToList(); }
    }

    /// <summary>Register a folder. Scan happens in the background unless scanNow is true.</summary>
    public void AddRoot(string path, bool scanNow = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
        {
            try { Directory.CreateDirectory(path); } catch { return; }
        }
        lock (_rebuildLock)
        {
            if (_roots.Any(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase))) return;
            _roots.Add(path);
        }
        AttachWatcher(path);
        if (scanNow) ScanRoot(path);
        else Task.Run(() => { ScanRoot(path); Changed?.Invoke(); });
    }

    /// <summary>Unregister a folder, drop its watchers and indexed rows under that path.</summary>
    public bool RemoveRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { path = Path.GetFullPath(path); } catch { return false; }

        lock (_rebuildLock)
        {
            var idx = _roots.FindIndex(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            path = _roots[idx];
            _roots.RemoveAt(idx);
        }

        DetachWatchersFor(path);

        foreach (var key in _items.Keys.ToList())
        {
            if (_items.TryGetValue(key, out var it) &&
                it.FilePath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
            {
                _items.TryRemove(key, out LibraryItem _);
            }
        }

        Changed?.Invoke();
        return true;
    }

    public void RebuildAsync()
    {
        Task.Run(() =>
        {
            lock (_rebuildLock)
            {
                _items.Clear();
                foreach (var root in _roots.ToList())
                    ScanRoot(root);
            }
            Changed?.Invoke();
        });
    }

    public void Rebuild()
    {
        lock (_rebuildLock)
        {
            _items.Clear();
            foreach (var root in _roots.ToList())
                ScanRoot(root);
        }
        Changed?.Invoke();
    }

    private void ScanRoot(string root)
    {
        try
        {
            foreach (var meta in EnumerateFilesCapped(root, "*.shammmeta"))
            {
                try { IndexEncrypted(meta); } catch { /* skip bad */ }
            }
            var counted = 0;
            foreach (var file in EnumerateFilesCapped(root, "*.*"))
            {
                if (counted++ > MaxFilesPerRoot) break;
                if (file.EndsWith(".shammvid", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.EndsWith(".shammmeta", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.EndsWith(".shammkey", StringComparison.OrdinalIgnoreCase)) continue;
                var kind = ClassifyPlain(file);
                if (kind == null) continue;
                try { IndexPlain(file, kind.Value); } catch { /* skip */ }
            }
        }
        catch { /* root may be mid-copy */ }
    }

    internal static MediaKind? ClassifyPlain(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;
        if (VideoExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return MediaKind.Video;
        if (PdfExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return MediaKind.Pdf;
        if (ImageExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return MediaKind.Image;
        if (ExeExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return MediaKind.Exe;
        return null;
    }

    private static IEnumerable<string> EnumerateFilesCapped(string root, string pattern)
    {
        var pending = new Stack<(string Dir, int Depth)>();
        pending.Push((root, 0));
        var yielded = 0;
        while (pending.Count > 0 && yielded < MaxFilesPerRoot)
        {
            var (dir, depth) = pending.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, pattern); }
            catch { continue; }
            foreach (var f in files)
            {
                yield return f;
                if (++yielded >= MaxFilesPerRoot) yield break;
            }
            if (depth >= MaxDepth) continue;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); }
            catch { continue; }
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                    continue;
                pending.Push((sub, depth + 1));
            }
        }
    }

    private void IndexEncrypted(string metaPath)
    {
        var json = File.ReadAllText(metaPath);
        var meta = JsonSerializer.Deserialize<VideoMetadata>(json);
        if (meta == null || string.IsNullOrWhiteSpace(meta.VideoId)) return;
        var dir = Path.GetDirectoryName(metaPath)!;
        var vidPath = Path.Combine(dir, $"{meta.VideoId}.shammvid");
        if (!File.Exists(vidPath)) return;

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
            SizeBytes = new FileInfo(vidPath).Length,
            IvBase64 = meta.IvBase64,
            WrappedContentKeyForShopsBase64 = meta.WrappedContentKeyForShopsBase64,
            IndexedAtUtc = DateTime.UtcNow,
        };
        item.SearchBlob =
            $"{item.Title} {item.Id} protected encrypted drm {item.FormatLabel} {item.KindLabel}"
                .ToLowerInvariant();
        _items[item.Id] = item;
    }

    private void IndexPlain(string path, MediaKind kind)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return;
        var minBytes = kind == MediaKind.Video ? MinVideoBytes : MinOtherBytes;
        if (fi.Length < minBytes) return;

        var id = "plain:" + path.ToLowerInvariant();
        var title = Path.GetFileNameWithoutExtension(path);
        var typeWord = kind switch
        {
            MediaKind.Video => "video",
            MediaKind.Pdf => "pdf document",
            MediaKind.Image => "image photo picture",
            MediaKind.Exe => "exe executable app",
            _ => "file",
        };
        var item = new LibraryItem
        {
            Id = id,
            Title = title,
            FilePath = path,
            IsEncrypted = false,
            Kind = kind,
            TokenPrice = 0,
            SizeBytes = fi.Length,
            IndexedAtUtc = DateTime.UtcNow,
        };
        item.SearchBlob =
            $"{title} {path} plain open free {typeWord} {fi.Extension} {item.KindLabel} {item.FormatLabel}"
                .ToLowerInvariant();
        _items[id] = item;
    }

    private void AttachWatcher(string root)
    {
        try
        {
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            void OnFs(object s, FileSystemEventArgs e) => DebouncedRescan(root);
            w.Created += OnFs;
            w.Changed += OnFs;
            w.Deleted += OnFs;
            w.Renamed += (s, e) => DebouncedRescan(root);
            _watchers.Add(w);
        }
        catch { /* no watch permission */ }
    }

    private void DetachWatchersFor(string root)
    {
        for (var i = _watchers.Count - 1; i >= 0; i--)
        {
            var w = _watchers[i];
            if (!string.Equals(w.Path, root, StringComparison.OrdinalIgnoreCase)) continue;
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            _watchers.RemoveAt(i);
        }
    }

    private System.Threading.Timer? _debounce;
    private void DebouncedRescan(string root)
    {
        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(_ =>
        {
            try
            {
                foreach (var key in _items.Keys.ToList())
                {
                    if (_items.TryGetValue(key, out var it) &&
                        it.FilePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        _items.TryRemove(key, out LibraryItem _);
                    }
                }
                ScanRoot(root);
                Changed?.Invoke();
            }
            catch { }
        }, null, 400, Timeout.Infinite);
    }

    /// <summary>
    /// Everything-style: space-separated tokens are AND-ed, case-insensitive
    /// substring match against precomputed SearchBlob. Returns ranked by title.
    /// </summary>
    public List<LibraryItem> Search(string? query, int max = 500)
    {
        var all = _items.Values.ToList();
        if (string.IsNullOrWhiteSpace(query))
            return all.OrderByDescending(i => i.IndexedAtUtc).Take(max).ToList();

        var tokens = query.Trim().ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return all.OrderByDescending(i => i.IndexedAtUtc).Take(max).ToList();

        return all
            .Where(i => tokens.All(t => i.SearchBlob.Contains(t, StringComparison.Ordinal)))
            .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _debounce?.Dispose();
    }
}
