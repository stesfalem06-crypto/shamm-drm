using System.IO;
using XSeller.Models;

namespace XSeller.Services;

public class TransferService
{
    private readonly AdbService _adb;
    private readonly LedgerDatabase _ledger;
    internal static readonly object LedgerGate = new();

    public TransferService(AdbService adb, LedgerDatabase ledger)
    {
        _adb = adb;
        _ledger = ledger;
    }

    public void SendEncrypted(VideoMetadata video, string shammvidPath, ConnectedDevice device)
    {
        if (device.State != "device")
            throw new InvalidOperationException("Phone is not authorized. Unlock it and tap Allow on the USB debugging prompt.");
        SendEncryptedVia(video, shammvidPath,
            () => _adb.GetHardwareFingerprint(device.Serial),
            (local, remoteName) => _adb.PushToDevice(device.Serial, local, remoteName),
            device.Serial, device.Model);
    }

    /// <summary>
    /// Transport-independent protected send (ADB or MTP). The crypto is unchanged:
    /// unwrap shop key → derive device key from SHA-256(androidId|abi) → wrap → push
    /// .shammvid/.shammkey/.shammmeta → ledger entry.
    /// </summary>
    public void SendEncryptedVia(VideoMetadata video, string shammvidPath, Func<byte[]> fingerprintProvider,
        Action<string, string> push, string ledgerSerial, string ledgerModel)
    {
        var contentKey = new byte[NativeCrypto.KeyLen];
        try
        {
            if (string.IsNullOrWhiteSpace(video.WrappedContentKeyForShopsBase64))
                throw new InvalidOperationException("Missing shop key material for this package.");
            var wrappedShop = Convert.FromBase64String(video.WrappedContentKeyForShopsBase64);
            var distKey = DistributionKey.Current;
            var rc = NativeCrypto.shamm_unwrap_key(
                wrappedShop, (uint)wrappedShop.Length,
                distKey, NativeCrypto.KeyLen,
                contentKey, NativeCrypto.KeyLen);
            if (rc != 0) throw new InvalidOperationException("Could not unlock package for shops.");

            var fingerprint = fingerprintProvider();
            // Weekly pass (ledger/pricing only — the crypto below is unchanged).
            var pass = Passes.Instance?.ActiveFor(Convert.ToHexString(fingerprint));
            var deviceKey = new byte[NativeCrypto.KeyLen];
            try
            {
                var rc2 = NativeCrypto.shamm_derive_device_key(
                    fingerprint, (uint)fingerprint.Length, deviceKey, NativeCrypto.KeyLen);
                if (rc2 != 0) throw new InvalidOperationException("Could not read this phone's identity.");

                var wrappedForPhone = new byte[NativeCrypto.WrappedKeyLen];
                var rc3 = NativeCrypto.shamm_wrap_key(
                    contentKey, NativeCrypto.KeyLen,
                    deviceKey, NativeCrypto.KeyLen,
                    wrappedForPhone, NativeCrypto.WrappedKeyLen);
                if (rc3 != 0) throw new InvalidOperationException("Failed to lock video to the phone.");

                // Temp names are unique per send so parallel sends of the same package
                // to several phones never overwrite each other's device-bound key.
                var tmpTag = Guid.NewGuid().ToString("N");
                var keyFilePath = Path.Combine(Path.GetTempPath(), $"{video.VideoId}.{tmpTag}.shammkey");
                var metaFilePath = Path.Combine(Path.GetTempPath(), $"{video.VideoId}.{tmpTag}.shammmeta");
                File.WriteAllBytes(keyFilePath, wrappedForPhone);
                File.WriteAllText(metaFilePath, System.Text.Json.JsonSerializer.Serialize(video));
                try
                {
                    push(shammvidPath, $"{video.VideoId}.shammvid");
                    push(keyFilePath, $"{video.VideoId}.shammkey");
                    // Public poster (unencrypted) before the meta, so Xama sees it when it picks up the package.
                    var thumbPath = ResolvePoster(video, shammvidPath, tmpTag);
                    if (thumbPath != null)
                    {
                        try { push(thumbPath, $"{video.VideoId}.shammthumb"); }
                        catch (Exception ex) { AdbService.Log("poster push failed (non-fatal): " + ex.Message); }
                    }
                    push(metaFilePath, $"{video.VideoId}.shammmeta"); // last: signals the package is complete
                }
                finally
                {
                    try { File.Delete(keyFilePath); } catch { }
                    try { File.Delete(metaFilePath); } catch { }
                    try { File.Delete(Path.Combine(Path.GetTempPath(), $"{video.VideoId}.{tmpTag}.shammthumb")); } catch { }
                }

                lock (LedgerGate) _ledger.RecordTransfer(new LedgerEntry
                {
                    VideoId = video.VideoId,
                    VideoTitle = video.Title,
                    DeviceSerial = ledgerSerial,
                    DeviceModel = pass == null ? ledgerModel : $"{ledgerModel} · pass #{pass.Id}",
                    TokenPrice = pass == null ? video.TokenPrice : 0,
                    SentAtUtc = DateTime.UtcNow,
                    UserId = Session.UserId,
                    UserName = Session.UserName,
                    ShopId = Session.ShopId,
                    Kind = pass == null ? "protected" : "pass-use",
                    Category = "protected",
                });
            }
            finally
            {
                NativeCrypto.shamm_wipe(deviceKey, NativeCrypto.KeyLen);
            }
        }
        finally
        {
            NativeCrypto.shamm_wipe(contentKey, NativeCrypto.KeyLen);
        }
    }

