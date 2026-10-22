using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XSeller.Models;

namespace XSeller.Services;

public sealed class SalesTotals
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public int Count { get; set; }
    public decimal Revenue { get; set; }
    public decimal Platform { get; set; }
    public decimal Shop { get; set; }
    public decimal Employee { get; set; }
}

public sealed class SalesReportData
{
    public string Format { get; set; } = "shamm-sales-report";
    public int Version { get; set; } = 1;
    public string ShopId { get; set; } = "";
    public string ShopName { get; set; } = "";
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string GeneratedBy { get; set; } = "";
    public int PlatformPercent { get; set; }
    public int ShopPercent { get; set; }
    public int EmployeePercent { get; set; }
    public List<SalesTotals> PerEmployee { get; set; } = new();
    public List<SalesTotals> PerItem { get; set; } = new();
    public SalesTotals Total { get; set; } = new();
    public List<LedgerEntry> Sales { get; set; } = new();
    /// <summary>Closings (agent settlements) whose closing time falls in the period.</summary>
    public List<ClosingRecord> Closings { get; set; } = new();
    /// <summary>Owed to the platform since the last closing (at report time).</summary>
    public decimal PlatformDueNow { get; set; }
    public int LastClosingNumber { get; set; }
    public List<string> IntegrityWarnings { get; set; } = new();
    public Dividends.Report? Dividends { get; set; }
}

/// <summary>Builds the admin dashboard numbers and the CSV / PDF / signed exports.</summary>
public static class SalesReport
{
    public static SalesReportData Build(LedgerDatabase ledger, DateTime fromUtc, DateTime toUtc, string shopName, Closings? closings)
    {
        var r = Build(ledger, fromUtc, toUtc, shopName);
        if (closings != null)
        {
            var hist = closings.History();
            r.Closings = hist.Where(c => c.ClosedAtUtc >= fromUtc && c.ClosedAtUtc < toUtc).ToList();
            r.LastClosingNumber = hist.LastOrDefault()?.Number ?? 0;
            r.PlatformDueNow = closings.Pending(shopName).PlatformDue;
            r.IntegrityWarnings = Integrity.Warnings(ledger, closings);
        }
        r.Dividends = Dividends.Build(ledger, fromUtc, toUtc);
        return r;
    }

    public static SalesReportData Build(LedgerDatabase ledger, DateTime fromUtc, DateTime toUtc, string shopName)
    {
        var sales = ledger.GetSales(fromUtc, toUtc);
        var (pp, sp, ep) = RevenueSplit.Current;
        SalesTotals Sum(string key, string label, IEnumerable<LedgerEntry> rows)
        {
            var list = rows.ToList();
            decimal rev = list.Sum(r => (decimal)r.TokenPrice);
            var (p, s, e) = RevenueSplit.Split(rev);
            return new SalesTotals { Key = key, Label = label, Count = list.Count, Revenue = rev, Platform = p, Shop = s, Employee = e };
        }
        return new SalesReportData
        {
            ShopId = Session.ShopId,
            ShopName = shopName,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            GeneratedBy = Session.UserName,
            PlatformPercent = pp, ShopPercent = sp, EmployeePercent = ep,
            PerEmployee = sales.GroupBy(s => s.UserId)
                .Select(g => Sum(g.Key, g.OrderByDescending(x => x.Id).First().UserName, g))
                .OrderByDescending(t => t.Revenue).ThenByDescending(t => t.Count).ToList(),
            PerItem = sales.GroupBy(s => s.VideoId)
                .Select(g => Sum(g.Key, g.First().VideoTitle + (g.First().Kind == "protected" ? " (protected)" : ""), g))
                .OrderByDescending(t => t.Revenue).ThenByDescending(t => t.Count).ToList(),
            Total = Sum("total", "TOTAL", sales),
            Sales = sales,
            Dividends = Dividends.Build(ledger, fromUtc, toUtc),
        };
    }

