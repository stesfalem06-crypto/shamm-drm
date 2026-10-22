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

        val fileBytes = keyFile.readBytes()
        val fingerprint = DeviceFingerprint.compute(context)
        // docs/FAMILY_GROUP.md: v1 = raw 60-byte wrap; v2 "SHK2" = per-device wraps, pick ours (fail closed).
        val wrapped = selectWrap(fileBytes, fingerprint) ?: return null

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
        private const val V1_LEN = 60
        private const val V2_HEADER = 8
        private const val V2_ENTRY = 92

        /** Returns the 60-byte wrap for [me] (32-byte fingerprint) or null (not licensed / unknown format). */
        fun selectWrap(file: ByteArray, me: ByteArray): ByteArray? {
            val isV2 = file.size >= V2_HEADER && file[0] == 'S'.code.toByte() && file[1] == 'H'.code.toByte() &&
                file[2] == 'K'.code.toByte() && file[3] == '2'.code.toByte()
            if (file.size == V1_LEN || !isV2) return file
            if (file[4].toInt() != 2) return null
            val count = file[5].toInt() and 0xFF
            if (count !in 1..3 || file.size < V2_HEADER + count * V2_ENTRY) return null
            for (i in 0 until count) {
                val off = V2_HEADER + i * V2_ENTRY
                if (me.size == 32 && (0 until 32).all { file[off + it] == me[it] }) {
                    return file.copyOfRange(off + 32, off + V2_ENTRY)
                }
            }
            return null
        }

        /** Recipient deviceIds (lowercase hex) of a v2 key, or null for v1/unknown. */
        fun recipients(file: ByteArray): List<String>? {
            if (file.size < V2_HEADER || String(file, 0, 4, Charsets.US_ASCII) != "SHK2" || file[4].toInt() != 2) return null
            val count = file[5].toInt() and 0xFF
            if (file.size < V2_HEADER + count * V2_ENTRY) return null
            return (0 until count).map { i ->
                val off = V2_HEADER + i * V2_ENTRY
                (0 until 32).joinToString("") { "%02x".format(file[off + it]) }
            }
        }

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
