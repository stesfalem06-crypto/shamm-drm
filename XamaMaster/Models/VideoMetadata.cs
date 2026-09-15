namespace XamaMaster.Models;

/// <summary>
/// Everything Xama Master knows about one encrypted video. Saved as JSON
/// next to the .shammvid file.
/// </summary>
public class VideoMetadata
{
    public string VideoId { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalFileName { get; set; } = "";
    public string Title { get; set; } = "";
    public int TokenPrice { get; set; }
    public long OriginalSizeBytes { get; set; }
    public DateTime EncryptedAtUtc { get; set; } = DateTime.UtcNow;
    public string IvBase64 { get; set; } = "";
    public bool IsVertical { get; set; }
    public string WrappedContentKeyForShopsBase64 { get; set; } = "";

    /// <summary>UI column: "Reels" vs "Standard". Not serialized specially.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FormatLabel => IsVertical ? "Reels" : "Standard";
}
