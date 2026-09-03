package com.shammapps.xama.crypto

import android.content.Context
import com.shammapps.xama.security.DeviceFingerprint
import java.io.File

/**
 * Given a video ID, finds its .shammvid + .shammkey files (pushed by
 * X Seller over USB into this app's private storage) and unwraps the
 * content key using a device key derived fresh from this phone's own
 * hardware fingerprint. If this isn't the phone the video was sent to,
 * the derived key won't match and unwrapping fails - the video simply
 * won't decrypt, by design (see crypto-core/src/lib.rs for why).
 */
class ShammKeyResolver(private val context: Context) {

    data class ResolvedVideo(val videoFile: File, val contentKey: ByteArray, val iv: ByteArray)

    fun resolve(videoId: String, ivBase64: String): ResolvedVideo? {
        val dir = File(context.getExternalFilesDir(null), "incoming")
        val videoFile = File(dir, "$videoId.shammvid")
        val keyFile = File(dir, "$videoId.shammkey")
        if (!videoFile.exists() || !keyFile.exists()) return null

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
        if (rc2 != 0) return null // wrong device, tampered file, or corrupted key - playback refused

        val iv = android.util.Base64.decode(ivBase64, android.util.Base64.DEFAULT)
        return ResolvedVideo(videoFile, contentKey, iv)
    }
}
