using System.IO;
using System.Windows;
using System.Windows.Controls;
using XSeller.Services;

namespace XSeller.Views;

/// <summary>Admin dashboard: sales per employee / item with the platform/shop/employee split,
/// exports, employees, prices, closing history, agents, tamper warnings.</summary>
public static class AdminWindow
{
    public static void Show(Window owner, LedgerDatabase ledger, Closings closings, string shopName, Action changed)
    {
        var win = Ui.Dialog("Admin dashboard — " + shopName, 1000, 720, owner);
        var tabs = new TabControl { Margin = new Thickness(8) };
        win.Content = tabs;
        tabs.Items.Add(new TabItem { Header = "Dashboard", Content = Dashboard(win, ledger, closings, shopName) });
        tabs.Items.Add(new TabItem { Header = "Closings", Content = ClosingsTab(win, ledger, closings, shopName, changed) });
        tabs.Items.Add(new TabItem { Header = "Employees", Content = Employees(win, changed) });
        tabs.Items.Add(new TabItem { Header = "Prices", Content = PricesTab(win) });
        win.ShowDialog();
    }

    private static UIElement Dashboard(Window win, LedgerDatabase ledger, Closings closings, string shopName)
    {
        var today = DateTime.Today;
        var from = new DatePicker { SelectedDate = new DateTime(today.Year, today.Month, 1), Width = 140, Margin = new Thickness(0, 0, 8, 0) };
        var to = new DatePicker { SelectedDate = today, Width = 140, Margin = new Thickness(0, 0, 8, 0) };
        var warn = new StackPanel();
        var totals = Ui.Text("", 14, true);
        var due = Ui.Text("", 14, true);
        var emp = Ui.Table(("Employee", "Label"), ("Sales", "Count"), ("Revenue", "Revenue"), ("Platform", "Platform"), ("Shop", "Shop"), ("Employee", "Employee"));
        var items = Ui.Table(("Item", "Label"), ("Sales", "Count"), ("Revenue", "Revenue"), ("Platform", "Platform"), ("Shop", "Shop"), ("Employee", "Employee"));
        emp.MaxHeight = 240; items.MaxHeight = 300;
        SalesReportData? data = null;

        void Reload()
        {
            var f = (from.SelectedDate ?? today).Date;
            var t = (to.SelectedDate ?? today).Date.AddDays(1);
            data = SalesReport.Build(ledger, f.ToUniversalTime(), t.ToUniversalTime(), shopName, closings);
            emp.ItemsSource = data.PerEmployee;
            items.ItemsSource = data.PerItem;
            var T = data.Total;
            totals.Text = $"Total: {T.Count} sale(s) · revenue {Ui.Money(T.Revenue)} · platform {data.PlatformPercent}% {Ui.Money(T.Platform)} · " +
                          $"shop {data.ShopPercent}% {Ui.Money(T.Shop)} · employees {data.EmployeePercent}% {Ui.Money(T.Employee)}";
            due.Text = $"Owed to platform since last closing (#{data.LastClosingNumber}): {Ui.Money(data.PlatformDueNow)}";
            warn.Children.Clear();
            foreach (var w in data.IntegrityWarnings) warn.Children.Add(Ui.Text("⚠ " + w, bold: true, fg: Ui.Warn));
            if (data.IntegrityWarnings.Count == 0) warn.Children.Add(Ui.Text("✓ Sales ledger and closing history intact (hash chains verified).", fg: Ui.Good));
        }
        from.SelectedDateChanged += (_, _) => Reload();
        to.SelectedDateChanged += (_, _) => Reload();
        string Stamp() => $"{(from.SelectedDate ?? today):yyyyMMdd}-{(to.SelectedDate ?? today):yyyyMMdd}";

        var panel = Ui.Stack(
            Ui.Row(new TextBlock { Text = "From ", VerticalAlignment = VerticalAlignment.Center }, from,
                   new TextBlock { Text = "To ", VerticalAlignment = VerticalAlignment.Center }, to,
                   Ui.Button("Refresh", (_, _) => Reload()),
                   Ui.Button("Export CSV…", (_, _) => ClosingWindow.SaveBytes(win, $"sales-{Stamp()}.csv", "CSV (*.csv)|*.csv", System.Text.Encoding.UTF8.GetBytes(SalesReport.ToCsv(data!)))),
                   Ui.Button("Export PDF…", (_, _) => ClosingWindow.SaveBytes(win, $"sales-{Stamp()}.pdf", "PDF (*.pdf)|*.pdf", SalesReport.ToPdf(data!))),
                   Ui.Button("Export signed report…", (_, _) => ClosingWindow.SaveBytes(win, $"sales-{Stamp()}.shammreport", "Signed report (*.shammreport)|*.shammreport", System.Text.Encoding.UTF8.GetBytes(SalesReport.ToSigned(data!))))),
            warn, due, totals,
            Ui.Text("Per employee", 15, true), emp,
            Ui.Text("Per item", 15, true), items);
        Reload();
        return Ui.Scroll(panel);
    }

