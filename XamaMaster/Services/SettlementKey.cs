using System.Text;

namespace XamaMaster.Services;

/// <summary>
/// MUST stay byte-for-byte identical to XSeller/Services/SettlementKey.cs.
/// See that file for the full explanation.
/// </summary>
internal static class SettlementKey
{
    private static readonly byte[] DevKey = SHA256Of("shamm-dev-settlement-key-CHANGE-BEFORE-LAUNCH");

    internal static byte[] Current => DevKey;

    private static byte[] SHA256Of(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(s));
    }
}
