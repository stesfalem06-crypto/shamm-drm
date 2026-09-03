package com.shammapps.xama.data

import android.media.MediaMetadataRetriever
import java.io.File

data class LocalVideo(
    val id: String,
    val title: String,
    val filePath: String,
    val isEncrypted: Boolean,
    val ivBase64: String? = null,
    /** For encrypted videos this MUST come from .shammmeta's IsVertical
     * field (set by Xama Master before encryption) - an encrypted file's
     * frame data can't be probed directly. Only plain/unencrypted files
     * get auto-detected via VideoOrientation.isVertical below. */
    val isVertical: Boolean = false,
)

object VideoOrientation {
    /** True if a video's height exceeds its width - drives routing into the
     * Reels-style vertical feed vs. the standard landscape player. */
    fun isVertical(filePath: String): Boolean {
        val retriever = MediaMetadataRetriever()
        return try {
            retriever.setDataSource(filePath)
            val width = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_WIDTH)?.toIntOrNull() ?: 0
            val height = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_HEIGHT)?.toIntOrNull() ?: 0
            height > width
        } catch (e: Exception) {
            false
        } finally {
            retriever.release()
        }
    }
}
