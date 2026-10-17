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
    private readonly MtpService _mtp = new();
    private List<MtpPhone> _mtpPhones = new();
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
        _batch = new BatchSender(_adb, _mtp, _transfer);
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
        _index.ScanEverything = LibraryRootsStore.LoadScanEverything();
        ScanEverythingCheck.IsChecked = _index.ScanEverything;
        _index.Start(LibraryRootsStore.LoadOrSeed());
        RefreshRootsList();
        StyleChips();
        ApplySearch();
        RefreshDebt();

        DevicesList.ItemsSource = _deviceRows;
        _adb.DevicesChanged += devices => Dispatcher.BeginInvoke(() => OnDevices(devices));
        _adb.StatusChanged += st => Dispatcher.BeginInvoke(() => AdbStatusText.Text = st);
        AdbStatusText.Text = _adb.AdbPath == null ? "adb: not found" : $"adb: {_adb.AdbPath} | starting server…";
        // Primary: MTP (no USB debugging). Secondary: adb. The list never depends on adb.
        _mtp.PhonesChanged += phones => Dispatcher.BeginInvoke(() =>
        {
            foreach (var gone in _mtpPhones.Where(o => phones.All(n => n.Id != o.Id)))
                if (_indexedPhones.Remove(gone.Id))
                {
                    var label = !string.IsNullOrWhiteSpace(gone.Model) && !gone.Name.Contains(gone.Model, StringComparison.OrdinalIgnoreCase)
                        ? $"{gone.Name} ({gone.Model})" : gone.Name;
                    _index.RemoveExternal($"[{label}]");
                }
            _mtpPhones = phones;
            RebuildDeviceRows();
        });
        _mtp.Start(2000);
        _adb.StartWatching(1000);
        StartUsbScan();

        StatusText.Text = "Loading index… type to search instantly.";
        Loaded += (_, _) => { SearchBox.Focus(); Keyboard.Focus(SearchBox); };
        SearchBox.PreviewKeyDown += SearchBox_PreviewKeyDown;
        ResultsList.SelectionChanged += (_, e) =>
        {
            foreach (var o in e.RemovedItems) if (o is LibraryItem r) r.IsSelected = false;
            foreach (var o in e.AddedItems) if (o is LibraryItem a) a.IsSelected = true;
        };
        Closed += (_, _) => { _usbTimer?.Stop(); _mtp.Dispose(); _adb.Dispose(); _index.Dispose(); };
    }

    // ---------------------------------------------------------------- USB / WMI hints

    private System.Windows.Threading.DispatcherTimer? _usbTimer;
    private bool _usbScanBusy;

    private void StartUsbScan()
    {
        _usbTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _usbTimer.Tick += async (_, _) => await ScanUsbAsync();
        _usbTimer.Start();
        _ = ScanUsbAsync();
    }

    private List<ConnectedDevice> _adbDevices = new();
    private List<UsbPhoneDetector.PnpPhone> _pnpPhones = new();
    private bool _driverPromptedThisSession;

    private async Task ScanUsbAsync()
    {
        if (_usbScanBusy) return;
        _usbScanBusy = true;
        try
        {
            _pnpPhones = await Task.Run(UsbPhoneDetector.Scan);
            RebuildDeviceRows();
            var driverMissing = _pnpPhones.FirstOrDefault(p => p.State == UsbPhoneDetector.PnpPhoneState.DriverMissing);
            if (driverMissing != null && !_mtpPhones.Any(m => m.StorageReady) && !_driverPromptedThisSession && !DriverInstaller.Installed &&
                !DriverInstaller.AttemptedBefore && DriverInstaller.Bundled)
            {
                // Plug-and-play: first time a phone lacks a driver, install ours automatically (one UAC prompt).
                _driverPromptedThisSession = true;
                await InstallDriversAsync($"Installing USB driver for {driverMissing.Name} — click Yes on the Windows prompt…");
            }
        }
        catch (Exception ex) { AdbService.Log("usb scan error: " + ex.Message); }
        finally { _usbScanBusy = false; }
    }

    private async Task InstallDriversAsync(string message)
    {
        StatusText.Text = message;
        var ok = await Task.Run(DriverInstaller.InstallElevated);
        StatusText.Text = ok ? "Phone drivers installed. Re-plug the phone if it does not appear within a few seconds."
                             : DriverInstaller.Bundled ? "Driver install was cancelled or failed — click “Install phone drivers” to retry."
                                                       : "Driver files are missing from this X Seller folder — reinstall X Seller.";
        _adb.Refresh();
    }

    private async void InstallDrivers_Click(object sender, MouseButtonEventArgs e)
    {
        _driverPromptedThisSession = true;
        await InstallDriversAsync("Installing phone USB drivers — click Yes on the Windows prompt…");
    }

    private async void Diagnostics_Click(object sender, MouseButtonEventArgs e)
    {
        StatusText.Text = "Collecting diagnostics…";
        try
        {
            var text = await Task.Run(() => Diagnostics.Collect(_adb, _mtp));
            Directory.CreateDirectory(FirstRunBootstrap.DocsRoot);
            File.WriteAllText(Diagnostics.DiagPath, text);
            try { Clipboard.SetText(text); } catch { }
            StatusText.Text = $"Diagnostics copied to clipboard and saved to {Diagnostics.DiagPath} — send it to support.";
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{Diagnostics.DiagPath}\"") { UseShellExecute = true }); } catch { }
        }
        catch (Exception ex) { StatusText.Text = "Diagnostics failed: " + ex.Message; }
    }

    private void RefreshPhones_Click(object sender, MouseButtonEventArgs e)
    {
        AdbStatusText.Text = "adb: restarting server…";
        _adb.Refresh();
        _mtp.Trigger();
        _ = ScanUsbAsync();
    }

    // WM_DEVICECHANGE: react instantly to USB plug/unplug.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            if (System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle) is { } src)
                src.AddHook(WndProc);
        }
        catch (Exception ex) { AdbService.Log("WndProc hook failed: " + ex.Message); }
    }

    private DateTime _lastDevChange;
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DEVICECHANGE = 0x0219;
        if (msg == WM_DEVICECHANGE && (DateTime.UtcNow - _lastDevChange).TotalMilliseconds > 300)
        {
            _lastDevChange = DateTime.UtcNow;
            AdbService.Log($"WM_DEVICECHANGE 0x{wParam.ToInt64():X}");
            _mtp.Trigger();
            _adb.TriggerPoll();
            Dispatcher.BeginInvoke(async () =>
            {
                await ScanUsbAsync();
                await Task.Delay(2000); // composite phones enumerate their interfaces in stages
                await ScanUsbAsync();
            });
        }
        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- thumbnail grid

    private bool _thumbMode;
    private int _perRow = 5;

    private void ViewDetails_Click(object sender, MouseButtonEventArgs e) => SetThumbMode(false);
    private void ViewThumbs_Click(object sender, MouseButtonEventArgs e) => SetThumbMode(true);

    private void SetThumbMode(bool on)
    {
        _thumbMode = on;
        var menu = ResultsList.ContextMenu ?? ThumbGrid.ContextMenu;
        ResultsList.ContextMenu = null; ThumbGrid.ContextMenu = null;
        if (on) ThumbGrid.ContextMenu = menu; else ResultsList.ContextMenu = menu;
        ResultsList.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ThumbGrid.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        void Paint(Border b, bool active)
        {
            b.Background = new SolidColorBrush(active ? Color.FromRgb(0xFF, 0x6B, 0x2C) : Color.FromRgb(0x2A, 0x2A, 0x3A));
            if (b.Child is TextBlock tb) tb.Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x9C));
        }
        Paint(ViewDetailsBtn, !on);
        Paint(ViewThumbsBtn, on);
        if (on) { RebuildThumbRows(); ThumbGrid.Focus(); }
        else ResultsList.Focus();
    }

    private void RebuildThumbRows()
    {
        if (ResultsList.ItemsSource is not List<LibraryItem> results) { ThumbGrid.ItemsSource = null; return; }
        var per = Math.Max(1, _perRow);
        var rows = new List<List<LibraryItem>>((results.Count + per - 1) / per);
        for (var i = 0; i < results.Count; i += per)
            rows.Add(results.GetRange(i, Math.Min(per, results.Count - i)));
        ThumbGrid.ItemsSource = rows;
    }

    private void ThumbGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var per = Math.Max(1, (int)((ThumbGrid.ActualWidth - 24) / 176));
        if (per == _perRow) return;
        _perRow = per;
        if (_thumbMode) RebuildThumbRows();
    }

    private int _anchorIndex = -1;

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LibraryItem item) return;
        ThumbGrid.Focus();
        if (e.ClickCount == 2) { OpenItems(new List<LibraryItem> { item }); e.Handled = true; return; }
        SelectTile(item, Keyboard.Modifiers);
        e.Handled = true;
    }

    private void Tile_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryItem item && !item.IsSelected)
            SelectTile(item, ModifierKeys.None);
    }

    private void SelectTile(LibraryItem item, ModifierKeys mods)
    {
        if (ResultsList.ItemsSource is not List<LibraryItem> results) return;
        var idx = results.IndexOf(item);
        if ((mods & ModifierKeys.Shift) != 0 && _anchorIndex >= 0)
        {
            ResultsList.SelectedItems.Clear();
            var (a, b) = (Math.Min(_anchorIndex, idx), Math.Max(_anchorIndex, idx));
            for (var i = a; i <= b; i++) ResultsList.SelectedItems.Add(results[i]);
            return;
        }
        if ((mods & ModifierKeys.Control) != 0)
        {
            if (item.IsSelected) ResultsList.SelectedItems.Remove(item); else ResultsList.SelectedItems.Add(item);
        }
        else
        {
            ResultsList.SelectedItems.Clear();
            ResultsList.SelectedItems.Add(item);
        }
        _anchorIndex = idx;
    }

    private void ThumbGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Enter && ctrl) { OpenItems(SelectedItems()); e.Handled = true; }
        else if (e.Key == Key.Enter) { QueueSelection(); e.Handled = true; }
        else if (e.Key == Key.A && ctrl) { ResultsList.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.C && ctrl && (Keyboard.Modifiers & ModifierKeys.Shift) != 0) { CopyPaths(); e.Handled = true; }
        else if (e.Key == Key.Escape) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
    }

    // ---------------------------------------------------------------- scan everything / phone index

    private void ScanEverything_Click(object sender, RoutedEventArgs e)
    {
        var on = ScanEverythingCheck.IsChecked == true;
        _index.ScanEverything = on;
        if (on) foreach (var d in FixedDrives()) _index.AddRoot(d);
        LibraryRootsStore.SaveScanEverything(on, _index.Roots);
        RefreshRootsList();
        StatusText.Text = on
            ? "Scanning everything: all fixed drives, including hidden and system folders. Results appear as they are found."
            : "Back to normal scanning (hidden/system folders and C:\\Windows skipped unless you add them).";
        Task.Run(_index.FullRescan);
    }

    private readonly HashSet<string> _indexedPhones = new();

    private async void IndexPhone_Click(object sender, RoutedEventArgs e)
    {
        var rows = _deviceRows.Where(r => r.IsChecked && r.MtpId != null && _mtp.IsReady(r.MtpId)).ToList();
        if (rows.Count == 0) { StatusText.Text = "Tick a ready phone (connected in File transfer mode) to index its storage."; return; }
        foreach (var r in rows)
        {
            var id = r.MtpId!;
            var name = r.Model;
            StatusText.Text = $"Indexing {name} storage over MTP… (large phones take a minute)";
            try
            {
                var entries = await Task.Run(() => _mtp.EnumerateAll(id, 300_000, CancellationToken.None));
                var items = entries.Select(x => LibraryIndex.MakePhoneItem(id, name, x.Path, (long)x.Size,
                    x.Modified?.ToUniversalTime() ?? default)).ToList();
                _index.SetExternal($"[{name}]", items);
                _indexedPhones.Add(id);
                StatusText.Text = $"Indexed {items.Count:N0} file(s) on {name}. Search shows them as [{name}]\\… — Open / Send copies them from the phone.";
            }
            catch (Exception ex) { StatusText.Text = $"Could not index {name}: {ex.Message}"; }
        }
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

        foreach (var o in ResultsList.SelectedItems) if (o is LibraryItem old) old.IsSelected = false;
        ResultsList.ItemsSource = results;
        if (_thumbMode) RebuildThumbRows();
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
                if (it.IsOnPhone)
                {
                    var phoneItem = it;
                    StatusText.Text = $"Copying {it.Name} from phone…";
                    Task.Run(() => LocalPathFor(phoneItem)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
                    {
                        if (t.IsFaulted) { StatusText.Text = $"Could not copy from phone: {t.Exception?.GetBaseException().Message}"; return; }
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(t.Result) { UseShellExecute = true }); StatusText.Text = "Opened " + phoneItem.Name; }
                        catch (Exception ex) { StatusText.Text = $"Could not open {phoneItem.Name}: {ex.Message}"; }
                    }));
                    continue;
                }
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
        if (it.IsOnPhone) return;
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
        _adbDevices = devices;
        RebuildDeviceRows();
    }

    /// <summary>
    /// Merge adb devices and Windows-PnP phones into the phone panel. A plugged phone
    /// always gets a row: adb state when adb sees it, otherwise the PnP state
    /// (Needs USB debugging / USB driver missing / Connecting…).
    /// </summary>
    private sealed class Wanted
    {
        public string Key = "";
        public string Model = "";
        public string State = "";
        public string? MtpId;
        public string? AdbSerial;
        public string Ledger = "";
        public string Transport = "";
    }

    private static bool SameSerial(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void RebuildDeviceRows()
    {
        var wanted = new List<Wanted>();
        // 1) MTP phones (primary, no USB debugging needed).
        foreach (var m in _mtpPhones)
        {
            var name = !string.IsNullOrWhiteSpace(m.Model) && !m.Name.Contains(m.Model, StringComparison.OrdinalIgnoreCase)
                ? $"{m.Name} ({m.Model})" : m.Name;
            wanted.Add(new Wanted
            {
                Key = "mtp:" + m.Id, Model = name, MtpId = m.Id,
                State = m.StorageReady ? "device" : "locked",
                Ledger = string.IsNullOrWhiteSpace(m.Serial) ? m.Id : m.Serial,
                Transport = "MTP",
            });
        }
        // 2) adb devices: merge into the MTP row with the same USB serial, else own row.
        foreach (var d in _adbDevices)
        {
            var match = wanted.FirstOrDefault(w => w.MtpId != null &&
                SameSerial(_mtpPhones.First(m => m.Id == w.MtpId).Serial, d.Serial));
            if (match == null && _mtpPhones.Count == 1 && _adbDevices.Count == 1 && wanted.Count == 1)
                match = wanted[0]; // one phone each way: same phone (serial formats can differ)
            if (match != null)
            {
                match.AdbSerial = d.Serial;
                if (d.State == "device") { match.State = "device"; match.Transport = match.Transport == "MTP" ? "MTP + ADB" : "ADB"; }
                continue;
            }
            wanted.Add(new Wanted { Key = d.Serial, Model = d.Model, State = d.State, AdbSerial = d.Serial, Ledger = d.Serial, Transport = "ADB" });
        }
        // 3) Windows PnP phones neither MTP nor adb can talk to (charge-only, locked, driver issue).
        // Rows that no PnP phone matches by serial; serial-less / differently formatted PnP phones pair with these.
        var unmatched = wanted.Count(w => !_pnpPhones.Any(p => SameSerial(p.Serial, w.Ledger) || SameSerial(p.Serial, w.AdbSerial)));
        foreach (var p in _pnpPhones)
        {
            if (wanted.Any(w => SameSerial(w.Ledger, p.Serial) || SameSerial(w.AdbSerial, p.Serial))) continue;
            if (unmatched > 0) { unmatched--; continue; }
            var state = p.State switch
            {
                UsbPhoneDetector.PnpPhoneState.DriverMissing => "driver-missing",
                UsbPhoneDetector.PnpPhoneState.AdbInterface => "adb-interface",
                _ => "charge-only",
            };
            wanted.Add(new Wanted { Key = p.Key, Model = p.Name, State = state, Ledger = p.Serial ?? p.Key, Transport = "USB" });
        }

        var seen = new HashSet<string>();
        foreach (var w in wanted)
        {
            if (!seen.Add(w.Key)) continue;
            var row = _deviceRows.FirstOrDefault(r => r.Serial == w.Key);
            if (row == null)
            {
                row = new DeviceRow(w.Key) { Model = w.Model, State = w.State };
                row.IsChecked = row.IsReady; // new ready phones are ticked by default
                _deviceRows.Add(row);
            }
            else
            {
                var wasReady = row.IsReady;
                row.Model = w.Model;
                row.State = w.State;
                if (!wasReady && row.IsReady && !row.Busy) row.IsChecked = true; // flipped to ready (unlocked / allowed)
            }
            row.MtpId = w.MtpId;
            row.AdbSerial = w.AdbSerial;
            row.LedgerSerial = w.Ledger;
            row.Transport = w.Transport;
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
        var locked = _deviceRows.Count(r => r.State is "locked" or "charge-only" or "needs-debugging");
        var unauthorized = _deviceRows.Count(r => r.State is "unauthorized" or "adb-interface" or "no permissions");
        var driver = _deviceRows.Count(r => r.State == "driver-missing");
        UsbHintText.Visibility = Visibility.Collapsed;
        DeviceBanner.Visibility = Visibility.Visible;
        if (locked > 0)
            DeviceBanner.Text = "Phone detected — unlock it, pull down the notification bar, tap “USB” and choose “File transfer”. Tap Allow if asked.";
        else if (ready > 0)
            DeviceBanner.Text = $"{ready} phone(s) ready. Select files (Enter adds to queue), tick phones, press Send.";
        else if (unauthorized > 0)
            DeviceBanner.Text = "Phone detected — unlock the screen and tap Allow. X Seller will connect automatically.";
        else if (driver > 0)
            DeviceBanner.Text = "Phone detected but Windows has no driver for it — click “Install phone drivers”, or switch the phone to File transfer.";
        else
            DeviceBanner.Visibility = Visibility.Collapsed;
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
            StatusText.Text = _deviceRows.Any(r => r.State is "locked" or "charge-only" or "unauthorized")
                ? "Phone not ready — unlock it and choose File transfer (tap Allow if asked)."
                : "Tick at least one ready phone. Plug the phone in, unlock it and choose File transfer.";
            return false;
        }
        var devices = rows.Select(r => new SendTarget(r.Serial, r.Model, r.MtpId, r.AdbSerial,
            string.IsNullOrWhiteSpace(r.LedgerSerial) ? r.Serial : r.LedgerSerial)).ToList();
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
            row.Progress = p.FileCount == 0 ? 100 : 100.0 * Math.Min(p.FileCount, p.FileIndex + p.FileFraction) / p.FileCount;
        });
        try
        {
            var results = await _batch.SendAsync(items, devices, progress, CancellationToken.None, LocalPathFor);
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
            RebuildDeviceRows();
        }
    }

    /// <summary>Phone-indexed rows are downloaded to a local cache before sending/opening.</summary>
    private string LocalPathFor(LibraryItem item)
    {
        if (!item.IsOnPhone) return item.FilePath;
        var local = Path.Combine(Path.GetTempPath(), "XSeller-phone", Math.Abs(item.FilePath.GetHashCode()).ToString("X"), item.Name);
        if (!File.Exists(local) || new FileInfo(local).Length != item.SizeBytes)
            _mtp.Download(item.MtpDeviceId!, item.MtpPath!, local);
        return local;
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
