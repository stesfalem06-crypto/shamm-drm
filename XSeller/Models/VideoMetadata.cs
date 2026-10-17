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

    /// <summary>Optional public poster: base64 JPEG 320x180 (also shipped as &lt;VideoId&gt;.shammthumb). Unencrypted.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PosterBase64 { get; set; }

    /// <summary>null/absent = video, "pdf" = protected PDF (same .shammvid crypto + key flow).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentType { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPdf => string.Equals(ContentType, "pdf", StringComparison.OrdinalIgnoreCase);

    [System.Text.Json.Serialization.JsonIgnore]
    public string FormatLabel => IsPdf ? "PDF" : IsVertical ? "Reels" : "Standard";
}
