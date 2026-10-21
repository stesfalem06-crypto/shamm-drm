package com.shammapps.xama.pass

import android.content.Context
import android.os.Environment
import org.json.JSONObject
import java.io.File
import java.text.SimpleDateFormat
import java.util.Locale
import java.util.TimeZone

/**
 * Display-only reader for the weekly pass file written by X Seller.
 * Not part of the DRM path: it never gates playback; it only shows a countdown.
 */
data class WeeklyPass(
    val shopName: String,
    val startMs: Long,
    val expiryMs: Long,
    val phonesUsed: Int,
    val phonesMax: Int,
    val signature: String,
) {
    enum class State { NONE, ACTIVE, LAST_DAY, EXPIRED }

    fun state(now: Long = System.currentTimeMillis()): State = when {
        now >= expiryMs -> State.EXPIRED
        expiryMs - now <= DAY -> State.LAST_DAY
        else -> State.ACTIVE
    }

    fun daysLeft(now: Long = System.currentTimeMillis()): Int =
        if (now >= expiryMs) 0 else ((expiryMs - now + DAY - 1) / DAY).toInt()

    companion object {
        private const val DAY = 24L * 60 * 60 * 1000

        fun candidates(ctx: Context): List<File> {
            val ext = Environment.getExternalStorageDirectory()
            return listOf(
                File(ext, "Android/media/${ctx.packageName}/xama-pass.json"),
                File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "Xama/.xama-pass.json"),
            )
        }

        /** Newest readable pass (latest expiry) or null. */
        fun load(ctx: Context): WeeklyPass? = candidates(ctx).mapNotNull { f ->
            runCatching { if (f.isFile && f.length() < 64 * 1024) parse(f.readText()) else null }.getOrNull()
        }.maxByOrNull { it.expiryMs }

        fun parse(text: String): WeeklyPass? {
            val o = JSONObject(text)
            val expiry = parseTime(o.opt("expiryUtc")) ?: return null
            return WeeklyPass(
                shopName = o.optString("shopName", "").trim().ifEmpty { "your shop" },
                startMs = parseTime(o.opt("startUtc")) ?: 0L,
                expiryMs = expiry,
                phonesUsed = o.optInt("phonesUsed", 0),
                phonesMax = o.optInt("phonesMax", 3),
                signature = o.optString("signature", ""),
            )
        }

        private fun parseTime(v: Any?): Long? {
            if (v == null || v == JSONObject.NULL) return null
            if (v is Number) return v.toLong().let { if (it < 100_000_000_000L) it * 1000 else it }
            val s = v.toString().trim()
            s.toLongOrNull()?.let { return if (it < 100_000_000_000L) it * 1000 else it }
            for (p in listOf("yyyy-MM-dd'T'HH:mm:ss.SSSX", "yyyy-MM-dd'T'HH:mm:ssX", "yyyy-MM-dd'T'HH:mmX", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd")) {
                runCatching {
                    val f = SimpleDateFormat(p, Locale.US).apply { timeZone = TimeZone.getTimeZone("UTC"); isLenient = false }
                    return f.parse(s)!!.time
                }
            }
            return null
        }
    }
}
