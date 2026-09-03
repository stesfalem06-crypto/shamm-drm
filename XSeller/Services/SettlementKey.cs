using System.Text;

namespace XSeller.Services;

/// <summary>
/// A second shared symmetric key, separate from DistributionKey, used ONLY
/// for the settlement handshake over USB: encrypting the ledger export
/// X Seller writes for the agent, and decrypting the settlement
/// acknowledgment Xama Master writes back. Kept separate from
/// DistributionKey on purpose - it protects a different kind of data
/// (business/financial records, not video content keys), so a future
/// rotation of one never has to touch the other.
///
/// MUST stay byte-for-byte identical to XamaMaster/Services/SettlementKey.cs.
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
