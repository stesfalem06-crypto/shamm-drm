using System.IO;

namespace XSeller.Models;

/// <summary>
/// Unified row in the Everything-style index: encrypted DRM packages and
/// plain media (video, PDF, image, exe) the shop can send freely.
/// </summary>
public class LibraryItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string? MetaPath { get; set; }
    public bool IsEncrypted { get; set; }
    public MediaKind Kind { get; set; } = MediaKind.Other;
    public int TokenPrice { get; set; }
    public bool IsVertical { get; set; }
    public long SizeBytes { get; set; }
    public DateTime IndexedAtUtc { get; set; } = DateTime.UtcNow;
    public string? IvBase64 { get; set; }
    public string? WrappedContentKeyForShopsBase64 { get; set; }

    public string KindLabel => Kind switch
    {
        MediaKind.Protected => "Protected",
        MediaKind.Video => "Video",
        MediaKind.Pdf => "PDF",
        MediaKind.Image => "Image",
        MediaKind.Exe => "Exe",
        _ => "Open",
    };

    public string FormatLabel
    {
        get
        {
            if (IsEncrypted)
                return IsVertical ? "Reels" : "DRM";
            var ext = Path.GetExtension(FilePath);
            if (string.IsNullOrEmpty(ext)) return KindLabel;
            return ext.TrimStart('.').ToUpperInvariant();
        }
    }

    public string SizeLabel =>
        SizeBytes >= 1_073_741_824 ? $"{SizeBytes / 1_073_741_824.0:0.0} GB" :
        SizeBytes >= 1_048_576 ? $"{SizeBytes / 1_048_576.0:0.0} MB" :
        SizeBytes >= 1024 ? $"{SizeBytes / 1024.0:0} KB" : $"{SizeBytes} B";

    /// <summary>Precomputed lowercase haystack for instant substring search.</summary>
    public string SearchBlob { get; set; } = "";

    public VideoMetadata? ToMetadata()
    {
        if (!IsEncrypted) return null;
        return new VideoMetadata
        {
            VideoId = Id,
            Title = Title,
            TokenPrice = TokenPrice,
            IsVertical = IsVertical,
            IvBase64 = IvBase64 ?? "",
            WrappedContentKeyForShopsBase64 = WrappedContentKeyForShopsBase64 ?? "",
        };
    }
}
