# Family group — multi-device unlock for protected movies

**Audience:** Xama (Android) implementers and X Seller. This doc is the contract; Xama is built in parallel against it.

## Goal
A customer’s **family group** has up to **3 phones**. A protected movie sold/sent to any one of them must play on **every** phone in that group, without weakening DRM: the content key never leaves the shop PC except **AES-256-GCM-wrapped once per device** (same crypto as today).

## Identifying phones
Same as weekly pass / Xama 0.4.0:
- `deviceId` = lowercase hex of `SHA-256(androidId | abi)` (64 hex chars), from `Android/media/com.shammapps.xama/xama-device.json` (or `Download/Xama/.xama-device.json`).
- Binary form (32 bytes) = `FromHex(deviceId)`.

## Who is in the group (X Seller)
- Shop registers a **Family group** by plugging each phone in (File transfer / MTP) and adding it.
- **Max 3 phones** per group; **max 3 swaps** per group (remove one + add another); every add/remove/swap is logged in the sealed sales ledger (`Kind=family-event`).
- **Reuse weekly pass phones when present:** if the target phone is on an **active weekly pass**, that pass’s active phone set **is** the family for wrapping (no second registration required). Otherwise X Seller uses the dedicated Family group that contains the phone. If neither applies, the send is single-device (one wrap), same as today.

Groups and passes are **shop-PC local** (same SQLite DB as the ledger). Not valid at another shop.

## `.shammkey` — versioned multi-recipient key file

### v1 (legacy, unchanged)
- Raw **60 bytes**: `[12-byte GCM nonce][32-byte ciphertext][16-byte GCM tag]`
- Produced by `shamm_wrap_key(contentKey, deviceKey)` (crypto-core).
- **Old Xama** continues to play packages that have a v1 `.shammkey` exactly as today.
- X Seller still writes v1 when there is only one recipient **and** you need maximum compatibility with old Xama builds; prefer v2 even for count=1 once Xama supports v2.

### v2 (family / multi-recipient)
Binary, little-endian where noted:

| Offset | Size | Field |
|--------|------|--------|
| 0 | 4 | Magic ASCII `SHK2` |
| 4 | 1 | `version` = `2` |
| 5 | 1 | `count` = 1..3 |
| 6 | 2 | `reserved` = `0` |
| 8 | `count × 92` | Recipients |

Each recipient (92 bytes):

| Size | Field |
|------|--------|
| 32 | `deviceId` raw (SHA-256 bytes, **not** hex) |
| 60 | wrap = same layout as v1 (`shamm_wrap_key` output for **that** device key) |

Total size = `8 + count×92` (e.g. 3 phones → 284 bytes).

**Rules**
- Recipients are sorted by `deviceId` ascending (byte order) so the same family always gets a stable file.
- No plaintext content key, no device private material — only per-device GCM wraps (identical strength to today’s single wrap).
- The receiving phone’s wrap **must** be included; other members’ wraps are included so copying the package to another family phone (or receiving the same send) unlocks without a second shop visit.

### Xama: how to unwrap
1. Read entire `.shammkey`.
2. If `length == 60` **or** magic ≠ `SHK2` → treat as **v1**: `shamm_unwrap_key(file, myDeviceKey)`.
3. If magic `SHK2` and `version == 2`:
   - Let `me` = this phone’s 32-byte device fingerprint.
   - Scan recipients; find entry where `deviceId == me`.
   - If none → not licensed for this phone (fail closed).
   - `shamm_unwrap_key(that 60-byte wrap, myDeviceKey)` → content key; play as today.
4. Unknown version → fail closed.
5. **Old Xama** that only understands v1: will reject v2 files (length ≠ 60 / unwrap BadLength). That is expected; old Xama must still play **old v1** packages. Ships that need family unlock require Xama that implements this doc.

No new crypto-core FFI is required: still `shamm_derive_device_key` + `shamm_wrap_key` / `shamm_unwrap_key` per recipient.

## Phone-side group file (signed, display + share hint)

On family create/change, and whenever a group phone is plugged in / receives a protected send, X Seller writes:

1. `Internal storage/Android/media/com.shammapps.xama/xama-family.json` (preferred)
2. `Internal storage/Download/Xama/.xama-family.json` (fallback)

```json
{
  "version": 1,
  "kind": "family-group",
  "groupId": 7,
  "shopName": "Abebe Media",
  "shopCode": "AB12CD34",
  "status": "active",
  "deviceId": "3f9c…(64 hex, this phone)",
  "members": [
    { "deviceId": "3f9c…", "model": "SM-A145F" },
    { "deviceId": "a1b2…", "model": "Redmi Note 12" }
  ],
  "phonesUsed": 2,
  "phonesMax": 3,
  "writtenUtc": "2026-10-22T09:12:05Z",
  "sig": "hex HMAC-SHA256"
}
```

- `status`: `active` | `removed` (this phone was swapped/removed from the group).
- `members`: **active** members only (deviceId lowercase hex + model).
- `sig` = lowercase hex `HMAC-SHA256(K, canonical)` where  
  `K = HMAC-SHA256(SettlementKey, "shamm-family-file-v1")`  
  and `canonical` = JSON object **without** `sig`, keys sorted ordinally, compact (`System.Text.Json` defaults). Advisory integrity only (key ships in X Seller).
- **Xama should:** show family membership; allow “share/copy package to another member” UX only to `members[].deviceId` listed here; **never** treat this file as authorization to unwrap — unwrap still requires a matching `.shammkey` recipient entry. Enforcement of playback is the key file + device key.

When the family set comes from an **active weekly pass**, X Seller still writes `xama-family.json` with `groupId` = pass id and an extra field `"source":"weekly-pass"` so Xama can correlate; pass file (`xama-pass.json`) remains as in WEEKLY_PASS.md.

## Send order (unchanged package completion)
`.shammvid` → `.shammkey` (v1 or v2) → `.shammthumb` (optional) → `.shammmeta` last.

## Security summary
| Asset | Leaves shop PC? |
|-------|-----------------|
| Content key (raw) | Never |
| Per-device wrap | Yes, inside `.shammkey` only |
| Device private keys | Never (derived on device from androidId\|abi) |
| Family member list | Yes, in signed `xama-family.json` (ids + models only) |

Swapping a phone off the group does **not** revoke already-delivered packages on the removed phone (those wraps remain in old `.shammkey` files). New sends omit the removed device. Same trust model as today’s USB delivery.

## Changelog
- **2026-10-22:** Initial contract (v2 `SHK2` key file + `xama-family.json`).
