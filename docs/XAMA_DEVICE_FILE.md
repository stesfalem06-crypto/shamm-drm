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

## Format (Xama 0.4.0, implemented): UTF-8 JSON

```json
{ "version": 1, "deviceId": "<64 hex chars>", "model": "SM-A515F", "manufacturer": "samsung", "app": "xama", "appVersion": "0.4.0" }
```

`deviceId` = hex SHA-256(UTF-8 of `normalize(androidId) + "|" + normalize(abi)`), the same value as `DeviceFingerprint.compute()`. X Seller hex-decodes it and uses it directly as the fingerprint for `shamm_derive_device_key`. The crypto and the `.shammkey` format are unchanged. An older/alternative form with `androidId` + `abi` (+ optional `fingerprintSha256`) is also accepted.

If no file is found, X Seller skips protected files for that phone and shows "Open Xama once on the phone". Plain files still go.

## Where X Seller puts files (Xama must scan these)

* Protected packages (`<VideoId>.shammvid`, `.shammkey`, `.shammmeta`) go to the first writable of:
  1. `Download/XamaIncoming/` (preferred)
  2. `Android/media/com.shammapps.xama/incoming/`. Xama moves files out of this folder when it scans, so a file that has disappeared means it was delivered.
* Plain files:
  * videos go to `Movies/Xama/`
  * audio goes to `Music/Xama/`
  * PDF, images, exe and everything else go to `Download/Xama/`

  These go into MediaStore after a media scan. Xama should query MediaStore for those RELATIVE_PATHs, or watch the folders.

## Security note
`androidId` is per-app-signing-key on Android 8+. So the file only works for the Xama build signed with the production key, which is the same as the adb path. The file isn't secret (adb could always read these values). It only lets X Seller compute the same binding that Xama computes when it plays the file.
