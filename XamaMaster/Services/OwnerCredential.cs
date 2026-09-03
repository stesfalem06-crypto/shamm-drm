using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XamaMaster.Services;

/// <summary>
/// Xama Master asks its owner to set a password on first run. That
/// password (never stored itself) derives the key that encrypts every
/// invoice CSV Master produces - so "only the owner of the software can
/// open it" is literally true: without the password, the key can't be
/// re-derived, and without the key, AES-GCM decryption fails.
///
/// What IS stored locally (%AppData%\XamaMaster\owner.json): a random
/// salt, plus a separate PBKDF2 hash used only to verify a re-entered
/// password matches - never the password or the encryption key itself.
/// </summary>
public class OwnerCredential
{
    private const int Iterations = 200_000;
    private const int KeyLen = 32;

    private readonly string _configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XamaMaster", "owner.json");

    private record StoredCredential(string SaltBase64, string VerifierBase64);

    public bool IsSetUp => File.Exists(_configPath);

    public void SetPassword(string password)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        var master = Derive(password, salt);
        // The verifier is a ONE-WAY function of the master secret, distinct
        // from the invoice key derived below - storing it must never let
        // anyone recover the actual encryption key from owner.json.
        var verifier = Expand(master, "verifier");

        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        var stored = new StoredCredential(Convert.ToBase64String(salt), Convert.ToBase64String(verifier));
        File.WriteAllText(_configPath, JsonSerializer.Serialize(stored));
    }

    /// <summary>Returns the 32-byte invoice-encryption key if the password
    /// is correct, or null if it's wrong / not set up yet.</summary>
    public byte[]? TryUnlock(string password)
    {
        if (!IsSetUp) return null;
        var stored = JsonSerializer.Deserialize<StoredCredential>(File.ReadAllText(_configPath))!;
        var salt = Convert.FromBase64String(stored.SaltBase64);
        var expectedVerifier = Convert.FromBase64String(stored.VerifierBase64);
        var master = Derive(password, salt);
        var candidateVerifier = Expand(master, "verifier");

        if (!CryptographicOperations.FixedTimeEquals(candidateVerifier, expectedVerifier)) return null;

        // Only reachable once the password's been proven correct: derive
        // the actual invoice key via a DIFFERENT label, so it's never the
        // same bytes as the stored verifier.
        return Expand(master, "invoice-key");
    }

    private static byte[] Derive(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeyLen);
    }

    private static byte[] Expand(byte[] master, string label)
    {
        using var hmac = new HMACSHA256(master);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(label));
    }
}