    /// <summary>Plain sale → ledger (attributed to the signed-in user; price from admin price settings).</summary>
    public void RecordPlainSale(LibraryItem item, string deviceSerial, string deviceModel)
    {
        try
        {
            lock (LedgerGate) _ledger.RecordTransfer(new LedgerEntry
            {
                VideoId = "plain:" + Path.GetFileName(item.FilePath),
                VideoTitle = item.Name,
                DeviceSerial = deviceSerial,
                DeviceModel = deviceModel,
                TokenPrice = Prices.PriceOf(item),
                SentAtUtc = DateTime.UtcNow,
                UserId = Session.UserId,
                UserName = Session.UserName,
                ShopId = Session.ShopId,
                Kind = "plain",
                Category = Prices.CategoryOf(item),
            });
        }
        catch (Exception ex) { AdbService.Log("record plain sale failed: " + ex.Message); }
    }

    /// <summary>&lt;VideoId&gt;.shammthumb next to the package, else a temp file from PosterBase64.</summary>
    private static string? ResolvePoster(VideoMetadata video, string shammvidPath, string tmpTag)
    {
        try
        {
            var side = Path.Combine(Path.GetDirectoryName(shammvidPath) ?? "", $"{video.VideoId}.shammthumb");
            if (File.Exists(side)) return side;
            if (string.IsNullOrWhiteSpace(video.PosterBase64)) return null;
            var tmp = Path.Combine(Path.GetTempPath(), $"{video.VideoId}.{tmpTag}.shammthumb");
            File.WriteAllBytes(tmp, Convert.FromBase64String(video.PosterBase64));
            return tmp;
        }
        catch { return null; }
    }

    /// <summary>Plain / open file → phone (Xama plain folder + Movies/Xama). No ledger charge.</summary>
    public void SendPlain(LibraryItem item, ConnectedDevice device)
    {
        if (device.State != "device")
            throw new InvalidOperationException("Phone is not authorized. Unlock it and tap Allow on the USB debugging prompt.");
        if (!File.Exists(item.FilePath))
            throw new FileNotFoundException("File missing", item.FilePath);
        var name = Path.GetFileName(item.FilePath);
        _adb.PushPlainToDevice(device.Serial, item.FilePath, name);
    }

    public void Send(LibraryItem item, ConnectedDevice device)
    {
        if (item.IsEncrypted)
        {
            var meta = item.ToMetadata() ?? throw new InvalidOperationException("Bad encrypted package.");
            SendEncrypted(meta, item.FilePath, device);
        }
        else
        {
            SendPlain(item, device);
        }
    }

    /// <summary>Copy any item to a USB drive / folder. Encrypted packages include .shammmeta.</summary>
    public void CopyToFolder(LibraryItem item, string destFolder)
    {
        Directory.CreateDirectory(destFolder);
        if (item.IsEncrypted)
        {
            var destVid = Path.Combine(destFolder, Path.GetFileName(item.FilePath));
            File.Copy(item.FilePath, destVid, true);
            if (!string.IsNullOrEmpty(item.MetaPath) && File.Exists(item.MetaPath))
                File.Copy(item.MetaPath, Path.Combine(destFolder, Path.GetFileName(item.MetaPath)), true);
            var thumb = Path.ChangeExtension(item.FilePath, ".shammthumb");
            if (File.Exists(thumb)) File.Copy(thumb, Path.Combine(destFolder, Path.GetFileName(thumb)), true);
        }
        else
        {
            File.Copy(item.FilePath, Path.Combine(destFolder, Path.GetFileName(item.FilePath)), true);
        }
    }
}
