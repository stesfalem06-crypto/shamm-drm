using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XamaMaster.Services;

public sealed class AgentInfo
{
    public string AgentId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public DateTime IssuedUtc { get; set; }
}

public sealed class VerifiedClosing
{
    public string ShopCode { get; set; } = "";
    public string ShopName { get; set; } = "";
    public int Number { get; set; }
    public decimal Amount { get; set; }
    public string DateYmd { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string ReceiptCode { get; set; } = "";
    public DateTime VerifiedUtc { get; set; }
    public string Source { get; set; } = "code";   // code | file
    public string RecordHash { get; set; } = "";
    public string PrevHash { get; set; } = "";
    public decimal PlatformDue { get; set; }
    public int SalesCount { get; set; }
    public string Note { get; set; } = "";
    public string AmountLabel => Amount.ToString("#,0.00", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Platform side of the offline closing scheme.
///   platform secret S: 32 random bytes, generated once, DPAPI-protected on this PC (back it up!).
///   agent key K_agent = HMAC-SHA256(S, "agent:" + agentId) — never stored in X Seller in clear;
///   the .shammagent file carries K_agent encrypted under the agent's passphrase.
///   receipt code = Crockford-base32(HMAC(K_agent, "XSC1|shopCode|n|amount|yyyyMMdd|agentId")[..8])[..12]
/// MUST match XSeller/Services/Closings.cs.
/// </summary>
public static class PlatformAgents
{
    public static string Dir => Path.Combine(FirstRunBootstrap.DocsRoot, "platform");
    private static string SecretPath => Path.Combine(Dir, "platform-secret.dpapi");
    private static string AgentsPath => Path.Combine(Dir, "agents.json");
    private static string LogPath => Path.Combine(Dir, "verified-closings.json");
    public const int AgentIterations = 600_000;

    private static byte[]? _secret;

    public static bool HasSecret => File.Exists(SecretPath);

    public static byte[] Secret()
    {
        if (_secret != null) return _secret;
        Directory.CreateDirectory(Dir);
        if (File.Exists(SecretPath))
            _secret = ProtectedData.Unprotect(File.ReadAllBytes(SecretPath), Encoding.UTF8.GetBytes("shamm-platform"), DataProtectionScope.CurrentUser);
        else
        {
            _secret = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(SecretPath, ProtectedData.Protect(_secret, Encoding.UTF8.GetBytes("shamm-platform"), DataProtectionScope.CurrentUser));
        }
        return _secret;
    }

    public static byte[] AgentKey(string agentId) => HMACSHA256.HashData(Secret(), Encoding.UTF8.GetBytes("agent:" + agentId));

    // ---------------- agents
    public static List<AgentInfo> Agents()
    {
        try { return File.Exists(AgentsPath) ? JsonSerializer.Deserialize<List<AgentInfo>>(File.ReadAllText(AgentsPath)) ?? new() : new(); }
        catch { return new(); }
    }

    private static void SaveAgents(List<AgentInfo> list)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(AgentsPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Create an agent and return the .shammagent credential JSON (give it to the agent on USB).</summary>
    public static string CreateAgentCredential(string name, string passphrase, string? existingId = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter the agent's name.");
        if ((passphrase ?? "").Length < 8) throw new ArgumentException("Agent passphrase must be at least 8 characters (the shop PC must not be able to guess it).");
        var id = existingId ?? "AG" + Closing.Base32(RandomNumberGenerator.GetBytes(5))[..6];
        var key = AgentKey(id);
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = Rfc2898DeriveBytes.Pbkdf2(passphrase!, salt, AgentIterations, HashAlgorithmName.SHA256, 32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[key.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(kek, 16)) gcm.Encrypt(nonce, key, ct, tag, Encoding.UTF8.GetBytes(id));
        var issued = DateTime.UtcNow;
        var cred = new JsonObject
        {
            ["Format"] = "shamm-agent", ["Version"] = 1, ["AgentId"] = id, ["AgentName"] = name.Trim(), ["IssuedUtc"] = issued,
            ["SaltB64"] = Convert.ToBase64String(salt), ["Iterations"] = AgentIterations, ["NonceB64"] = Convert.ToBase64String(nonce),
            ["EncKeyB64"] = Convert.ToBase64String(ct.Concat(tag).ToArray()),
            ["KeyCheck"] = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("check")))[..16],
        };
        var list = Agents();
        list.RemoveAll(a => a.AgentId == id);
        list.Add(new AgentInfo { AgentId = id, AgentName = name.Trim(), IssuedUtc = issued });
        SaveAgents(list);
        return cred.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // ---------------- verified log
    public static List<VerifiedClosing> Log()
    {
        try { return File.Exists(LogPath) ? JsonSerializer.Deserialize<List<VerifiedClosing>>(File.ReadAllText(LogPath)) ?? new() : new(); }
        catch { return new(); }
    }

    private static void SaveLog(List<VerifiedClosing> list)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(LogPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Normalize(string code)
    {
        var sb = new StringBuilder();
        foreach (var ch in (code ?? "").ToUpperInvariant())
        {
            if (ch is '-' or ' ') continue;
            sb.Append(ch switch { 'O' => '0', 'I' or 'L' => '1', _ => ch });
        }
        return sb.ToString();
    }

    public sealed record VerifyResult(bool Valid, string Message, VerifiedClosing? Entry);

    /// <summary>Verify a receipt code typed (or scanned) by the owner. Tries every agent (or the one named) and ±1 day.</summary>
    public static VerifyResult VerifyCode(string shopCode, int number, decimal amount, DateTime date, string code, string? agentId = null)
    {
        shopCode = (shopCode ?? "").Trim().ToUpperInvariant();
        var want = Normalize(code);
        if (want.Length != 12) return new(false, "Receipt code must be 12 characters (XXXX-XXXX-XXXX).", null);
        var agents = Agents();
        if (agentId != null && agents.All(a => a.AgentId != agentId)) agents.Add(new AgentInfo { AgentId = agentId, AgentName = agentId });
        foreach (var a in agents.Where(a => agentId == null || a.AgentId == agentId))
        {
            var key = AgentKey(a.AgentId);
            foreach (var d in new[] { date, date.AddDays(-1), date.AddDays(1) })
            {
                var c = Normalize(Closing.ReceiptCode(key, shopCode, number, amount, d, a.AgentId));
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(c), Encoding.ASCII.GetBytes(want))) continue;
                var entry = new VerifiedClosing
                {
                    ShopCode = shopCode, Number = number, Amount = Math.Round(amount, 2), DateYmd = d.ToString("yyyy-MM-dd"),
                    AgentId = a.AgentId, AgentName = a.AgentName, ReceiptCode = Closing.ReceiptCode(key, shopCode, number, amount, d, a.AgentId),
                    VerifiedUtc = DateTime.UtcNow, Source = "code",
                };
                return new(true, Record(entry), entry);
            }
        }
        return new(false, "INVALID — code does not match this shop code / closing # / amount / date for any of your agents. " +
                          "Check the numbers; if they're right, the receipt was not produced by your agent.", null);
    }

    /// <summary>Import the agent's signed .shammclosing file (full detail).</summary>
    public static VerifyResult ImportClosingFile(string json)
    {
        var f = JsonNode.Parse(json) ?? throw new InvalidDataException("Not a closing file.");
        if ((string?)f["Format"] != "shamm-closing") throw new InvalidDataException("Not a signed closing file (.shammclosing).");
        var agentId = (string?)f["AgentId"] ?? "";
        var payload = Convert.FromBase64String((string?)f["Payload"] ?? "");
        var mac = Convert.FromHexString((string?)f["HmacSha256"] ?? "");
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(AgentKey(agentId), payload), mac))
            return new(false, "INVALID SIGNATURE — the file was edited or was not produced with one of your agents.", null);
        var r = JsonNode.Parse(payload)!;
        decimal Dec(string k) => r[k]?.GetValue<decimal>() ?? 0m;
        var closed = r["ClosedAtUtc"]!.GetValue<DateTime>().ToUniversalTime();
        var entry = new VerifiedClosing
        {
            ShopCode = ((string?)r["ShopCode"] ?? "").ToUpperInvariant(), ShopName = (string?)r["ShopName"] ?? "",
            Number = r["Number"]!.GetValue<int>(), Amount = Dec("AmountReceived"), DateYmd = closed.ToString("yyyy-MM-dd"),
            AgentId = agentId, AgentName = (string?)r["AgentName"] ?? agentId, ReceiptCode = (string?)r["ReceiptCode"] ?? "",
            VerifiedUtc = DateTime.UtcNow, Source = "file", RecordHash = (string?)r["RecordHash"] ?? "", PrevHash = (string?)r["PrevHash"] ?? "",
            PlatformDue = Dec("PlatformDue"), SalesCount = r["SalesCount"]?.GetValue<int>() ?? 0,
        };
        var expect = Closing.ReceiptCode(AgentKey(agentId), entry.ShopCode, entry.Number, entry.Amount, closed, agentId);
        if (Normalize(expect) != Normalize(entry.ReceiptCode)) return new(false, "INVALID — receipt code inside the file doesn't match its contents.", null);
        return new(true, Record(entry), entry);
    }

    private static string Record(VerifiedClosing e)
    {
        var log = Log();
        var notes = new List<string>();
        var same = log.FirstOrDefault(x => x.ShopCode == e.ShopCode && x.Number == e.Number);
        if (same != null)
        {
            if (same.Amount != e.Amount) notes.Add($"WARNING: closing #{e.Number} was already verified with a DIFFERENT amount ({same.AmountLabel}).");
            else notes.Add($"Closing #{e.Number} was already verified before.");
            if (e.Source == "file" && string.IsNullOrEmpty(same.RecordHash)) { log.Remove(same); log.Add(e); SaveLog(log); }
        }
        else { log.Add(e); SaveLog(log); }
        // Chain continuity with the previous closing's file (when both were imported as files).
        var prev = log.FirstOrDefault(x => x.ShopCode == e.ShopCode && x.Number == e.Number - 1 && !string.IsNullOrEmpty(x.RecordHash));
        if (!string.IsNullOrEmpty(e.PrevHash) && prev != null && prev.RecordHash != e.PrevHash)
            notes.Add($"WARNING: closing #{e.Number} does not chain to closing #{prev.Number} — the shop's closing history was rewritten.");
        if (e.Number == 1 && !string.IsNullOrEmpty(e.PrevHash) && e.PrevHash != "GENESIS")
            notes.Add("WARNING: closing #1 does not start a fresh chain.");
        var gaps = Gaps(e.ShopCode);
        if (gaps.Count > 0) notes.Add($"Missing closings for shop {e.ShopCode}: #{string.Join(", #", gaps)} (never verified).");
        return $"VALID ✓  Shop {e.ShopCode} · closing #{e.Number} · amount {e.AmountLabel} · {e.DateYmd} · agent {e.AgentName}" +
               (notes.Count > 0 ? "\n" + string.Join("\n", notes) : "");
    }

    public static List<int> Gaps(string shopCode)
    {
        var nums = Log().Where(x => x.ShopCode == shopCode).Select(x => x.Number).Distinct().OrderBy(n => n).ToList();
        if (nums.Count == 0) return new();
        return Enumerable.Range(1, nums[^1]).Except(nums).ToList();
    }

    // ---------------- secret backup (needed to use another PC / reinstall)
    public static byte[] ExportSecret(string passphrase)
    {
        if ((passphrase ?? "").Length < 10) throw new ArgumentException("Backup passphrase must be at least 10 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = Rfc2898DeriveBytes.Pbkdf2(passphrase!, salt, AgentIterations, HashAlgorithmName.SHA256, 32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[32]; var tag = new byte[16];
        using (var gcm = new AesGcm(kek, 16)) gcm.Encrypt(nonce, Secret(), ct, tag);
        var o = new JsonObject
        {
            ["Format"] = "shamm-platform-secret", ["Salt"] = Convert.ToBase64String(salt), ["Nonce"] = Convert.ToBase64String(nonce),
            ["Data"] = Convert.ToBase64String(ct.Concat(tag).ToArray()), ["Agents"] = JsonSerializer.SerializeToNode(Agents()),
        };
        return Encoding.UTF8.GetBytes(o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void ImportSecret(string json, string passphrase)
    {
        var o = JsonNode.Parse(json)!;
        if ((string?)o["Format"] != "shamm-platform-secret") throw new InvalidDataException("Not a platform secret backup.");
        var kek = Rfc2898DeriveBytes.Pbkdf2(passphrase ?? "", Convert.FromBase64String((string)o["Salt"]!), AgentIterations, HashAlgorithmName.SHA256, 32);
        var blob = Convert.FromBase64String((string)o["Data"]!);
        var secret = new byte[32];
        try { using var gcm = new AesGcm(kek, 16); gcm.Decrypt(Convert.FromBase64String((string)o["Nonce"]!), blob[..32], blob[32..], secret); }
        catch (CryptographicException) { throw new UnauthorizedAccessException("Wrong backup passphrase."); }
        Directory.CreateDirectory(Dir);
        File.WriteAllBytes(SecretPath, ProtectedData.Protect(secret, Encoding.UTF8.GetBytes("shamm-platform"), DataProtectionScope.CurrentUser));
        _secret = secret;
        var agents = o["Agents"]?.Deserialize<List<AgentInfo>>();
        if (agents != null)
        {
            var list = Agents();
            foreach (var a in agents) if (list.All(x => x.AgentId != a.AgentId)) list.Add(a);
            SaveAgents(list);
        }
    }
}

/// <summary>Receipt code primitives — byte-for-byte the same as XSeller/Services/Closings.cs.</summary>
public static class Closing
{
    public static string Money(decimal d) => d.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    public static string ReceiptCode(byte[] key, string shopCode, int number, decimal amount, DateTime closedUtc, string agentId)
    {
        var msg = $"XSC1|{shopCode.ToUpperInvariant()}|{number}|{Money(Math.Round(amount, 2))}|{closedUtc:yyyyMMdd}|{agentId}";
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(msg));
        var b32 = Base32(mac[..8])[..12];
        return $"{b32[..4]}-{b32[4..8]}-{b32[8..12]}";
    }

    public static string Base32(byte[] data)
    {
        const string A = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(A[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(A[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>Parse the closing QR text: XSC1;shop=..;n=..;amt=..;d=yyyyMMdd;agent=..;c=..</summary>
    public static Dictionary<string, string>? ParseQr(string text)
    {
        text = (text ?? "").Trim();
        if (!text.StartsWith("XSC1;", StringComparison.OrdinalIgnoreCase)) return null;
        return text.Split(';').Skip(1).Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());
    }
}
