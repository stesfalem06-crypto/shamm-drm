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
