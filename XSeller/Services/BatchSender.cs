using System.IO;
using XSeller.Models;

namespace XSeller.Services;

public sealed record DeviceSendResult(string Serial, string Model, int Sent, int Failed, int Skipped, bool Disconnected);

public sealed record SendProgress(string Serial, int FileIndex, int FileCount, string FileName, string Status, bool Done, double FileFraction = 0);

/// <summary>One phone to send to. MTP is primary (no USB debugging); adb is used when ready and MTP is not, or as a fallback.</summary>
public sealed record SendTarget(string Key, string Model, string? MtpId, string? AdbSerial, string LedgerSerial);

/// <summary>Errors that retrying cannot fix (e.g. Xama has not registered the phone yet).</summary>
public sealed class PermanentSendException(string message) : Exception(message);

/// <summary>
/// Pushes many files (mixed plain + protected) to many phones. Phones in parallel,
/// files per phone sequential, byte-level progress on MTP. Protected packages use
/// the unchanged <see cref="TransferService"/> crypto with either the MTP device file
/// (Xama-written androidId/abi) or adb as the fingerprint source.
/// </summary>
public sealed class BatchSender
{
    private readonly AdbService _adb;
    private readonly MtpService _mtp;
    private readonly TransferService _transfer;

    public int MaxAttempts { get; init; } = 3;
    public TimeSpan ReconnectWait { get; init; } = TimeSpan.FromSeconds(15);

    public BatchSender(AdbService adb, MtpService mtp, TransferService transfer)
    {
        _adb = adb;
        _mtp = mtp;
        _transfer = transfer;
    }

    public Task<DeviceSendResult[]> SendAsync(IReadOnlyList<LibraryItem> items,
        IReadOnlyList<SendTarget> targets, IProgress<SendProgress> progress, CancellationToken ct,
        Func<LibraryItem, string>? localPathFor = null)
    {
        var tasks = targets.Select(t => Task.Run(() => SendToDevice(items, t, progress, ct, localPathFor), ct));
        return Task.WhenAll(tasks);
    }

    private bool MtpReady(SendTarget t) => t.MtpId != null && _mtp.IsReady(t.MtpId);
    private bool AdbReady(SendTarget t) => t.AdbSerial != null && _adb.IsReady(t.AdbSerial);

    private async Task<DeviceSendResult> SendToDevice(IReadOnlyList<LibraryItem> items, SendTarget target,
        IProgress<SendProgress> progress, CancellationToken ct, Func<LibraryItem, string>? localPathFor)
    {
        int sent = 0, failed = 0;
        byte[]? mtpFingerprint = null;
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            var name = item.Name;
            var ok = false;
            for (var attempt = 1; attempt <= MaxAttempts && !ok; attempt++)
            {
                if (!await WaitReady(target, ct))
                {
                    var skipped = items.Count - i;
                    progress.Report(new SendProgress(target.Key, i, items.Count, name,
                        $"Disconnected — {sent} sent, {skipped} skipped", true));
                    return new DeviceSendResult(target.Key, target.Model, sent, failed, skipped, true);
                }
                var idx = i;
                void Frac(double f) => progress.Report(new SendProgress(target.Key, idx, items.Count, name,
                    $"Sending {idx + 1}/{items.Count}: {name} ({f:P0})", false, f));
                progress.Report(new SendProgress(target.Key, i, items.Count, name,
                    attempt == 1 ? $"Sending {i + 1}/{items.Count}: {name}" : $"Retry {attempt}/{MaxAttempts}: {name}", false));
                try
                {
                    var local = localPathFor?.Invoke(item) ?? item.FilePath;
                    SendOne(item, local, target, ref mtpFingerprint, Frac);
                    ok = true;
                }
                catch (PermanentSendException ex)
                {
                    progress.Report(new SendProgress(target.Key, i, items.Count, name, $"Skipped {name}: {ex.Message}", false));
                    break;
                }
                catch (Exception ex) when (attempt < MaxAttempts)
                {
                    AdbService.Log($"send {name} -> {target.Model} attempt {attempt} failed: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                }
                catch (Exception ex)
                {
                    AdbService.Log($"send {name} -> {target.Model} failed: {ex}");
                    progress.Report(new SendProgress(target.Key, i, items.Count, name, $"Failed: {name} — {ex.Message}", false));
                }
            }
            if (ok) sent++; else failed++;
        }
        progress.Report(new SendProgress(target.Key, items.Count, items.Count, "",
            failed == 0 ? $"Done — {sent} file(s) sent" : $"Done — {sent} sent, {failed} failed/skipped", true));
        return new DeviceSendResult(target.Key, target.Model, sent, failed, 0, false);
    }

    private void SendOne(LibraryItem item, string localPath, SendTarget t, ref byte[]? mtpFingerprint, Action<double> frac)
    {
        if (!item.IsEncrypted)
        {
            if (MtpReady(t)) { _mtp.SendPlain(t.MtpId!, localPath, frac); return; }
            if (AdbReady(t))
            {
                _transfer.SendPlain(new LibraryItem { FilePath = localPath, Title = item.Title }, new ConnectedDevice(t.AdbSerial!, t.Model, "device"));
                return;
            }
            throw new IOException("Phone not ready.");
        }

        var meta = item.ToMetadata() ?? throw new PermanentSendException("Bad encrypted package.");
        if (MtpReady(t))
        {
            if (mtpFingerprint == null)
            {
                var file = _mtp.ReadDeviceFile(t.MtpId!);
                if (file != null) mtpFingerprint = MtpService.FingerprintFrom(file);
            }
            if (mtpFingerprint != null)
            {
                var fp = mtpFingerprint;
                _transfer.SendEncryptedVia(meta, localPath, () => fp,
                    (local, remoteName) => _mtp.PushProtectedPart(t.MtpId!, local, remoteName,
                        remoteName.EndsWith(".shammvid", StringComparison.OrdinalIgnoreCase) ? frac : null),
                    t.LedgerSerial, t.Model);
                return;
            }
            if (!AdbReady(t))
                throw new PermanentSendException("Open Xama once on the phone (it registers the phone for protected files), then send again.");
        }
        if (AdbReady(t))
        {
            _transfer.SendEncrypted(meta, localPath, new ConnectedDevice(t.AdbSerial!, t.Model, "device"));
            return;
        }
        throw new IOException("Phone not ready.");
    }

    private async Task<bool> WaitReady(SendTarget t, CancellationToken ct)
    {
        if (MtpReady(t) || AdbReady(t)) return true;
        var deadline = DateTime.UtcNow + ReconnectWait;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct);
            if (MtpReady(t) || AdbReady(t)) return true;
        }
        return false;
    }
}
