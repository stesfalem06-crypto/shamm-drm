package com.shammapps.xama.data

import android.content.Context
import com.google.gson.Gson
import java.io.File

class VideoRepository(private val context: Context) {
    private val gson = Gson()

    /** Encrypted videos pushed in by X Seller over USB. */
    fun loadIncoming(): List<LocalVideo> {
        val dir = File(context.getExternalFilesDir(null), "incoming")
        if (!dir.exists()) return emptyList()

        return dir.listFiles { f -> f.extension == "shammmeta" }?.mapNotNull { metaFile ->
            try {
                val meta = gson.fromJson(metaFile.readText(), ShammMeta::class.java)
                val videoFile = File(dir, "${meta.VideoId}.shammvid")
                if (!videoFile.exists()) return@mapNotNull null
                LocalVideo(
                    id = meta.VideoId,
                    title = meta.Title,
                    filePath = videoFile.absolutePath,
                    isEncrypted = true,
                    ivBase64 = meta.IvBase64,
                    isVertical = meta.IsVertical,
                )
            } catch (e: Exception) {
                null // skip anything unreadable/corrupt rather than crash the library screen
            }
        } ?: emptyList()
    }

    /** Plain, non-encrypted videos the shop also transferred - Xama plays
     * these with zero restrictions, per the product requirement. */
    fun loadPlain(): List<LocalVideo> {
        val dir = File(context.getExternalFilesDir(null), "plain")
        if (!dir.exists()) return emptyList()

        return dir.listFiles { f -> f.extension in listOf("mp4", "mkv", "mov", "webm") }?.map { f ->
            LocalVideo(
                id = f.nameWithoutExtension,
                title = f.nameWithoutExtension,
                filePath = f.absolutePath,
                isEncrypted = false,
                isVertical = VideoOrientation.isVertical(f.absolutePath),
            )
        } ?: emptyList()
    }
}
