# Xama device-id file (X Seller MTP binding)

X Seller sends to phones over MTP (no USB debugging). Over MTP it can't read the
phone's `ANDROID_ID` the way adb did. So Xama publishes the same two inputs it already
uses in `DeviceFingerprint.kt`, and X Seller derives the identical device key from them.

## Where Xama writes it (on every app start, and whenever it changes)

Primary (required). This is Xama's own media dir. No permission is needed, it's visible over MTP, and it's readable on Android 11+:

    <primary external storage>/Android/media/com.shammapps.xama/xama-device.json
    i.e. context.externalMediaDirs[0]/xama-device.json

Fallback (optional, for OEMs that hide Android/media over MTP). Write it through MediaStore.Downloads with RELATIVE_PATH "Download/Xama/":

    Download/Xama/.xama-device.json

X Seller reads these paths in this order and uses the first valid file:

1. `Android/media/com.shammapps.xama/xama-device.json`
2. `Download/Xama/.xama-device.json`
3. `Download/Xama/xama-device.json`

After writing, call `MediaScannerConnection.scanFile(...)` on the file. Without that, Windows MTP may not show a new file until the phone is replugged.

## Format: UTF-8 JSON, no BOM

```json
{
  "schema": 1,
  "app": "com.shammapps.xama",
  "androidId": "9774d56d682e549c",
  "abi": "arm64-v8a",
  "fingerprintSha256": "3F1A...64 HEX CHARS...",
  "model": "SM-A515F",
  "manufacturer": "samsung",
  "appVersion": "0.2.0",
  "writtenAtUtc": "2026-10-17T18:00:00Z"
}
```

| field | required | value |
|---|---|---|
| schema | yes | `1` |
| app | yes | `com.shammapps.xama` |
| androidId | yes | `DeviceFingerprint.normalize(Settings.Secure.ANDROID_ID)` |
| abi | yes | `DeviceFingerprint.normalize(Build.SUPPORTED_ABIS.firstOrNull())` |
| fingerprintSha256 | recommended | uppercase or lowercase hex of `DeviceFingerprint.compute(context)`, i.e. SHA-256(UTF-8 of `androidId + "|" + abi`) |
| model, manufacturer, appVersion, writtenAtUtc | optional | informational only |

X Seller recomputes SHA-256(`androidId|abi`) using the same normalization: trim whitespace/CR/LF, and treat empty, `null` or `unknown` as "". If `fingerprintSha256` is present and doesn't match, X Seller refuses to send. The device key, wrapping and the `.shammkey` format are all unchanged.

## Where X Seller puts files (Xama must scan these)

* Protected packages (`<VideoId>.shammvid`, `.shammkey`, `.shammmeta`) go to the first writable of:
  1. `Android/media/com.shammapps.xama/incoming/`. **Add this to `ShammKeyResolver.incomingDirs`**: `File(context.externalMediaDirs[0], "incoming")`.
  2. `Download/XamaIncoming/` (already in incomingDirs).
* Plain files:
  * videos go to `Movies/Xama/`
  * audio goes to `Music/Xama/`
  * PDF, images, exe and everything else go to `Download/Xama/`

  These go into MediaStore after a media scan. Xama should query MediaStore for those RELATIVE_PATHs, or watch the folders.

## Security note
`androidId` is per-app-signing-key on Android 8+. So the file only works for the Xama build signed with the production key, which is the same as the adb path. The file isn't secret (adb could always read these values). It only lets X Seller compute the same binding that Xama computes when it plays the file.
