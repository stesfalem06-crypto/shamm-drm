using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace XSeller.Services;

/// <summary>
/// P/Invoke into shamm_crypto.dll. Must sit next to XSeller.exe (excluded from single-file bundle).
/// Call EnsureLoaded() at startup for a clear error if missing.
/// </summary>
internal static class NativeCrypto
{
    private const string Lib = "shamm_crypto";
    public const int KeyLen = 32;
    public const int IvLen = 16;
    public const int WrappedKeyLen = 12 + 32 + 16; // nonce + ciphertext + tag

    private static bool _ready;
    private static string? _path;

    static NativeCrypto() => NativeLibrary.SetDllImportResolver(typeof(NativeCrypto).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? search)
    {
        if (!name.Equals("shamm_crypto", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("shamm_crypto.dll", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;
        EnsureLoaded();
        return NativeLibrary.Load(_path!);
    }

    public static void EnsureLoaded()
    {
        if (_ready) return;
        foreach (var dir in CandidateDirs())
        {
            var p = Path.Combine(dir, "shamm_crypto.dll");
            if (!File.Exists(p)) continue;
            if (NativeLibrary.TryLoad(p, out _))
            {
                _path = p;
                _ready = true;
                return;
            }
            throw new DllNotFoundException(
                $"Found shamm_crypto.dll at:\n{p}\n\nbut Windows could not load it.\n" +
                "It is probably the wrong architecture (x86 vs x64) or corrupted. Reinstall X Seller.");
        }
        throw new DllNotFoundException(
            "shamm_crypto.dll was not found next to X Seller.\n\nLooked in:\n  " +
            string.Join("\n  ", CandidateDirs()) +
            "\n\nReinstall from the official installer — the DLL must ship beside the .exe.");
    }

    public static string? LoadedPath { get { EnsureLoaded(); return _path; } }

    private static List<string> CandidateDirs()
    {
        var seen = new List<string>();
        void Add(string? d)
        {
            if (string.IsNullOrEmpty(d)) return;
            d = d.TrimEnd('\\', '/');
            if (!seen.Contains(d, StringComparer.OrdinalIgnoreCase)) seen.Add(d);
        }
        try { Add(Path.GetDirectoryName(Environment.ProcessPath)); } catch { }
        try { Add(AppContext.BaseDirectory); } catch { }
        try
        {
            var loc = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(loc)) Add(Path.GetDirectoryName(loc));
        }
        catch { }
        try { Add(Directory.GetCurrentDirectory()); } catch { }
        return seen;
    }

    [DllImport(Lib)] public static extern int shamm_derive_device_key(
        byte[] fingerprint, uint fingerprintLen, byte[] outKey, uint outKeyLen);

    [DllImport(Lib)] public static extern int shamm_wrap_key(
        byte[] contentKey, uint contentKeyLen,
        byte[] wrappingKey, uint wrappingKeyLen,
        byte[] outWrapped, uint outWrappedLen);

    [DllImport(Lib)] public static extern int shamm_unwrap_key(
        byte[] wrapped, uint wrappedLen,
        byte[] wrappingKey, uint wrappingKeyLen,
        byte[] outKey, uint outKeyLen);

    [DllImport(Lib)] public static extern int shamm_wipe(byte[] buf, uint len);
}
