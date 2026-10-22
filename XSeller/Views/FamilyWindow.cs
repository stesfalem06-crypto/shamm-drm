using System.Windows;
using System.Windows.Controls;
using XSeller.Services;

namespace XSeller.Views;

/// <summary>Register / manage a customer family group (up to 3 phones). Reuses weekly-pass phone lists when present.</summary>
public static class FamilyWindow
{
    private sealed class Row
    {
        public FamilyGroup G = null!;
        public long Id => G.Id;
        public string Name => G.Name;
        public string PhonesCount => $"{G.ActivePhones.Count()}/{FamilyGroups.MaxPhones}";
        public string PhoneModels => string.Join(", ", G.ActivePhones.Select(p => p.Model));
        public string Employee => G.UserName;
        public string Swaps => $"{G.Swaps}/{FamilyGroups.MaxSwaps}";
        public string Created => G.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public static void Show(Window owner, FamilyGroups families, Passes passes,
        Func<List<ConnectedPhone>> connected, string? preselectDeviceId, string shopName, Action<FamilyGroup?> changed)
    {
        var win = Ui.Dialog("Family group", 960, 680, owner);
        var root = new StackPanel();
        win.Content = Ui.Scroll(root);
        root.Children.Add(Ui.Text("Family group", 20, true));
        root.Children.Add(Ui.Text(
            $"Up to {FamilyGroups.MaxPhones} phones. Protected movies sent to any member unlock on all members " +
            $"(multi-recipient .shammkey). Max {FamilyGroups.MaxSwaps} swaps, logged. " +
            "If a phone is on an active weekly pass, that pass’s phones are used automatically when sending — no need to duplicate them here.",
            fg: Ui.Muted));

        var phoneBox = new ComboBox { DisplayMemberPath = "Label", Padding = new Thickness(6, 3, 6, 3), MinWidth = 320 };
        var phoneStatus = Ui.Text("", 14, true);
        var nameBox = new TextBox { Padding = new Thickness(4), Width = 220, Margin = new Thickness(0, 0, 8, 0) };
        var grid = Ui.Table(("Name", "Name"), ("Phones", "PhoneModels"), ("Count", "PhonesCount"),
            ("Employee", "Employee"), ("Swaps", "Swaps"), ("Created", "Created"), ("#", "Id"));
        grid.MaxHeight = 280;
        var passPhoneBox = new ComboBox { DisplayMemberPath = "Model", Padding = new Thickness(6, 3, 6, 3), MinWidth = 240 };
        var passBox = new ComboBox { DisplayMemberPath = "Label", Padding = new Thickness(6, 3, 6, 3), MinWidth = 280 };

        FamilyGroup? Sel() => (grid.SelectedItem as Row)?.G;
        ConnectedPhone? SelPhone() => phoneBox.SelectedItem as ConnectedPhone;

        void Reload(long? keep = null)
        {
            var k = keep ?? Sel()?.Id;
            var rows = families.All().AsEnumerable().Reverse()
                .Where(g => g.ActivePhones.Any())
                .Select(g => new Row { G = g }).ToList();
            grid.ItemsSource = rows;
            if (k != null) grid.SelectedItem = rows.FirstOrDefault(r => r.Id == k);

            var cur = SelPhone()?.DeviceId ?? preselectDeviceId;
            var phones = connected();
            phoneBox.ItemsSource = phones;
            phoneBox.SelectedItem = phones.FirstOrDefault(p => p.DeviceId == cur) ?? phones.FirstOrDefault();

            var activePasses = passes.All().Where(p => p.IsActiveAt(passes.Now()))
                .Select(p => new PassPick($"Pass #{p.Id} · {p.ActivePhones.Count()} phone(s) · {string.Join(", ", p.ActivePhones.Select(x => x.Model))}", p)).ToList();
            passBox.ItemsSource = activePasses;
            if (activePasses.Count > 0 && passBox.SelectedIndex < 0) passBox.SelectedIndex = 0;
            UpdatePhone();
        }

        void UpdatePhone()
        {
            var ph = SelPhone();
            if (ph == null) { phoneStatus.Text = "No identified phone connected."; phoneStatus.Foreground = Ui.Muted; return; }
            if (passes.ActiveFor(ph.DeviceId) is { } pass)
            {
                phoneStatus.Text = $"On weekly pass #{pass.Id} ({pass.ActivePhones.Count()}/{Passes.MaxPhones}) — sends wrap for all pass phones.";
                phoneStatus.Foreground = Ui.Good;
            }
            else if (families.ForDevice(ph.DeviceId) is { } g)
            {
                phoneStatus.Text = $"Family #{g.Id} “{g.Name}” · {g.ActivePhones.Count()}/{FamilyGroups.MaxPhones}";
                phoneStatus.Foreground = Ui.Good;
            }
            else { phoneStatus.Text = "Not in a family group (solo unlock when sending)."; phoneStatus.Foreground = Ui.Muted; }
        }

        void Done(FamilyGroup? g, string msg)
        {
            Reload(g?.Id);
            try { changed(g); } catch { }
            MessageBox.Show(win, msg, "Family group");
        }
        void Try(Action a) { try { a(); } catch (Exception ex) { Ui.Error(win, ex.Message); } }

        phoneBox.SelectionChanged += (_, _) => UpdatePhone();
        grid.SelectionChanged += (_, _) => passPhoneBox.ItemsSource = Sel()?.ActivePhones.ToList();

        root.Children.Add(Ui.Text("Connected phone", 15, true));
        root.Children.Add(Ui.Row(phoneBox, Ui.Button("Refresh", (_, _) => Reload())));
        root.Children.Add(phoneStatus);
        root.Children.Add(Ui.Row(
            nameBox,
            Ui.Button("Create family with this phone", (_, _) => Try(() =>
            {
                if (SelPhone() is not { } ph) return;
                var g = families.Create(nameBox.Text, ph.DeviceId, ph.Model);
                nameBox.Clear();
                Done(g, $"Family #{g.Id} created.");
            }), true),
            Ui.Button("Add this phone to selected family", (_, _) => Try(() =>
            {
                if (SelPhone() is not { } ph || Sel() is not { } g) { Ui.Error(win, "Pick a phone and a family."); return; }
                families.AddPhone(g.Id, ph.DeviceId, ph.Model);
                Done(g, $"{ph.Model} added.");
            }))));

        root.Children.Add(Ui.Text("From weekly pass", 15, true));
        root.Children.Add(Ui.Row(passBox, Ui.Button("Create family from pass phones", (_, _) => Try(() =>
        {
            if (passBox.SelectedItem is not PassPick pick) { Ui.Error(win, "No active weekly pass."); return; }
            var pass = pick.Pass;
            var g = families.CreateFromPass(pass, string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim());
            Done(g, $"Family #{g.Id} mirrors pass #{pass.Id}.");
        }))));

        root.Children.Add(Ui.Text("Families", 15, true));
        root.Children.Add(grid);
        root.Children.Add(Ui.Row(new TextBlock { Text = "Phone in selected family: ", VerticalAlignment = VerticalAlignment.Center }, passPhoneBox,
            Ui.Button("Remove", (_, _) => Try(() =>
            {
                if (Sel() is not { } g || passPhoneBox.SelectedItem is not FamilyPhone old) return;
                if (MessageBox.Show(win, $"Remove {old.Model} from family #{g.Id}?", "Family group", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                families.RemovePhone(g.Id, old.DeviceId);
                Done(g, "Removed.");
            })),
            Ui.Button("Swap: connected phone replaces selected", (_, _) => Try(() =>
            {
                if (SelPhone() is not { } ph || Sel() is not { } g || passPhoneBox.SelectedItem is not FamilyPhone old)
                { Ui.Error(win, "Select family phone to replace and a connected phone."); return; }
                families.Swap(g.Id, old.DeviceId, ph.DeviceId, ph.Model);
                Done(g, "Swapped.");
            })),
            Ui.Button("Rename…", (_, _) => Try(() =>
            {
                if (Sel() is not { } g) return;
                var n = AdminWindow.Prompt(win, "Family name:", false, g.Name);
                if (n == null) return;
                families.Rename(g.Id, n);
                Done(g, "Renamed.");
            }))));
        root.Children.Add(Ui.Row(Ui.Button("Close", (_, _) => win.Close())));
        Reload();
        win.ShowDialog();
    }

    private sealed record PassPick(string Label, WeeklyPass Pass);
}
