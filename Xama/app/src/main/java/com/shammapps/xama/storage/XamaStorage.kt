package com.shammapps.xama.storage

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.media.MediaScannerConnection
import android.os.Build
import android.os.Environment
import androidx.core.content.ContextCompat
import com.google.gson.GsonBuilder
import com.shammapps.xama.BuildConfig
import com.shammapps.xama.security.DeviceFingerprint
import java.io.File

/**
 * Shared-storage helpers for the MTP ("File transfer") workflow with X Seller.
 *
 * Folders the shop PC writes into over MTP:
 *  - protected packages: Download/XamaIncoming, Android/media/com.shammapps.xama/incoming
 *  - plain files:        Movies/Xama, Music/Xama, Download/Xama
 * Identity file X Seller reads:
 *  - Android/media/com.shammapps.xama/xama-device.json
 *  - Download/Xama/.xama-device.json
 */
object XamaStorage {
    private const val PREFS = "xama_ui"
    const val KEY_SCAN_ALL = "scan_all_folders"

    fun root(): File = Environment.getExternalStorageDirectory()

    /** Android/media/com.shammapps.xama (writable by the app without any permission). */
    fun mediaDir(context: Context): File {
        @Suppress("DEPRECATION")
        val fromApi = context.externalMediaDirs.firstOrNull { it != null }
        return fromApi ?: File(root(), "Android/media/${context.packageName}")
    }

    fun mediaIncomingDir(context: Context) = File(mediaDir(context), "incoming")

    fun publicDir(type: String): File = try {
        Environment.getExternalStoragePublicDirectory(type)
    } catch (_: Exception) {
        File(root(), type)
    }

    fun plainDirs(): List<File> = listOf(
        File(publicDir(Environment.DIRECTORY_MOVIES), "Xama"),
        File(publicDir(Environment.DIRECTORY_MUSIC), "Xama"),
        File(publicDir(Environment.DIRECTORY_DOWNLOADS), "Xama"),
    )

