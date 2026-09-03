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

    // Simple, first-run-editable shop identity so exports/invoices are
    // labeled sensibly. Stored as plain text next to the ledger - not
    // secret, just a label.
    private readonly string _shopNamePath;
    private string _shopName = "Shop";

    private List<VideoMetadata> _allVideos = new();
    private List<ConnectedDevice> _connectedDevices = new();

    // Shop's local copy of the library, delivered from the company however
    // videos physically reach the shop (USB drive from Xama Master, etc.)
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

        LoadLibrary();
        RefreshDevices();
        RefreshDebt();
    }

    private void LoadOrPromptShopName()
    {
        if (File.Exists(_shopNamePath))
        {
            var saved = File.ReadAllText(_shopNamePath).Trim();
            if (!string.IsNullOrWhiteSpace(saved)) { _shopName = saved; return; }
        }
        // Defaults to the PC's machine name - good enough to tell shops
        // apart on sight; edit Documents\XSeller\shopname.txt to rename.
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
            catch { /* skip unreadable metadata rather than crash */ }
        }
        ResultsList.ItemsSource = _allVideos;
        StatusText.Text = $"Library loaded: {_allVideos.Count} video(s). Drop new .shammvid/.shammmeta pairs into " +
                           $"Documents\\XSeller\\Library to make them sellable.";
    }

    // Instant filter-as-you-type, the way "Everything" search feels.
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        ResultsList.ItemsSource = string.IsNullOrEmpty(q)
            ? _allVideos
            : _allVideos.Where(v => v.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || v.VideoId.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void RefreshDevices()
    {
        try
        {
            _connectedDevices = _adb.ListDevices();
            DevicesList.ItemsSource = _connectedDevices.Select(d => $"{d.Model}  ({d.Serial})").ToList();
            StatusText.Text = _connectedDevices.Count == 0
                ? "No phones detected. Plug in a phone with USB debugging enabled."
                : $"{_connectedDevices.Count} phone(s) connected.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't reach ADB: {ex.Message}";
        }
    }

    private void RefreshDebt()
    {
        DebtText.Text = $"{_ledger.GetOutstandingDebt()} tokens";
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

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not VideoMetadata video)
        {
            StatusText.Text = "Select a video from the list first.";
            return;
        }
        if (DevicesList.SelectedIndex < 0 || DevicesList.SelectedIndex >= _connectedDevices.Count)
        {
            StatusText.Text = "Select a connected phone first.";
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
            IsEnabled = false;
            StatusText.Text = $"Sending \"{video.Title}\" to {device.Model}...";
            _transfer.SendVideo(video, shammvidPath, device);
            RefreshDebt();
            StatusText.Text = $"Sent \"{video.Title}\" to {device.Model}. +{video.TokenPrice} token(s) added to the tab.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Send failed: {ex.Message}";
        }
        finally
        {
            IsEnabled = true;
        }
    }
}
