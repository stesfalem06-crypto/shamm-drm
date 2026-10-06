package com.shammapps.xama.data

/**
 * Process-wide snapshot of the last library scan so secondary screens
 * (player playlist, reels, audio queue) don't have to rescan MediaStore on
 * the main thread. Falls back gracefully when empty (e.g. after process death).
 */
object LibraryCache {
    @Volatile
    var items: List<LocalVideo> = emptyList()
        private set

    @Volatile
    var lastScanMs: Long = 0L
        private set

    fun update(list: List<LocalVideo>) {
        items = list
        lastScanMs = System.currentTimeMillis()
    }

    fun byIds(ids: List<String>): List<LocalVideo> {
        if (ids.isEmpty()) return emptyList()
        val map = items.associateBy { it.id }
        return ids.mapNotNull { map[it] }
    }
}
