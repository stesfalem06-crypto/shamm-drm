using System.Windows;
using System.Windows.Controls;
using XSeller.Services;

namespace XSeller.Views;

public sealed record ConnectedPhone(string DeviceId, string Model, string? MtpId)
{
    public string Label => $"{Model}  ({DeviceId[..Math.Min(8, DeviceId.Length)]})";
}

/// <summary>Weekly pass: activate for a plugged-in phone, add family phones, remove / swap, and the passes list.</summary>
public static class PassWindow
{
    private sealed class PassRow
    {
        public WeeklyPass P = null!;
        public long Id => P.Id;
        public string Status { get; init; } = "";
        public string DaysLeft { get; init; } = "";
        public string PhonesCount { get; init; } = "";
        public string PhoneModels { get; init; } = "";
        public string Employee => P.UserName;
        public string Activated => P.ActivatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string Expires => P.ExpiresUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string Swaps => $"{P.Swaps}/{Passes.MaxSwaps}";
        public int Price => P.Price;
    }

    public static void Show(Window owner, Passes passes, Func<List<ConnectedPhone>> connected, string? preselectDeviceId, Action<WeeklyPass> changed)
    {
        var win = Ui.Dialog("Weekly pass", 980, 700, owner);
        var root = new StackPanel();
        win.Content = Ui.Scroll(root);

        var price = Prices.GetCategory("pass");
        root.Children.Add(Ui.Text("Weekly pass", 20, true));
        root.Children.Add(Ui.Text($"7 days from activation (this PC's clock) · up to {Passes.MaxPhones} phones · protected movies free, plain files charged as normal · " +
                                  $"valid only at this shop. Price: {price}" + (price == 0 ? "  ⚠ set the pass price in Admin dashboard → Prices" : ""), fg: Ui.Muted));
        if (passes.ClockRolledBack)
            root.Children.Add(Ui.Text("⚠ The PC clock is behind the last recorded time — pass days are counted from the last recorded time.", bold: true, fg: Ui.Warn));

        var phoneBox = new ComboBox { DisplayMemberPath = "Label", Padding = new Thickness(6, 3, 6, 3), MinWidth = 320 };
        var phoneStatus = Ui.Text("", 14, true);
        var grid = Ui.Table(("Phones", "PhoneModels"), ("Pass #", "Id"), ("Status", "Status"), ("Days left", "DaysLeft"), ("Phones", "PhonesCount"),
            ("Employee", "Employee"), ("Activated", "Activated"), ("Expires", "Expires"), ("Swaps", "Swaps"), ("Price", "Price"));
        grid.MaxHeight = 300;
        var passPhoneBox = new ComboBox { DisplayMemberPath = "Model", Padding = new Thickness(6, 3, 6, 3), MinWidth = 260 };
        var showExpired = new CheckBox { Content = "Show expired", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };

        WeeklyPass? SelPass() => (grid.SelectedItem as PassRow)?.P;
        ConnectedPhone? SelPhone() => phoneBox.SelectedItem as ConnectedPhone;

        void Reload(long? selectPass = null)
        {
            var now = passes.Now();
            var keep = selectPass ?? SelPass()?.Id;
            var rows = passes.All().AsEnumerable().Reverse()
                .Where(p => showExpired.IsChecked == true || p.IsActiveAt(now))
                .Select(p => new PassRow
                {
                    P = p,
                    Status = p.IsActiveAt(now) ? "Active" : "Expired",
                    DaysLeft = p.IsActiveAt(now) ? $"{p.DaysLeft(now)} ({p.Left(now).TotalHours:0} h)" : "—",
                    PhonesCount = $"{p.ActivePhones.Count()}/{Passes.MaxPhones}",
                    PhoneModels = string.Join(", ", p.ActivePhones.Select(x => x.Model)),
                }).ToList();
            grid.ItemsSource = rows;
            if (keep != null) grid.SelectedItem = rows.FirstOrDefault(r => r.Id == keep);

            var cur = SelPhone()?.DeviceId ?? preselectDeviceId;
            var phones = connected();
            phoneBox.ItemsSource = phones;
            phoneBox.SelectedItem = phones.FirstOrDefault(p => p.DeviceId == cur) ?? phones.FirstOrDefault();
            UpdatePhone();
        }

        void UpdatePhone()
        {
            var ph = SelPhone();
            if (ph == null) { phoneStatus.Text = "No identified phone connected (plug in, File transfer, open Xama once)."; phoneStatus.Foreground = Ui.Muted; return; }
            var now = passes.Now();
            var a = passes.ActiveFor(ph.DeviceId);
            if (a != null)
            {
                phoneStatus.Text = $"Pass #{a.Id} active · {a.DaysLeft(now)} day(s) left · phones {a.ActivePhones.Count()}/{Passes.MaxPhones} · until {a.ExpiresUtc.ToLocalTime():ddd dd MMM HH:mm}";
                phoneStatus.Foreground = a.DaysLeft(now) <= 1 ? System.Windows.Media.Brushes.DarkOrange : Ui.Good;
            }
            else if (passes.LatestFor(ph.DeviceId) is { } old)
            { phoneStatus.Text = $"Pass #{old.Id} expired {old.ExpiresUtc.ToLocalTime():ddd dd MMM HH:mm}"; phoneStatus.Foreground = Ui.Warn; }
            else { phoneStatus.Text = "No pass"; phoneStatus.Foreground = Ui.Muted; }
        }

        void Done(WeeklyPass? p, string msg)
        {
            Reload(p?.Id);
            if (p != null) changed(passes.All().First(x => x.Id == p.Id));
            MessageBox.Show(win, msg, "Weekly pass");
        }

        void Try(Action a) { try { a(); } catch (Exception ex) { Ui.Error(win, ex.Message); } }

        phoneBox.SelectionChanged += (_, _) => UpdatePhone();
        grid.SelectionChanged += (_, _) => passPhoneBox.ItemsSource = SelPass()?.ActivePhones.ToList();
        showExpired.Click += (_, _) => Reload();

        root.Children.Add(Ui.Text("Connected phone", 15, true));
        root.Children.Add(Ui.Row(phoneBox, Ui.Button("Refresh", (_, _) => Reload())));
        root.Children.Add(phoneStatus);
        root.Children.Add(Ui.Row(
            Ui.Button("Activate weekly pass", (_, _) => Try(() =>
            {
                if (SelPhone() is not { } ph) return;
                if (MessageBox.Show(win, $"Sell a 7-day pass for {ph.Model} at {price}? This is recorded as a sale by {Session.UserName}.", "Weekly pass",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var p = passes.Activate(ph.DeviceId, ph.Model);
                Done(p, $"Pass #{p.Id} active until {p.ExpiresUtc.ToLocalTime():ddd dd MMM HH:mm}.");
            }), true),
            Ui.Button("Add this phone to selected pass", (_, _) => Try(() =>
            {
                if (SelPhone() is not { } ph) return;
                if (SelPass() is not { } p) { Ui.Error(win, "Select a pass in the list first."); return; }
                passes.AddPhone(p.Id, ph.DeviceId, ph.Model);
                Done(p, $"{ph.Model} added to pass #{p.Id}.");
            })),
            Ui.Button("Swap: this phone replaces the chosen pass phone", (_, _) => Try(() =>
            {
                if (SelPhone() is not { } ph) return;
                if (SelPass() is not { } p || passPhoneBox.SelectedItem is not PassPhone old) { Ui.Error(win, "Select a pass and the phone to replace (below the list)."); return; }
                if (MessageBox.Show(win, $"Replace {old.Model} with {ph.Model} on pass #{p.Id}? (swap {p.Swaps + 1} of {Passes.MaxSwaps}, logged)", "Weekly pass",
                        MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                passes.Swap(p.Id, old.DeviceId, ph.DeviceId, ph.Model);
                Done(p, "Swapped.");
            }))));

        root.Children.Add(Ui.Text("Passes", 15, true));
        root.Children.Add(Ui.Row(showExpired));
        root.Children.Add(grid);
        root.Children.Add(Ui.Row(new TextBlock { Text = "Phone on selected pass: ", VerticalAlignment = VerticalAlignment.Center }, passPhoneBox,
            Ui.Button("Remove phone", (_, _) => Try(() =>
            {
                if (SelPass() is not { } p || passPhoneBox.SelectedItem is not PassPhone old) return;
                if (MessageBox.Show(win, $"Remove {old.Model} from pass #{p.Id}? (logged)", "Weekly pass", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                passes.RemovePhone(p.Id, old.DeviceId);
                Done(p, $"{old.Model} removed from pass #{p.Id}.");
            }))));
        root.Children.Add(Ui.Row(Ui.Button("Close", (_, _) => win.Close())));
        Reload();
        win.ShowDialog();
    }
}
