package com.shammapps.xama.util

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.media.MediaMetadataRetriever
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.util.LruCache
import android.widget.ImageView
import com.shammapps.xama.R
import com.shammapps.xama.data.LocalVideo
import java.util.concurrent.Executors

/**
 * Bounded thumbnail / album-art extractor. One shared pool + memory cache so
 * the library never opens dozens of MediaMetadataRetriever instances at once
 * (main cause of freezes / OOM kills).
 *
 * Protected videos are never decoded for thumbnails (their bytes are encrypted).
 */
object ThumbLoader {
    private val main = Handler(Looper.getMainLooper())
    // 2 workers max — MMR is heavy; more = stalls and crashes
    private val pool = Executors.newFixedThreadPool(2)
    private val cache: LruCache<String, Bitmap> = object : LruCache<String, Bitmap>(
        (Runtime.getRuntime().maxMemory() / 1024 / 12).toInt().coerceIn(8_192, 24_576)
    ) {
        override fun sizeOf(key: String, value: Bitmap): Int = value.byteCount / 1024
    }
    /** Remembers audio files with no embedded art so we don't retry them. */
    private val noArt = HashSet<String>()

    fun load(context: Context, video: LocalVideo, into: ImageView, tagKey: Any) {
        if (video.isEncrypted) {
            into.tag = tagKey
            into.setImageDrawable(null)
            into.setBackgroundResource(R.drawable.thumb_protected)
            return
        }
        val key = video.contentUri ?: video.filePath
        if (key.isBlank()) return

        into.tag = tagKey
        into.setImageDrawable(null)
        // Audio rows draw their own placeholder underneath the image view.
        if (video.isAudio) into.background = null else into.setBackgroundResource(R.drawable.thumb_placeholder)

        cache.get(key)?.let { bmp ->
            if (into.tag == tagKey) {
                into.setImageBitmap(bmp)
                into.background = null
            }
            return
        }
        if (video.isAudio && synchronized(noArt) { key in noArt }) return

        val app = context.applicationContext
        pool.execute {
            val bmp = extract(app, video, 320)
            if (bmp != null) {
                synchronized(cache) { cache.put(key, bmp) }
            } else if (video.isAudio) {
                synchronized(noArt) { noArt.add(key) }
            }
            main.post {
                if (into.tag == tagKey && bmp != null) {
                    into.setImageBitmap(bmp)
                    into.background = null
                }
            }
        }
    }

    /** Larger artwork for the audio player screen (not cached). */
    fun loadLarge(context: Context, video: LocalVideo, callback: (Bitmap?) -> Unit) {
        if (video.isEncrypted) { callback(null); return }
        val app = context.applicationContext
        pool.execute {
            val bmp = extract(app, video, 900)
            main.post { callback(bmp) }
        }
    }

    private fun extract(context: Context, video: LocalVideo, maxW: Int): Bitmap? {
        val r = MediaMetadataRetriever()
        return try {
            when {
                !video.contentUri.isNullOrBlank() ->
                    r.setDataSource(context, Uri.parse(video.contentUri))
                video.filePath.isNotBlank() -> r.setDataSource(video.filePath)
                else -> return null
            }
            // Prefer embedded picture (album art / poster) when available (fast)
            val embedded = try {
                r.embeddedPicture?.let { decodeSampled(it, maxW) }
            } catch (_: Exception) {
                null
            }
            val raw = embedded ?: if (video.isAudio) null
            else r.getFrameAtTime(1_000_000, MediaMetadataRetriever.OPTION_CLOSEST_SYNC)
            scale(raw, maxW)
        } catch (_: Throwable) {
            null
        } finally {
            try { r.release() } catch (_: Exception) {}
        }
    }

    private fun decodeSampled(bytes: ByteArray, maxW: Int): Bitmap? {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        var sample = 1
        while (bounds.outWidth / (sample * 2) >= maxW) sample *= 2
        return BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample })
    }

    private fun scale(src: Bitmap?, maxW: Int): Bitmap? {
        if (src == null || src.isRecycled) return null
        if (src.width <= maxW) return src
        val h = (src.height * (maxW.toFloat() / src.width)).toInt().coerceAtLeast(1)
        return try {
            val out = Bitmap.createScaledBitmap(src, maxW, h, true)
            if (out != src) src.recycle()
            out
        } catch (_: Throwable) {
            src
        }
    }
}
