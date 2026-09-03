using System.IO;
using System.Windows;
using XamaMaster.Models;
using XamaMaster.Services;

namespace XamaMaster;

public partial class SettlementWindow : Window
{
    private readonly SettlementService _settlement = new();
    private readonly OwnerCredential _owner = new();
    private LedgerExport? _export;

    private readonly string _invoiceDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XamaMaster", "Invoices");

    public SettlementWindow()
    {
        InitializeComponent();
    }

    private void LoadExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Ledger export (*.shammledger)|*.shammledger",
            Title = "Select the ledger export from the shop's USB drive"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            _export = _settlement.ReadExport(dlg.FileName);
            ShopLabel.Text = $"Shop: {_export.ShopName}  ·  exported {_export.ExportedAtUtc:g}";
            EntriesList.ItemsSource = _export.UnsettledEntries;
            TotalText.Text = $"Total: {_export.UnsettledEntries.Sum(x => x.TokenPrice)} tokens";
            StatusText.Text = $"Loaded {_export.UnsettledEntries.Count} unsettled sale(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void Settle_Click(object sender, RoutedEventArgs e)
    {
        if (_export == null)
        {
            StatusText.Text = "Load a ledger export first.";
            return;
        }

        var selected = EntriesList.SelectedItems.Cast<LedgerEntryDto>().ToList();
        var toSettle = selected.Count > 0 ? selected : _export.UnsettledEntries;
        if (toSettle.Count == 0)
        {
            StatusText.Text = "Nothing to settle.";
            return;
        }

        var invoiceKey = GetInvoiceKeyOrPrompt();
        if (invoiceKey == null) return; // cancelled or wrong password

        try
        {
            var (invoicePath, ackPath) = _settlement.Settle(_export, toSettle, invoiceKey, _invoiceDir);
            StatusText.Text = $"Settled {toSettle.Count} sale(s). Invoice saved locally. " +
                               $"Copy \"{Path.GetFileName(ackPath)}\" back onto the shop's USB drive.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Settlement failed: {ex.Message}";
        }
    }

    /// <summary>First run: set a new owner password. After that: prompt and
    /// verify against the stored verifier. Returns null if the user
    /// cancels or gets the password wrong.</summary>
    private byte[]? GetInvoiceKeyOrPrompt()
    {
        if (!_owner.IsSetUp)
        {
            var setup = new PasswordPromptWindow("Set an owner password (protects every invoice you generate):")
            {
                Owner = this
            };
            if (setup.ShowDialog() != true || string.IsNullOrWhiteSpace(setup.EnteredPassword))
                return null;
            _owner.SetPassword(setup.EnteredPassword);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var prompt = new PasswordPromptWindow(
                attempt == 0 ? "Enter owner password to generate this invoice:" : "Incorrect password. Try again:")
            { Owner = this };
            if (prompt.ShowDialog() != true) return null;

            var key = _owner.TryUnlock(prompt.EnteredPassword ?? "");
            if (key != null) return key;
        }

        StatusText.Text = "Too many incorrect attempts. Settlement cancelled.";
        return null;
    }
}
