package com.shammapps.xama.data

import android.media.MediaMetadataRetriever

data class LocalVideo(
    val id: String,
    val title: String,
    /** Absolute path when available (may be empty on scoped-storage devices). */
    val filePath: String,
    val isEncrypted: Boolean,
    val ivBase64: String? = null,
    val isVertical: Boolean = false,
    /** content:// URI from MediaStore — preferred for playback of device videos. */
    val contentUri: String? = null,
    val durationMs: Long = 0L,
)

object VideoOrientation {
    fun isVertical(filePath: String): Boolean {
        if (filePath.isBlank()) return false
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
