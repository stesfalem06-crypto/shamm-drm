using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XSeller.Services;

namespace XSeller.Views;

/// <summary>
/// Settlement with the platform agent: shows what is owed since the last closing, the agent
/// signs in with the platform-issued credential, enters the cash received, and X Seller appends
/// a hash-chained closing record and shows the Closing Receipt Code + QR for the owner.
/// </summary>
public static class ClosingWindow
{
    public static void Show(Window owner, Closings closings, LedgerDatabase ledger, string shopName, Action onClosed)
    {
        var win = Ui.Dialog("Closing — settle with platform agent", 760, 720, owner);
        var root = new StackPanel();
        win.Content = Ui.Scroll(root);

        var pending = closings.Pending(shopName);
        var warnings = Integrity.Warnings(ledger, closings);

        root.Children.Add(Ui.Text($"Closing #{pending.Number}", 20, bold: true));
        root.Children.Add(Ui.Text($"{shopName}  ·  shop code {pending.ShopCode}", fg: Ui.Muted));
        foreach (var w in warnings) root.Children.Add(Ui.Text("⚠ " + w, bold: true, fg: Ui.Warn));
        var bak = BackupService.GetHealth();
        root.Children.Add(Ui.Text("Backups: " + bak.Summary, fg: bak.AnyOk ? Ui.Good : Ui.Warn));
        if (!string.IsNullOrEmpty(BackupService.LastStartupMessage))
            root.Children.Add(Ui.Text("Startup: " + BackupService.LastStartupMessage, fg: Ui.Muted));
        root.Children.Add(Ui.Text($"Period: {pending.PeriodStartUtc.ToLocalTime():yyyy-MM-dd HH:mm} → now  ·  {pending.SalesCount} sale(s)"));

        var summary = new Grid { Margin = new Thickness(0, 6, 0, 10) };
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        summary.ColumnDefinitions.Add(new ColumnDefinition());
        void Line(int row, string k, string v, bool bold = false)
        {
            summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var a = Ui.Text(k, 14, bold); var b = Ui.Text(v, 14, bold);
            Grid.SetRow(a, row); Grid.SetRow(b, row); Grid.SetColumn(b, 1);
            summary.Children.Add(a); summary.Children.Add(b);
        }
        Line(0, "Revenue", Ui.Money(pending.Revenue));
        Line(1, $"Platform {pending.PlatformPercent}% — DUE from shop", Ui.Money(pending.PlatformDue), true);
        Line(2, $"Shop {pending.ShopPercent}%", Ui.Money(pending.ShopShare));
        Line(3, $"Employees {pending.EmployeePercent}% (total)", Ui.Money(pending.EmployeeTotal));
        root.Children.Add(summary);

        var emp = Ui.Table(("Employee", "UserName"), ("Sales", "Count"), ("Revenue", "Revenue"), ("10% share", "Share"));
        ((DataGridTextColumn)emp.Columns[3]).Header = $"{pending.EmployeePercent}% share";
        emp.ItemsSource = pending.Employees;
        emp.MaxHeight = 220;
        root.Children.Add(emp);

        var div = Dividends.Build(ledger, pending.PeriodStartUtc, pending.PeriodEndUtc);
        root.Children.Add(Ui.Text("Movie dividends (this period)", 15, true));
        foreach (var line in Dividends.SummaryLines(div).Skip(1)) root.Children.Add(Ui.Text(line, fg: Ui.Muted));
        var dgrid = Ui.Table(("Title", "Title"), ("Transfers", "Transfers"), ("Share of revenue", "ShareOfRevenue"),
            ("Movie payout", "MoviePayout"), ("Platform kept", "PlatformKept"));
        dgrid.ItemsSource = div.Movies.Take(30).ToList();
        dgrid.MaxHeight = 180;
        root.Children.Add(dgrid);
        if (div.Movies.Count > 30) root.Children.Add(Ui.Text($"(showing top 30 of {div.Movies.Count} — full list in Admin → Movie dividends / CSV export)", fg: Ui.Muted));

        var agents = closings.Agents();
        if (agents.Count == 0)
        {
            root.Children.Add(Ui.Text("No platform agent is registered on this PC. The agent must import their credential file (.shammagent, " +
                                      "issued by the platform owner in Xama Master) first.", bold: true, fg: Ui.Warn));
            root.Children.Add(Ui.Row(Ui.Button("Import agent credential…", (_, _) =>
            {
                if (ImportAgent(win, closings)) { win.Close(); Show(owner, closings, ledger, shopName, onClosed); }
            }), Ui.Button("Close", (_, _) => win.Close())));
            win.ShowDialog();
            return;
        }

        root.Children.Add(Ui.Text("Platform agent", 15, bold: true));
        var agentBox = new ComboBox { ItemsSource = agents, DisplayMemberPath = "AgentName", SelectedIndex = 0, Padding = new Thickness(6, 3, 6, 3) };
        var pass = new PasswordBox { Padding = new Thickness(4) };
        var amount = new TextBox { Text = ClosingRecord.Money(pending.PlatformDue), Padding = new Thickness(4), FontSize = 15 };
        var err = Ui.Text("", fg: Ui.Warn);
        root.Children.Add(Ui.Labeled("Agent", agentBox, 220));
        root.Children.Add(Ui.Labeled("Agent passphrase (agent types it)", pass, 220));
        root.Children.Add(Ui.Labeled("Amount received (cash)", amount, 220));
        root.Children.Add(err);
        var result = new StackPanel();
        Button? confirm = null;
        confirm = Ui.Button("Confirm closing", (_, _) =>
        {
            if (agentBox.SelectedItem is not AgentCredential a) { err.Text = "Pick the agent."; return; }
            if (!decimal.TryParse(amount.Text.Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amt) || amt < 0)
            { err.Text = "Enter the amount received (number)."; return; }
            if (amt != pending.PlatformDue &&
                MessageBox.Show(win, $"Amount received {Ui.Money(amt)} differs from the platform due {Ui.Money(pending.PlatformDue)}. Record it anyway?",
                    "Closing", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                var rec = closings.Close(pending, a, pass.Password, amt);
                pass.Clear();
                confirm!.IsEnabled = false; agentBox.IsEnabled = false; amount.IsEnabled = false; pass.IsEnabled = false;
                err.Text = "";
                ShowReceipt(win, result, closings, rec);
                onClosed();
            }
            catch (UnauthorizedAccessException) { err.Text = "Wrong agent passphrase. (Only the platform agent knows it.)"; pass.Clear(); }
            catch (Exception ex) { err.Text = ex.Message; }
        }, true);
        root.Children.Add(Ui.Row(confirm, Ui.Button("Import another agent…", (_, _) => { if (ImportAgent(win, closings)) agentBox.ItemsSource = closings.Agents(); }),
            Ui.Button("Close", (_, _) => win.Close())));
        root.Children.Add(result);
        win.ShowDialog();
    }

    public static bool ImportAgent(Window owner, Closings closings)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Agent credential (*.shammagent)|*.shammagent", Title = "Import platform agent credential" };
        if (dlg.ShowDialog(owner) != true) return false;
        try
        {
            var a = AgentCredential.Parse(File.ReadAllText(dlg.FileName));
            closings.ImportAgent(a);
            MessageBox.Show(owner, $"Agent “{a.AgentName}” registered on this PC.", "X Seller");
            return true;
        }
        catch (Exception ex) { Ui.Error(owner, "Import failed: " + ex.Message); return false; }
    }