    private static UIElement ClosingsTab(Window win, LedgerDatabase ledger, Closings closings, string shopName, Action changed)
    {
        var grid = Ui.Table(("Closed (UTC)", "ClosedAtUtc"), ("#", "Number"), ("Agent", "AgentName"), ("Sales", "SalesCount"), ("Revenue", "Revenue"),
            ("Platform due", "PlatformDue"), ("Received", "AmountReceived"), ("Receipt code", "ReceiptCode"));
        grid.MaxHeight = 380;
        var status = new StackPanel();
        var receipt = new StackPanel();
        void Reload()
        {
            grid.ItemsSource = closings.History().AsEnumerable().Reverse().ToList();
            status.Children.Clear();
            var problems = closings.VerifyHistory();
            if (problems.Count == 0) status.Children.Add(Ui.Text("✓ Closing history chain intact. History is append-only and cannot be cleared.", fg: Ui.Good));
            foreach (var p in problems) status.Children.Add(Ui.Text("⚠ " + p, bold: true, fg: Ui.Warn));
            var agents = closings.Agents();
            status.Children.Add(Ui.Text(agents.Count == 0 ? "No platform agent registered yet." : "Agents: " + string.Join(", ", agents.Select(a => $"{a.AgentName} ({a.AgentId})")), fg: Ui.Muted));
        }
        grid.SelectionChanged += (_, _) => { if (grid.SelectedItem is ClosingRecord r) ClosingWindow.ShowReceipt(win, receipt, closings, r); };
        Reload();
        return Ui.Scroll(Ui.Stack(
            Ui.Row(Ui.Button("Start closing with agent…", (_, _) => { ClosingWindow.Show(win, closings, ledger, shopName, changed); Reload(); changed(); }),
                   Ui.Button("Import agent credential…", (_, _) => { ClosingWindow.ImportAgent(win, closings); Reload(); })),
            status, grid, Ui.Text("Select a closing to reprint its receipt / QR.", fg: Ui.Muted), receipt));
    }

