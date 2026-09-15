namespace XSeller.Models;

public class VideoMetadata
{
    public string VideoId { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string Title { get; set; } = "";
    public int TokenPrice { get; set; }
    public long OriginalSizeBytes { get; set; }
    public DateTime EncryptedAtUtc { get; set; }
    public string IvBase64 { get; set; } = "";
    public bool IsVertical { get; set; }
    public string WrappedContentKeyForShopsBase64 { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string FormatLabel => IsVertical ? "Reels" : "Standard";
}
