package com.shammapps.xama.data

import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import com.google.gson.Gson
import java.io.File

class VideoRepository(private val context: Context) {
    private val gson = Gson()

    fun loadAll(): List<LocalVideo> {
        val protected = loadIncoming()
        val device = loadDeviceVideos()
        val appPlain = loadAppPlain()
        val seen = HashSet<String>()
        val out = ArrayList<LocalVideo>(protected.size + device.size + appPlain.size)
        fun addAll(list: List<LocalVideo>) {
            for (v in list) {
                val key = v.contentUri ?: v.filePath.ifEmpty { v.id }
                if (seen.add(key)) out.add(v)
            }
        }
        addAll(protected)
        addAll(device)
        addAll(appPlain)
        return out
    }

    fun loadFolders(videos: List<LocalVideo> = loadAll()): List<VideoFolder> {
        return videos
            .groupBy { it.folderName.ifBlank { "Other" } }
            .map { (name, list) -> VideoFolder(name, list.size, list) }
            .sortedWith(
                compareByDescending<VideoFolder> { it.name == "Protected" }
                    .thenByDescending { it.videoCount }
                    .thenBy { it.name.lowercase() }
            )
    }

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
                    folderName = "Protected",
                )
            } catch (_: Exception) {
                null
            }
        } ?: emptyList()
    }

    fun loadAppPlain(): List<LocalVideo> {
        val dir = File(context.getExternalFilesDir(null), "plain")
        if (!dir.exists()) return emptyList()
        return dir.listFiles { f -> f.extension.lowercase() in VIDEO_EXTS }?.map { f ->
            LocalVideo(
                id = "plain-${f.name}",
                title = f.nameWithoutExtension,
                filePath = f.absolutePath,
                isEncrypted = false,
                isVertical = false,
                folderName = "Xama",
            )
        } ?: emptyList()
    }

    /** MediaStore scan — capped so huge libraries don't freeze the UI thread. */
    fun loadDeviceVideos(limit: Int = 400): List<LocalVideo> {
        val results = ArrayList<LocalVideo>()
        val collection: Uri = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            MediaStore.Video.Media.getContentUri(MediaStore.VOLUME_EXTERNAL)
        } else {
            MediaStore.Video.Media.EXTERNAL_CONTENT_URI
        }
        // Avoid DATA column on modern Android (slow / restricted)
        val projection = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            arrayOf(
                MediaStore.Video.Media._ID,
                MediaStore.Video.Media.DISPLAY_NAME,
                MediaStore.Video.Media.DURATION,
                MediaStore.Video.Media.WIDTH,
                MediaStore.Video.Media.HEIGHT,
                MediaStore.Video.Media.BUCKET_DISPLAY_NAME,
            )
        } else {
            arrayOf(
                MediaStore.Video.Media._ID,
                MediaStore.Video.Media.DISPLAY_NAME,
                MediaStore.Video.Media.DATA,
                MediaStore.Video.Media.DURATION,
                MediaStore.Video.Media.WIDTH,
                MediaStore.Video.Media.HEIGHT,
                MediaStore.Video.Media.BUCKET_DISPLAY_NAME,
            )
        }
        val sort = "${MediaStore.Video.Media.DATE_ADDED} DESC"
        try {
            context.contentResolver.query(collection, projection, null, null, sort)?.use { cursor ->
                val idCol = cursor.getColumnIndexOrThrow(MediaStore.Video.Media._ID)
                val nameCol = cursor.getColumnIndexOrThrow(MediaStore.Video.Media.DISPLAY_NAME)
                val durCol = cursor.getColumnIndexOrThrow(MediaStore.Video.Media.DURATION)
                val wCol = cursor.getColumnIndexOrThrow(MediaStore.Video.Media.WIDTH)
                val hCol = cursor.getColumnIndexOrThrow(MediaStore.Video.Media.HEIGHT)
                val bucketCol = cursor.getColumnIndexOrThrow(MediaStore.Video.Media.BUCKET_DISPLAY_NAME)
                val dataCol = if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q)
                    cursor.getColumnIndex(MediaStore.Video.Media.DATA) else -1

                while (cursor.moveToNext() && results.size < limit) {
                    val id = cursor.getLong(idCol)
                    val name = cursor.getString(nameCol) ?: "Video"
                    val durationMs = cursor.getLong(durCol)
                    if (durationMs in 1 until 500) continue
                    val width = cursor.getInt(wCol)
                    val height = cursor.getInt(hCol)
                    val bucket = cursor.getString(bucketCol)?.takeIf { it.isNotBlank() } ?: "Other"
                    val path = if (dataCol >= 0) try { cursor.getString(dataCol) ?: "" } catch (_: Exception) { "" } else ""
                    val contentUri = ContentUris.withAppendedId(
                        MediaStore.Video.Media.EXTERNAL_CONTENT_URI, id
                    ).toString()
                    results.add(
                        LocalVideo(
                            id = "ms-$id",
                            title = name.substringBeforeLast('.').ifBlank { name },
                            filePath = path,
                            contentUri = contentUri,
                            isEncrypted = false,
                            isVertical = height > width && height > 0,
                            durationMs = durationMs,
                            folderName = bucket,
                        )
                    )
                }
            }
        } catch (_: SecurityException) {
        } catch (_: Exception) {
        }
        return results
    }

    companion object {
        private val VIDEO_EXTS = setOf("mp4", "mkv", "mov", "webm", "avi", "3gp", "m4v", "ts", "flv")
    }
}
