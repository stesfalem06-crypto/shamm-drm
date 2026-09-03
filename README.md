# Shamm DRM Suite — Build Plan & Status

Three apps, one shared crypto core, built in phases. Each phase is finished
and explained before the next one starts.

## Components
- **Xama Master** (Windows) — imports & encrypts videos, sets token price
- **X Seller** (Windows) — shop-owner app: search library, send to a phone over USB, tracks debt
- **Xama** (Android) — video player; plays protected + regular videos, TikTok-style feed for vertical clips
- **crypto-core** (Rust) — shared encryption engine used by all three, so the security logic is defined once, not reimplemented per platform

## Phase status
- [x] Phase 1 — Shared crypto core (key generation, device-locked key wrapping, seekable video encryption) + free GitHub Actions build for both Windows (.dll) and Android (.so)
- [x] Phase 2 — Xama Master (Windows encryptor app): import a video, set a token price, encrypt to `.shammvid` + `.shammmeta`, library view. GitHub Actions builds the final portable `.exe` automatically.
- [x] Phase 3 — X Seller (Windows shop app): instant search over the local library, connected-phone detection via USB/ADB (bundled automatically), per-device key wrapping at send time, local debt ledger. GitHub Actions builds the final portable `.exe` with adb.exe included.
- [x] Phase 4 — Xama (Android player): library screen, standard player for landscape/plain videos, TikTok-style vertical swipe feed for reels, device-locked decryption, screenshot/recording block, root/emulator refusal, obfuscated release build. GitHub Actions builds the `.apk` automatically (debug-signed, no Play Store signing needed yet).
- [x] Phase 5 — Settlement flow: X Seller exports an encrypted ledger to USB, Xama Master reads it, generates an owner-password-protected CSV invoice (`.shamminvoice`), and writes a `.shammack` file the agent carries back to mark the shop's sales as paid.
- [ ] Phase 6 — GitHub walkthrough: get everything onto GitHub (no command line), first .apk/.exe build

## Why Rust for the crypto core
Windows apps are C#, the Android app is Kotlin — normally that means writing
the encryption logic twice and hoping the two copies never drift apart,
which is exactly the kind of bug that breaks DRM security. Instead, the
crypto logic lives once in Rust and compiles to a `.dll` (Windows) and `.so`
(Android) from the identical source, via the included GitHub Actions
workflow (`.github/workflows/build-crypto-core.yml`) — free, automatic, no
local toolchain needed.
