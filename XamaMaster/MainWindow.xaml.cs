using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using XamaMaster.Models;
using XamaMaster.Services;
// FirstRunBootstrap creates Library on startup

namespace XamaMaster;

public partial class MainWindow : Window
{
    private readonly VideoEncryptor _encryptor = new();
    private readonly ObservableCollection<VideoMetadata> _library = new();
    private string? _selectedFile;

    private readonly string _outputDir = XamaMaster.Services.FirstRunBootstrap.LibraryDir;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(_outputDir);
        LibraryList.ItemsSource = _library;
        _library.CollectionChanged += (_, _) => UpdateEmptyState();
        LoadExistingLibrary();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        LibraryEmpty.Visibility = _library.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadExistingLibrary()
    {
        _library.Clear();
        if (!Directory.Exists(_outputDir)) return;
        foreach (var metaFile in Directory.GetFiles(_outputDir, "*.shammmeta"))
        {
            try
            {
                var json = File.ReadAllText(metaFile);
                var meta = System.Text.Json.JsonSerializer.Deserialize<VideoMetadata>(json);
                if (meta != null) _library.Add(meta);
            }
            catch { /* skip corrupt */ }
        }
        StatusText.Text = _library.Count == 0
            ? "Ready. Encrypt your first video to start the catalog."
            : $"Loaded {_library.Count} item(s) from library.";
        UpdateEmptyState();
    }

    private void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Videos and PDFs (*.mp4;*.mkv;*.mov;*.avi;*.webm;*.pdf)|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.pdf|" +
                     "Video files (*.mp4;*.mkv;*.mov;*.avi;*.webm)|*.mp4;*.mkv;*.mov;*.avi;*.webm|" +
                     "PDF documents (*.pdf)|*.pdf|All files|*.*",
            Title = "Select a video or PDF to encrypt"
        };
        if (dlg.ShowDialog() == true)
        {
            _selectedFile = dlg.FileName;
            if (string.IsNullOrWhiteSpace(TitleBox.Text))
                TitleBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
            StatusText.Text = $"Selected: {Path.GetFileName(dlg.FileName)}";
        }
    }

    private async void Encrypt_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFile == null)
        {
            StatusText.Text = "Choose a video or PDF file first.";
            return;
        }
        if (!int.TryParse(PriceBox.Text, out var price) || price < 0)
        {
            StatusText.Text = "Token price must be a whole number (0 or more).";
            return;
        }
        var title = string.IsNullOrWhiteSpace(TitleBox.Text)
            ? Path.GetFileNameWithoutExtension(_selectedFile)
            : TitleBox.Text.Trim();
        var isVertical = VerticalCheck.IsChecked == true;
        var source = _selectedFile;

        try
        {
            EncryptButton.IsEnabled = false;
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressLabel.Text = $"Encrypting \"{title}\"… this can take a while for large files.";
            StatusText.Text = ProgressLabel.Text;

            var isPdf = VideoEncryptor.IsPdfPath(source);
            var meta = await Task.Run(() => isPdf
                ? _encryptor.EncryptPdf(source, title, price, _outputDir)
                : _encryptor.EncryptVideo(source, title, price, _outputDir, isVertical));

            _library.Add(meta);
            UpdateEmptyState();
            StatusText.Text = $"Encrypted \"{meta.Title}\" — {meta.TokenPrice} token(s) · {meta.FormatLabel}. Ready to distribute to shops.";
            _selectedFile = null;
            TitleBox.Text = "";
            PriceBox.Text = "1";
            VerticalCheck.IsChecked = false;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Encryption failed: {ex.Message}";
        }
        finally
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            EncryptButton.IsEnabled = true;
        }
    }

    private void OpenPlatformTools_Click(object sender, RoutedEventArgs e)
    {
        try { Views.PlatformToolsWindow.Show(this); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Xama Master"); }
    }

    private void OpenSettlement_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettlementWindow { Owner = this };
        window.ShowDialog();
    }
}
