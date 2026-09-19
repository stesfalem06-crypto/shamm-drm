package com.shammapps.xama.util

import android.content.Context
import android.graphics.Bitmap
import android.media.MediaMetadataRetriever
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.util.LruCache
import android.widget.ImageView
import com.shammapps.xama.R
import com.shammapps.xama.data.LocalVideo
import java.util.concurrent.Executors
import java.util.concurrent.Future

/**
 * Bounded thumbnail extractor. One shared pool + memory cache so the library
 * grid never opens dozens of MediaMetadataRetriever instances at once
 * (main cause of freezes / OOM kills).
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

    fun load(context: Context, video: LocalVideo, into: ImageView, tagKey: Any) {
        if (video.isEncrypted) {
            into.setImageDrawable(null)
            into.setBackgroundResource(R.drawable.thumb_placeholder)
            return
        }
        val key = video.contentUri ?: video.filePath
        if (key.isBlank()) return

        into.tag = tagKey
        into.setImageDrawable(null)
        into.setBackgroundResource(R.drawable.thumb_placeholder)

        cache.get(key)?.let { bmp ->
            if (into.tag == tagKey) {
                into.setImageBitmap(bmp)
                into.background = null
            }
            return
        }

        val app = context.applicationContext
        pool.execute {
            val bmp = extract(app, video, key)
            if (bmp != null) {
                synchronized(cache) { cache.put(key, bmp) }
            }
            main.post {
                if (into.tag == tagKey && bmp != null) {
                    into.setImageBitmap(bmp)
                    into.background = null
                }
            }
        }
    }

    private fun extract(context: Context, video: LocalVideo, key: String): Bitmap? {
        val r = MediaMetadataRetriever()
        return try {
            when {
                !video.contentUri.isNullOrBlank() ->
                    r.setDataSource(context, Uri.parse(video.contentUri))
                video.filePath.isNotBlank() -> r.setDataSource(video.filePath)
                else -> return null
            }
            // Prefer embedded thumbnail when available (fast)
            val embedded = try {
                r.embeddedPicture?.let {
                    android.graphics.BitmapFactory.decodeByteArray(it, 0, it.size)
                }
            } catch (_: Exception) {
                null
            }
            val raw = embedded ?: r.getFrameAtTime(1_000_000, MediaMetadataRetriever.OPTION_CLOSEST_SYNC)
            scale(raw, 320)
        } catch (_: Throwable) {
            null
        } finally {
            try { r.release() } catch (_: Exception) {}
        }
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
