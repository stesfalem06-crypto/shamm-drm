namespace XSeller.Models;

/// <summary>One row = one video successfully sent to one phone. This is the
/// billing record: sum of TokenPrice across all rows since the last
/// settlement = what the shop owes the agent.</summary>
public class LedgerEntry
{
    public int Id { get; set; }
    public string VideoId { get; set; } = "";
    public string VideoTitle { get; set; } = "";
    public string DeviceSerial { get; set; } = "";
    public string DeviceModel { get; set; } = "";
    public int TokenPrice { get; set; }
    public DateTime SentAtUtc { get; set; }
    public bool Settled { get; set; } // flipped to true when the agent reconciles via USB (Phase 5)
}
