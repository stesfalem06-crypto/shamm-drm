using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using XSeller.Models;

namespace XSeller.Services;

/// <summary>
/// Everything-style in-memory index of encrypted + plain videos.
/// FileSystemWatcher keeps it fresh; Search() is pure RAM substring matching
/// (no disk I/O on each keystroke). Scans run off the UI thread and are capped
/// so a huge Downloads folder cannot freeze or crash the shop app.
/// </summary>
public sealed class LibraryIndex : IDisposable
{
    private static readonly string[] VideoExts =
        [".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".ts", ".flv", ".3gp"];

    private const int MaxFilesPerRoot = 4000;
    private const int MaxDepth = 6;

    private readonly ConcurrentDictionary<string, LibraryItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _rebuildLock = new();
    private readonly List<string> _roots = new();

    public event Action? Changed;

    public int Count => _items.Count;

    public IReadOnlyCollection<string> Roots => _roots;

    /// <summary>Register a folder. Scan happens in the background unless immediate is true.</summary>
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
                var ext = Path.GetExtension(file);
                if (!VideoExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                if (file.EndsWith(".shammvid", StringComparison.OrdinalIgnoreCase)) continue;
                try { IndexPlain(file); } catch { /* skip */ }
            }
        }
        catch { /* root may be mid-copy */ }
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
            TokenPrice = meta.TokenPrice,
            IsVertical = meta.IsVertical,
            SizeBytes = new FileInfo(vidPath).Length,
            IvBase64 = meta.IvBase64,
            WrappedContentKeyForShopsBase64 = meta.WrappedContentKeyForShopsBase64,
            IndexedAtUtc = DateTime.UtcNow,
        };
        item.SearchBlob = $"{item.Title} {item.Id} protected encrypted {item.FormatLabel}".ToLowerInvariant();
        _items[item.Id] = item;
    }

    private void IndexPlain(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length < 1024) return;
        var id = "plain:" + path.ToLowerInvariant();
        var title = Path.GetFileNameWithoutExtension(path);
        var item = new LibraryItem
        {
            Id = id,
            Title = title,
            FilePath = path,
            IsEncrypted = false,
            TokenPrice = 0,
            SizeBytes = fi.Length,
            IndexedAtUtc = DateTime.UtcNow,
        };
        item.SearchBlob = $"{title} {path} plain open free {fi.Extension}".ToLowerInvariant();
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
