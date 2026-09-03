using System.IO;
using System.Text.Json;
using XamaMaster.Models;

namespace XamaMaster.Services;

public class VideoEncryptor
{
    private const int ChunkSize = 4 * 1024 * 1024; // 4MB - balances memory use vs. I/O calls

    /// <summary>
    /// Encrypts sourcePath into two files next to it in outputDir:
    ///   {videoId}.shammvid   - the encrypted video body (AES-256-CTR)
    ///   {videoId}.shammmeta  - JSON metadata (title, price, IV, wrapped key)
    /// Returns the metadata so the caller (MainWindow) can add it to the
    /// on-screen library list and the local catalog database.
    /// </summary>
    public VideoMetadata EncryptVideo(string sourcePath, string title, int tokenPrice, string outputDir, bool isVertical)
    {
        var meta = new VideoMetadata
        {
            OriginalFileName = Path.GetFileName(sourcePath),
            Title = title,
            TokenPrice = tokenPrice,
            OriginalSizeBytes = new FileInfo(sourcePath).Length,
            IsVertical = isVertical,
        };

        // 1. Fresh random content key + IV for this video only.
        var contentKey = new byte[NativeCrypto.KeyLen];
        var rc = NativeCrypto.shamm_generate_key(contentKey, NativeCrypto.KeyLen);
        if (rc != 0) throw new InvalidOperationException($"Key generation failed ({rc})");

        var iv = new byte[NativeCrypto.IvLen];
        System.Security.Cryptography.RandomNumberGenerator.Fill(iv);
        meta.IvBase64 = Convert.ToBase64String(iv);

        // 2. Encrypt the file body, chunk by chunk, so a multi-GB video
        //    never needs to sit fully in memory at once.
        var outVideoPath = Path.Combine(outputDir, $"{meta.VideoId}.shammvid");
        using (var input = File.OpenRead(sourcePath))
        using (var output = File.Create(outVideoPath))
        {
            var buffer = new byte[ChunkSize];
            ulong offset = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                var chunk = read == buffer.Length ? buffer : buffer[..read];
                var cryptRc = NativeCrypto.shamm_ctr_crypt(
                    contentKey, NativeCrypto.KeyLen, iv, NativeCrypto.IvLen,
                    offset, chunk, (uint)chunk.Length);
                if (cryptRc != 0) throw new InvalidOperationException($"Encryption failed at offset {offset} ({cryptRc})");

                output.Write(chunk, 0, chunk.Length);
                offset += (ulong)chunk.Length;
            }
        }

        // 3. Wrap the content key with the shop distribution key so X Seller
        //    can read it later (see DistributionKey.cs for why).
        var wrapped = new byte[NativeCrypto.WrappedKeyLen];
        var wrapRc = NativeCrypto.shamm_wrap_key(
            contentKey, NativeCrypto.KeyLen,
            DistributionKey.Current, NativeCrypto.KeyLen,
            wrapped, NativeCrypto.WrappedKeyLen);
        if (wrapRc != 0) throw new InvalidOperationException($"Key wrap failed ({wrapRc})");
        meta.WrappedContentKeyForShopsBase64 = Convert.ToBase64String(wrapped);

        // 4. Content key has done its job for this run - wipe it from memory.
        NativeCrypto.shamm_wipe(contentKey, NativeCrypto.KeyLen);

        // 5. Save metadata alongside the encrypted body.
        var metaPath = Path.Combine(outputDir, $"{meta.VideoId}.shammmeta");
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

        return meta;
    }
}
