namespace XamaMaster.Models;

/// <summary>
/// Everything Xama Master knows about one encrypted video. Saved as JSON
/// next to the .shammvid file, and mirrored into the local catalog.db so
/// the library list and search don't need to re-read every file on disk.
/// </summary>
public class VideoMetadata
{
    public string VideoId { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalFileName { get; set; } = "";
    public string Title { get; set; } = "";
    public int TokenPrice { get; set; }
    public long OriginalSizeBytes { get; set; }
    public DateTime EncryptedAtUtc { get; set; } = DateTime.UtcNow;

    /// 16-byte AES-CTR IV for the video body, Base64-encoded. Not secret -
    /// safe to store in plain metadata (see crypto-core/src/lib.rs notes).
    public string IvBase64 { get; set; } = "";

    /// True if the source video's height exceeds its width. Captured by
    /// Xama Master BEFORE encryption (see VideoEncryptor) because once a
    /// video is encrypted, its frame data is unreadable to any standard
    /// video-metadata tool - Xama can't inspect an encrypted file's
    /// dimensions itself, so this has to travel with the metadata instead.
    public bool IsVertical { get; set; }

    /// The video's 32-byte content key, encrypted with the distribution key
    /// baked into every legitimate X Seller install (see DistributionKey.cs).
    /// Base64-encoded. This is what lets X Seller re-wrap the key per-phone
    /// without Xama Master needing to be present at send time.
    public string WrappedContentKeyForShopsBase64 { get; set; } = "";
}
