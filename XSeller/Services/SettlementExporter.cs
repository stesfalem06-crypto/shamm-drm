using System.Text.Json;
using XSeller.Models;

namespace XSeller.Services;

public class SettlementExporter
{
    private readonly LedgerDatabase _ledger;
    private readonly string _shopName;

    public SettlementExporter(LedgerDatabase ledger, string shopName)
    {
        _ledger = ledger;
        _shopName = shopName;
    }

    /// <summary>
    /// Writes an encrypted snapshot of every unsettled sale to the given
    /// path (the agent's USB drive). Xama Master reads this at the
    /// settlement visit; the shop can keep selling in the meantime since
    /// this is a copy, not a lock on the ledger.
    /// </summary>
    public string ExportForAgent(string destinationFolder)
    {
        var unsettled = GetUnsettledEntries();
        var export = new
        {
            ShopName = _shopName,
            ExportedAtUtc = DateTime.UtcNow,
            UnsettledEntries = unsettled,
        };
        var json = JsonSerializer.Serialize(export);
        var encrypted = GcmBlob.Encrypt(SettlementKey.Current, System.Text.Encoding.UTF8.GetBytes(json));

        var fileName = $"xseller-ledger-{_shopName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.shammledger";
        var fullPath = Path.Combine(destinationFolder, fileName);
        File.WriteAllBytes(fullPath, encrypted);
        return fullPath;
    }

    /// <summary>Reads a .shammack file the agent brought back from Xama
    /// Master and flips the covered rows to Settled, zeroing that portion
    /// of the shop's running debt.</summary>
    public int ApplySettlementAck(string ackFilePath)
    {
        var encrypted = File.ReadAllBytes(ackFilePath);
        var json = System.Text.Encoding.UTF8.GetString(GcmBlob.Decrypt(SettlementKey.Current, encrypted));
        var ack = JsonSerializer.Deserialize<SettlementAckDto>(json)
                  ?? throw new InvalidOperationException("Settlement file is empty or invalid.");

        return _ledger.MarkSettled(ack.SettledEntryIds);
    }

    private List<LedgerEntry> GetUnsettledEntries()
    {
        return _ledger.GetRecentTransfers(int.MaxValue).Where(e => !e.Settled).ToList();
    }

    private record SettlementAckDto(string ShopName, DateTime SettledAtUtc, List<int> SettledEntryIds, int TotalTokensSettled);
}
