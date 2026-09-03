package com.shammapps.xama.player

import android.net.Uri
import androidx.media3.common.C
import androidx.media3.datasource.BaseDataSource
import androidx.media3.datasource.DataSpec
import com.shammapps.xama.crypto.NativeCrypto
import java.io.RandomAccessFile

/**
 * Feeds ExoPlayer decrypted bytes straight from an encrypted .shammvid file
 * without ever writing a decrypted copy to disk. Because the video body is
 * AES-256-CTR (see crypto-core/src/lib.rs for why CTR was chosen over GCM),
 * this can serve ANY byte range on request - which is exactly what makes
 * seeking/scrubbing through a protected video work smoothly.
 *
 * contentKey should already be wiped (NativeCrypto.shamm_wipe) by the
 * caller once playback ends or this source is closed.
 */
class ShammDataSource(
    private val contentKey: ByteArray,
    private val iv: ByteArray,
) : BaseDataSource(true) {

    private var file: RandomAccessFile? = null
    private var uri: Uri? = null
    private var bytesRemaining: Long = 0
    private var streamPosition: Long = 0

    override fun open(dataSpec: DataSpec): Long {
        uri = dataSpec.uri
        val f = RandomAccessFile(dataSpec.uri.path, "r")
        file = f
        f.seek(dataSpec.position)
        streamPosition = dataSpec.position

        bytesRemaining = if (dataSpec.length != C.LENGTH_UNSET.toLong())
            dataSpec.length else f.length() - dataSpec.position

        return bytesRemaining
    }

    override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
        if (bytesRemaining == 0L) return C.RESULT_END_OF_INPUT
        val toRead = minOf(length.toLong(), bytesRemaining).toInt()

        val temp = ByteArray(toRead)
        val read = file!!.read(temp, 0, toRead)
        if (read == -1) return C.RESULT_END_OF_INPUT

        // Decrypt exactly this chunk, positioned at its true offset within
        // the overall file - CTR mode lets us do this independent of every
        // other chunk, which is what makes random-access seeking possible.
        val rc = NativeCrypto.shamm_ctr_crypt(
            contentKey, NativeCrypto.KEY_LEN,
            iv, NativeCrypto.IV_LEN,
            streamPosition,
            temp, read
        )
        if (rc != 0) throw java.io.IOException("Decryption failed (code $rc) - file may be corrupted.")

        System.arraycopy(temp, 0, buffer, offset, read)
        streamPosition += read
        bytesRemaining -= read
        return read
    }

    override fun getUri(): Uri? = uri

    override fun close() {
        file?.close()
        file = null
    }
}
