using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XamaMaster.Services;

namespace XamaMaster.Views;

/// <summary>Platform owner tools: agents, verify closing codes, import signed closing files, verified log with gap detection,
/// verify signed sales reports, platform secret backup.</summary>
public static class PlatformToolsWindow
{
    static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xB0, 0x00, 0x20));
    static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x60));

    public static void Show(Window owner)
    {
        var win = new Window
        {
            Title = "Platform tools — agents & closings", Width = 900, Height = 700, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White, Foreground = Brushes.Black,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13,
        };
        win.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        win.Resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
        win.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0xCC, 0xE4, 0xF7));
        win.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Brushes.Black;
        win.Resources[typeof(Button)] = new Style(typeof(Button)) { Setters = { new Setter(Control.PaddingProperty, new Thickness(12, 5, 12, 5)), new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0)) } };
        var tabs = new TabControl { Margin = new Thickness(8) };
        var log = LogTab();
        tabs.Items.Add(new TabItem { Header = "Verify closing", Content = VerifyTab(win, log.Reload) });
        tabs.Items.Add(new TabItem { Header = "Verified closings", Content = log.Ui });
        tabs.Items.Add(new TabItem { Header = "Agents", Content = AgentsTab(win) });
        tabs.Items.Add(new TabItem { Header = "Sales report", Content = ReportTab(win) });
        tabs.Items.Add(new TabItem { Header = "Platform secret", Content = SecretTab(win) });
        win.Content = tabs;
        win.ShowDialog();
    }

    static TextBlock T(string s, double size = 13, bool bold = false, Brush? fg = null) => new()
    { Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = fg ?? Brushes.Black, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };

    static Button B(string s, RoutedEventHandler h, bool def = false) { var b = new Button { Content = s, IsDefault = def }; b.Click += h; return b; }

    static StackPanel Row(params UIElement[] k) { var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) }; foreach (var x in k) p.Children.Add(x); return p; }

    static FrameworkElement L(string label, FrameworkElement input)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(input, 1); g.Children.Add(input);
        return g;
    }

    static ScrollViewer S(params UIElement[] kids)
    {
        var p = new StackPanel();
        foreach (var k in kids) p.Children.Add(k);
        return new ScrollViewer { Content = p, Padding = new Thickness(16), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    static TextBox Box(string text = "") => new() { Text = text, Padding = new Thickness(4) };

    static void Save(Window w, string name, string filter, byte[] data)
    {
        var d = new Microsoft.Win32.SaveFileDialog { FileName = name, Filter = filter };
        if (d.ShowDialog(w) == true) File.WriteAllBytes(d.FileName, data);
    }

    static UIElement VerifyTab(Window win, Action reload)
    {
        var qr = Box();
        var shop = Box(); var num = Box(); var amt = Box(); var code = Box();
        code.FontFamily = new FontFamily("Consolas"); code.FontSize = 18;
        var date = new DatePicker { SelectedDate = DateTime.Today, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        var result = T("", 15, true);
        string? agentFromQr = null;
        qr.TextChanged += (_, _) =>
        {
            var p = Closing.ParseQr(qr.Text);
            if (p == null) return;
            if (p.TryGetValue("shop", out var v)) shop.Text = v;
            if (p.TryGetValue("n", out v)) num.Text = v;
            if (p.TryGetValue("amt", out v)) amt.Text = v;
            if (p.TryGetValue("c", out v)) code.Text = v;
            if (p.TryGetValue("agent", out v)) agentFromQr = v;
            if (p.TryGetValue("d", out v) && DateTime.TryParseExact(v, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d)) date.SelectedDate = d;
        };
        void Show(PlatformAgents.VerifyResult r)
        {
            result.Text = r.Message;
            result.Foreground = !r.Valid ? Warn : r.Message.Contains("WARNING") ? Brushes.DarkOrange : Good;
            reload();
        }
        return S(
            T("Verify a closing receipt", 18, true),
            T("Enter what the agent brought: the shop code, closing #, amount received, date and the 12-character receipt code from the receipt. " +
              "Or paste the QR text (scan the receipt QR with any scanner app that types text).", fg: Muted),
            L("QR text (optional)", qr),
            L("Shop code", shop), L("Closing #", num), L("Amount received", amt), L("Date (closing day)", date), L("Receipt code", code),
            Row(B("Verify", (_, _) =>
            {
                if (!int.TryParse(num.Text.Trim(), out var n) || n < 1) { result.Text = "Closing # must be a number."; result.Foreground = Warn; return; }
                if (!decimal.TryParse(amt.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var a))
                { result.Text = "Amount must be a number."; result.Foreground = Warn; return; }
                try { Show(PlatformAgents.VerifyCode(shop.Text, n, a, date.SelectedDate ?? DateTime.Today, code.Text, agentFromQr)); }
                catch (Exception ex) { result.Text = ex.Message; result.Foreground = Warn; }
            }, true),
            B("Import signed closing file (.shammclosing)…", (_, _) =>
            {
                var d = new Microsoft.Win32.OpenFileDialog { Filter = "Signed closing (*.shammclosing)|*.shammclosing", Multiselect = true };
                if (d.ShowDialog(win) != true) return;
                var sb = new StringBuilder(); var ok = true;
                foreach (var f in d.FileNames.OrderBy(x => x))
                {
                    try { var r = PlatformAgents.ImportClosingFile(File.ReadAllText(f)); ok &= r.Valid && !r.Message.Contains("WARNING"); sb.AppendLine(Path.GetFileName(f) + ": " + r.Message); }
                    catch (Exception ex) { ok = false; sb.AppendLine(Path.GetFileName(f) + ": " + ex.Message); }
                }
                result.Text = sb.ToString().Trim(); result.Foreground = ok ? Good : Warn; reload();
            })),
            result);
    }

    static (UIElement Ui, Action Reload) LogTab()
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, MinHeight = 200, MaxHeight = 460, Background = Brushes.White };
        foreach (var (h, p) in new[] { ("Shop", "ShopCode"), ("Name", "ShopName"), ("#", "Number"), ("Amount", "AmountLabel"), ("Date", "DateYmd"), ("Agent", "AgentName"), ("Code", "ReceiptCode"), ("Source", "Source"), ("Verified (UTC)", "VerifiedUtc") })
            grid.Columns.Add(new DataGridTextColumn { Header = h, Binding = new System.Windows.Data.Binding(p) });
        var gaps = new StackPanel();
        void Reload()
        {
            var log = PlatformAgents.Log();
            grid.ItemsSource = log.OrderBy(x => x.ShopCode).ThenByDescending(x => x.Number).ToList();
            gaps.Children.Clear();
            foreach (var shop in log.Select(x => x.ShopCode).Distinct().OrderBy(x => x))
            {
                var g = PlatformAgents.Gaps(shop);
                var total = log.Where(x => x.ShopCode == shop).GroupBy(x => x.Number).Sum(x => x.First().Amount);
                gaps.Children.Add(g.Count == 0
                    ? T($"Shop {shop}: closings #1..#{log.Where(x => x.ShopCode == shop).Max(x => x.Number)} all verified · total received {Closing.Money(total)}", fg: Good)
                    : T($"Shop {shop}: MISSING closing(s) #{string.Join(", #", g)} — ask the agent for those receipts · total received {Closing.Money(total)}", bold: true, fg: Warn));
            }
            if (log.Count == 0) gaps.Children.Add(T("No closings verified yet.", fg: Muted));
        }
        Reload();
        return (S(T("Verified closings per shop", 18, true), gaps, grid), Reload);
    }

    static UIElement AgentsTab(Window win)
    {
        var list = new ListBox { MinHeight = 100, MaxHeight = 240, DisplayMemberPath = "AgentName", Margin = new Thickness(0, 0, 0, 10) };
        void Reload() => list.ItemsSource = PlatformAgents.Agents().Select(a => new { AgentName = $"{a.AgentName}  ({a.AgentId}, issued {a.IssuedUtc:yyyy-MM-dd})", a.AgentId, Name = a.AgentName }).ToList();
        Reload();
        var name = Box(); var p1 = new PasswordBox { Padding = new Thickness(4) }; var p2 = new PasswordBox { Padding = new Thickness(4) };
        var msg = T("");
        return S(
            T("Platform agents", 18, true),
            T("Create a credential for each field agent. Give the .shammagent file to the agent (USB); they import it once in X Seller " +
              "(Closing → Import agent credential). Only the agent knows the passphrase; the shop can't make valid receipt codes without it. " +
              "Use 8+ characters — a long passphrase is what keeps a dishonest shop from guessing it.", fg: Muted),
            list,
            T("New agent", 15, true),
            L("Agent name", name), L("Agent passphrase", p1), L("Repeat passphrase", p2),
            Row(B("Create credential file…", (_, _) =>
            {
                try
                {
                    if (p1.Password != p2.Password) throw new ArgumentException("Passphrases don't match.");
                    var json = PlatformAgents.CreateAgentCredential(name.Text, p1.Password);
                    var id = System.Text.Json.Nodes.JsonNode.Parse(json)!["AgentId"]!.ToString();
                    Save(win, $"{name.Text.Trim().Replace(' ', '_')}-{id}.shammagent", "Agent credential (*.shammagent)|*.shammagent", Encoding.UTF8.GetBytes(json));
                    msg.Text = $"Agent {id} created."; msg.Foreground = Good; p1.Clear(); p2.Clear(); name.Clear(); Reload();
                }
                catch (Exception ex) { msg.Text = ex.Message; msg.Foreground = Warn; }
            }, true)),
            msg);
    }

    static UIElement ReportTab(Window win)
    {
        var res = T("");
        return S(
            T("Verify a signed sales report", 18, true),
            T("Shops can export a signed .shammreport from the X Seller admin dashboard. Load it here to check the signature and read the totals.", fg: Muted),
            Row(B("Open .shammreport…", (_, _) =>
            {
                var d = new Microsoft.Win32.OpenFileDialog { Filter = "Signed report (*.shammreport)|*.shammreport" };
                if (d.ShowDialog(win) != true) return;
                try { res.Text = ReportVerifier.Describe(File.ReadAllText(d.FileName)); res.Foreground = res.Text.StartsWith("VALID") ? Good : Warn; }
                catch (Exception ex) { res.Text = ex.Message; res.Foreground = Warn; }
            })),
            res);
    }

    static UIElement SecretTab(Window win)
    {
        var p = new PasswordBox { Padding = new Thickness(4) };
        var msg = T("");
        return S(
            T("Platform secret", 18, true),
            T("All agent keys and receipt codes derive from a secret created on this PC (stored DPAPI-protected under Documents\\XamaMaster\\platform). " +
              "If it is lost, existing agent credentials and receipts can no longer be verified. Back it up (encrypted with a passphrase) and keep the file safe; " +
              "restore it on another PC to verify there.", fg: Muted),
            L("Backup passphrase", p),
            Row(B("Export backup…", (_, _) =>
            {
                try { Save(win, "xama-platform-secret.shammsecret", "Platform secret (*.shammsecret)|*.shammsecret", PlatformAgents.ExportSecret(p.Password)); msg.Text = "Backup saved."; msg.Foreground = Good; }
                catch (Exception ex) { msg.Text = ex.Message; msg.Foreground = Warn; }
            }),
            B("Restore backup…", (_, _) =>
            {
                if (PlatformAgents.HasSecret && MessageBox.Show(win, "Replace this PC's platform secret? Agents created here with a different secret stop working.", "Xama Master", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                var d = new Microsoft.Win32.OpenFileDialog { Filter = "Platform secret (*.shammsecret)|*.shammsecret" };
                if (d.ShowDialog(win) != true) return;
                try { PlatformAgents.ImportSecret(File.ReadAllText(d.FileName), p.Password); msg.Text = "Platform secret restored."; msg.Foreground = Good; }
                catch (Exception ex) { msg.Text = ex.Message; msg.Foreground = Warn; }
            })),
            msg);
    }
}
