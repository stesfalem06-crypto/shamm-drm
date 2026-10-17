using System.IO;

namespace XSeller.Models;

/// <summary>
/// Unified row in the Everything-style index: encrypted DRM packages and
/// plain media (video, PDF, image, exe) the shop can send freely.
/// </summary>
public class LibraryItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

    /// <summary>Set when the row comes from a phone indexed over MTP (phone-as-root).</summary>
    public string? MtpDeviceId { get; set; }
    public bool IsOnPhone => MtpDeviceId != null;
    /// <summary>Remote WPD path on the phone (FilePath holds a display path "[Phone]\...").</summary>
    public string? MtpPath { get; set; }

    private bool _isSelected;
    /// <summary>Mirrors ResultsList selection so the thumbnail grid can highlight tiles.</summary>
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; Raise(nameof(IsSelected)); } }

    private System.Windows.Media.ImageSource? _thumb;
    private bool _thumbRequested;
    /// <summary>Lazy Shell thumbnail (only requested when a tile is actually shown).</summary>
    public System.Windows.Media.ImageSource? Thumbnail
    {
        get
        {
            if (_thumb == null && !_thumbRequested)
            {
                _thumbRequested = true;
                XSeller.Services.ThumbnailService.Request(this);
            }
            return _thumb;
        }
        set { _thumb = value; Raise(nameof(Thumbnail)); }
    }

    public string KindGlyph => Kind switch
    {
        MediaKind.Protected => "🔒",
        MediaKind.Video => "🎬",
        MediaKind.Pdf => "📄",
        MediaKind.Image => "🖼",
        MediaKind.Exe => "⚙",
        _ => "📁",
    };

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
    public DateTime ModifiedUtc { get; set; }

    /// <summary>Display name (file name; protected packages show their title).</summary>
    public string Name => _name ??= IsEncrypted ? Title : Path.GetFileName(FilePath);
    private string? _name;

    /// <summary>Containing folder, Everything-style "Path" column.</summary>
    public string Folder => IsOnPhone ? FilePath[..Math.Max(0, FilePath.LastIndexOf('\\'))] : Path.GetDirectoryName(FilePath) ?? "";

    public string TypeLabel => IsEncrypted ? "Protected (" + FormatLabel + ")"
        : Kind == MediaKind.Other ? FormatLabel + " file" : FormatLabel + " " + KindLabel;

    public string ModifiedLabel => ModifiedUtc == default ? "" : ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string KindLabel => Kind switch
    {
        MediaKind.Protected => "Protected",
        MediaKind.Video => "Video",
        MediaKind.Pdf => "PDF",
        MediaKind.Image => "Image",
        MediaKind.Exe => "Exe",
        _ => "File",
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
