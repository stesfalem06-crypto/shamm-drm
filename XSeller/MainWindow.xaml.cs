using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private readonly BatchSender _batch;
    private readonly System.Collections.ObjectModel.ObservableCollection<DeviceRow> _deviceRows = new();
    private readonly List<LibraryItem> _queue = new();
    private FilterKind _filter = FilterKind.All;
    private string _shopName = "Shop";
    private SortColumn _sort = SortColumn.Name;
    private bool _sortDesc;
    private CancellationTokenSource? _searchCts;
    private bool _sending;

    private readonly string _shopNamePath = Path.Combine(FirstRunBootstrap.DocsRoot, "shopname.txt");

    public MainWindow()
    {
        InitializeComponent();
        _transfer = new TransferService(_adb, _ledger);
        _batch = new BatchSender(_adb, _transfer);
        LoadShopName();
        _settlement = new SettlementExporter(_ledger, _shopName);
        ShopNameText.Text = "·  " + _shopName;

        // Ensure canonical Library/Plain folders always exist for drops even if removed from index.
        try { Directory.CreateDirectory(FirstRunBootstrap.LibraryDir); } catch { }
        try { Directory.CreateDirectory(FirstRunBootstrap.PlainDir); } catch { }

        // Persisted roots (seeds defaults on first run). Cache loads + rescan run in the background.
        _index.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            RefreshRootsList();
            ApplySearch(keepSelection: true);
        });
        _index.Start(LibraryRootsStore.LoadOrSeed());
        RefreshRootsList();
        StyleChips();
        ApplySearch();
        RefreshDebt();

        DevicesList.ItemsSource = _deviceRows;
        _adb.DevicesChanged += devices => Dispatcher.BeginInvoke(() => OnDevices(devices));
        _adb.StatusChanged += st => Dispatcher.BeginInvoke(() => AdbStatusText.Text = st);
        AdbStatusText.Text = _adb.AdbPath == null ? "adb: not found" : $"adb: {_adb.AdbPath} | starting server…";
        _adb.StartWatching(1000);
        StartUsbScan();

        StatusText.Text = "Loading index… type to search instantly.";
        Loaded += (_, _) => { SearchBox.Focus(); Keyboard.Focus(SearchBox); };
        SearchBox.PreviewKeyDown += SearchBox_PreviewKeyDown;
        Closed += (_, _) => { _usbTimer?.Stop(); _adb.Dispose(); _index.Dispose(); };
    }

    // ---------------------------------------------------------------- USB / WMI hints

    private System.Windows.Threading.DispatcherTimer? _usbTimer;
    private bool _usbScanBusy;

    private void StartUsbScan()
    {
        _usbTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _usbTimer.Tick += async (_, _) => await ScanUsbAsync();
        _usbTimer.Start();
        _ = ScanUsbAsync();
    }

    private async Task ScanUsbAsync()
    {
        if (_usbScanBusy) return;
        // Only needed while adb sees no authorized phone.
        if (_adb.LastDevices.Any(d => d.State == "device")) { UsbHintText.Visibility = Visibility.Collapsed; return; }
        _usbScanBusy = true;
        try
        {
            var findings = await Task.Run(UsbPhoneDetector.Scan);
            var adbSeesSomething = _adb.LastDevices.Count > 0;
            var driver = findings.FirstOrDefault(f => f.Kind == "driver");
            var mtp = findings.FirstOrDefault(f => f.Kind == "mtp");
            var adbIf = findings.FirstOrDefault(f => f.Kind == "adb");
            string? hint = null;
            if (driver != null)
                hint = $"Phone detected ({driver.Name}) but its USB driver is missing — install the phone maker's USB driver " +
                       "(Samsung USB Driver / Google USB Driver), then press Refresh.";
            else if (!adbSeesSomething && adbIf != null)
                hint = $"Phone detected ({adbIf.Name}) — unlock it and tap Allow, then press Refresh if it does not appear.";
            else if (!adbSeesSomething && mtp != null)
                hint = $"Phone detected ({mtp.Name}) - turn on USB debugging (install Xama for guided setup).";
            UsbHintText.Text = hint ?? "";
            UsbHintText.Visibility = hint == null ? Visibility.Collapsed : Visibility.Visible;
            if (hint != null) DevicesEmpty.Visibility = Visibility.Collapsed;
            else if (_deviceRows.Count == 0) DevicesEmpty.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { AdbService.Log("usb hint error: " + ex.Message); }
        finally { _usbScanBusy = false; }
    }

    private void RefreshPhones_Click(object sender, MouseButtonEventArgs e)
    {
        AdbStatusText.Text = "adb: restarting server…";
        _adb.Refresh();
        _ = ScanUsbAsync();
    }

    private void TryAddRoot(string? path, bool persist)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (_index.AddRoot(path) && persist) PersistRoots();
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
        var roots = _index.Roots.ToList();
        if (RootsList.Items.Count != roots.Count || !roots.SequenceEqual(RootsList.Items.Cast<object>().Select(o => o?.ToString() ?? "")))
            RootsList.ItemsSource = roots;
        var fixedDrives = FixedDrives();
        AllDrivesCheck.IsChecked = fixedDrives.Count > 0 &&
            fixedDrives.All(d => roots.Any(r => string.Equals(r, d, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<string> FixedDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    private void AllDrives_Click(object sender, RoutedEventArgs e)
    {
        var on = AllDrivesCheck.IsChecked == true;
        foreach (var d in FixedDrives())
        {
            if (on) _index.AddRoot(d);
            else _index.RemoveRoot(d);
        }
        PersistRoots();
        RefreshRootsList();
        StatusText.Text = on
            ? "Indexing all fixed drives in the background — results appear as they are found."
            : "Stopped indexing whole drives.";
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

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Everything-style: arrows/Enter jump from the search box into the results.
        if (e.Key is Key.Down or Key.Enter && ResultsList.Items.Count > 0)
        {
            if (ResultsList.SelectedIndex < 0) ResultsList.SelectedIndex = 0;
            ResultsList.UpdateLayout();
            if (ResultsList.ItemContainerGenerator.ContainerFromIndex(ResultsList.SelectedIndex) is ListViewItem lvi)
                lvi.Focus();
            else ResultsList.Focus();
            if (e.Key == Key.Enter) QueueSelection();
            e.Handled = true;
        }
    }

    private Func<LibraryItem, bool>? CurrentFilter() => _filter switch
    {
        FilterKind.Protected => i => i.Kind == MediaKind.Protected || i.IsEncrypted,
        FilterKind.Video => i => i.Kind == MediaKind.Video,
        FilterKind.Pdf => i => i.Kind == MediaKind.Pdf,
        FilterKind.Image => i => i.Kind == MediaKind.Image,
        FilterKind.Exe => i => i.Kind == MediaKind.Exe,
        _ => null,
    };

    /// <summary>Runs the search on a background thread; a newer keystroke cancels the older one.</summary>
    private async void ApplySearch(bool keepSelection = false)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var q = SearchBox.Text;
        var filter = CurrentFilter();
        var sort = _sort;
        var desc = _sortDesc;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<LibraryItem> results;
        try
        {
            results = await Task.Run(() => _index.Search(q, filter, sort, desc, cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { StatusText.Text = $"Search error: {ex.Message}"; return; }
        if (cts.IsCancellationRequested || !ReferenceEquals(cts, _searchCts)) return;
        sw.Stop();

        HashSet<string>? keep = null;
        if (keepSelection && ResultsList.SelectedItems.Count > 0)
            keep = ResultsList.SelectedItems.Cast<LibraryItem>().Select(i => i.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        ResultsList.ItemsSource = results;
        if (keep != null && keep.Count <= 2000)
        {
            foreach (var it in results)
                if (keep.Contains(it.FilePath)) ResultsList.SelectedItems.Add(it);
        }
        LibraryEmpty.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        IndexCountText.Text = $"{_index.Count:N0} indexed · {results.Count:N0} shown · {sw.ElapsedMilliseconds} ms"
                              + (_index.IsScanning ? " · scanning…" : "");
        if (!_sending && StatusText.Text.StartsWith("Loading index"))
            StatusText.Text = _index.IsScanning ? "Loading index… type to search instantly." : $"Indexed {_index.Count:N0} files.";
    }

    private void ResultsHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader h || h.Tag is not string tag) return;
        if (!Enum.TryParse<SortColumn>(tag, out var col)) return;
        if (_sort == col) _sortDesc = !_sortDesc;
        else { _sort = col; _sortDesc = col is SortColumn.Size or SortColumn.Modified; }
        foreach (var c in ((GridView)ResultsList.View).Columns)
            if (c.Header is GridViewColumnHeader ch && ch.Tag is string t)
                ch.Content = HeaderText(t) + (t == tag ? (_sortDesc ? "  ▼" : "  ▲") : "");
        ApplySearch(keepSelection: true);
    }

    private static string HeaderText(string tag) => tag switch
    {
        "Modified" => "Date modified",
        _ => tag,
    };

    // ---------------------------------------------------------------- results actions

    private List<LibraryItem> SelectedItems() =>
        ResultsList.SelectedItems.Cast<LibraryItem>().ToList();

    private void ResultsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Key == Key.Enter && ctrl) { OpenItems(SelectedItems()); e.Handled = true; }
        else if (e.Key == Key.Enter) { QueueSelection(); e.Handled = true; }
        else if (e.Key == Key.C && ctrl && shift) { CopyPaths(); e.Handled = true; }
        else if (e.Key == Key.A && ctrl) { ResultsList.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape || (e.Key == Key.F && ctrl)) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (!ctrl && e.Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or Key.Space or Key.Back)
        {
            // Typing anywhere goes back to the search box, like Everything.
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is LibraryItem it) OpenItems(new List<LibraryItem> { it });
    }

    private void OpenItems(List<LibraryItem> items)
    {
        foreach (var it in items.Take(10))
        {
            try
            {
                if (it.IsEncrypted)
                {
                    // Protected packages are not playable on the PC; show them in Explorer instead.
                    OpenFolder(it);
                    continue;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(it.FilePath) { UseShellExecute = true });
            }
            catch (Exception ex) { StatusText.Text = $"Could not open {it.Name}: {ex.Message}"; }
        }
    }

    private static void OpenFolder(LibraryItem it)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{it.FilePath}\"")
        { UseShellExecute = true });
    }

    private void CopyPaths()
    {
        var items = SelectedItems();
        if (items.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, items.Select(i => i.FilePath)));
            StatusText.Text = $"Copied {items.Count} path(s).";
        }
        catch (Exception ex) { StatusText.Text = $"Copy failed: {ex.Message}"; }
    }

    private void QueueSelection()
    {
        var added = 0;
        foreach (var it in SelectedItems())
        {
            if (_queue.Any(q => string.Equals(q.FilePath, it.FilePath, StringComparison.OrdinalIgnoreCase))) continue;
            _queue.Add(it);
            added++;
        }
        UpdateQueueText();
        StatusText.Text = added > 0 ? $"Added {added} file(s) to the send queue." : "Already queued.";
    }

    private void UpdateQueueText()
    {
        if (_queue.Count == 0) { QueueText.Text = "Queue empty · Enter adds selection"; return; }
        var prot = _queue.Count(q => q.IsEncrypted);
        var bytes = _queue.Sum(q => q.SizeBytes);
        QueueText.Text = $"Queue: {_queue.Count} file(s) · {new LibraryItem { SizeBytes = bytes }.SizeLabel}"
                         + (prot > 0 ? $" · {prot} protected" : "");
    }

    private void ClearQueue_Click(object sender, MouseButtonEventArgs e)
    {
        _queue.Clear();
        UpdateQueueText();
    }

    private void MenuOpen_Click(object sender, RoutedEventArgs e) => OpenItems(SelectedItems());
    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        foreach (var it in SelectedItems().Take(5))
        {
            try { OpenFolder(it); } catch (Exception ex) { StatusText.Text = $"Could not open folder: {ex.Message}"; }
        }
    }
    private void MenuCopyPath_Click(object sender, RoutedEventArgs e) => CopyPaths();
    private void MenuQueue_Click(object sender, RoutedEventArgs e) => QueueSelection();
    private async void MenuSend_Click(object sender, RoutedEventArgs e) => await SendAsync(SelectedItems());

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

    private void ChipAll_Click(object sender, MouseButtonEventArgs e)
        => SetFilter(FilterKind.All);
    private void ChipVideo_Click(object sender, MouseButtonEventArgs e)
        => SetFilter(FilterKind.Video);
    private void ChipPdf_Click(object sender, MouseButtonEventArgs e)
        => SetFilter(FilterKind.Pdf);
    private void ChipImage_Click(object sender, MouseButtonEventArgs e)
        => SetFilter(FilterKind.Image);
    private void ChipExe_Click(object sender, MouseButtonEventArgs e)
        => SetFilter(FilterKind.Exe);
    private void ChipProtected_Click(object sender, MouseButtonEventArgs e)
        => SetFilter(FilterKind.Protected);

    private void OnDevices(List<ConnectedDevice> devices)
    {
        var seen = new HashSet<string>();
        foreach (var d in devices)
        {
            seen.Add(d.Serial);
            var row = _deviceRows.FirstOrDefault(r => r.Serial == d.Serial);
            if (row == null)
            {
                row = new DeviceRow(d.Serial) { Model = d.Model, State = d.State };
                row.IsChecked = row.IsReady; // new ready phones are ticked by default
                _deviceRows.Add(row);
            }
            else
            {
                var wasReady = row.IsReady;
                row.Model = d.Model;
                row.State = d.State;
                if (!wasReady && row.IsReady && !row.Busy) row.IsChecked = true; // flipped to ready after Allow
            }
            if (!row.IsReady && !row.Busy) row.IsChecked = false;
        }
        for (var i = _deviceRows.Count - 1; i >= 0; i--)
        {
            var row = _deviceRows[i];
            if (seen.Contains(row.Serial)) continue;
            if (row.Busy) { row.State = "disconnected"; continue; } // keep visible while its send winds down
            _deviceRows.RemoveAt(i);
        }

        DevicesEmpty.Visibility = _deviceRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var ready = _deviceRows.Count(r => r.IsReady);
        var unauthorized = _deviceRows.Count(r => r.State == "unauthorized");
        if (unauthorized > 0)
        {
            DeviceBanner.Visibility = Visibility.Visible;
            DeviceBanner.Text = "Phone detected — unlock the screen and tap Allow on the USB debugging prompt. X Seller will connect automatically.";
        }
        else if (ready > 0)
        {
            DeviceBanner.Visibility = Visibility.Visible;
            DeviceBanner.Text = $"{ready} phone(s) ready. Select files (Enter adds to queue), tick phones, press Send.";
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
        var items = _queue.Count > 0 ? _queue.ToList() : SelectedItems();
        var fromQueue = _queue.Count > 0;
        if (await SendAsync(items) && fromQueue)
        {
            _queue.Clear();
            UpdateQueueText();
        }
    }

    /// <summary>Send files to every ticked ready phone, with per-phone/per-file progress.</summary>
    private async Task<bool> SendAsync(List<LibraryItem> items)
    {
        if (_sending) { StatusText.Text = "A send is already running."; return false; }
        if (items.Count == 0)
        {
            StatusText.Text = "Select files in the list (or queue them with Enter) first.";
            return false;
        }
        var rows = _deviceRows.Where(r => r.IsChecked && r.IsReady).ToList();
        if (rows.Count == 0)
        {
            StatusText.Text = _deviceRows.Any(r => r.State == "unauthorized")
                ? "Phone not authorized yet — unlock it and tap Allow."
                : "Tick at least one ready phone. If none appear, enable USB debugging once and tap Allow.";
            return false;
        }
        var devices = rows.Select(r => new ConnectedDevice(r.Serial, r.Model, "device")).ToList();
        var bySerial = rows.ToDictionary(r => r.Serial);

        _sending = true;
        SendButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressLabel.Text = $"Sending {items.Count} file(s) to {devices.Count} phone(s)…";
        StatusText.Text = ProgressLabel.Text;
        foreach (var r in rows) { r.Busy = true; r.Progress = 0; r.ProgressText = "Queued"; }

        var progress = new Progress<SendProgress>(p =>
        {
            if (!bySerial.TryGetValue(p.Serial, out var row)) return;
            row.ProgressText = p.Status;
            row.Progress = p.FileCount == 0 ? 100 : 100.0 * p.FileIndex / p.FileCount;
        });
        try
        {
            var results = await _batch.SendAsync(items, devices, progress, CancellationToken.None);
            var sent = results.Sum(r => r.Sent);
            var failed = results.Sum(r => r.Failed);
            var dropped = results.Where(r => r.Disconnected).Select(r => r.Model).ToList();
            StatusText.Text = $"Sent {sent} file transfer(s) to {results.Length} phone(s)"
                              + (failed > 0 ? $" · {failed} failed" : "")
                              + (dropped.Count > 0 ? $" · disconnected: {string.Join(", ", dropped)}" : "") + ".";
            if (items.Any(i => i.IsEncrypted)) RefreshDebt();
            return failed == 0 && dropped.Count == 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Send failed: {ex.Message}";
            return false;
        }
        finally
        {
            foreach (var r in rows) { r.Busy = false; }
            _sending = false;
            ProgressPanel.Visibility = Visibility.Collapsed;
            SendButton.IsEnabled = true;
            OnDevices(_adb.LastDevices.ToList());
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
