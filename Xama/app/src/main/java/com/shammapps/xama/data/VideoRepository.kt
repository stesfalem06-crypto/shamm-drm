package com.shammapps.xama.data

import android.content.ContentUris
import android.content.Context
import android.media.MediaMetadataRetriever
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import com.google.gson.Gson
import java.io.File

class VideoRepository(private val context: Context) {
    private val gson = Gson()

    /**
     * Full library: protected videos, device videos (MediaStore), files in the
     * app's `plain` folder (video + audio) and device music (MediaStore audio).
     *
     * Plain files pushed into the app folder are often also copied to a public
     * folder (e.g. Movies/Xama) and therefore show up again via MediaStore; those
     * duplicates are collapsed by file name + size (or by resolved path).
     */
    fun loadAll(): List<LocalVideo> {
        val protected = loadIncoming()
        val device = loadDeviceVideos()
        val appPlain = loadAppPlain()
        val audio = loadDeviceAudio()
        val seen = HashSet<String>()
        val fileKeys = HashSet<String>()
        val out = ArrayList<LocalVideo>(protected.size + device.size + appPlain.size + audio.size)

        fun fileKey(v: LocalVideo): String? {
            val name = v.displayName.ifBlank { v.filePath.substringAfterLast('/') }
            if (name.isBlank() || v.sizeBytes <= 0) return null
            return name.lowercase() + ":" + v.sizeBytes
        }

        fun addAll(list: List<LocalVideo>) {
            for (v in list) {
                val key = v.contentUri ?: v.filePath.ifEmpty { v.id }
                if (!seen.add(key)) continue
                if (v.filePath.isNotEmpty() && !seen.add("path:" + v.filePath)) continue
                if (!v.isEncrypted) {
                    val fk = fileKey(v)
                    if (fk != null && !fileKeys.add(fk)) continue
                }
                out.add(v)
            }
        }
        addAll(protected)
        addAll(device)
        addAll(audio)
        addAll(appPlain)
        return out
    }

    fun loadFolders(videos: List<LocalVideo> = loadAll()): List<VideoFolder> {
        return videos
            .filter { !it.isAudio }
            .groupBy { it.folderName.ifBlank { "Other" } }
            .map { (name, list) -> VideoFolder(name, list.size, list) }
            .sortedWith(
                compareByDescending<VideoFolder> { it.name == "Protected" }
                    .thenByDescending { it.videoCount }
                    .thenBy { it.name.lowercase() }
            )
    }

    fun loadIncoming(): List<LocalVideo> {
        val dirs = com.shammapps.xama.crypto.ShammKeyResolver.incomingDirs(context)
        val out = ArrayList<LocalVideo>()
        val seen = HashSet<String>()
        for (dir in dirs) {
            if (!dir.exists()) continue
            dir.listFiles { f -> f.extension == "shammmeta" }?.forEach { metaFile ->
                try {
                    val meta = gson.fromJson(metaFile.readText(), ShammMeta::class.java)
                    val videoFile = File(dir, "${meta.VideoId}.shammvid")
                    if (!videoFile.exists()) return@forEach
                    if (!seen.add(meta.VideoId)) return@forEach
                    out.add(
                        LocalVideo(
                            id = meta.VideoId,
                            title = meta.Title,
                            filePath = videoFile.absolutePath,
                            isEncrypted = true,
                            ivBase64 = meta.IvBase64,
                            isVertical = meta.IsVertical,
                            folderName = "Protected",
                            dateAddedSec = metaFile.lastModified() / 1000,
                        )
                    )
                } catch (_: Exception) { }
            }
        }
        return out
    }