    fun hasAllFilesAccess(): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) Environment.isExternalStorageManager() else true

    fun hasLegacyRead(context: Context): Boolean =
        ContextCompat.checkSelfPermission(context, Manifest.permission.READ_EXTERNAL_STORAGE) ==
            PackageManager.PERMISSION_GRANTED

    /** True when a raw File walk of shared storage is allowed. */
    fun canWalkStorage(context: Context): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) Environment.isExternalStorageManager()
        else hasLegacyRead(context)

    fun scanAllEnabled(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_SCAN_ALL, false)

    fun setScanAll(context: Context, value: Boolean) =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY_SCAN_ALL, value).apply()

    // ------------------------------------------------------------ identity file

    /** SHA-256(androidId|abi) hex - read-only use of the protected-playback fingerprint. */
    fun deviceIdHex(context: Context): String =
        DeviceFingerprint.compute(context).joinToString("") { "%02x".format(it) }

    data class Identity(
        val version: Int,
        val deviceId: String,
        val model: String,
        val manufacturer: String,
        val app: String,
        val appVersion: String,
    )

    /**
     * Writes the identity file to every MTP-visible location X Seller checks
     * (docs/XAMA_DEVICE_FILE.md): Android/media/<pkg>/xama-device.json,
     * Download/Xama/.xama-device.json and Download/Xama/xama-device.json (non-dot,
     * for phones that hide dot files over MTP). [force] rewrites even if unchanged
     * (done on every app launch) and always re-runs the media scanner.
     */
    fun writeDeviceIdentity(context: Context, force: Boolean = false): List<File> {
        val json = GsonBuilder().setPrettyPrinting().create().toJson(
            Identity(1, deviceIdHex(context), Build.MODEL ?: "", Build.MANUFACTURER ?: "", "xama", BuildConfig.VERSION_NAME)
        )
        val downloadXama = File(publicDir(Environment.DIRECTORY_DOWNLOADS), "Xama")
        val targets = listOf(
            File(mediaDir(context), "xama-device.json"),
            File(downloadXama, ".xama-device.json"),
            File(downloadXama, "xama-device.json"),
        )
        val written = ArrayList<File>()
        var anyChange = false
        for (f in targets) {
            try {
                f.parentFile?.mkdirs()
                if (force || !f.exists() || f.readText() != json) {
                    f.writeText(json)
                    anyChange = true
                }
                written.add(f)
            } catch (_: Exception) {
                // Android 10+: Download/Xama may be blocked for direct writes (e.g. file owned
                // by another app). Fall back to MediaStore.Downloads.
                if (f.parentFile == downloadXama && writeViaMediaStore(context, f.name, json)) {
                    anyChange = true
                }
            }
        }
        try { mediaIncomingDir(context).mkdirs() } catch (_: Exception) {}
        try { File(publicDir(Environment.DIRECTORY_DOWNLOADS), "XamaIncoming").mkdirs() } catch (_: Exception) {}
        if (written.isNotEmpty() && (force || anyChange)) {
            // Windows MTP only shows files MediaStore knows about.
            try {
                MediaScannerConnection.scanFile(context, written.map { it.absolutePath }.toTypedArray(), null, null)
            } catch (_: Exception) {
            }
        }
        return written
    }

    private fun writeViaMediaStore(context: Context, name: String, json: String): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return false
        return try {
            val resolver = context.contentResolver
            val collection = android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI
            val rel = Environment.DIRECTORY_DOWNLOADS + "/Xama/"
            // Reuse our own existing row if present.
            val existing = resolver.query(
                collection, arrayOf(android.provider.MediaStore.MediaColumns._ID),
                "${android.provider.MediaStore.MediaColumns.RELATIVE_PATH}=? AND ${android.provider.MediaStore.MediaColumns.DISPLAY_NAME}=?",
                arrayOf(rel, name), null
            )?.use { c -> if (c.moveToFirst()) android.content.ContentUris.withAppendedId(collection, c.getLong(0)) else null }
            val uri = existing ?: resolver.insert(collection, android.content.ContentValues().apply {
                put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME, name)
                put(android.provider.MediaStore.MediaColumns.MIME_TYPE, "application/json")
                put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH, rel)
            }) ?: return false
            resolver.openOutputStream(uri, "wt")?.use { it.write(json.toByteArray(Charsets.UTF_8)) } ?: return false
            true
        } catch (_: Exception) {
            false
        }
    }

    fun identityWritten(context: Context): Boolean = File(mediaDir(context), "xama-device.json").exists()

    // ------------------------------------------------------------ package import

    private val PACKAGE_EXTS = setOf("shammvid", "shammkey", "shammmeta", "shammthumb")

    /**
     * Moves protected packages dropped in Android/media/<pkg>/incoming into the app's
     * private incoming folder, which the existing (unchanged) key resolver reads.
     * The .shammmeta file is moved last so a half-copied package is never listed.
     */
    /**
     * Moves complete protected packages found anywhere (SHAREit, Xender, Bluetooth,
     * Download, ...) into the app's private incoming folder - the same import approach
     * as Android/media, so the unchanged key resolver can open them.
     * [metaFiles] are `<VideoId>.shammmeta` files; the package is moved only when the
     * matching .shammvid and .shammkey sit next to it.
     */
    fun importPackages(context: Context, metaFiles: List<File>) {
        val dest = File(context.getExternalFilesDir(null) ?: return, "incoming")
        val privateRoot = context.getExternalFilesDir(null)?.absolutePath ?: return
        for (meta in metaFiles) {
            try {
                if (meta.absolutePath.startsWith(privateRoot)) continue
                val dir = meta.parentFile ?: continue
                val base = meta.nameWithoutExtension
                val vid = File(dir, "$base.shammvid")
                val key = File(dir, "$base.shammkey")
                if (!vid.exists() || !key.exists()) continue
                val now = System.currentTimeMillis()
                if (listOf(meta, vid, key).any { now - it.lastModified() < 3_000 }) continue
                dest.mkdirs()
                val parts = listOf(vid, key, File(dir, "$base.shammthumb"), meta).filter { it.exists() }
                for (f in parts) moveInto(f, File(dest, f.name))
            } catch (_: Exception) {
            }
        }
    }

    private fun moveInto(f: File, target: File) {
        if (target.exists()) target.delete()
        if (!f.renameTo(target)) {
            f.copyTo(target, overwrite = true)
            if (target.length() == f.length()) f.delete()
        }
    }

    fun importMediaIncoming(context: Context) {
        val src = mediaIncomingDir(context)
        val dest = File(context.getExternalFilesDir(null) ?: return, "incoming")
        val files = src.listFiles { f -> f.isFile && f.extension.lowercase() in PACKAGE_EXTS } ?: return
        if (files.isEmpty()) return
        dest.mkdirs()
        val ordered = files.sortedBy { if (it.extension.equals("shammmeta", true)) 1 else 0 }
        for (f in ordered) {
            // Skip files still being written by MTP (modified in the last few seconds).
            if (System.currentTimeMillis() - f.lastModified() < 3_000) continue
            val target = File(dest, f.name)
            try {
                if (target.exists()) target.delete()
                if (!f.renameTo(target)) {
                    f.copyTo(target, overwrite = true)
                    if (target.length() == f.length()) f.delete()
                }
            } catch (_: Exception) {
            }
        }
    }
}
