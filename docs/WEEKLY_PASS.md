# Weekly pass (X Seller, offline, cable-only)

## What it is
- Sold by an employee in X Seller → **Weekly pass…** for a phone connected by USB (File transfer / MTP).
- The phone is identified by its Xama `deviceId` (hex SHA-256(androidId|abi)) read from `xama-device.json` (or via adb when available).
- Lasts **7 days from activation**, measured by **this shop PC's clock**. Valid **only at this shop PC** (stored in its local ledger database).
- Covers **up to 3 phones**. While a phone is plugged in the employee can add family phones, remove a phone, or swap one for another (max 3 swaps per pass). Every change is logged.
- While a pass is active for a phone: **protected (.shamm\*) transfers are not charged** (recorded at price 0, kind `pass-use`); **plain files are charged as normal**.
- The DRM path is unchanged: protected files are still wrapped to the phone's device key exactly as before.

## Price and accounting
- Price: Admin dashboard → Prices → “weekly pass” (default 0 — set it).
- Activation = ledger entry `VideoId=weekly-pass`, `Kind=pass`, `Category=pass`, `TokenPrice=<price>`, attributed to the signed-in employee. It is in the hash-chained ledger, the dashboard, the reports and the **closing totals / 50-40-10 split** like any other sale.
- Add / remove / swap → ledger entries `VideoId=weekly-pass-event`, `Kind=pass-event`, price 0 (`DeviceSerial` = deviceId, title e.g. “Pass #12: phone removed”).
- Protected transfer under a pass → ledger entry with the movie id, `Kind=pass-use`, price 0, `DeviceModel` ends with `· pass #<id>` (audit).
- State tables (same SQLite DB): `passes`, `pass_phones` (append-only triggers: no deletes, activation/expiry/price can't be edited).

## Clock rollback guard
Pass time uses `now = max(PC clock, newest ledger SentAtUtc, highest time ever seen)`. Moving the clock back therefore can't extend a pass; the window shows a warning and the ledger chain check also reports the rollback (see CLOSING.md).

## UI
- Each connected phone row shows a badge: green **Pass: N days left** (amber when 1 day left), red **Pass expired**, grey **No pass**, plus “Phones 2/3 · until <expiry>”.
- **Weekly pass…** window: connected phone status, Activate / Add to selected pass / Swap, the passes list (active + expired: days left, phones, employee, activation, expiry, swaps, price) and Remove phone.

## Phone-side pass file (display only — for Xama)
On activation, on any change, and **every time a pass phone is plugged in**, X Seller writes (MTP) the same JSON to:
1. `Internal storage/Android/media/com.shammapps.xama/xama-pass.json` (preferred)
2. `Internal storage/Download/Xama/.xama-pass.json` (fallback; may be hidden over MTP)

```json
{
  "deviceId": "3f9c…(64 hex, lower case)",
  "expiresUtc": "2026-10-28T09:12:00Z",
  "kind": "weekly-pass",
  "passId": 12,
  "phones": ["SM-A145F", "Redmi Note 12"],
  "phonesMax": 3,
  "phonesUsed": 2,
  "shopCode": "AB12CD34",
  "shopName": "Abebe Media",
  "startUtc": "2026-10-21T09:12:00Z",
  "status": "active",
  "version": 1,
  "writtenUtc": "2026-10-21T09:12:05Z",
  "sig": "hex HMAC-SHA256"
}
```
- `status`: `active` | `expired` | `removed` (this phone was removed/swapped off the pass).
- `sig` = lower-case hex `HMAC-SHA256(K, canonical)` where `K = HMAC-SHA256(SettlementKey, "shamm-pass-file-v1")` (SettlementKey as in `XSeller/Services/SettlementKey.cs`) and `canonical` = the JSON object **without** `sig`, keys sorted ordinally, compact (`System.Text.Json` default: no spaces, non-ASCII and HTML-sensitive chars such as < > & ' + escaped as `\uXXXX`). Easiest for Xama: treat the sig as advisory, or re-serialize exactly as above. Integrity hint only — the key ships inside X Seller.
- **Xama should:** show “Weekly pass · <shopName> · N days left (until <local expiresUtc>) · phones used/max” when `status=active` and `deviceId` equals its own deviceId; show “expired”/“removed” otherwise; compute days left from its own clock only for display. **Enforcement is on the shop PC only**; Xama must not unlock or charge anything based on this file.

## Limits
- Not valid across shops or PCs (by design). Reinstalling X Seller with a fresh database loses passes.
- adb-only phones (no MTP) get the badge/pricing but no pass file.
