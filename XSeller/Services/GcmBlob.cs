using System.Security.Cryptography;

namespace XSeller.Services;

/// <summary>
/// The Rust crypto core's shamm_wrap_key/unwrap_key only handle fixed
/// 32-byte content keys (see crypto-core/src/lib.rs) - exactly what's
/// needed for video key wrapping, but not a fit for encrypting a
/// variable-length JSON ledger export. .NET's built-in AesGcm class covers
/// that case directly and correctly, so settlement blobs use it rather
/// than stretching the native core to a job it wasn't designed for.
///
/// Output layout: [12-byte nonce][ciphertext][16-byte tag]
/// </summary>
internal static class GcmBlob
{
    internal static byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[12 + ciphertext.Length + 16];
        Buffer.BlockCopy(nonce, 0, output, 0, 12);
        Buffer.BlockCopy(ciphertext, 0, output, 12, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, output, 12 + ciphertext.Length, 16);
        return output;
    }

    /// <summary>Throws CryptographicException if the key is wrong or the
    /// data was tampered with - callers should treat that as "this file
    /// isn't a genuine Shamm settlement file."</summary>
    internal static byte[] Decrypt(byte[] key, byte[] blob)
    {
        var nonce = blob[..12];
        var tag = blob[^16..];
        var ciphertext = blob[12..^16];
        var plaintext = new byte[ciphertext.Length];

        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
