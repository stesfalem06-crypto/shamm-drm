using System.Text;

namespace XSeller.Services;

/// <summary>
/// MUST stay byte-for-byte identical to XamaMaster/Services/DistributionKey.cs.
/// See that file for the full design explanation. In short: this is the
/// shared secret every legitimate X Seller install uses to read the content
/// keys Xama Master encrypted before shipping videos out to shops.
/// </summary>
internal static class DistributionKey
{
    private static readonly byte[] DevKey = SHA256Of("shamm-dev-distribution-key-CHANGE-BEFORE-LAUNCH");

    internal static byte[] Current => DevKey;

    private static byte[] SHA256Of(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(s));
    }
}
