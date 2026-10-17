using System.Management;
using System.Text;

namespace XSeller.Services;

/// <summary>
/// Windows-only (WMI Win32_PnPEntity) detection of Android phones independent of adb,
/// so a plugged phone ALWAYS shows up even when USB debugging is off or its ADB
/// driver is missing.
/// </summary>
public static class UsbPhoneDetector
{
    public enum PnpPhoneState { NeedsDebugging, DriverMissing, AdbInterface }

    /// <summary>A physical phone grouped from its PnP nodes (parent + MTP/ADB interfaces).</summary>
    public sealed record PnpPhone(string Key, string? Serial, string Name, string VidPid, PnpPhoneState State);

    public sealed record PnpEntity(string Name, string DeviceId, string PnpClass, uint Error, string Service);

    // Common Android USB vendor IDs.
    private static readonly string[] AndroidVids =
    [
        "VID_04E8", "VID_18D1", "VID_22B8", "VID_2717", "VID_12D1", "VID_0BB4", "VID_1004",
        "VID_2A70", "VID_22D9", "VID_2D95", "VID_0E8D", "VID_1782", "VID_05C6", "VID_0FCE",
        "VID_19D2", "VID_17EF", "VID_0489", "VID_2916", "VID_1EBF", "VID_2A45", "VID_29A9",
        "VID_1BBB", "VID_2AE5", "VID_0502", "VID_413C", "VID_109B", "VID_2B4C", "VID_1D45",
        "VID_2970", "VID_1F3A", "VID_2207", "VID_0B05", "VID_3654",
    ];

    private static readonly string[] NotPhoneWords =
        ["hub", "keyboard", "mouse", "receiver", "bluetooth", "card reader", "mass storage", "printer", "camera"];

    public static List<PnpEntity> QueryUsbEntities()
    {
        var list = new List<PnpEntity>();
        if (!OperatingSystem.IsWindows()) return list;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, PNPClass, ConfigManagerErrorCode, Service FROM Win32_PnPEntity " +
                "WHERE DeviceID LIKE 'USB%' OR PNPClass = 'WPD' OR PNPClass = 'AndroidUsbDeviceClass'");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    list.Add(new PnpEntity(
                        (mo["Name"] as string) ?? "",
                        (mo["DeviceID"] as string) ?? "",
                        (mo["PNPClass"] as string) ?? "",
                        mo["ConfigManagerErrorCode"] is uint e ? e : 0u,
                        (mo["Service"] as string) ?? ""));
                }
            }
        }
        catch (Exception ex)
        {
            AdbService.Log("WMI query failed: " + ex.Message);
        }
        return list;
    }

    private static string? VidPidOf(string deviceId)
    {
        var u = deviceId.ToUpperInvariant();
        var v = u.IndexOf("VID_", StringComparison.Ordinal);
        var p = u.IndexOf("PID_", StringComparison.Ordinal);
        if (v < 0 || p < 0 || v + 8 > u.Length || p + 8 > u.Length) return null;
        return u.Substring(v, 8) + "&" + u.Substring(p, 8);
    }

    private static bool IsAdbLike(PnpEntity e) =>
        e.Name.Contains("ADB", StringComparison.OrdinalIgnoreCase) ||
        e.PnpClass.Equals("AndroidUsbDeviceClass", StringComparison.OrdinalIgnoreCase) ||
        e.DeviceId.Contains("&MI_01", StringComparison.OrdinalIgnoreCase) && e.Name.Contains("Android", StringComparison.OrdinalIgnoreCase);

    /// <summary>Group PnP nodes into physical phones. Never throws.</summary>
    public static List<PnpPhone> Scan() => Group(QueryUsbEntities());

    public static List<PnpPhone> Group(List<PnpEntity> all)
    {
        var phones = new List<PnpPhone>();
        try
        {
            foreach (var g in all.Select(e => (e, vp: VidPidOf(e.DeviceId)))
                         .Where(x => x.vp != null).GroupBy(x => x.vp!))
            {
                var members = g.Select(x => x.e).ToList();
                var androidVid = AndroidVids.Any(v => g.Key.StartsWith(v, StringComparison.Ordinal));
                var wpd = members.FirstOrDefault(m => m.PnpClass.Equals("WPD", StringComparison.OrdinalIgnoreCase));
                var adb = members.FirstOrDefault(IsAdbLike);
                var broken = members.FirstOrDefault(m => m.Error != 0);
                var parentName = members.Select(m => m.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "";
                if (NotPhoneWords.Any(w => parentName.Contains(w, StringComparison.OrdinalIgnoreCase)) && wpd == null && adb == null)
                    continue;

                PnpPhoneState state;
                if (adb != null && adb.Error == 0) state = PnpPhoneState.AdbInterface;
                else if (broken != null && (androidVid || adb != null || wpd != null)) state = PnpPhoneState.DriverMissing;
                else if (wpd != null && (androidVid || wpd.DeviceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase) || members.Count > 1))
                    state = PnpPhoneState.NeedsDebugging;
                else if (androidVid) state = PnpPhoneState.NeedsDebugging;
                else continue;

                // Parent node "USB\VID_x&PID_y\SERIAL" carries the same serial adb reports.
                string? serial = null;
                foreach (var m in members)
                {
                    var parts = m.DeviceId.Split('\\');
                    if (parts.Length == 3 && !parts[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase) && !parts[2].Contains('&'))
                    { serial = parts[2]; break; }
                }
                var name = wpd?.Name;
                if (string.IsNullOrWhiteSpace(name)) name = parentName;
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("USB Composite", StringComparison.OrdinalIgnoreCase))
                    name = "Android phone";
                phones.Add(new PnpPhone("usb:" + (serial ?? g.Key), serial, name!, g.Key, state));
            }
        }
        catch (Exception ex) { AdbService.Log("PnP grouping failed: " + ex.Message); }
        return phones;
    }

    public static string Dump(List<PnpEntity> all)
    {
        var sb = new StringBuilder();
        foreach (var e in all.OrderBy(e => e.DeviceId))
            sb.AppendLine($"{e.PnpClass,-22} err={e.Error,-3} svc={e.Service,-12} {e.Name}  [{e.DeviceId}]");
        return sb.ToString();
    }
}
