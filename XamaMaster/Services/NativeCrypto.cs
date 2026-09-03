using System.Runtime.InteropServices;

namespace XamaMaster.Services;

/// <summary>
/// Thin, exact wrapper around shamm_crypto.dll (built from crypto-core/ in
/// Phase 1). This is the ONLY file in Xama Master allowed to call into the
/// native library directly - everything else goes through VideoEncryptor,
/// so if the native signatures ever change, there's exactly one place to fix.
/// </summary>
internal static class NativeCrypto
{
    private const string Lib = "shamm_crypto.dll";

    [DllImport(Lib)] internal static extern int shamm_generate_key(byte[] outKey, uint outKeyLen);

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

    [DllImport(Lib)] internal static extern int shamm_ctr_crypt(
        byte[] contentKey, uint contentKeyLen,
        byte[] iv, uint ivLen,
        ulong streamOffset,
        byte[] data, uint dataLen);

    [DllImport(Lib)] internal static extern int shamm_wipe(byte[] buf, uint len);

    internal const int KeyLen = 32;
    internal const int IvLen = 16;
    internal const int WrappedKeyLen = 12 + 32 + 16; // nonce + ciphertext + tag
}
