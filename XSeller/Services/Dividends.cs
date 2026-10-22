using System.Globalization;
using System.Text;
using XSeller.Models;

namespace XSeller.Services;

/// <summary>
/// Movie dividends: of the platform's share of revenue (default 50%), a configurable cut
/// (default 40% of that share → 20% of total revenue) is paid out to movies in proportion to
/// their transfers in the period. Pass transfers (Kind=pass-use) and normally-priced protected
/// sales (Kind=protected) both count. Pass revenue (Kind=pass activations) is the pool that
/// gets allocated across movies that were transferred under a pass; protected sales contribute
/// their own revenue to their movie's share. Shop 40% and employee 10% are unchanged.
/// </summary>
public static class Dividends
{
    public const int DefaultMoviePercentOfPlatform = 40; // 40% of the platform's 50% = 20% of total

    public static int MoviePercentOfPlatform
    {
        get
        {
            try
            {
                if (int.TryParse(Accounts.Instance.Get("movie_pct_of_platform"), out var v) && v >= 0 && v <= 100) return v;
            }
            catch { }
            return DefaultMoviePercentOfPlatform;
        }
        set => Accounts.Instance.Set("movie_pct_of_platform", Math.Clamp(value, 0, 100).ToString());
    }

    public sealed class MovieRow
    {
        public string VideoId { get; set; } = "";
        public string Title { get; set; } = "";
        public int PassTransfers { get; set; }
        public int PaidTransfers { get; set; }
        public int Transfers => PassTransfers + PaidTransfers;
        public decimal PaidRevenue { get; set; }          // sum of TokenPrice of Kind=protected
        public decimal PassAllocated { get; set; }        // share of pass-activation revenue
        public decimal ShareOfRevenue => PaidRevenue + PassAllocated;
        public decimal MoviePayout { get; set; }          // MoviePct% of (platform% of ShareOfRevenue)
        public decimal PlatformKept { get; set; }         // rest of platform% of ShareOfRevenue
    }

    public sealed class Report
    {
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public int PlatformPercent { get; set; }
        public int ShopPercent { get; set; }
        public int EmployeePercent { get; set; }
        public int MoviePercentOfPlatform { get; set; }
        /// <summary>Movie% × Platform% / 100 — e.g. 40% of 50% = 20% of total revenue.</summary>
        public decimal MovieEffectivePercent => MoviePercentOfPlatform * PlatformPercent / 100m;
        public decimal PassRevenue { get; set; }
        public decimal PaidProtectedRevenue { get; set; }
        public int PassTransfers { get; set; }
        public int PaidTransfers { get; set; }
        public decimal TotalMoviePayout { get; set; }
        public decimal TotalPlatformKept { get; set; }
        public List<MovieRow> Movies { get; set; } = new();
    }

