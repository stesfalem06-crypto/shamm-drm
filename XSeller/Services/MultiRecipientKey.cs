using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace XSeller.Services;

/// <summary>
/// Builds .shammkey files. v1 = raw 60-byte wrap (legacy). v2 = SHK2 multi-recipient (docs/FAMILY_GROUP.md).
/// Uses existing shamm_wrap_key only — no new native APIs.
/// </summary>
public static class MultiRecipientKey
{
    public const int WrapLen = 60; // nonce 12 + key 32 + tag 16
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("SHK2");

    /// <summary>Wrap contentKey for each recipient fingerprint (raw 32 bytes). Returns v2 if count&gt;1 or forceV2; else v1.</summary>
    public static byte[] Build(byte[] contentKey, IReadOnlyList<(byte[] Fingerprint, string DeviceIdHex)> recipients, bool forceV2 = true)
    {
        if (recipients.Count == 0) throw new ArgumentException("At least one recipient.");
        if (recipients.Count > FamilyGroups.MaxPhones) throw new ArgumentException("At most 3 recipients.");

        var wraps = new List<(byte[] Id, byte[] Wrap)>();
        foreach (var (fp, _) in recipients.OrderBy(r => Convert.ToHexString(r.Fingerprint), StringComparer.Ordinal))
        {
            if (fp.Length != 32) throw new ArgumentException("Fingerprint must be 32 bytes.");
            var deviceKey = new byte[NativeCrypto.KeyLen];
            try
            {
                var rc = NativeCrypto.shamm_derive_device_key(fp, (uint)fp.Length, deviceKey, NativeCrypto.KeyLen);
                if (rc != 0) throw new InvalidOperationException("Could not derive a device key for a family member.");
                var wrap = new byte[NativeCrypto.WrappedKeyLen];
                var rc2 = NativeCrypto.shamm_wrap_key(contentKey, NativeCrypto.KeyLen, deviceKey, NativeCrypto.KeyLen, wrap, NativeCrypto.WrappedKeyLen);
                if (rc2 != 0) throw new InvalidOperationException("Failed to wrap the content key for a family member.");
                wraps.Add((fp.ToArray(), wrap));
            }
            finally { NativeCrypto.shamm_wipe(deviceKey, NativeCrypto.KeyLen); }
        }

        if (!forceV2 && wraps.Count == 1) return wraps[0].Wrap;

        // SHK2 header + recipients
        var buf = new byte[8 + wraps.Count * (32 + WrapLen)];
        Magic.CopyTo(buf, 0);
        buf[4] = 2;
        buf[5] = (byte)wraps.Count;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6, 2), 0);
        var o = 8;
        foreach (var (id, wrap) in wraps)
        {
            id.CopyTo(buf, o); o += 32;
            wrap.CopyTo(buf, o); o += WrapLen;
        }
        return buf;
    }

    public static byte[] FingerprintFromHex(string hex)
    {
        hex = FamilyGroups.Norm(hex);
        if (hex.Length != 64) throw new ArgumentException("deviceId must be 64 hex chars.");
        return Convert.FromHexString(hex);
    }
}
