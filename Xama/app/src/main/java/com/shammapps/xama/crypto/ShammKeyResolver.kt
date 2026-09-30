package com.shammapps.xama.crypto

import android.content.Context
import android.os.Environment
import com.shammapps.xama.security.DeviceFingerprint
import java.io.File

class ShammKeyResolver(private val context: Context) {

    data class ResolvedVideo(val videoFile: File, val contentKey: ByteArray, val iv: ByteArray)

    fun resolve(videoId: String, ivBase64: String): ResolvedVideo? {
        val dirs = incomingDirs(context)
        val videoFile = dirs.map { File(it, "$videoId.shammvid") }.firstOrNull { it.exists() } ?: return null
        val keyFile = dirs.map { File(it, "$videoId.shammkey") }.firstOrNull { it.exists() }
            ?: File(videoFile.parentFile, "$videoId.shammkey")
        if (!keyFile.exists()) return null

        val wrapped = keyFile.readBytes()
        val fingerprint = DeviceFingerprint.compute(context)

        val deviceKey = ByteArray(NativeCrypto.KEY_LEN)
        val rc1 = NativeCrypto.shamm_derive_device_key(
            fingerprint, fingerprint.size, deviceKey, NativeCrypto.KEY_LEN
        )
        if (rc1 != 0) return null

        val contentKey = ByteArray(NativeCrypto.KEY_LEN)
        val rc2 = NativeCrypto.shamm_unwrap_key(
            wrapped, wrapped.size, deviceKey, NativeCrypto.KEY_LEN, contentKey, NativeCrypto.KEY_LEN
        )
        NativeCrypto.shamm_wipe(deviceKey, NativeCrypto.KEY_LEN)
        if (rc2 != 0) return null

        val iv = android.util.Base64.decode(ivBase64, android.util.Base64.DEFAULT)
        return ResolvedVideo(videoFile, contentKey, iv)
    }

    companion object {
        fun incomingDirs(context: Context): List<File> {
            val out = ArrayList<File>()
            context.getExternalFilesDir(null)?.let { out.add(File(it, "incoming")) }
            try {
                val pub = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
                out.add(File(pub, "XamaIncoming"))
            } catch (_: Exception) {}
            out.add(File("/sdcard/Download/XamaIncoming"))
            out.add(File("/storage/emulated/0/Download/XamaIncoming"))
            return out.distinctBy { it.absolutePath }
        }
    }
}
