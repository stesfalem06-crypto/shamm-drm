package com.shammapps.xama.data

import android.content.Context

/** Lightweight continue-watching list (most recent first, max 20). */
object WatchHistory {
    private const val PREFS = "xama_watch"
    private const val KEY = "recent_ids"

    fun record(context: Context, videoId: String) {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val current = prefs.getString(KEY, "")!!.split(',').filter { it.isNotBlank() }.toMutableList()
        current.remove(videoId)
        current.add(0, videoId)
        while (current.size > 20) current.removeAt(current.lastIndex)
        prefs.edit().putString(KEY, current.joinToString(",")).apply()
    }

    fun recentIds(context: Context): List<String> {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        return prefs.getString(KEY, "")!!.split(',').filter { it.isNotBlank() }
    }

    fun resolve(context: Context, all: List<LocalVideo>): List<LocalVideo> {
        val byId = all.associateBy { it.id }
        return recentIds(context).mapNotNull { byId[it] }
    }
}
