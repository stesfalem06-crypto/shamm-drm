package com.shammapps.xama.share

import android.content.Context
import android.os.Environment
import com.shammapps.xama.storage.XamaStorage
import org.json.JSONObject
import java.io.File

/**
 * Family group file written by X Seller (docs/FAMILY_GROUP.md). Display + share hint only:
 * it decides who protected packages may be SENT to; playback still needs a matching .shammkey entry.
 */
data class Family(
    val groupId: Long,
    val shopName: String,
    val status: String,
    val deviceId: String,
    val members: List<Member>,
    val phonesMax: Int,
    val source: String,
) {
    data class Member(val deviceId: String, val model: String)

    val active get() = status.equals("active", true)

    fun isMember(id: String) = active && members.any { it.deviceId.equals(id, true) }
    fun others(me: String) = if (active) members.filterNot { it.deviceId.equals(me, true) } else emptyList()

    companion object {
        fun files(ctx: Context) = listOf(
            File(XamaStorage.mediaDir(ctx), "xama-family.json"),
            File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "Xama/.xama-family.json"),
        )

        /** This phone's family (deviceId must match), newest file wins. */
        fun load(ctx: Context, myId: String): Family? = files(ctx)
            .filter { runCatching { it.isFile && it.length() < 256 * 1024 }.getOrDefault(false) }
            .sortedByDescending { it.lastModified() }
            .firstNotNullOfOrNull { f -> runCatching { parse(f.readText()) }.getOrNull() }
            ?.takeIf { it.deviceId.isEmpty() || it.deviceId.equals(myId, true) }

        fun parse(text: String): Family? {
            val o = JSONObject(text)
            val arr = o.optJSONArray("members") ?: return null
            val members = (0 until arr.length()).mapNotNull { i ->
                val m = arr.optJSONObject(i) ?: return@mapNotNull null
                val id = m.optString("deviceId").trim().lowercase()
                if (id.length != 64) null else Member(id, m.optString("model", "Phone"))
            }
            return Family(
                groupId = o.optLong("groupId"),
                shopName = o.optString("shopName", ""),
                status = o.optString("status", "active"),
                deviceId = o.optString("deviceId", "").trim().lowercase(),
                members = members,
                phonesMax = o.optInt("phonesMax", 3),
                source = o.optString("source", ""),
            )
        }
    }
}
