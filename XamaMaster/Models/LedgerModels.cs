namespace XamaMaster.Models;

/// <summary>Must stay shape-identical to XSeller/Models/LedgerEntry.cs -
/// this is what travels inside the encrypted USB export/ack files.</summary>
public class LedgerEntryDto
{
    public int Id { get; set; }
    public string VideoId { get; set; } = "";
    public string VideoTitle { get; set; } = "";
    public string DeviceSerial { get; set; } = "";
    public string DeviceModel { get; set; } = "";
    public int TokenPrice { get; set; }
    public DateTime SentAtUtc { get; set; }
}

/// <summary>The full contents of one X Seller -> Xama Master USB export.</summary>
public class LedgerExport
{
    public string ShopName { get; set; } = "";
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
    public List<LedgerEntryDto> UnsettledEntries { get; set; } = new();
}

/// <summary>What Xama Master writes back to the shop's USB drive after
/// the agent settles up - X Seller applies this to flip rows to Settled.</summary>
public class SettlementAck
{
    public string ShopName { get; set; } = "";
    public DateTime SettledAtUtc { get; set; } = DateTime.UtcNow;
    public List<int> SettledEntryIds { get; set; } = new();
    public int TotalTokensSettled { get; set; }
}
