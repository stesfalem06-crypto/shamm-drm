using System.IO;
using System.Text.Json;

namespace XSeller.Services;

/// <summary>
/// Persists the configured media-folder roots next to other XSeller app data
/// (%DOCUMENTS%\XSeller\library-roots.json). First launch seeds the default
/// set; after that Add/Remove survive restarts, including removed defaults.
/// </summary>
public static class LibraryRootsStore
{
    public const int CurrentVersion = 1;

    public static string StorePath => Path.Combine(FirstRunBootstrap.DocsRoot, "library-roots.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public sealed class RootsDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public List<string> Roots { get; set; } = new();
    }

    /// <summary>Default roots seeded only when the store file does not exist yet.</summary>
    public static List<string> DefaultRoots()
    {
        var list = new List<string>
        {
            FirstRunBootstrap.LibraryDir,
            FirstRunBootstrap.PlainDir,
        };

        void AddIfPresent(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Path.GetFullPath(path);
            }
            catch { return; }
            if (!Directory.Exists(path)) return;
            if (list.Any(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(path);
        }

        AddIfPresent(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        AddIfPresent(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        AddIfPresent(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddIfPresent(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XamaMaster", "Library"));

        return list;
    }

    /// <summary>
    /// Load configured roots. Missing file → seed defaults and persist.
    /// Corrupt/unreadable file → fall back to defaults and rewrite.
    /// </summary>
    public static List<string> LoadOrSeed()
    {
        Directory.CreateDirectory(FirstRunBootstrap.DocsRoot);
        if (!File.Exists(StorePath))
        {
            var seeded = Normalize(DefaultRoots());
            Save(seeded);
            return seeded;
        }

        try
        {
            var json = File.ReadAllText(StorePath);
            var doc = JsonSerializer.Deserialize<RootsDocument>(json);
            if (doc?.Roots == null)
            {
                var seeded = Normalize(DefaultRoots());
                Save(seeded);
                return seeded;
            }
            return Normalize(doc.Roots);
        }
        catch
        {
            var seeded = Normalize(DefaultRoots());
            Save(seeded);
            return seeded;
        }
    }

    public static void Save(IEnumerable<string> roots)
    {
        Directory.CreateDirectory(FirstRunBootstrap.DocsRoot);
        var doc = new RootsDocument
        {
            Version = CurrentVersion,
            Roots = Normalize(roots),
        };
        var tmp = StorePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOpts));
        File.Copy(tmp, StorePath, overwrite: true);
        try { File.Delete(tmp); } catch { }
    }

    private static List<string> Normalize(IEnumerable<string> roots)
    {
        var result = new List<string>();
        foreach (var raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full;
            try { full = Path.GetFullPath(raw.Trim()); }
            catch { continue; }
            if (result.Any(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(full);
        }
        return result;
    }
}
