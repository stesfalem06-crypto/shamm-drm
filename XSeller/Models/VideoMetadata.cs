namespace XSeller.Models;

/// <summary>Must stay identical in shape to XamaMaster/Models/VideoMetadata.cs
/// (X Seller reads the .shammmeta files Xama Master produces).</summary>
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
}
