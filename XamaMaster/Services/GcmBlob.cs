using System.Security.Cryptography;

namespace XamaMaster.Services;

/// <summary>Same helper and layout as XSeller/Services/GcmBlob.cs - see
/// that file for the explanation. Used here both for the settlement
/// exchange AND for the owner-password-protected CSV invoice.</summary>
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
