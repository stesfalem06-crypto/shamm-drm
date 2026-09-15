using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using XSeller.Models;
using XSeller.Services;

namespace XSeller;

public partial class MainWindow : Window
{
    private readonly AdbService _adb = new();
    private readonly LedgerDatabase _ledger = new();
    private readonly TransferService _transfer;
    private readonly SettlementExporter _settlement;

    private readonly string _shopNamePath;
    private string _shopName = "Shop";

    private List<VideoMetadata> _allVideos = new();
    private List<ConnectedDevice> _connectedDevices = new();

    private readonly string _libraryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XSeller", "Library");

    public MainWindow()
    {
        InitializeComponent();
        _transfer = new TransferService(_adb, _ledger);
        Directory.CreateDirectory(_libraryDir);

        _shopNamePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XSeller", "shopname.txt");
        LoadOrPromptShopName();
        _settlement = new SettlementExporter(_ledger, _shopName);
        ShopNameText.Text = $"·  {_shopName}";

        LoadLibrary();
        RefreshDevices();
        RefreshDebt();
        UpdateSearchPlaceholder();
    }

    private void LoadOrPromptShopName()
    {
        if (File.Exists(_shopNamePath))
        {
            var saved = File.ReadAllText(_shopNamePath).Trim();
            if (!string.IsNullOrWhiteSpace(saved)) { _shopName = saved; return; }
        }
        _shopName = Environment.MachineName;
        Directory.CreateDirectory(Path.GetDirectoryName(_shopNamePath)!);
        File.WriteAllText(_shopNamePath, _shopName);
    }

    private void LoadLibrary()
    {
        _allVideos.Clear();
        foreach (var metaFile in Directory.GetFiles(_libraryDir, "*.shammmeta"))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<VideoMetadata>(File.ReadAllText(metaFile));
                if (meta != null) _allVideos.Add(meta);
            }
            catch { /* skip */ }
        }
        ApplyFilter(SearchBox.Text);
        StatusText.Text = _allVideos.Count == 0
            ? "Library empty — copy packages from Xama Master into Documents\\XSeller\\Library."
            : $"Loaded {_allVideos.Count} video(s). Type to search.";
    }

    private void ApplyFilter(string? query)
    {
        var q = (query ?? "").Trim();
        IEnumerable<VideoMetadata> filtered = _allVideos;
        if (!string.IsNullOrEmpty(q))
        {
            filtered = _allVideos.Where(v =>
                (v.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (v.VideoId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        var list = filtered.ToList();
        ResultsList.ItemsSource = list;
        LibraryEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSearchPlaceholder()
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchPlaceholder();
        ApplyFilter(SearchBox.Text);
    }

    private void RefreshDevices()
    {
        try
        {
            _connectedDevices = _adb.ListDevices();
            DevicesList.ItemsSource = _connectedDevices
                .Select(d => $"{d.Model}  ({d.Serial})")
                .ToList();
            DevicesEmpty.Visibility = _connectedDevices.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
            if (_connectedDevices.Count > 0)
                StatusText.Text = $"{_connectedDevices.Count} phone(s) connected.";
            else
                StatusText.Text = "No phone detected. Enable USB debugging, plug in, then Refresh.";
        }
        catch (Exception ex)
        {
            _connectedDevices = new();
            DevicesList.ItemsSource = null;
            DevicesEmpty.Visibility = Visibility.Visible;
            StatusText.Text = $"Could not list devices: {ex.Message}";
        }
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void RefreshDebt()
    {
        var debt = _ledger.GetOutstandingDebt();
        DebtText.Text = $"{debt} token{(debt == 1 ? "" : "s")}";
    }

    private void ExportForAgent_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select the USB drive to export to" };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var path = _settlement.ExportForAgent(dlg.FolderName);
            StatusText.Text = $"Exported ledger for the agent: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Export failed: {ex.Message}";
        }
    }

    private void ImportSettlement_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Settlement files (*.shammack)|*.shammack",
            Title = "Select the settlement file from the agent's USB drive"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var count = _settlement.ApplySettlementAck(dlg.FileName);
            RefreshDebt();
            StatusText.Text = $"Applied settlement: {count} sale(s) marked paid. Outstanding balance updated.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Import failed: {ex.Message}";
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not VideoMetadata video)
        {
            StatusText.Text = "Select a video from the list first.";
            return;
        }
        if (DevicesList.SelectedIndex < 0 || DevicesList.SelectedIndex >= _connectedDevices.Count)
        {
            StatusText.Text = "Select a connected phone first. If none appear, enable USB debugging and Refresh.";
            return;
        }
        var device = _connectedDevices[DevicesList.SelectedIndex];
        var shammvidPath = Path.Combine(_libraryDir, $"{video.VideoId}.shammvid");
        if (!File.Exists(shammvidPath))
        {
            StatusText.Text = "Encrypted video file missing from library folder.";
            return;
        }

        try
        {
            SendButton.IsEnabled = false;
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressLabel.Text = $"Sending \"{video.Title}\" to {device.Model}…";
            StatusText.Text = ProgressLabel.Text;

            await Task.Run(() => _transfer.SendVideo(video, shammvidPath, device));

            RefreshDebt();
            StatusText.Text = $"Sent \"{video.Title}\" to {device.Model}. +{video.TokenPrice} token(s) added to the tab.";
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
}
