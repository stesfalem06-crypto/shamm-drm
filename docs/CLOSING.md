# Offline settlement / closing (X Seller ↔ platform agent ↔ Xama Master)

Everything is offline. No server.

## Roles
- **Seller** (employee) / **Admin** (shop owner) — optional local accounts in X Seller (PBKDF2-SHA256, 210k iterations, salted).
- **Platform agent** — the platform owner's field agent who collects cash. Authenticates with a credential the shop does not control.

## Keys
- Xama Master creates a random 32-byte **platform secret S** (DPAPI-protected, `Documents\XamaMaster\platform\platform-secret.dpapi`; back it up from *Agents & closings → Platform secret*).
- Agent key `K_agent = HMAC-SHA256(S, "agent:" + agentId)`.
- `.shammagent` (given to the agent on USB, imported once in X Seller): `K_agent` encrypted with AES-256-GCM under `PBKDF2-SHA256(passphrase, salt, 600000)`, AAD = agentId, plus `KeyCheck = hex(HMAC(K_agent,"check"))[..16]`.
  X Seller stores only the encrypted blob; the shop can't derive `K_agent` without the agent passphrase.

## Closing in X Seller
1. *Closing (platform agent)…* shows everything since the last closing: revenue, platform 50 % (due), shop 40 %, employees 10 % (per employee).
2. Agent picks their name, types their passphrase, enters the cash received → confirm.
3. A closing record is appended (SQLite table `closings`, append-only triggers + SHA-256 hash chain: `RecordHash = SHA256(canonical fields | PrevHash)`, genesis = `GENESIS`). The "owed" counter restarts from the next sale; history can't be cleared.
4. Receipt is shown + printable + PDF + QR, and a signed `.shammclosing` file can be exported (only in that session — it needs `K_agent`).

## Receipt code
```
msg  = "XSC1|" + shopCode + "|" + closingNumber + "|" + amount("0.00", invariant) + "|" + yyyyMMdd(closed UTC) + "|" + agentId
code = CrockfordBase32(HMAC-SHA256(K_agent, msg)[0..8])[0..12]   → shown as XXXX-XXXX-XXXX
```
shopCode = first 8 chars of the shop id, upper case. QR text: `XSC1;shop=…;n=…;amt=…;d=yyyyMMdd;agent=…;c=…`.

## Xama Master: Agents & closings
- **Agents** – create agent credential files.
- **Verify closing** – type the shop code, closing #, amount, date, code (or paste the QR text) → VALID/INVALID (tries all agents, ±1 day for time zones).
- **Import .shammclosing** – checks `HMAC(K_agent, payload)`, recomputes the receipt code, logs the full record, and checks the hash chain against the previous imported closing.
- **Verified closings** – log per shop; gaps (missing closing numbers) are flagged.
- **Sales report** – verify a signed `.shammreport` from the X Seller admin dashboard.

## Tamper detection in X Seller
- Sales ledger rows are hash-chained (`PrevHash`, `RowHash`) and protected by append-only triggers; edits, deletions and tail truncation (vs. pinned head / last closing) are reported.
- Closing history chain, numbering and count are checked.
- The clock moving backwards (vs. the highest time seen, or sale times out of order) is reported.
Warnings appear in the admin dashboard, the closing screen and the status bar at startup.

## Weekly passes in closings
Weekly pass activations are ledger sales (`Kind=pass`, price from Admin → Prices) attributed to the employee who sold them, so they are included in “owed since last closing”, the closing totals and the 50/40/10 split. Protected transfers covered by a pass are recorded at price 0 (`Kind=pass-use`) and pass add/remove/swap events at price 0 (`Kind=pass-event`), so they are counted as sales lines but add no revenue. See WEEKLY_PASS.md.

## Movie dividends
Of the platform's share of revenue (default 50%), a configurable cut (default **40% of that share** → 20% of total revenue) is paid out to movies in proportion to their transfers in the period. Pass activations supply the pass-revenue pool (allocated by `pass-use` count per movie); normally-priced protected sales (`Kind=protected`) add their own revenue to that movie's share. Shop 40% and employee 10% are unchanged. Setting: Admin → Movie dividends → “Movie % of platform”. Shown on the closing screen for the pending period, in the admin tab (CSV export), and inside the signed/CSV/PDF sales report. See also WEEKLY_PASS.md.

## Hidden backups
See BACKUPS.md. Closing and diagnostics show backup health; a restore is logged as a `Kind=system` ledger entry.
