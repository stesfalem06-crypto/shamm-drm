using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XSeller.Models;
using XSeller.Services;


namespace XSeller;

public partial class MainWindow : Window
{
    private enum FilterKind { All, Video, Pdf, Image, Exe, Protected }

    private readonly AdbService _adb = new();
    private readonly LedgerDatabase _ledger = new();
    private readonly TransferService _transfer;
    private readonly SettlementExporter _settlement;
    private readonly LibraryIndex _index = new();

    private List<ConnectedDevice> _devices = new();
    private FilterKind _filter = FilterKind.All;
    private string _shopName = "Shop";

    private readonly string _shopNamePath = Path.Combine(FirstRunBootstrap.DocsRoot, "shopname.txt");

    public MainWindow()
    {
        InitializeComponent();
        _transfer = new TransferService(_adb, _ledger);
        LoadShopName();
        _settlement = new SettlementExporter(_ledger, _shopName);
        ShopNameText.Text = "·  " + _shopName;

        // Load persisted roots (seeds defaults on first run). Scans run in the background.
        foreach (var root in LibraryRootsStore.LoadOrSeed())
            TryAddRoot(root, persist: false);

        // Ensure canonical Library/Plain folders always exist for drops even if removed from index.
        try { Directory.CreateDirectory(FirstRunBootstrap.LibraryDir); } catch { }
        try { Directory.CreateDirectory(FirstRunBootstrap.PlainDir); } catch { }

        _index.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            RefreshRootsList();
            ApplySearch();
        });
        RefreshRootsList();
        StyleChips();
        ApplySearch();
        RefreshDebt();

        _adb.DevicesChanged += devices => Dispatcher.Invoke(() => OnDevices(devices));
        _adb.StartWatching(1200);
        OnDevices(_adb.ListDevices());

        StatusText.Text = $"Indexed {_index.Count} file(s). Type to search instantly.";
        Closed += (_, _) => { _adb.Dispose(); _index.Dispose(); };
    }

    private void TryAddRoot(string? path, bool persist)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            _index.AddRoot(path);
            if (persist) PersistRoots();
        }
        catch { }
    }

    private void PersistRoots()
    {
        try { LibraryRootsStore.Save(_index.Roots); }
        catch { /* disk full / ACL — index still works in-session */ }
    }

    private void RefreshRootsList()
    {
        RootsList.ItemsSource = _index.Roots
            .Select(r => r)
            .ToList();
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
            FilterKind.Protected => results.Where(i => i.Kind == MediaKind.Protected || i.IsEncrypted).ToList(),
            FilterKind.Video => results.Where(i => i.Kind == MediaKind.Video).ToList(),
            FilterKind.Pdf => results.Where(i => i.Kind == MediaKind.Pdf).ToList(),
            FilterKind.Image => results.Where(i => i.Kind == MediaKind.Image).ToList(),
            FilterKind.Exe => results.Where(i => i.Kind == MediaKind.Exe).ToList(),
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
            {
                tb.Foreground = on ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x9C));
                tb.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
        Paint(ChipAll, _filter == FilterKind.All);
        Paint(ChipVideo, _filter == FilterKind.Video);
        Paint(ChipPdf, _filter == FilterKind.Pdf);
        Paint(ChipImage, _filter == FilterKind.Image);
        Paint(ChipExe, _filter == FilterKind.Exe);
        Paint(ChipProtected, _filter == FilterKind.Protected);
    }

    private void SetFilter(FilterKind kind)
    {
        _filter = kind;
        StyleChips();
        ApplySearch();
    }

    private void ChipAll_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => SetFilter(FilterKind.All);
    private void ChipVideo_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => SetFilter(FilterKind.Video);
    private void ChipPdf_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => SetFilter(FilterKind.Pdf);
    private void ChipImage_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => SetFilter(FilterKind.Image);
    private void ChipExe_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => SetFilter(FilterKind.Exe);
    private void ChipProtected_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => SetFilter(FilterKind.Protected);

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
            DeviceBanner.Text = $"{ready.Count} phone(s) ready. Select a file and press Send.";
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
            StatusText.Text = "Select a file in the list first.";
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
            StatusText.Text = "Select a file first.";
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
        var before = _index.Roots.Count;
        TryAddRoot(dlg.FolderName, persist: true);
        RefreshRootsList();
        ApplySearch();
        if (_index.Roots.Count == before)
            StatusText.Text = $"Already indexing {dlg.FolderName}.";
        else
            StatusText.Text = $"Indexing {dlg.FolderName}… {_index.Count} files in index.";
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is not string path)
        {
            StatusText.Text = "Select a media folder in the list to remove.";
            return;
        }
        if (!_index.RemoveRoot(path))
        {
            StatusText.Text = "Could not remove that folder.";
            return;
        }
        PersistRoots();
        RefreshRootsList();
        ApplySearch();
        StatusText.Text = $"Removed folder from index: {path}";
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
