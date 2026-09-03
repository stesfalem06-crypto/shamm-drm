package com.shammapps.xama.security

import android.annotation.SuppressLint
import android.content.Context
import android.os.Build
import android.provider.Settings
import java.security.MessageDigest

/**
 * Rebuilds this phone's hardware fingerprint locally. MUST produce
 * byte-for-byte the same result as XSeller/Services/AdbService.cs's
 * GetHardwareFingerprint(), which combines:
 *   Android ID | hardware serial | primary CPU ABI
 * then SHA-256 hashes it. If either side changes its formula without the
 * other, every video sent after that point becomes unplayable.
 */
object DeviceFingerprint {

    @SuppressLint("HardwareIds")
    fun compute(context: Context): ByteArray {
        val androidId = Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID) ?: ""
        val serial = deviceSerial()
        val abi = Build.SUPPORTED_ABIS.firstOrNull() ?: ""

        val combined = "$androidId|$serial|$abi"
        val digest = MessageDigest.getInstance("SHA-256")
        return digest.digest(combined.toByteArray(Charsets.UTF_8))
    }

    /**
     * Build.SERIAL is deprecated/restricted on modern Android (returns
     * "UNKNOWN" without a special permission on API 29+). ADB, running with
     * shell privileges over USB, can still read the real ro.serialno prop -
     * which is exactly what X Seller does. So on-device we deliberately
     * read the SAME underlying prop the same way ADB shell does, via
     * getprop, to keep both sides in agreement.
     */
    private fun deviceSerial(): String {
        return try {
            val process = ProcessBuilder("getprop", "ro.serialno").start()
            process.inputStream.bufferedReader().readText().trim()
        } catch (e: Exception) {
            Build.SERIAL ?: "unknown"
        }
    }
}