    private static string M(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    public static string ToCsv(SalesReportData r)
    {
        var sb = new StringBuilder();
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        sb.AppendLine($"# X Seller sales report,{Q(r.ShopName)},shop {r.ShopId},{r.FromUtc:yyyy-MM-dd} to {r.ToUtc.AddSeconds(-1):yyyy-MM-dd} (UTC),split {r.PlatformPercent}/{r.ShopPercent}/{r.EmployeePercent}");
        sb.AppendLine();
        sb.AppendLine("Section,Name,Count,Revenue,Platform,Shop,Employee");
        foreach (var t in r.PerEmployee) sb.AppendLine($"Employee,{Q(t.Label)},{t.Count},{M(t.Revenue)},{M(t.Platform)},{M(t.Shop)},{M(t.Employee)}");
        foreach (var t in r.PerItem) sb.AppendLine($"Item,{Q(t.Label)},{t.Count},{M(t.Revenue)},{M(t.Platform)},{M(t.Shop)},{M(t.Employee)}");
        var T = r.Total;
        sb.AppendLine($"Total,TOTAL,{T.Count},{M(T.Revenue)},{M(T.Platform)},{M(T.Shop)},{M(T.Employee)}");
        sb.AppendLine();
        sb.AppendLine($"Owed to platform since last closing (#{r.LastClosingNumber}),{M(r.PlatformDueNow)}");
        foreach (var w in r.IntegrityWarnings) sb.AppendLine($"WARNING,{Q(w)}");
        sb.AppendLine();
        sb.AppendLine("Closing,ClosedUtc,Agent,Sales,Revenue,PlatformDue,AmountReceived,ReceiptCode,RecordHash");
        foreach (var c in r.Closings)
            sb.AppendLine($"{c.Number},{c.ClosedAtUtc:O},{Q(c.AgentName)},{c.SalesCount},{M(c.Revenue)},{M(c.PlatformDue)},{M(c.AmountReceived)},{c.ReceiptCode},{c.RecordHash}");
        sb.AppendLine();
        if (r.Dividends != null)
        {
            sb.AppendLine();
            sb.Append(Dividends.ToCsv(r.Dividends));
        }
        sb.AppendLine("SaleId,TimeUtc,User,UserId,ShopId,Kind,Category,Item,ItemId,Price,DeviceModel,DeviceSerial,Settled");
        foreach (var s in r.Sales)
            sb.AppendLine($"{s.Id},{s.SentAtUtc:O},{Q(s.UserName)},{s.UserId},{s.ShopId},{s.Kind},{s.Category},{Q(s.VideoTitle)},{Q(s.VideoId)},{s.TokenPrice},{Q(s.DeviceModel)},{Q(s.DeviceSerial)},{(s.Settled ? 1 : 0)}");
        return sb.ToString();
    }

    // ---------------- signed export (tamper-evident) ----------------

    /// <summary>HMAC key derived from the shared SettlementKey (also in Xama Master).</summary>
    private static byte[] SigningKey() =>
        HMACSHA256.HashData(SettlementKey.Current, Encoding.UTF8.GetBytes("shamm-report-signing-v1"));

    public sealed record SignedReport(string Format, int Version, string Payload, string HmacSha256);

    public static string ToSigned(SalesReportData r)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r));
        var mac = Convert.ToHexString(HMACSHA256.HashData(SigningKey(), payload));
        return JsonSerializer.Serialize(new SignedReport("shamm-signed-report", 1, Convert.ToBase64String(payload), mac),
            new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Returns the report if the signature is valid, else throws.</summary>
    public static SalesReportData Verify(string signedJson)
    {
        var f = JsonSerializer.Deserialize<SignedReport>(signedJson) ?? throw new InvalidDataException("Not a signed report.");
        var payload = Convert.FromBase64String(f.Payload);
        var expected = HMACSHA256.HashData(SigningKey(), payload);
        if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(f.HmacSha256)))
            throw new InvalidDataException("Signature INVALID — the report was edited or is not from X Seller.");
        return JsonSerializer.Deserialize<SalesReportData>(payload) ?? throw new InvalidDataException("Empty report.");
    }

    // ---------------- PDF (dependency-free, text only) ----------------

    public static byte[] ToPdf(SalesReportData r)
    {
        var lines = new List<string>();
        string Row(string a, int c, decimal rev, decimal p, decimal s, decimal e) =>
            $"{Trim(a, 44),-44} {c,6} {M(rev),12} {M(p),12} {M(s),12} {M(e),12}";
        lines.Add($"X SELLER SALES REPORT  -  {r.ShopName}  (shop {r.ShopId[..Math.Min(8, r.ShopId.Length)]})");
        lines.Add($"Period: {r.FromUtc:yyyy-MM-dd} to {r.ToUtc.AddSeconds(-1):yyyy-MM-dd} (UTC)   Generated {r.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC by {r.GeneratedBy}");
        lines.Add($"Split: Platform {r.PlatformPercent}% / Shop {r.ShopPercent}% / Employee {r.EmployeePercent}%");
        lines.Add("");
        var head = $"{"",-44} {"Count",6} {"Revenue",12} {"Platform",12} {"Shop",12} {"Employee",12}";
        lines.Add("PER EMPLOYEE"); lines.Add(head); lines.Add(new string('-', head.Length));
        foreach (var t in r.PerEmployee) lines.Add(Row(t.Label, t.Count, t.Revenue, t.Platform, t.Shop, t.Employee));
        lines.Add(new string('-', head.Length));
        var T = r.Total;
        lines.Add(Row("TOTAL", T.Count, T.Revenue, T.Platform, T.Shop, T.Employee));
        lines.Add("");
        lines.Add("PER ITEM"); lines.Add(head); lines.Add(new string('-', head.Length));
        foreach (var t in r.PerItem) lines.Add(Row(t.Label, t.Count, t.Revenue, t.Platform, t.Shop, t.Employee));
        lines.Add("");
        lines.Add($"OWED TO PLATFORM SINCE LAST CLOSING (#{r.LastClosingNumber}): {M(r.PlatformDueNow)}");
        if (r.Dividends != null)
        {
            lines.Add("");
            foreach (var line in Dividends.SummaryLines(r.Dividends)) lines.Add(line);
            lines.Add($"{"Title",-40} {"Xfers",6} {"Share",12} {"Movie pay",12} {"Platform",12}");
            foreach (var m in r.Dividends.Movies.Take(40))
                lines.Add($"{Trim(m.Title, 40),-40} {m.Transfers,6} {M(m.ShareOfRevenue),12} {M(m.MoviePayout),12} {M(m.PlatformKept),12}");
        }
        foreach (var w in r.IntegrityWarnings) lines.Add("WARNING: " + Trim(w, 110));
        lines.Add("");
        lines.Add("CLOSINGS (agent settlements) IN PERIOD");
        lines.Add($"{"#",4} {"Closed (UTC)",-17} {"Agent",-20} {"Sales",6} {"Platform due",13} {"Received",12} {"Receipt code",-15}");
        foreach (var c in r.Closings)
            lines.Add($"{c.Number,4} {c.ClosedAtUtc:yyyy-MM-dd HH:mm} {Trim(c.AgentName, 20),-20} {c.SalesCount,6} {M(c.PlatformDue),13} {M(c.AmountReceived),12} {c.ReceiptCode,-15}");
        lines.Add("");
        lines.Add("SALES");
        lines.Add($"{"Time (UTC)",-17} {"User",-16} {"Item",-40} {"Price",7} {"Device",-24}");
        foreach (var s in r.Sales)
            lines.Add($"{s.SentAtUtc:yyyy-MM-dd HH:mm} {Trim(s.UserName, 16),-16} {Trim(s.VideoTitle, 40),-40} {s.TokenPrice,7} {Trim(s.DeviceModel, 24),-24}");
        lines.Add("");
        lines.Add("Use the signed .shammreport export for tamper-evident collection by the platform.");
        return MiniPdf.Text(lines);
    }

    private static string Trim(string s, int n) => (s ?? "").Length <= n ? s ?? "" : s![..(n - 1)] + "~";
}

