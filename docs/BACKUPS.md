# Hidden encrypted database backups (X Seller)

## Why
The sales ledger, closings, passes and accounts live in one SQLite file (`Documents\XSeller\ledger.db`). If a shop deletes or edits that file, revenue and closing history are lost. Encrypted backups in separate hidden locations let X Seller recover automatically.

## Where
X Seller writes `ledger.shammbak` to each of these (created if missing, marked Hidden+System):
1. `%ProgramData%\Microsoft\Windows\Caches\.xseller\` (label: ProgramData)
2. `<other fixed drive>\System Volume Information\.xseller\` when another local fixed drive is present (e.g. `D:`)
3. `%LocalAppData%\Microsoft\CLR\.xseller\` (label: AppData Local)

There is **no UI to clear** backups. Diagnostics and Admin → Backups only show health.

## Format
AES-256-GCM. Key = `HMAC-SHA256(SettlementKey, "shamm-db-backup-v1")` (key is inside the app; protects against casual deletion/editing, not against a determined attacker with the binary).
Header (BinaryWriter): magic `"SHAMMBAK1"` · version `1` · UTC ticks · plaintext length · 12-byte nonce · ciphertext · 16-byte tag. AAD = magic bytes.

## When
After every sale (`RecordTransfer`), every closing, every pass change; also every 30 minutes. Writes are debounced (~4 s) and run off the UI thread. Before copying, X Seller runs `PRAGMA wal_checkpoint(TRUNCATE)` and clears the SQLite connection pool.

## Startup restore
Before the main window opens the DB, X Seller:
1. Scores the main DB (hash-chain OK? last sale id? closing count?).
2. Decrypts every backup and scores each.
3. If the main DB is **missing**, **chain-broken**, or **shorter** than the best backup (fewer sales, or same sales but fewer closings), it moves the broken main aside (`ledger.db.broken-<timestamp>`), restores the best backup, and leaves a `.restored` marker.
4. After Accounts init, a ledger entry `VideoId=db-restore`, `Kind=system` records the restore (hash-chained).

## Visibility
- Admin dashboard → **Backups** tab (health per slot, “Back up now”).
- Closing screen and Closings tab show a one-line health summary.
- Diagnostics button includes the full backup section.
