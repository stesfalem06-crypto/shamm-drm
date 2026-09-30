package com.shammapps.xama.security

import android.annotation.SuppressLint
import android.content.Context
import android.os.Build
import android.provider.Settings
import java.security.MessageDigest

/**
 * Device binding fingerprint. MUST match XSeller AdbService.GetHardwareFingerprint.
 *
 * Serial is intentionally NOT used. Apps on Android 10+ cannot read ro.serialno
 * (Build.SERIAL is "unknown"), while ADB can. That mismatch made every protected
 * video unplayable. Binding is Android ID + primary ABI only.
 */
object DeviceFingerprint {

    @SuppressLint("HardwareIds")
    fun compute(context: Context): ByteArray {
        val androidId = normalize(
            Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID)
        )
        val abi = normalize(Build.SUPPORTED_ABIS.firstOrNull())
        val combined = "$androidId|$abi"
        return MessageDigest.getInstance("SHA-256").digest(combined.toByteArray(Charsets.UTF_8))
    }

    fun normalize(raw: String?): String {
        val s = raw?.trim()?.trim('\r', '\n') ?: ""
        if (s.isEmpty() || s.equals("null", true) || s.equals("unknown", true)) return ""
        return s
    }
}
