using System.IO;

namespace XamaMaster.Services;

public static class FirstRunBootstrap
{
    public static string DocsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XamaMaster");

    public static string LibraryDir => Path.Combine(DocsRoot, "Library");

    public static void Run()
    {
        Directory.CreateDirectory(LibraryDir);
        Directory.CreateDirectory(DocsRoot);
        var note = Path.Combine(LibraryDir, "OUTPUT_GOES_HERE.txt");
        if (!File.Exists(note))
            File.WriteAllText(note,
                "Encrypted packages are written here after you click Encrypt.\n" +
                "Copy them to each shop's Documents\\XSeller\\Library folder.\n");
    }
}
