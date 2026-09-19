using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XSeller.Models;
using XSeller.Services;


namespace XSeller;

public partial class MainWindow : Window
{
    private enum FilterKind { All, Protected, Open }

    private readonly AdbService _adb = new();
    private readonly LedgerDatabase _ledger = new();
    private readonly TransferService _transfer;
    private readonly SettlementExporter _settlement;
    private readonly LibraryIndex _index = new();

    private List<ConnectedDevice> _devices = new();
    private FilterKind _filter = FilterKind.All;
    private string _shopName = "Shop";

    private readonly string _libraryDir = FirstRunBootstrap.LibraryDir;
    private readonly string _plainDir = FirstRunBootstrap.PlainDir;
    private readonly string _shopNamePath = Path.Combine(FirstRunBootstrap.DocsRoot, "shopname.txt");

    public MainWindow()
    {
        InitializeComponent();
        _transfer = new TransferService(_adb, _ledger);
        LoadShopName();
        _settlement = new SettlementExporter(_ledger, _shopName);
        ShopNameText.Text = "·  " + _shopName;

        _index.AddRoot(_libraryDir);
        _index.AddRoot(_plainDir);
        // Common media locations for open files
        TryAddUserFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        TryAddUserFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

        _index.Changed += () => Dispatcher.Invoke(ApplySearch);
        ApplySearch();
        RefreshDebt();

        _adb.DevicesChanged += devices => Dispatcher.Invoke(() => OnDevices(devices));
        _adb.StartWatching(1200);
        OnDevices(_adb.ListDevices());

        StatusText.Text = $"Indexed {_index.Count} file(s). Type to search instantly.";
        Closed += (_, _) => { _adb.Dispose(); _index.Dispose(); };
    }