/// <summary>Tiny text-only PDF writer (Courier, A4 landscape). Non-Latin-1 characters become '?'.</summary>
internal static class MiniPdf
{
    public static byte[] Text(List<string> lines, int perPage = 52)
    {
        var pages = lines.Chunk(perPage).ToList();
        if (pages.Count == 0) pages.Add(new[] { "" });
        var objs = new List<string>();
        // 1 catalog, 2 pages, 3 font, then (page, content) pairs
        var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{4 + i * 2} 0 R"));
        objs.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objs.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
        objs.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>");
        for (var i = 0; i < pages.Count; i++)
        {
            var sb = new StringBuilder("BT /F1 8.5 Tf 10 TL 28 560 Td\n");
            foreach (var l in pages[i]) sb.Append('(').Append(Esc(l)).Append(") Tj T*\n");
            sb.Append($"ET BT /F1 7 Tf 760 20 Td (Page {i + 1}/{pages.Count}) Tj ET\n");
            var content = sb.ToString();
            objs.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 842 595] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>");
            objs.Add($"<< /Length {Latin1(content).Length} >>\nstream\n{content}endstream");
        }
        using var ms = new MemoryStream();
        void W(string s) { var b = Latin1(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objs.Count; i++) { offsets.Add(ms.Position); W($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        var xref = ms.Position;
        W($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    private static string Esc(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (ch is '(' or ')' or '\\') sb.Append('\\').Append(ch);
            else if (ch < 32 || ch > 255) sb.Append('?');
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}
