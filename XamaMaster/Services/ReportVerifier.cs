using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace XamaMaster.Services;

/// <summary>Checks X Seller's signed sales report (.shammreport). Key derivation MUST match XSeller/Services/SalesReport.cs.</summary>
public static class ReportVerifier
{
    private static byte[] Key() => HMACSHA256.HashData(SettlementKey.Current, Encoding.UTF8.GetBytes("shamm-report-signing-v1"));

    public static string Describe(string json)
    {
        var f = JsonNode.Parse(json) ?? throw new InvalidDataException("Not a report.");
        if ((string?)f["Format"] != "shamm-signed-report") throw new InvalidDataException("Not a signed X Seller report.");
        var payload = Convert.FromBase64String((string)f["Payload"]!);
        var ok = CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Key(), payload), Convert.FromHexString((string)f["HmacSha256"]!));
        if (!ok) return "INVALID SIGNATURE — the report was edited.";
        var r = JsonNode.Parse(payload)!;
        var t = r["Total"]!;
        var sb = new StringBuilder();
        sb.AppendLine($"VALID ✓  {r["ShopName"]} (shop {((string?)r["ShopId"] ?? "")[..Math.Min(8, ((string?)r["ShopId"] ?? "").Length)].ToUpperInvariant()})");
        sb.AppendLine($"Period {r["FromUtc"]} → {r["ToUtc"]}, generated {r["GeneratedAtUtc"]} by {r["GeneratedBy"]}");
        sb.AppendLine($"Sales {t["Count"]} · revenue {t["Revenue"]} · platform {t["Platform"]} · shop {t["Shop"]} · employees {t["Employee"]}");
        sb.AppendLine($"Owed to platform since last closing (#{r["LastClosingNumber"]}): {r["PlatformDueNow"]}");
        foreach (var e in r["PerEmployee"]?.AsArray() ?? new JsonArray())
            sb.AppendLine($"  {e!["Label"]}: {e["Count"]} sales, revenue {e["Revenue"]}, share {e["Employee"]}");
        foreach (var w in r["IntegrityWarnings"]?.AsArray() ?? new JsonArray()) sb.AppendLine("WARNING: " + w);
        return sb.ToString().Trim();
    }
}
