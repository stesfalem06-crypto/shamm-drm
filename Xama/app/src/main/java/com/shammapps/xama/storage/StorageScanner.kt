package com.shammapps.xama.storage

import android.content.Context
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken
import java.io.File

/**
 * Fast, incremental shared-storage walker.
 *
 * Each directory's listing is cached (keyed by path + lastModified). A directory's
 * mtime changes whenever an entry is added/removed/renamed inside it, so unchanged
 * directories are served from the cache without listing them again. Only one stat
 * per directory is needed on repeat scans.
 *
 * Receive folders used by SHAREit / Xender / Bluetooth / WhatsApp / Telegram /
 * Nearby/Quick Share / ShareMe / Zapya are walked first so the file cap never cuts
 * them off.
 */
class StorageScanner(private val context: Context) {

    data class Found(val file: File, val folderLabel: String, val size: Long, val mtime: Long)

    @androidx.annotation.Keep
    private data class DirEntry(
        val mtime: Long,
        val dirs: List<String>,
        val files: List<FileEntry>,
    )

    @androidx.annotation.Keep
    private data class FileEntry(val name: String, val size: Long, val mtime: Long)

    companion object {
        private const val CACHE_FILE = "storage_scan_cache.json"
        private val lock = Any()
        @Volatile private var memCache: HashMap<String, DirEntry>? = null

        /** Relative to the shared-storage root. Missing folders are simply skipped. */
        val RECEIVE_FOLDERS = listOf(
            // SHAREit (all variants)
            "SHAREit", "SHAREit/videos", "SHAREit/files", "SHAREit/audios", "SHAREit/apps",
            "SHAREit Lite", "Download/SHAREit", "Download/SHAREit Lite",
            "Android/media/com.lenovo.anyshare.gps/SHAREit",
            "Android/media/com.lenovo.anyshare.gps",
            "Android/media/com.lenovo.anyshare",
            "Android/media/shareit.lite",
            "Android/media/shareit.premium",
            "Android/media/com.lenovo.anyshare.lite",
            // Xender
            "Xender", "Download/Xender", "Android/media/cn.xender",
            // Bluetooth / Nearby / Quick Share
            "Bluetooth", "Download/Bluetooth", "Download/Nearby Share", "Download/Quick Share",
            // ShareMe (Xiaomi), Zapya
            "ShareMe", "MIUI/ShareMe", "Download/ShareMe", "Android/media/com.xiaomi.midrop",
            "Zapya", "Android/media/com.dewmobile.kuaiya.play",
            // Messengers
            "WhatsApp/Media", "Android/media/com.whatsapp/WhatsApp/Media",
            "Android/media/com.whatsapp.w4b/WhatsApp Business/Media",
            "Telegram", "Download/Telegram", "Android/media/org.telegram.messenger",
            "imo", "Android/media/com.imo.android.imoim",
        )

        /** Never descend into these (huge, no user media). */
        private val SKIP_DIRS = setOf(".thumbnails", ".Trash", "Android/obb", ".cache", "cache", "LOST.DIR")

        fun labelFor(path: String, dirName: String): String {
            val p = path.lowercase()
            return when {
                "shareit" in p || "anyshare" in p -> "SHAREit"
                "xender" in p -> "Xender"
                "bluetooth" in p -> "Bluetooth"
                "whatsapp" in p -> "WhatsApp"
                "telegram" in p -> "Telegram"
                "shareme" in p || "midrop" in p -> "ShareMe"
                "zapya" in p || "kuaiya" in p -> "Zapya"
                "nearby share" in p || "quick share" in p -> "Quick Share"
                else -> dirName.ifBlank { "Other" }
            }
        }
    }

    private val gson = Gson()

    private fun loadCache(): HashMap<String, DirEntry> {
        memCache?.let { return it }
        val f = File(context.filesDir, CACHE_FILE)
        val map: HashMap<String, DirEntry> = try {
            if (f.exists()) gson.fromJson(f.readText(), object : TypeToken<HashMap<String, DirEntry>>() {}.type)
                ?: HashMap() else HashMap()
        } catch (_: Throwable) {
            HashMap()
        }
        memCache = map
        return map
    }

    private fun saveCache(map: HashMap<String, DirEntry>) {
        try {
            val tmp = File(context.filesDir, "$CACHE_FILE.tmp")
            tmp.writeText(gson.toJson(map))
            tmp.renameTo(File(context.filesDir, CACHE_FILE))
        } catch (_: Throwable) {
        }
    }

    /**
     * Walks [roots] (absolute) breadth-first. Calls [accept] on every file name to
     * decide whether it is interesting (media or protected package).
     */
    fun walk(
        roots: List<File>,
        accept: (String) -> Boolean,
        maxFiles: Int = 50_000,
        maxDepth: Int = 16,
    ): List<Found> = synchronized(lock) {
        val cache = loadCache()
        val fresh = HashMap<String, DirEntry>(cache.size.coerceAtLeast(256))
        val out = ArrayList<Found>()
        val visited = HashSet<String>()
        val rootPath = XamaStorage.root().absolutePath
        val queue = ArrayDeque<Pair<File, Int>>()
        roots.forEach { queue.addLast(it to 0) }
        var changed = false

        while (queue.isNotEmpty() && out.size < maxFiles) {
            val (dir, depth) = queue.removeFirst()
            val path = dir.absolutePath
            if (!visited.add(path)) continue
            val rel = path.removePrefix(rootPath).trimStart('/')
            if (dir.name in SKIP_DIRS || rel in SKIP_DIRS) continue
            val mtime = dir.lastModified()
            if (mtime == 0L) continue // missing / unreadable
            var entry = cache[path]
            if (entry == null || entry.mtime != mtime) {
                val children = try { dir.listFiles() } catch (_: Exception) { null } ?: continue
                val dirs = ArrayList<String>()
                val files = ArrayList<FileEntry>()
                for (c in children) {
                    if (c.isDirectory) dirs.add(c.name)
                    else if (accept(c.name)) files.add(FileEntry(c.name, c.length(), c.lastModified()))
                }
                entry = DirEntry(mtime, dirs, files)
                changed = true
            }
            fresh[path] = entry
            val label = labelFor(path, dir.name)
            for (f in entry.files) {
                if (f.size <= 0) continue
                out.add(Found(File(dir, f.name), label, f.size, f.mtime))
                if (out.size >= maxFiles) break
            }
            if (depth < maxDepth) entry.dirs.forEach { queue.addLast(File(dir, it) to depth + 1) }
        }
        // Keep cache entries for parts not visited this time (e.g. receive-only scan).
        for ((k, v) in cache) if (k !in fresh) fresh[k] = v
        if (changed || fresh.size != cache.size) {
            memCache = fresh
            saveCache(fresh)
        }
        out
    }

    fun receiveRoots(): List<File> {
        val root = XamaStorage.root()
        return RECEIVE_FOLDERS.map { File(root, it) }
    }
}
