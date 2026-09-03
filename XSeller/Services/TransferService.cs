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

    /// <summary>
    /// Sends one video to one connected phone:
    ///   1. Unwrap the video's content key using the shared distribution key
    ///   2. Read the phone's hardware fingerprint over USB, derive its device key
    ///   3. Re-wrap the content key for THIS phone only
    ///   4. Push the encrypted video + the new wrapped key to the phone
    ///   5. Log the sale to the local ledger (increases the shop's running debt)
    /// </summary>
    public void SendVideo(VideoMetadata video, string shammvidPath, ConnectedDevice device)
    {
        // 1. Unwrap with the shop distribution key to get the raw content key.
        var wrappedForShop = Convert.FromBase64String(video.WrappedContentKeyForShopsBase64);
        var contentKey = new byte[NativeCrypto.KeyLen];
        var rc1 = NativeCrypto.shamm_unwrap_key(
            wrappedForShop, (uint)wrappedForShop.Length,
            DistributionKey.Current, (uint)DistributionKey.Current.Length,
            contentKey, NativeCrypto.KeyLen);
        if (rc1 != 0)
            throw new InvalidOperationException(
                "Could not read this video's key. The file may be corrupted or not from an official Xama Master.");

        try
        {
            // 2. Fingerprint + derive this specific phone's key.
            var fingerprint = _adb.GetHardwareFingerprint(device.Serial);
            var deviceKey = new byte[NativeCrypto.KeyLen];
            var rc2 = NativeCrypto.shamm_derive_device_key(
                fingerprint, (uint)fingerprint.Length, deviceKey, NativeCrypto.KeyLen);
            if (rc2 != 0) throw new InvalidOperationException("Could not read this phone's identity over USB.");

            try
            {
                // 3. Wrap for this phone only.
                var wrappedForPhone = new byte[NativeCrypto.WrappedKeyLen];
                var rc3 = NativeCrypto.shamm_wrap_key(
                    contentKey, NativeCrypto.KeyLen,
                    deviceKey, NativeCrypto.KeyLen,
                    wrappedForPhone, NativeCrypto.WrappedKeyLen);
                if (rc3 != 0) throw new InvalidOperationException("Failed to lock this video to the phone.");

                // 4. Push video body + the phone-specific key file + IV alongside it.
                var keyFilePath = Path.Combine(Path.GetTempPath(), $"{video.VideoId}.shammkey");
                File.WriteAllBytes(keyFilePath, wrappedForPhone);
                var metaFilePath = Path.Combine(Path.GetTempPath(), $"{video.VideoId}.shammmeta");
                File.WriteAllText(metaFilePath, System.Text.Json.JsonSerializer.Serialize(video));
                try
                {
                    _adb.PushToDevice(device.Serial, shammvidPath, $"{video.VideoId}.shammvid");
                    _adb.PushToDevice(device.Serial, keyFilePath, $"{video.VideoId}.shammkey");
                    _adb.PushToDevice(device.Serial, metaFilePath, $"{video.VideoId}.shammmeta");
                }
                finally
                {
                    File.Delete(keyFilePath);
                    File.Delete(metaFilePath);
                }

                // 5. Log the sale.
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
}
