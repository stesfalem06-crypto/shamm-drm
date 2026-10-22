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

    /// <summary>Optional public poster: base64 JPEG 320x180 (also shipped as &lt;VideoId&gt;.shammthumb). Unencrypted.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PosterBase64 { get; set; }

    /// <summary>
    /// Payload type. null/absent = video (all packages made before PDF support), "pdf" = PDF document.
    /// The encrypted body is still &lt;VideoId&gt;.shammvid with the same AES-256-CTR + key wrapping,
    /// so key delivery and device binding are identical for both types.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentType { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPdf => string.Equals(ContentType, "pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>UI column: "PDF", "Reels" or "Standard". Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FormatLabel => IsPdf ? "PDF" : IsVertical ? "Reels" : "Standard";
}
