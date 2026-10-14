using System.Management;

namespace XSeller.Services;

/// <summary>
/// Windows-only (WMI Win32_PnPEntity) check for Android phones that adb cannot see:
/// phones in plain MTP/file-transfer mode (USB debugging off) and phones whose ADB
/// interface has no driver (Samsung/Google/other OEM USB driver missing).
/// </summary>
public static class UsbPhoneDetector
{
    public sealed record Finding(string Kind, string Name, string DeviceId);

    // Common Android USB vendor IDs.
    private static readonly string[] AndroidVids =
    [
        "VID_04E8", // Samsung
        "VID_18D1", // Google / AOSP
        "VID_22B8", // Motorola
        "VID_2717", // Xiaomi
        "VID_12D1", // Huawei
        "VID_0BB4", // HTC
        "VID_1004", // LG
        "VID_2A70", // OnePlus
        "VID_22D9", // OPPO / realme
        "VID_2D95", // vivo
        "VID_0E8D", // MediaTek (Tecno / Infinix / itel and many others)
        "VID_1782", // Spreadtrum / Unisoc
        "VID_05C6", // Qualcomm
        "VID_0FCE", // Sony
        "VID_19D2", // ZTE
        "VID_17EF", // Lenovo
        "VID_0489", // Foxconn / Nokia
        "VID_2916", // Yota / misc
        "VID_1EBF", // misc
    ];

    /// <summary>Returns findings; never throws (empty on non-Windows / WMI failure).</summary>
    public static List<Finding> Scan()
    {
        var list = new List<Finding>();
        if (!OperatingSystem.IsWindows()) return list;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, PNPClass, ConfigManagerErrorCode, Service FROM Win32_PnPEntity " +
                "WHERE DeviceID LIKE 'USB%'");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var id = (mo["DeviceID"] as string) ?? "";
                    var name = (mo["Name"] as string) ?? "Android phone";
                    var cls = (mo["PNPClass"] as string) ?? "";
                    var err = mo["ConfigManagerErrorCode"] is uint e ? e : 0u;
                    var upper = id.ToUpperInvariant();
                    var isAndroidVid = AndroidVids.Any(v => upper.Contains(v));
                    var looksAdb = name.Contains("ADB", StringComparison.OrdinalIgnoreCase) ||
                                   cls.Equals("AndroidUsbDeviceClass", StringComparison.OrdinalIgnoreCase);

                    if (err != 0 && (looksAdb || isAndroidVid))
                        list.Add(new Finding("driver", name, id));       // e.g. code 28: no driver
                    else if (cls.Equals("WPD", StringComparison.OrdinalIgnoreCase) && upper.StartsWith("USB\\"))
                        list.Add(new Finding("mtp", name, id));          // MTP/PTP phone, adb off
                    else if (looksAdb)
                        list.Add(new Finding("adb", name, id));          // ADB interface present + driver OK
                }
            }
        }
        catch (Exception ex)
        {
            AdbService.Log("WMI scan failed: " + ex.Message);
        }
        return list;
    }
}