    private void TryAddUserFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try { _index.AddRoot(path); } catch { }
    }

    private void LoadShopName()
    {
        try
        {
            if (File.Exists(_shopNamePath))
            {
                var s = File.ReadAllText(_shopNamePath).Trim();
                if (!string.IsNullOrWhiteSpace(s)) { _shopName = s; return; }
            }
            _shopName = Environment.MachineName;
            Directory.CreateDirectory(Path.GetDirectoryName(_shopNamePath)!);
            File.WriteAllText(_shopNamePath, _shopName);
        }
        catch { _shopName = "Shop"; }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        ApplySearch();
    }

    private void ApplySearch()
    {
        var q = SearchBox.Text;
        var results = _index.Search(q);
        results = _filter switch
        {
            FilterKind.Protected => results.Where(i => i.IsEncrypted).ToList(),
            FilterKind.Open => results.Where(i => !i.IsEncrypted).ToList(),
            _ => results
        };
        ResultsList.ItemsSource = results;
        LibraryEmpty.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        IndexCountText.Text = $"{_index.Count} indexed · {results.Count} shown";
    }

    private void StyleChips()
    {
        void Paint(Border b, bool on)
        {
            b.Background = new SolidColorBrush(
                on ? Color.FromRgb(0xFF, 0x6B, 0x2C) : Color.FromRgb(0x2A, 0x2A, 0x3A));
            if (b.Child is TextBlock tb)
                tb.Foreground = on ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x9C));
        }
        Paint(ChipAll, _filter == FilterKind.All);
        Paint(ChipProtected, _filter == FilterKind.Protected);
        Paint(ChipOpen, _filter == FilterKind.Open);
    }

    private void ChipAll_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    { _filter = FilterKind.All; StyleChips(); ApplySearch(); }
    private void ChipProtected_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    { _filter = FilterKind.Protected; StyleChips(); ApplySearch(); }
    private void ChipOpen_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    { _filter = FilterKind.Open; StyleChips(); ApplySearch(); }

    private void OnDevices(List<ConnectedDevice> devices)
    {
        _devices = devices;
        var ready = devices.Where(d => d.State == "device").ToList();
        var unauthorized = devices.Where(d => d.State == "unauthorized").ToList();
        DevicesList.ItemsSource = devices
            .Select(d => d.State == "device"
                ? $"{d.Model}  ·  ready"
                : d.State == "unauthorized"
                    ? $"{d.Model}  ·  tap Allow on phone"
                    : $"{d.Model}  ·  {d.State}")
            .ToList();
        DevicesEmpty.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (unauthorized.Count > 0)
        {
            DeviceBanner.Visibility = Visibility.Visible;
            DeviceBanner.Text = "Phone detected — unlock the screen and tap Allow on the USB debugging prompt. X Seller will connect automatically.";
        }
        else if (ready.Count > 0)
        {
            DeviceBanner.Visibility = Visibility.Visible;
            DeviceBanner.Text = $"{ready.Count} phone(s) ready. Select a video and press Send.";
            StatusText.Text = $"{ready.Count} phone(s) connected and optimized.";
        }
        else
        {
            DeviceBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshDebt()
    {
        try
        {
            var debt = _ledger.GetOutstandingDebt();
            DebtText.Text = $"{debt} token{(debt == 1 ? "" : "s")}";
        }
        catch { DebtText.Text = "0 tokens"; }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not LibraryItem item)
        {
            StatusText.Text = "Select a video in the list first.";
            return;
        }
        var ready = _devices.Where(d => d.State == "device").ToList();
        if (DevicesList.SelectedIndex < 0 || DevicesList.SelectedIndex >= _devices.Count)
        {
            StatusText.Text = "Select a connected phone. If none appear, enable USB debugging once and tap Allow.";
            return;
        }
        var device = _devices[DevicesList.SelectedIndex];
        if (device.State != "device")
        {
            StatusText.Text = "That phone is not authorized yet — unlock it and tap Allow.";
            return;
        }

        try
        {
            SendButton.IsEnabled = false;
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressLabel.Text = item.IsEncrypted
                ? $"Locking & sending \"{item.Title}\" to {device.Model}…"
                : $"Sending open file \"{item.Title}\" to {device.Model}…";
            StatusText.Text = ProgressLabel.Text;

            await Task.Run(() => _transfer.Send(item, device));

            if (item.IsEncrypted) RefreshDebt();
            StatusText.Text = item.IsEncrypted
                ? $"Sent protected \"{item.Title}\" · +{item.TokenPrice} token(s). Plays only on this phone in Xama."
                : $"Sent open \"{item.Title}\" · available in Xama and other players.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Send failed: {ex.Message}";
        }
        finally
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            SendButton.IsEnabled = true;
        }
    }

    private void CopyUsb_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not LibraryItem item)
        {
            StatusText.Text = "Select a video first.";
            return;
        }
        var wpf = new Microsoft.Win32.OpenFolderDialog { Title = "Select USB drive or folder" };
        if (wpf.ShowDialog() != true) return;
        try
        {
            _transfer.CopyToFolder(item, wpf.FolderName);
            StatusText.Text = item.IsEncrypted
                ? $"Copied protected package to {wpf.FolderName} (includes metadata)."
                : $"Copied open file to {wpf.FolderName}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Copy failed: {ex.Message}";
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Add folder to search index" };
        if (dlg.ShowDialog() != true) return;
        _index.AddRoot(dlg.FolderName);
        ApplySearch();
        StatusText.Text = $"Indexing {dlg.FolderName}… {_index.Count} files in index.";
    }

    private void ExportForAgent_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select USB drive for ledger export" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var path = _settlement.ExportForAgent(dlg.FolderName);
            StatusText.Text = $"Exported ledger: {Path.GetFileName(path)}";
        }
        catch (Exception ex) { StatusText.Text = $"Export failed: {ex.Message}"; }
    }

    private void ImportSettlement_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Settlement (*.shammack)|*.shammack",
            Title = "Import settlement acknowledgment"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var n = _settlement.ApplySettlementAck(dlg.FileName);
            RefreshDebt();
            StatusText.Text = $"Settlement applied: {n} sale(s) marked paid.";
        }
        catch (Exception ex) { StatusText.Text = $"Import failed: {ex.Message}"; }
    }
}