    public static Report Build(LedgerDatabase ledger, DateTime fromUtc, DateTime toUtc)
    {
        var sales = ledger.GetSales(fromUtc, toUtc);
        var (pp, sp, ep) = RevenueSplit.Current;
        var moviePct = MoviePercentOfPlatform;
        var passRev = sales.Where(s => s.Kind == "pass").Sum(s => (decimal)s.TokenPrice);
        var passUses = sales.Where(s => s.Kind == "pass-use").ToList();
        var paid = sales.Where(s => s.Kind == "protected").ToList();

        // Allocate pass revenue across movies by pass-use count.
        var passCount = passUses.GroupBy(s => s.VideoId).ToDictionary(g => g.Key, g => g.Count());
        var totalPassUses = passUses.Count;
        decimal Alloc(string vid) => totalPassUses == 0 ? 0 : passRev * passCount.GetValueOrDefault(vid) / totalPassUses;

        var ids = passUses.Select(s => s.VideoId).Concat(paid.Select(s => s.VideoId)).Distinct();
        var rows = new List<MovieRow>();
        foreach (var id in ids)
        {
            var title = (passUses.FirstOrDefault(s => s.VideoId == id) ?? paid.First(s => s.VideoId == id)).VideoTitle;
            var pCount = passCount.GetValueOrDefault(id);
            var paidRows = paid.Where(s => s.VideoId == id).ToList();
            var paidRev = paidRows.Sum(s => (decimal)s.TokenPrice);
            var allocated = Alloc(id);
            var share = paidRev + allocated;
            var platformSlice = Math.Round(share * pp / 100m, 4);
            var moviePay = Math.Round(platformSlice * moviePct / 100m, 2);
            var kept = Math.Round(platformSlice - moviePay, 2);
            rows.Add(new MovieRow
            {
                VideoId = id, Title = title,
                PassTransfers = pCount, PaidTransfers = paidRows.Count,
                PaidRevenue = paidRev, PassAllocated = Math.Round(allocated, 2),
                MoviePayout = moviePay, PlatformKept = kept,
            });
        }
        rows = rows.OrderByDescending(r => r.MoviePayout).ThenByDescending(r => r.Transfers).ThenBy(r => r.Title).ToList();
        return new Report
        {
            FromUtc = fromUtc, ToUtc = toUtc,
            PlatformPercent = pp, ShopPercent = sp, EmployeePercent = ep,
            MoviePercentOfPlatform = moviePct,
            PassRevenue = passRev,
            PaidProtectedRevenue = paid.Sum(s => (decimal)s.TokenPrice),
            PassTransfers = passUses.Count,
            PaidTransfers = paid.Count,
            TotalMoviePayout = rows.Sum(r => r.MoviePayout),
            TotalPlatformKept = rows.Sum(r => r.PlatformKept),
            Movies = rows,
        };
    }

    private static string M(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    public static string ToCsv(Report r)
    {
        var sb = new StringBuilder();
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        sb.AppendLine($"# Movie dividends,{r.FromUtc:yyyy-MM-dd} to {r.ToUtc.AddSeconds(-1):yyyy-MM-dd} (UTC)");
        sb.AppendLine($"# Split platform/shop/employee,{r.PlatformPercent}/{r.ShopPercent}/{r.EmployeePercent}");
        sb.AppendLine($"# Movie % of platform share,{r.MoviePercentOfPlatform} (= {M(r.MovieEffectivePercent)}% of total revenue)");
        sb.AppendLine($"# Pass revenue (activations),{M(r.PassRevenue)},pass transfers,{r.PassTransfers}");
        sb.AppendLine($"# Paid protected revenue,{M(r.PaidProtectedRevenue)},paid transfers,{r.PaidTransfers}");
        sb.AppendLine($"# Total movie payout,{M(r.TotalMoviePayout)},platform kept,{M(r.TotalPlatformKept)}");
        sb.AppendLine();
        sb.AppendLine("VideoId,Title,PassTransfers,PaidTransfers,Transfers,PaidRevenue,PassAllocated,ShareOfRevenue,MoviePayout,PlatformKept");
        foreach (var m in r.Movies)
            sb.AppendLine($"{Q(m.VideoId)},{Q(m.Title)},{m.PassTransfers},{m.PaidTransfers},{m.Transfers},{M(m.PaidRevenue)},{M(m.PassAllocated)},{M(m.ShareOfRevenue)},{M(m.MoviePayout)},{M(m.PlatformKept)}");
        return sb.ToString();
    }

    public static List<string> SummaryLines(Report r)
    {
        return
        [
            $"Movie dividends ({r.FromUtc:yyyy-MM-dd} → {r.ToUtc.AddSeconds(-1):yyyy-MM-dd} UTC)",
            $"Movie cut: {r.MoviePercentOfPlatform}% of platform's {r.PlatformPercent}%  (= {M(r.MovieEffectivePercent)}% of total). Shop {r.ShopPercent}% + employee {r.EmployeePercent}% unchanged.",
            $"Pass revenue {M(r.PassRevenue)} across {r.PassTransfers} pass transfer(s); paid protected revenue {M(r.PaidProtectedRevenue)} across {r.PaidTransfers} sale(s).",
            $"Total movie payout {M(r.TotalMoviePayout)}; platform keeps {M(r.TotalPlatformKept)} of its share.",
        ];
    }
}