    /** Plain (unprotected) files dropped into the app's private `plain` folder. */
    fun loadAppPlain(): List<LocalVideo> {
        val dir = File(context.getExternalFilesDir(null), "plain")
        if (!dir.exists()) return emptyList()
        val files = dir.listFiles { f ->
            val ext = f.extension.lowercase()
            f.isFile && (ext in VIDEO_EXTS || ext in AUDIO_EXTS)
        } ?: return emptyList()
        val readTags = files.size <= 60
        return files.sortedByDescending { it.lastModified() }.map { f ->
            val isAudio = f.extension.lowercase() in AUDIO_EXTS
            var title = f.nameWithoutExtension
            var artist = ""
            var album = ""
            var duration = 0L
            if (isAudio && readTags) {
                val r = MediaMetadataRetriever()
                try {
                    r.setDataSource(f.absolutePath)
                    r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_TITLE)?.takeIf { it.isNotBlank() }?.let { title = it }
                    artist = r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_ARTIST).orEmpty()
                    album = r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_ALBUM).orEmpty()
                    duration = r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull() ?: 0L
                } catch (_: Throwable) {
                } finally {
                    try { r.release() } catch (_: Exception) {}
                }
            }
            LocalVideo(
                id = "plain-${f.name}",
                title = title,
                filePath = f.absolutePath,
                isEncrypted = false,
                isVertical = false,
                folderName = "Xama",
                isAudio = isAudio,
                artist = artist,
                album = album,
                durationMs = duration,
                sizeBytes = f.length(),
                displayName = f.name,
                dateAddedSec = f.lastModified() / 1000,
            )
        }
    }

    /** MediaStore scan — capped so huge libraries don't freeze the UI thread. */
    fun loadDeviceVideos(limit: Int = 400): List<LocalVideo> {
        val results = ArrayList<LocalVideo>()
        val collection: Uri = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            MediaStore.Video.Media.getContentUri(MediaStore.VOLUME_EXTERNAL)
        } else {
            MediaStore.Video.Media.EXTERNAL_CONTENT_URI
        }
        val cols = arrayListOf(
            MediaStore.Video.Media._ID,
            MediaStore.Video.Media.DISPLAY_NAME,
            MediaStore.Video.Media.DURATION,
            MediaStore.Video.Media.WIDTH,
            MediaStore.Video.Media.HEIGHT,
            MediaStore.Video.Media.BUCKET_DISPLAY_NAME,
            MediaStore.Video.Media.SIZE,
            MediaStore.Video.Media.DATE_ADDED,
        )
        // Avoid DATA column on modern Android (slow / restricted)
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) cols.add(MediaStore.Video.Media.DATA)
        val sort = "${MediaStore.Video.Media.DATE_ADDED} DESC"
        try {
            context.contentResolver.query(collection, cols.toTypedArray(), null, null, sort)?.use { cursor ->
                val idCol = cursor.getColumnIndexOrThrow(MediaStore.Video.Media._ID)
                val nameCol = cursor.getColumnIndexOrThrow(MediaStore.Video.Media.DISPLAY_NAME)
                val durCol = cursor.getColumnIndexOrThrow(MediaStore.Video.Media.DURATION)
                val wCol = cursor.getColumnIndexOrThrow(MediaStore.Video.Media.WIDTH)
                val hCol = cursor.getColumnIndexOrThrow(MediaStore.Video.Media.HEIGHT)
                val bucketCol = cursor.getColumnIndexOrThrow(MediaStore.Video.Media.BUCKET_DISPLAY_NAME)
                val sizeCol = cursor.getColumnIndex(MediaStore.Video.Media.SIZE)
                val dateCol = cursor.getColumnIndex(MediaStore.Video.Media.DATE_ADDED)
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
                            sizeBytes = if (sizeCol >= 0) cursor.getLong(sizeCol) else 0L,
                            displayName = name,
                            dateAddedSec = if (dateCol >= 0) cursor.getLong(dateCol) else 0L,
                        )
                    )
                }
            }
        } catch (_: SecurityException) {
        } catch (_: Exception) {
        }
        return results
    }

    /** Device music via MediaStore (skips ringtones / notification sounds). */
    fun loadDeviceAudio(limit: Int = 600): List<LocalVideo> {
        val results = ArrayList<LocalVideo>()
        val collection: Uri = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            MediaStore.Audio.Media.getContentUri(MediaStore.VOLUME_EXTERNAL)
        } else {
            MediaStore.Audio.Media.EXTERNAL_CONTENT_URI
        }
        val cols = arrayListOf(
            MediaStore.Audio.Media._ID,
            MediaStore.Audio.Media.DISPLAY_NAME,
            MediaStore.Audio.Media.TITLE,
            MediaStore.Audio.Media.ARTIST,
            MediaStore.Audio.Media.ALBUM,
            MediaStore.Audio.Media.DURATION,
            MediaStore.Audio.Media.SIZE,
            MediaStore.Audio.Media.DATE_ADDED,
        )
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            cols.add(MediaStore.Audio.Media.BUCKET_DISPLAY_NAME)
        } else {
            cols.add(MediaStore.Audio.Media.DATA)
        }
        val selection = "${MediaStore.Audio.Media.IS_MUSIC} != 0 OR (" +
            "${MediaStore.Audio.Media.IS_RINGTONE} = 0 AND " +
            "${MediaStore.Audio.Media.IS_NOTIFICATION} = 0 AND " +
            "${MediaStore.Audio.Media.IS_ALARM} = 0 AND " +
            "${MediaStore.Audio.Media.DURATION} >= 30000)"
        val sort = "${MediaStore.Audio.Media.DATE_ADDED} DESC"
        try {
            context.contentResolver.query(collection, cols.toTypedArray(), selection, null, sort)?.use { c ->
                val idCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media._ID)
                val nameCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media.DISPLAY_NAME)
                val titleCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media.TITLE)
                val artistCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media.ARTIST)
                val albumCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media.ALBUM)
                val durCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media.DURATION)
                val sizeCol = c.getColumnIndex(MediaStore.Audio.Media.SIZE)
                val dateCol = c.getColumnIndex(MediaStore.Audio.Media.DATE_ADDED)
                val bucketCol = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q)
                    c.getColumnIndex(MediaStore.Audio.Media.BUCKET_DISPLAY_NAME) else -1
                val dataCol = if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q)
                    c.getColumnIndex(MediaStore.Audio.Media.DATA) else -1

                while (c.moveToNext() && results.size < limit) {
                    val id = c.getLong(idCol)
                    val name = c.getString(nameCol) ?: "Track"
                    val path = if (dataCol >= 0) try { c.getString(dataCol) ?: "" } catch (_: Exception) { "" } else ""
                    val bucket = when {
                        bucketCol >= 0 -> c.getString(bucketCol)
                        path.isNotBlank() -> File(path).parentFile?.name
                        else -> null
                    }?.takeIf { it.isNotBlank() } ?: "Music"
                    val rawArtist = c.getString(artistCol).orEmpty()
                    results.add(
                        LocalVideo(
                            id = "ma-$id",
                            title = c.getString(titleCol)?.takeIf { it.isNotBlank() }
                                ?: name.substringBeforeLast('.').ifBlank { name },
                            filePath = path,
                            contentUri = ContentUris.withAppendedId(
                                MediaStore.Audio.Media.EXTERNAL_CONTENT_URI, id
                            ).toString(),
                            isEncrypted = false,
                            durationMs = c.getLong(durCol),
                            folderName = bucket,
                            isAudio = true,
                            artist = if (rawArtist == "<unknown>") "" else rawArtist,
                            album = c.getString(albumCol).orEmpty(),
                            sizeBytes = if (sizeCol >= 0) c.getLong(sizeCol) else 0L,
                            displayName = name,
                            dateAddedSec = if (dateCol >= 0) c.getLong(dateCol) else 0L,
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
        val VIDEO_EXTS = setOf("mp4", "mkv", "mov", "webm", "avi", "3gp", "m4v", "ts", "flv")
        val AUDIO_EXTS = setOf("mp3", "m4a", "aac", "wav", "flac", "ogg", "opus")
    }
}
