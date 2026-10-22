package com.shammapps.xama.data

import android.content.Context

/** Local, offline Likes and Favorites (ids only, stored in SharedPreferences). */
object Favorites {
    private const val PREFS = "xama_favs"
    const val LIKED = "liked"
    const val FAV = "fav"

    private fun ids(ctx: Context, key: String): LinkedHashSet<String> =
        LinkedHashSet(ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(key, "")!!
            .split('\n').filter { it.isNotBlank() })

    private fun save(ctx: Context, key: String, set: Collection<String>) =
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString(key, set.joinToString("\n")).apply()

    fun has(ctx: Context, key: String, id: String) = id in ids(ctx, key)
    fun isLiked(ctx: Context, id: String) = has(ctx, LIKED, id)
    fun isFav(ctx: Context, id: String) = has(ctx, FAV, id)

    /** Toggles and returns the new state. Newest first. */
    fun toggle(ctx: Context, key: String, id: String): Boolean {
        val s = ids(ctx, key)
        val on = if (s.remove(id)) false else true
        val out = if (on) listOf(id) + s else s.toList()
        save(ctx, key, out)
        return on
    }

    fun set(ctx: Context, key: String, id: String, on: Boolean) {
        if (has(ctx, key, id) != on) toggle(ctx, key, id)
    }

    fun list(ctx: Context, key: String): List<String> = ids(ctx, key).toList()

    fun <T> resolve(ctx: Context, key: String, all: List<T>, idOf: (T) -> String): List<T> {
        val byId = all.associateBy(idOf)
        return list(ctx, key).mapNotNull { byId[it] }
    }
}
