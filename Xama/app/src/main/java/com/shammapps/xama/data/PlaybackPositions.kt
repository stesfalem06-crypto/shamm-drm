package com.shammapps.xama.data

import android.content.Context

/** Remembers where the user stopped in each item (resume + continue-watching progress). */
object PlaybackPositions {
    private const val PREFS = "xama_positions"
    private const val MAX_ENTRIES = 300

    data class Entry(val positionMs: Long, val durationMs: Long) {
        val fraction: Float
            get() = if (durationMs > 0) (positionMs.toFloat() / durationMs).coerceIn(0f, 1f) else 0f
    }

    fun save(context: Context, id: String, positionMs: Long, durationMs: Long) {
        if (id.isBlank()) return
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val nearEnd = durationMs > 0 && positionMs >= durationMs - 5_000
        val editor = prefs.edit()
        if (nearEnd || positionMs < 3_000) {
            // Finished (or barely started): nothing to resume.
            editor.remove(id)
        } else {
            editor.putString(id, "$positionMs:$durationMs:${System.currentTimeMillis()}")
        }
        editor.apply()
        if (prefs.all.size > MAX_ENTRIES) trim(context)
    }

    fun get(context: Context, id: String): Entry? {
        val raw = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(id, null) ?: return null
        val parts = raw.split(':')
        val pos = parts.getOrNull(0)?.toLongOrNull() ?: return null
        val dur = parts.getOrNull(1)?.toLongOrNull() ?: 0L
        return Entry(pos, dur)
    }

    fun clear(context: Context, id: String) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().remove(id).apply()
    }

    private fun trim(context: Context) {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val sorted = prefs.all.entries.sortedBy {
            (it.value as? String)?.split(':')?.getOrNull(2)?.toLongOrNull() ?: 0L
        }
        val editor = prefs.edit()
        sorted.take((sorted.size - MAX_ENTRIES).coerceAtLeast(0)).forEach { editor.remove(it.key) }
        editor.apply()
    }
}
