using System.IO;
using XSeller.Models;

namespace XSeller.Services;

public class TransferService
{
    private readonly AdbService _adb;
    private readonly LedgerDatabase _ledger;

    public TransferService(AdbService adb, LedgerDatabase ledger)
    {
        _adb = adb;
        _ledger = ledger;
    }

    public void SendEncrypted(VideoMetadata video, string shammvidPath, ConnectedDevice device)
    {
        if (device.State != "device")
            throw new InvalidOperationException("Phone is not authorized. Unlock it and tap Allow on the USB debugging prompt.");

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

            var fingerprint = _adb.GetHardwareFingerprint(device.Serial);
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

                var keyFilePath = Path.Combine(Path.GetTempPath(), $"{video.VideoId}.shammkey");
                var metaFilePath = Path.Combine(Path.GetTempPath(), $"{video.VideoId}.shammmeta");
                File.WriteAllBytes(keyFilePath, wrappedForPhone);
                File.WriteAllText(metaFilePath, System.Text.Json.JsonSerializer.Serialize(video));
                try
                {
                    _adb.PushToDevice(device.Serial, shammvidPath, $"{video.VideoId}.shammvid");
                    _adb.PushToDevice(device.Serial, keyFilePath, $"{video.VideoId}.shammkey");
                    _adb.PushToDevice(device.Serial, metaFilePath, $"{video.VideoId}.shammmeta");
                }
                finally
                {
                    try { File.Delete(keyFilePath); } catch { }
                    try { File.Delete(metaFilePath); } catch { }
                }

                _ledger.RecordTransfer(new LedgerEntry
                {
                    VideoId = video.VideoId,
                    VideoTitle = video.Title,
                    DeviceSerial = device.Serial,
                    DeviceModel = device.Model,
                    TokenPrice = video.TokenPrice,
                    SentAtUtc = DateTime.UtcNow,
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
        }
        else
        {
            File.Copy(item.FilePath, Path.Combine(destFolder, Path.GetFileName(item.FilePath)), true);
        }
    }
}
