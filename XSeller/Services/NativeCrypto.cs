using System.Runtime.InteropServices;

namespace XSeller.Services;

internal static class NativeCrypto
{
    private const string Lib = "shamm_crypto.dll";

    [DllImport(Lib)] internal static extern int shamm_derive_device_key(
        byte[] fingerprint, uint fingerprintLen, byte[] outKey, uint outKeyLen);

    [DllImport(Lib)] internal static extern int shamm_wrap_key(
        byte[] contentKey, uint contentKeyLen,
        byte[] deviceKey, uint deviceKeyLen,
        byte[] outWrapped, uint outWrappedLen);

    [DllImport(Lib)] internal static extern int shamm_unwrap_key(
        byte[] wrapped, uint wrappedLen,
        byte[] deviceKey, uint deviceKeyLen,
        byte[] outContentKey, uint outContentKeyLen);

    [DllImport(Lib)] internal static extern int shamm_wipe(byte[] buf, uint len);

    internal const int KeyLen = 32;
    internal const int WrappedKeyLen = 12 + 32 + 16;
}
