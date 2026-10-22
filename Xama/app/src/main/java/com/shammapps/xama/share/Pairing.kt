package com.shammapps.xama.share

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject

/**
 * Fixed share partners for PLAIN files: at most 2 phones, keyed by Xama deviceId
 * (SHA-256(androidId|abi) hex, as in xama-device.json). Unpairing only after a 30-day cooldown.
 */
object Pairing {
    const val MAX = 2
    const val COOLDOWN_MS = 30L * 24 * 60 * 60 * 1000
    private const val PREFS = "xama_share"
    private const val KEY = "peers"

    data class Peer(val deviceId: String, val name: String, val pairedAt: Long)

    fun peers(ctx: Context): List<Peer> = runCatching {
        val a = JSONArray(ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY, "[]"))
        (0 until a.length()).map { i ->
            val o = a.getJSONObject(i)
            Peer(o.getString("d"), o.optString("n", "Phone"), o.optLong("t"))
        }
    }.getOrDefault(emptyList())

    private fun save(ctx: Context, list: List<Peer>) {
        val a = JSONArray()
        list.forEach { a.put(JSONObject().put("d", it.deviceId).put("n", it.name).put("t", it.pairedAt)) }
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString(KEY, a.toString()).apply()
    }

    fun isPaired(ctx: Context, id: String) = peers(ctx).any { it.deviceId.equals(id, true) }
    fun hasRoomFor(ctx: Context, id: String) = isPaired(ctx, id) || peers(ctx).size < MAX

    /** Adds the peer if there's room. Returns false when already full with other phones. */
    fun add(ctx: Context, id: String, name: String): Boolean {
        val list = peers(ctx)
        if (list.any { it.deviceId.equals(id, true) }) return true
        if (list.size >= MAX) return false
        save(ctx, list + Peer(id.lowercase(), name, System.currentTimeMillis()))
        return true
    }

    fun unpairAvailableAt(p: Peer) = p.pairedAt + COOLDOWN_MS
    fun canUnpair(p: Peer, now: Long = System.currentTimeMillis()) = now >= unpairAvailableAt(p)

    fun remove(ctx: Context, id: String): Boolean {
        val p = peers(ctx).firstOrNull { it.deviceId.equals(id, true) } ?: return false
        if (!canUnpair(p)) return false
        save(ctx, peers(ctx).filterNot { it.deviceId.equals(id, true) })
        return true
    }
}
