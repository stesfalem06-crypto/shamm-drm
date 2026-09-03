using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using XamaMaster.Models;
using XamaMaster.Services;

namespace XamaMaster;

public partial class MainWindow : Window
{
    private readonly VideoEncryptor _encryptor = new();
    private readonly ObservableCollection<VideoMetadata> _library = new();
    private string? _selectedFile;

    // All encrypted output lives here. In a later phase this becomes
    // configurable and syncs into X Seller's local library folder.
    private readonly string _outputDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XamaMaster", "Library");

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(_outputDir);
        LibraryList.ItemsSource = _library;
        LoadExistingLibrary();
    }

    private void LoadExistingLibrary()
    {
        foreach (var metaFile in Directory.GetFiles(_outputDir, "*.shammmeta"))
        {
            try
            {
                var json = File.ReadAllText(metaFile);
                var meta = System.Text.Json.JsonSerializer.Deserialize<VideoMetadata>(json);
                if (meta != null) _library.Add(meta);
            }
            catch { /* skip unreadable/corrupt metadata rather than crash the app */ }
        }
        StatusText.Text = $"Loaded {_library.Count} video(s) from library.";
    }

    private void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Video files (*.mp4;*.mkv;*.mov;*.avi)|*.mp4;*.mkv;*.mov;*.avi|All files|*.*",
            Title = "Select a video to encrypt"
        };
        if (dlg.ShowDialog() == true)
        {
            _selectedFile = dlg.FileName;
            if (string.IsNullOrWhiteSpace(TitleBox.Text))
                TitleBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
            StatusText.Text = $"Selected: {Path.GetFileName(dlg.FileName)}";
        }
    }

    private void Encrypt_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFile == null)
        {
            StatusText.Text = "Choose a video file first.";
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

        try
        {
            StatusText.Text = "Encrypting... this can take a while for large files.";
            IsEnabled = false;

            var meta = _encryptor.EncryptVideo(_selectedFile, title, price, _outputDir, VerticalCheck.IsChecked == true);

            _library.Add(meta);
            StatusText.Text = $"Encrypted \"{meta.Title}\" — {meta.TokenPrice} token(s). Ready to distribute to shops.";
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
            IsEnabled = true;
        }
    }

    private void OpenSettlement_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettlementWindow { Owner = this };
        window.ShowDialog();
    }
}