    public static BitmapImage Qr(string text)
    {
        var data = new QRCoder.QRCodeGenerator().CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
        var bmp = new BitmapImage();
        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(png); bmp.EndInit(); bmp.Freeze();
        return bmp;
    }

    public static void ShowReceipt(Window win, Panel host, Closings closings, ClosingRecord rec)
    {
        host.Children.Clear();
        var card = new Border { BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), Padding = new Thickness(16), Margin = new Thickness(0, 10, 0, 10), Background = Brushes.White };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = Ui.Stack(
            Ui.Text($"CLOSING RECEIPT  #{rec.Number}", 16, true),
            Ui.Text($"Shop {rec.ShopName}  (code {rec.ShopCode})"),
            Ui.Text($"{rec.ClosedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} local  ·  agent {rec.AgentName}"),
            Ui.Text($"Sales {rec.SalesCount}  ·  platform due {Ui.Money(rec.PlatformDue)}"),
            Ui.Text($"Amount received: {Ui.Money(rec.AmountReceived)}", 15, true),
            Ui.Text("Receipt code", fg: Ui.Muted),
            new TextBox { Text = rec.ReceiptCode, IsReadOnly = true, FontSize = 26, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0) },
            Ui.Text("Give this code to the platform owner with the cash. Verify in Xama Master → Platform tools → Verify closing.", 11, fg: Ui.Muted));
        g.Children.Add(left);
        var img = new Image { Source = Qr(rec.QrText), Width = 180, Height = 180, Margin = new Thickness(12, 0, 0, 0) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        Grid.SetColumn(img, 1);
        g.Children.Add(img);
        card.Child = g;
        host.Children.Add(card);
        var signed = closings.SignedClosingFile(rec.Number);
        host.Children.Add(Ui.Row(
            Ui.Button("Print receipt", (_, _) =>
            {
                var pd = new PrintDialog();
                if (pd.ShowDialog() == true) pd.PrintVisual(card, $"Closing receipt #{rec.Number}");
            }),
            Ui.Button("Save receipt PDF…", (_, _) => SaveBytes(win, $"closing-{rec.ShopCode}-{rec.Number}.pdf", "PDF (*.pdf)|*.pdf", Closings.ReceiptPdf(rec))),
            Ui.Button("Export signed closing file (USB)…", (_, _) =>
            {
                if (signed == null) { Ui.Error(win, "The signed file can only be exported right after closing (it needs the agent key)."); return; }
                SaveBytes(win, $"closing-{rec.ShopCode}-{rec.Number}.shammclosing", "Signed closing (*.shammclosing)|*.shammclosing", System.Text.Encoding.UTF8.GetBytes(signed));
            })));
        if (signed == null) host.Children.Add(Ui.Text("Signed closing file: only available in the session the closing was made.", 11, fg: Ui.Muted));
    }

    public static void SaveBytes(Window owner, string name, string filter, byte[] data)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = name, Filter = filter };
        if (dlg.ShowDialog(owner) != true) return;
        try { File.WriteAllBytes(dlg.FileName, data); }
        catch (Exception ex) { Ui.Error(owner, "Save failed: " + ex.Message); }
    }
}
