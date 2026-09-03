using System.Text;
using System.Text.Json;
using XamaMaster.Models;

namespace XamaMaster.Services;

public class SettlementService
{
    /// <summary>Reads a .shammledger file the agent brought back from a
    /// shop visit. Only Xama Master can open this - it's encrypted with
    /// SettlementKey, the same shared secret X Seller used to write it.</summary>
    public LedgerExport ReadExport(string exportFilePath)
    {
        var encrypted = File.ReadAllBytes(exportFilePath);
        byte[] plaintext;
        try
        {
            plaintext = GcmBlob.Decrypt(SettlementKey.Current, encrypted);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException(
                "This doesn't look like a genuine X Seller ledger export (wrong file, or it's been altered).");
        }
        return JsonSerializer.Deserialize<LedgerExport>(Encoding.UTF8.GetString(plaintext))
               ?? throw new InvalidOperationException("Ledger export file is empty or invalid.");
    }

    /// <summary>
    /// Call after the agent has collected payment for the entries the
    /// owner chose to settle. Produces two files:
    ///   - the encrypted CSV invoice (owner-password-protected, for your records)
    ///   - the .shammack file to copy back onto the USB drive for the shop's X Seller to import
    /// </summary>
    public (string InvoicePath, string AckPath) Settle(
        LedgerExport export, List<LedgerEntryDto> settledEntries, byte[] invoiceKey, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

        // 1. CSV invoice, encrypted with the owner's own derived key -
        //    nobody without the Xama Master password can read this, ever,
        //    including someone who has full access to this machine's files.
        var csv = BuildCsv(export.ShopName, settledEntries);
        var encryptedCsv = GcmBlob.Encrypt(invoiceKey, Encoding.UTF8.GetBytes(csv));
        var invoicePath = Path.Combine(outputDir, $"invoice-{export.ShopName}-{stamp}.shamminvoice");
        File.WriteAllBytes(invoicePath, encryptedCsv);

        // 2. Ack file for the shop - encrypted with SettlementKey (not the
        //    owner password) since X Seller, not the owner, needs to open it.
        var ack = new SettlementAck
        {
            ShopName = export.ShopName,
            SettledAtUtc = DateTime.UtcNow,
            SettledEntryIds = settledEntries.Select(e => e.Id).ToList(),
            TotalTokensSettled = settledEntries.Sum(e => e.TokenPrice),
        };
        var ackJson = JsonSerializer.Serialize(ack);
        var encryptedAck = GcmBlob.Encrypt(SettlementKey.Current, Encoding.UTF8.GetBytes(ackJson));
        var ackPath = Path.Combine(outputDir, $"{export.ShopName}-{stamp}.shammack");
        File.WriteAllBytes(ackPath, encryptedAck);

        return (invoicePath, ackPath);
    }

    /// <summary>Decrypts a .shamminvoice CSV using the owner's key - the
    /// only way this file is ever readable again.</summary>
    public string DecryptInvoice(string invoicePath, byte[] invoiceKey)
    {
        var encrypted = File.ReadAllBytes(invoicePath);
        return Encoding.UTF8.GetString(GcmBlob.Decrypt(invoiceKey, encrypted));
    }

    private static string BuildCsv(string shopName, List<LedgerEntryDto> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Shop,VideoTitle,DeviceModel,DeviceSerial,TokenPrice,SentAtUtc");
        foreach (var e in entries)
        {
            sb.AppendLine($"{Csv(shopName)},{Csv(e.VideoTitle)},{Csv(e.DeviceModel)},{Csv(e.DeviceSerial)},{e.TokenPrice},{e.SentAtUtc:O}");
        }
        sb.AppendLine();
        sb.AppendLine($",,,Total,{entries.Sum(e => e.TokenPrice)},");
        return sb.ToString();
    }

    private static string Csv(string field) => field.Contains(',') ? $"\"{field.Replace("\"", "\"\"")}\"" : field;
}
