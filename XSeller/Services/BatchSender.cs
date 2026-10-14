using XSeller.Models;

namespace XSeller.Services;

public sealed record DeviceSendResult(string Serial, string Model, int Sent, int Failed, int Skipped, bool Disconnected);

public sealed record SendProgress(string Serial, int FileIndex, int FileCount, string FileName, string Status, bool Done);

/// <summary>
/// Pushes many files (mixed plain + protected) to many phones. Phones run in parallel,
/// files per phone run sequentially. Each file uses the existing
/// <see cref="TransferService.Send"/> (protected → device-bound key + ledger entry;
/// plain → plain/public folders). Failed files retry; a phone that disconnects is
/// abandoned without stopping the others.
/// </summary>
public sealed class BatchSender
{
    private readonly AdbService _adb;
    private readonly TransferService _transfer;

    public int MaxAttempts { get; init; } = 3;
    public TimeSpan ReconnectWait { get; init; } = TimeSpan.FromSeconds(15);

    public BatchSender(AdbService adb, TransferService transfer)
    {
        _adb = adb;
        _transfer = transfer;
    }

    public Task<DeviceSendResult[]> SendAsync(IReadOnlyList<LibraryItem> items,
        IReadOnlyList<ConnectedDevice> devices, IProgress<SendProgress> progress, CancellationToken ct)
    {
        var tasks = devices.Select(d => Task.Run(() => SendToDevice(items, d, progress, ct), ct));
        return Task.WhenAll(tasks);
    }

    private async Task<DeviceSendResult> SendToDevice(IReadOnlyList<LibraryItem> items, ConnectedDevice device,
        IProgress<SendProgress> progress, CancellationToken ct)
    {
        int sent = 0, failed = 0;
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            var name = item.Name;
            var ok = false;
            for (var attempt = 1; attempt <= MaxAttempts && !ok; attempt++)
            {
                if (!await WaitReady(device.Serial, ct))
                {
                    var skipped = items.Count - i;
                    progress.Report(new SendProgress(device.Serial, i, items.Count, name,
                        $"Disconnected — {sent} sent, {skipped} skipped", true));
                    return new DeviceSendResult(device.Serial, device.Model, sent, failed, skipped, true);
                }
                progress.Report(new SendProgress(device.Serial, i, items.Count, name,
                    attempt == 1 ? $"Sending {i + 1}/{items.Count}: {name}" : $"Retry {attempt}/{MaxAttempts}: {name}", false));
                try
                {
                    _transfer.Send(item, device);
                    ok = true;
                }
                catch (Exception ex) when (attempt < MaxAttempts)
                {
                    _ = ex;
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                }
                catch (Exception ex)
                {
                    progress.Report(new SendProgress(device.Serial, i, items.Count, name, $"Failed: {name} — {ex.Message}", false));
                }
            }
            if (ok) sent++; else failed++;
        }
        progress.Report(new SendProgress(device.Serial, items.Count, items.Count, "",
            failed == 0 ? $"Done — {sent} file(s) sent" : $"Done — {sent} sent, {failed} failed", true));
        return new DeviceSendResult(device.Serial, device.Model, sent, failed, 0, false);
    }

    private async Task<bool> WaitReady(string serial, CancellationToken ct)
    {
        if (_adb.IsReady(serial)) return true;
        var deadline = DateTime.UtcNow + ReconnectWait;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct);
            if (_adb.IsReady(serial)) return true;
        }
        return false;
    }
}
