package com.shammapps.xama.util

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.Base64
import java.io.File
import java.io.FileOutputStream

/**
 * Posters (thumbnails) for protected videos. Order of preference:
 *  1. `<VideoId>.shammthumb` (plain JPEG) next to the package, from X Seller
 *  2. `PosterBase64` inside .shammmeta (decoded once into app-private storage)
 *  3. a frame captured in-app during the first authorised playback (app-private)
 * Nothing here touches the encrypted video, keys or the decryption pipeline.
 */
object Posters {
    private const val MAX_POSTER_BYTES = 200 * 1024

    private fun dir(context: Context) = File(context.filesDir, "posters").apply { mkdirs() }

    private fun safe(id: String) = id.replace(Regex("[^A-Za-z0-9._-]"), "_")

    fun capturedFile(context: Context, id: String) = File(dir(context), safe(id) + ".cap.jpg")

    fun resolveShipped(context: Context, packageDir: File, id: String, base64: String?): String? {
        val thumb = File(packageDir, "$id.shammthumb")
        if (thumb.isFile && thumb.length() in 1..MAX_POSTER_BYTES) return thumb.absolutePath
        if (!base64.isNullOrBlank()) {
            val out = File(dir(context), safe(id) + ".meta.jpg")
            if (out.isFile && out.length() > 0) return out.absolutePath
            try {
                val bytes = Base64.decode(base64, Base64.DEFAULT)
                if (bytes.size in 1..MAX_POSTER_BYTES) {
                    out.writeBytes(bytes)
                    return out.absolutePath
                }
            } catch (_: Exception) {
            }
        }
        return null
    }

    /** Best available poster file for a protected item, or null. */
    fun bestFile(context: Context, id: String, shipped: String?): File? {
        shipped?.let { File(it) }?.takeIf { it.isFile }?.let { return it }
        return capturedFile(context, id).takeIf { it.isFile && it.length() > 0 }
    }

    fun decode(file: File, maxW: Int): Bitmap? = try {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.absolutePath, bounds)
        var sample = 1
        while (bounds.outWidth / (sample * 2) >= maxW) sample *= 2
        BitmapFactory.decodeFile(file.absolutePath, BitmapFactory.Options().apply { inSampleSize = sample })
    } catch (_: Throwable) {
        null
    }

    /** Stores a downscaled (max 320 px wide) JPEG captured from playback. Skips black frames. */
    fun saveCaptured(context: Context, id: String, frame: Bitmap): Boolean {
        if (isMostlyBlack(frame)) return false
        val w = 320
        val h = (frame.height * (w.toFloat() / frame.width)).toInt().coerceAtLeast(1)
        return try {
            val scaled = Bitmap.createScaledBitmap(frame, w, h, true)
            FileOutputStream(capturedFile(context, id)).use { scaled.compress(Bitmap.CompressFormat.JPEG, 80, it) }
            if (scaled != frame) scaled.recycle()
            ThumbLoader.invalidate(id)
            true
        } catch (_: Throwable) {
            false
        }
    }

    private fun isMostlyBlack(b: Bitmap): Boolean {
        var bright = 0
        var total = 0
        val stepX = (b.width / 16).coerceAtLeast(1)
        val stepY = (b.height / 9).coerceAtLeast(1)
        var y = 0
        while (y < b.height) {
            var x = 0
            while (x < b.width) {
                val c = b.getPixel(x, y)
                val l = ((c shr 16 and 0xFF) + (c shr 8 and 0xFF) + (c and 0xFF)) / 3
                if (l > 24) bright++
                total++
                x += stepX
            }
            y += stepY
        }
        return total == 0 || bright * 10 < total // < 10% non-dark samples
    }
}