    private static UIElement Employees(Window win, Action changed)
    {
        var grid = Ui.Table(("Name", "Name"), ("Role", "RoleLabel"), ("Status", "StatusLabel"), ("Created (UTC)", "CreatedUtc"));
        grid.MaxHeight = 360;
        void Reload() => grid.ItemsSource = Accounts.Instance.ListUsers();
        var name = new TextBox { Width = 180, Padding = new Thickness(4), Margin = new Thickness(0, 0, 8, 0) };
        var role = new ComboBox { ItemsSource = new[] { "Seller (employee)", "Admin (owner)" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 8, 0) };
        var pin = new PasswordBox { Width = 140, Padding = new Thickness(4), Margin = new Thickness(0, 0, 8, 0) };
        UserAccount? Sel() => grid.SelectedItem as UserAccount;
        Reload();
        var on = Accounts.Instance.Enabled;
        return Ui.Scroll(Ui.Stack(
            Ui.Text(on ? "Accounts are ON: users sign in at startup and each sale is attributed to them." :
                         "Accounts are OFF (all sales go to “Default”). Use “Enable accounts” in the main window to turn them on.", fg: Ui.Muted),
            Ui.Text("Add employee", 15, true),
            Ui.Row(new TextBlock { Text = "Name ", VerticalAlignment = VerticalAlignment.Center }, name, role,
                   new TextBlock { Text = "PIN ", VerticalAlignment = VerticalAlignment.Center }, pin,
                   Ui.Button("Add", (_, _) =>
                   {
                       try
                       {
                           Accounts.Instance.AddUser(name.Text, role.SelectedIndex == 1 ? UserRole.Admin : UserRole.Seller, pin.Password);
                           name.Clear(); pin.Clear(); Reload(); changed();
                       }
                       catch (Exception ex) { Ui.Error(win, ex.Message); }
                   })),
            grid,
            Ui.Row(
                Ui.Button("Reset PIN…", (_, _) =>
                {
                    if (Sel() is not { } u) return;
                    var p = Prompt(win, $"New PIN / password for {u.Name}", true);
                    if (p == null) return;
                    try { Accounts.Instance.ResetSecret(u.Id, p); MessageBox.Show(win, "PIN changed.", "X Seller"); }
                    catch (Exception ex) { Ui.Error(win, ex.Message); }
                }),
                Ui.Button("Disable / enable", (_, _) =>
                {
                    if (Sel() is not { } u) return;
                    try { Accounts.Instance.SetDisabled(u.Id, !u.Disabled); Reload(); }
                    catch (Exception ex) { Ui.Error(win, ex.Message); }
                }),
                Ui.Button("Turn accounts off", (_, _) =>
                {
                    if (MessageBox.Show(win, "Turn off login? Sales will be attributed to “Default” again. Users are kept.", "X Seller", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    Accounts.Instance.DisableAccounts(); Session.SignOut(); changed();
                }))));
    }

    private static UIElement PricesTab(Window win)
    {
        var boxes = new Dictionary<string, TextBox>();
        var panel = Ui.Stack(
            Ui.Text("Prices for plain (unprotected) items", 15, true),
            Ui.Text("Protected videos use the price inside their package. Plain items use the category price below, unless a per-item " +
                    "price was set (right-click an item in the results → “Set price…”).", fg: Ui.Muted));
        foreach (var c in Prices.Categories)
        {
            var tb = new TextBox { Text = Prices.GetCategory(c).ToString(), Width = 120, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left };
            boxes[c] = tb;
            panel.Children.Add(Ui.Labeled(c, tb, 120));
        }
        var (pp, sp, ep) = RevenueSplit.Current;
        panel.Children.Add(Ui.Row(Ui.Button("Save prices", (_, _) =>
        {
            foreach (var (c, tb) in boxes)
            {
                if (!int.TryParse(tb.Text.Trim(), out var v) || v < 0) { Ui.Error(win, $"Price for {c} must be a whole number ≥ 0."); return; }
                Prices.SetCategory(c, v);
            }
            MessageBox.Show(win, "Prices saved.", "X Seller");
        }, true)));
        panel.Children.Add(Ui.Text($"Revenue split: platform {pp}% · shop {sp}% · employee {ep}% (set by the platform).", fg: Ui.Muted));
        return Ui.Scroll(panel);
    }

    public static string? Prompt(Window owner, string question, bool secret = false, string initial = "")
    {
        var win = Ui.Dialog("X Seller", 420, 200, owner);
        win.ResizeMode = ResizeMode.NoResize;
        Control input = secret ? new PasswordBox { Padding = new Thickness(4) } : new TextBox { Text = initial, Padding = new Thickness(4) };
        string? result = null;
        win.Content = Ui.Scroll(Ui.Stack(Ui.Text(question), input, Ui.Row(
            Ui.Button("OK", (_, _) => { result = input is PasswordBox p ? p.Password : ((TextBox)input).Text; win.DialogResult = true; }, true),
            Ui.Button("Cancel", (_, _) => win.DialogResult = false))));
        win.Loaded += (_, _) => input.Focus();
        win.ShowDialog();
        return result;
    }
}
