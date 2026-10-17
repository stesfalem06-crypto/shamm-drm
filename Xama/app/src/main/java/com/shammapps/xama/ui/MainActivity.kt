package com.shammapps.xama.ui

import android.Manifest
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.text.Editable
import android.text.TextWatcher
import android.view.View
import android.view.animation.AnimationUtils
import android.view.inputmethod.InputMethodManager
import android.widget.EditText
import android.widget.ImageButton
import android.widget.ImageView
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
import androidx.activity.OnBackPressedCallback
import androidx.activity.result.contract.ActivityResultContracts
import androidx.annotation.OptIn
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.media3.common.C
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import androidx.recyclerview.widget.GridLayoutManager
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.google.common.util.concurrent.ListenableFuture
import com.shammapps.xama.BuildConfig
import com.shammapps.xama.R
import com.shammapps.xama.data.LibraryCache
import com.shammapps.xama.data.LocalVideo
import com.shammapps.xama.data.VideoFolder
import com.shammapps.xama.data.VideoRepository
import com.shammapps.xama.data.WatchHistory
import com.shammapps.xama.player.PlaybackService
import java.util.concurrent.Executors

/**
 * Library home in the classic local-player style shop customers already know:
 * folder list -> video list (thumbnail, duration, size, resolution, NEW badge),
 * list/grid toggle, sort and search in the toolbar, docked bottom navigation
 * (Videos / Music / Protected / Me).
 */
@OptIn(UnstableApi::class)
class MainActivity : AppCompatActivity() {

    companion object {
        const val EXTRA_VIDEO_ID = "video_id"
        const val EXTRA_TITLE = "title"
        const val EXTRA_FILE_PATH = "file_path"
        const val EXTRA_CONTENT_URI = "content_uri"
        const val EXTRA_IS_ENCRYPTED = "is_encrypted"
        const val EXTRA_IV_BASE64 = "iv_base64"
        const val EXTRA_START_INDEX = "start_index"

        private const val STATE_TAB = "tab"
        private const val STATE_FOLDER = "folder"
        private const val STATE_QUERY = "query"
        private const val PREFS = "xama_ui"
    }

    private enum class Tab { VIDEOS, REELS, MUSIC, PROTECTED, PROFILE }
    private enum class Sort(val label: String) { NAME("Name"), DATE("Date"), SIZE("Size"), LENGTH("Length") }

    private lateinit var repository: VideoRepository
    private var allItems: List<LocalVideo> = emptyList()
    private var videos: List<LocalVideo> = emptyList()
    private var audio: List<LocalVideo> = emptyList()
    private var folders: List<VideoFolder> = emptyList()
    private var tab = Tab.VIDEOS
    private var query = ""
    private var searchOpen = false
    private var openFolder: VideoFolder? = null
    /** Drilled-in music folder (Music > Folders). */
    private var openMusicFolder: VideoFolder? = null
    private var musicFolders: List<VideoFolder> = emptyList()
    /** Per-screen List / Thumbnails choice for video lists. */
    private fun videoScreenKey(): String = when {
        tab == Tab.PROTECTED -> "protected"
        tab == Tab.REELS -> "reels"
        openFolder != null -> "folder"
        else -> "search"
    }
    private fun cardsFor(key: String = videoScreenKey()) = prefs.getBoolean("cards_$key", false)
    private fun isVideoListScreen(): Boolean =
        tab == Tab.PROTECTED || (tab == Tab.REELS && !reelsByFolder) ||
            (tab == Tab.VIDEOS && (openFolder != null || query.isNotEmpty()))
    private var reelsByFolder = false
    private var musicByFolder = false
    private var pendingFolderName: String? = null
    private var gridMode = false
    private var sort = Sort.DATE
    private var sortDescending = true
    private var loading = false
    private var hasLoadedOnce = false
    private var scanStarted = false
    private var signature = ""
    private val loadExecutor = Executors.newSingleThreadExecutor()
    private val handler = Handler(Looper.getMainLooper())

    // Mini player
    private var controllerFuture: ListenableFuture<MediaController>? = null
    private var controller: MediaController? = null
    private var miniArtKey: String? = null
    private val miniTicker = object : Runnable {
        override fun run() {
            updateMiniProgress()
            handler.postDelayed(this, 1000)
        }
    }
    private val miniListener = object : Player.Listener {
        override fun onEvents(player: Player, events: Player.Events) = bindMiniPlayer()
    }

    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { result ->
        if (result.values.none { it }) {
            Toast.makeText(this, "Allow media access to browse videos and music on this phone.", Toast.LENGTH_LONG).show()
        }
        reloadAsync(force = true)
    }

    private val prefs by lazy { getSharedPreferences(PREFS, Context.MODE_PRIVATE) }

    // ---------------------------------------------------------------- lifecycle

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        repository = VideoRepository(this)
        gridMode = prefs.getBoolean("grid_mode", false)
        sort = Sort.values().getOrElse(prefs.getInt("sort", Sort.DATE.ordinal)) { Sort.DATE }
        sortDescending = prefs.getBoolean("sort_desc", true)
        reelsByFolder = prefs.getBoolean("reels_by_folder", false)
        musicByFolder = prefs.getBoolean("music_by_folder", false)

        savedInstanceState?.let { s ->
            tab = Tab.values().getOrElse(s.getInt(STATE_TAB, 0)) { Tab.VIDEOS }
            pendingFolderName = s.getString(STATE_FOLDER)
            query = s.getString(STATE_QUERY).orEmpty()
            searchOpen = query.isNotEmpty()
        }

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                when {
                    searchOpen -> closeSearch()
                    openMusicFolder != null -> {
                        openMusicFolder = null
                        render(animate = true)
                    }
                    openFolder != null -> {
                        openFolder = null
                        render(animate = true)
                    }
                    tab != Tab.VIDEOS -> switchTab(Tab.VIDEOS)
                    else -> {
                        isEnabled = false
                        onBackPressedDispatcher.onBackPressed()
                    }
                }
            }
        })

        val search = findViewById<EditText>(R.id.searchInput)
        if (query.isNotEmpty()) search.setText(query)
        search.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {
                query = s?.toString()?.trim().orEmpty()
                render()
            }
            override fun afterTextChanged(s: Editable?) {}
        })

        findViewById<View>(R.id.btnSearch).setOnClickListener { if (searchOpen) closeSearch() else openSearch() }
        findViewById<View>(R.id.btnViewMode).setOnClickListener { toggleViewMode() }
        findViewById<View>(R.id.btnSort).setOnClickListener { showSortSheet() }
        findViewById<View>(R.id.btnMore).setOnClickListener { showMoreSheet() }
        findViewById<View>(R.id.btnBackFolder).setOnClickListener { onBackPressedDispatcher.onBackPressed() }

        findViewById<View>(R.id.navVideos).setOnClickListener { switchTab(Tab.VIDEOS) }
        findViewById<View>(R.id.navReels).setOnClickListener { switchTab(Tab.REELS) }
        findViewById<View>(R.id.modeAll).setOnClickListener { setByFolder(false) }
        findViewById<View>(R.id.modeFolders).setOnClickListener { setByFolder(true) }
        findViewById<View>(R.id.modePlay).setOnClickListener { openReels(reelsFeed(videos), null) }
        findViewById<View>(R.id.rowScanAll).setOnClickListener { toggleScanAll() }
        findViewById<View>(R.id.navMusic).setOnClickListener { switchTab(Tab.MUSIC) }
        findViewById<View>(R.id.navProtected).setOnClickListener { switchTab(Tab.PROTECTED) }
        findViewById<View>(R.id.navProfile).setOnClickListener { switchTab(Tab.PROFILE) }

        findViewById<View>(R.id.rowAbout).setOnClickListener { openAbout() }
        findViewById<View>(R.id.rowUsbSetup).setOnClickListener { openUsbSetup() }
        findViewById<View>(R.id.rowRescan).setOnClickListener { rescan() }
        findViewById<View>(R.id.rowClearHistory).setOnClickListener {
            WatchHistory.clear(this)
            Toast.makeText(this, "Watch history cleared", Toast.LENGTH_SHORT).show()
            render()
        }
        findViewById<TextView>(R.id.profileVersion).text = "v${BuildConfig.VERSION_NAME}"

        findViewById<View>(R.id.miniBody).setOnClickListener {
            startActivity(Intent(this, AudioPlayerActivity::class.java))
        }
        findViewById<View>(R.id.miniPlayPause).setOnClickListener {
            controller?.let { if (it.playWhenReady) it.pause() else { if (it.playbackState == Player.STATE_IDLE) it.prepare(); it.play() } }
        }
        findViewById<View>(R.id.miniNext).setOnClickListener { controller?.seekToNext() }

        updateNav()
        updateToolbar()

        if (LibraryCache.items.isNotEmpty()) applyLibrary(LibraryCache.items)

        // Rewrite every identity-file copy on each launch (+ media scan) so X Seller sees it over MTP.
        if (savedInstanceState == null) {
            val app = applicationContext
            loadExecutor.execute {
                try { com.shammapps.xama.storage.XamaStorage.writeDeviceIdentity(app, force = true) } catch (_: Exception) {}
            }
        }

        // First run: show the USB / shop-PC setup guide before asking for media access.
        if (savedInstanceState == null && !OnboardingActivity.isDone(this)) {
            startActivity(Intent(this, OnboardingActivity::class.java))
        } else {
            startScan()
        }
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        outState.putInt(STATE_TAB, tab.ordinal)
        outState.putString(STATE_FOLDER, openFolder?.name)
        outState.putString(STATE_QUERY, query)
    }

    override fun onStart() {
        super.onStart()
        connectMiniPlayer()
    }

    override fun onResume() {
        super.onResume()
        if (!scanStarted) {
            if (OnboardingActivity.isDone(this)) startScan()
            return
        }
        if (tab == Tab.PROFILE) bindScanAll()
        if (hasLoadedOnce && System.currentTimeMillis() - LibraryCache.lastScanMs > 1500) {
            reloadAsync(force = false)
        } else if (hasLoadedOnce) {
            refreshLight()
        }
    }

    override fun onStop() {
        super.onStop()
        releaseMiniPlayer()
    }

    override fun onDestroy() {
        super.onDestroy()
        handler.removeCallbacksAndMessages(null)
        loadExecutor.shutdown()
    }

    // ---------------------------------------------------------------- loading

    private fun startScan() {
        if (scanStarted) return
        scanStarted = true
        ensurePermission()
    }

    private fun mediaPermissions(): Array<String> =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            arrayOf(Manifest.permission.READ_MEDIA_VIDEO, Manifest.permission.READ_MEDIA_AUDIO)
        } else {
            arrayOf(Manifest.permission.READ_EXTERNAL_STORAGE)
        }

    private fun missingPermissions(): List<String> = mediaPermissions().filter {
        ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED
    }

    private fun ensurePermission() {
        val missing = missingPermissions()
        reloadAsync(force = true)
        if (missing.isNotEmpty()) permissionLauncher.launch(missing.toTypedArray())
    }

    private var reloadQueued = false

    private fun rescan() {
        Toast.makeText(this, "Rescanning…", Toast.LENGTH_SHORT).show()
        reloadAsync(force = true)
    }

    private fun reloadAsync(force: Boolean) {
        if (loading) {
            if (force) reloadQueued = true
            return
        }
        loading = true
        if (allItems.isEmpty()) render()
        loadExecutor.execute {
            try { com.shammapps.xama.storage.XamaStorage.writeDeviceIdentity(applicationContext) } catch (_: Exception) {}
            val items = try { repository.loadAll() } catch (_: Exception) { emptyList() }
            runOnUiThread {
                loading = false
                if (isFinishing || isDestroyed) return@runOnUiThread
                val newSig = items.joinToString("|") { it.id + ":" + it.title }
                val changed = newSig != signature || !hasLoadedOnce || force
                hasLoadedOnce = true
                LibraryCache.update(items)
                if (changed) applyLibrary(items) else refreshLight()
                if (reloadQueued) {
                    reloadQueued = false
                    reloadAsync(force = true)
                }
            }
        }
    }

    private fun applyLibrary(items: List<LocalVideo>) {
        allItems = items
        signature = items.joinToString("|") { it.id + ":" + it.title }
        videos = items.filter { !it.isAudio }
        audio = items.filter { it.isAudio }
        folders = try { repository.loadFolders(videos) } catch (_: Exception) { emptyList() }
        musicFolders = audio.groupBy { it.folderName.ifBlank { "Other" } }
            .map { (name, list) -> VideoFolder(name, list.size, list) }
            .sortedBy { it.name.lowercase() }
        openMusicFolder = openMusicFolder?.let { f -> musicFolders.find { it.name == f.name } }
        openFolder = openFolder?.let { f -> folders.find { it.name == f.name } }
        pendingFolderName?.let { name ->
            openFolder = folders.find { it.name == name }
            pendingFolderName = null
        }
        render()
    }

    /** Refresh progress bars / NEW badges without rebuilding lists (keeps scroll). */
    private fun refreshLight() {
        findViewById<RecyclerView>(R.id.contentList).adapter?.notifyDataSetChanged()
    }

    // ---------------------------------------------------------------- toolbar / navigation

    private fun openSearch() {
        searchOpen = true
        updateToolbar()
        val input = findViewById<EditText>(R.id.searchInput)
        input.requestFocus()
        (getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager)
            .showSoftInput(input, InputMethodManager.SHOW_IMPLICIT)
    }

    private fun closeSearch() {
        val input = findViewById<EditText>(R.id.searchInput)
        (getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager)
            .hideSoftInputFromWindow(input.windowToken, 0)
        searchOpen = false
        input.setText("")
        updateToolbar()
    }

    private fun updateToolbar() {
        val inList = tab != Tab.PROFILE
        findViewById<View>(R.id.titleBlock).visibility = if (searchOpen) View.GONE else View.VISIBLE
        findViewById<View>(R.id.searchInput).visibility = if (searchOpen) View.VISIBLE else View.GONE
        findViewById<ImageButton>(R.id.btnSearch).apply {
            visibility = if (inList) View.VISIBLE else View.GONE
            setImageResource(if (searchOpen) R.drawable.ic_close else R.drawable.ic_search)
        }
        val videoScreen = tab == Tab.VIDEOS || tab == Tab.PROTECTED || tab == Tab.REELS
        findViewById<ImageButton>(R.id.btnViewMode).apply {
            visibility = if (videoScreen && !searchOpen) View.VISIBLE else View.GONE
            val altOn = if (isVideoListScreen()) cardsFor() else gridMode
            setImageResource(if (altOn) R.drawable.ic_list else R.drawable.ic_grid)
            contentDescription = if (isVideoListScreen()) "List / Thumbnails" else "List / Grid"
        }
        findViewById<View>(R.id.btnSort).visibility = if (inList && !searchOpen) View.VISIBLE else View.GONE
        findViewById<View>(R.id.btnBackFolder).visibility =
            if ((openFolder != null || openMusicFolder != null) && !searchOpen) View.VISIBLE else View.GONE
        findViewById<EditText>(R.id.searchInput).hint = when {
            tab == Tab.MUSIC -> "Search music"
            openFolder != null -> "Search in ${openFolder?.name}"
            else -> "Search videos"
        }
    }

    private fun switchTab(t: Tab) {
        if (t == tab && openFolder == null && openMusicFolder == null) {
            findViewById<RecyclerView>(R.id.contentList).smoothScrollToPosition(0)
            return
        }
        if (searchOpen) closeSearch()
        tab = t
        openFolder = null
        openMusicFolder = null
        updateNav()
        render(animate = true)
    }

    private fun openAbout() = startActivity(Intent(this, AboutActivity::class.java))
    private fun openUsbSetup() = startActivity(Intent(this, OnboardingActivity::class.java))

    private fun updateNav() {
        fun paint(icon: Int, label: Int, selected: Boolean) {
            val color = ContextCompat.getColor(this, if (selected) R.color.accent else R.color.text_muted)
            findViewById<ImageView>(icon).setColorFilter(color)
            findViewById<TextView>(label).setTextColor(color)
        }
        paint(R.id.navReelsIcon, R.id.navReelsLabel, tab == Tab.REELS)
        paint(R.id.navVideosIcon, R.id.navVideosLabel, tab == Tab.VIDEOS)
        paint(R.id.navMusicIcon, R.id.navMusicLabel, tab == Tab.MUSIC)
        paint(R.id.navProtectedIcon, R.id.navProtectedLabel, tab == Tab.PROTECTED)
        paint(R.id.navProfileIcon, R.id.navProfileLabel, tab == Tab.PROFILE)
    }

    /** Folder lists: List / Grid. Video lists: List / Thumbnails (remembered per screen). */
    private fun toggleViewMode() {
        if (isVideoListScreen()) {
            val key = videoScreenKey()
            val cards = !cardsFor(key)
            prefs.edit().putBoolean("cards_$key", cards).apply()
            Toast.makeText(this, if (cards) "Thumbnails" else "List", Toast.LENGTH_SHORT).show()
        } else {
            gridMode = !gridMode
            prefs.edit().putBoolean("grid_mode", gridMode).apply()
        }
        render(animate = true)
    }

    private fun showSortSheet() {
        val sheet = OptionSheet(this, "Sort by", if (sortDescending) "Descending" else "Ascending")
        Sort.values().forEach { s ->
            sheet.add(s.label, R.drawable.ic_sort, checked = s == sort) {
                if (sort == s) sortDescending = !sortDescending else {
                    sort = s
                    sortDescending = s != Sort.NAME
                }
                prefs.edit().putInt("sort", sort.ordinal).putBoolean("sort_desc", sortDescending).apply()
                render(animate = true)
            }
        }
        sheet.add(if (sortDescending) "Switch to ascending" else "Switch to descending", R.drawable.ic_refresh) {
            sortDescending = !sortDescending
            prefs.edit().putBoolean("sort_desc", sortDescending).apply()
            render(animate = true)
        }
        sheet.show()
    }

    private fun showMoreSheet() {
        OptionSheet(this, "Xama")
            .add("Refresh", R.drawable.ic_refresh) { rescan() }
            .add(
                if (isVideoListScreen()) (if (cardsFor()) "List view" else "Thumbnails view")
                else (if (gridMode) "List view" else "Grid view"),
                R.drawable.ic_grid
            ) { toggleViewMode() }
            .add("Connect to shop PC (USB setup)", R.drawable.ic_usb) { openUsbSetup() }
            .add("About", R.drawable.ic_info) { openAbout() }
            .show()
    }

    // ---------------------------------------------------------------- rendering

    /** Thumbnail cards: one column on phones, more on tablets / landscape. */
    private fun cardSpan(): Int = (resources.configuration.screenWidthDp / 360).coerceIn(1, 4)

    private fun gridSpan(): Int {
        val w = resources.configuration.screenWidthDp
        return (w / 170).coerceIn(2, 6)
    }

    private fun sortVideos(list: List<LocalVideo>): List<LocalVideo> {
        val sorted = when (sort) {
            Sort.NAME -> list.sortedBy { it.title.lowercase() }
            Sort.DATE -> list.sortedBy { it.dateAddedSec }
            Sort.SIZE -> list.sortedBy { it.sizeBytes }
            Sort.LENGTH -> list.sortedBy { it.durationMs }
        }
        return if (sortDescending) sorted.reversed() else sorted
    }

    private fun sortFolders(list: List<VideoFolder>): List<VideoFolder> {
        val sorted = when (sort) {
            Sort.NAME -> list.sortedBy { it.name.lowercase() }
            Sort.DATE -> list.sortedBy { f -> f.videos.maxOfOrNull { it.dateAddedSec } ?: 0L }
            Sort.SIZE -> list.sortedBy { f -> f.videos.sumOf { it.sizeBytes } }
            Sort.LENGTH -> list.sortedBy { it.videoCount }
        }
        return if (sortDescending) sorted.reversed() else sorted
    }

    private fun matches(v: LocalVideo): Boolean =
        query.isEmpty() || v.title.contains(query, true) || v.folderName.contains(query, true) ||
            v.artist.contains(query, true) || v.album.contains(query, true)

    private fun watchedIds(): Set<String> = WatchHistory.recentIds(this).toHashSet()

    private fun render(animate: Boolean = false) {
        val list = findViewById<RecyclerView>(R.id.contentList)
        val profile = findViewById<View>(R.id.profilePanel)
        val empty = findViewById<View>(R.id.emptyState)
        val loadingView = findViewById<View>(R.id.loadingState)
        val header = findViewById<TextView>(R.id.headerTitle)
        val sub = findViewById<TextView>(R.id.videoCountText)

        updateToolbar()
        updateModeBar()
        list.visibility = View.GONE
        profile.visibility = View.GONE
        empty.visibility = View.GONE
        loadingView.visibility = View.GONE
        sub.visibility = View.GONE

        if (loading && allItems.isEmpty() && tab != Tab.PROFILE) {
            header.text = getString(R.string.app_name)
            loadingView.visibility = View.VISIBLE
            return
        }

        val watched = watchedIds()
        val folder = openFolder
        if (folder != null && tab == Tab.VIDEOS) {
            header.text = folder.name
            sub.visibility = View.VISIBLE
            sub.text = if (folder.videoCount == 1) "1 video" else "${folder.videoCount} videos"
            showVideos(list, sortVideos(folder.videos.filter { matches(it) }), watched, animate)
            return
        }

        when (tab) {
            Tab.VIDEOS -> {
                header.text = getString(R.string.app_name)
                if (query.isNotEmpty()) {
                    // Search across all videos, like the classic players do.
                    showVideos(list, sortVideos(videos.filter { matches(it) }).take(500), watched, animate)
                    return
                }
                val folderList = sortFolders(folders)
                if (folderList.isEmpty()) {
                    showEmpty(emptyFor(Tab.VIDEOS))
                } else {
                    list.visibility = View.VISIBLE
                    list.layoutManager = if (gridMode) GridLayoutManager(this, gridSpan()) else LinearLayoutManager(this)
                    list.adapter = MxFolderAdapter(folderList, gridMode, watched) { f ->
                        if (searchOpen) closeSearch()
                        openFolder = f
                        render(animate = true)
                    }
                    if (animate) animateList(list)
                }
            }
            Tab.PROTECTED -> {
                header.text = "Protected"
                showVideos(list, sortVideos(videos.filter { it.isEncrypted && matches(it) }), watched, animate)
            }
            Tab.REELS -> {
                header.text = "Reels"
                if (reelsByFolder && query.isEmpty()) {
                    val fl = sortFolders(folders)
                    if (fl.isEmpty()) showEmpty(emptyFor(Tab.VIDEOS)) else {
                        list.visibility = View.VISIBLE
                        list.layoutManager = if (gridMode) GridLayoutManager(this, gridSpan()) else LinearLayoutManager(this)
                        // Tapping a folder plays that folder as a swipe feed.
                        list.adapter = MxFolderAdapter(fl, gridMode, watched) { f -> openReels(reelsFeed(f.videos), null) }
                        if (animate) animateList(list)
                    }
                } else {
                    val feed = reelsFeed(videos.filter { matches(it) })
                    if (feed.isEmpty()) showEmpty(emptyFor(Tab.VIDEOS)) else {
                        list.visibility = View.VISIBLE
                        val cards = cardsFor("reels")
                        list.layoutManager = if (cards) GridLayoutManager(this, cardSpan()) else LinearLayoutManager(this)
                        list.adapter = MxVideoAdapter(feed, cards, watched) { v -> openReels(feed, v) }
                        if (animate) animateList(list)
                    }
                }
            }
            Tab.MUSIC -> {
                val mf = openMusicFolder
                header.text = mf?.name ?: "Music"
                if (mf == null && musicByFolder && query.isEmpty()) {
                    if (musicFolders.isEmpty()) showEmpty(emptyFor(Tab.MUSIC)) else {
                        list.visibility = View.VISIBLE
                        list.layoutManager = LinearLayoutManager(this)
                        list.adapter = MxFolderAdapter(sortFolders(musicFolders), false, emptySet(), countLabel = "tracks") { f ->
                            openMusicFolder = f
                            render(animate = true)
                        }
                        if (animate) animateList(list)
                    }
                } else {
                    val source = mf?.videos ?: audio
                    val tracks = sortVideos(source.filter { matches(it) })
                    if (tracks.isEmpty()) {
                        showEmpty(emptyFor(Tab.MUSIC))
                    } else {
                        list.visibility = View.VISIBLE
                        list.layoutManager = LinearLayoutManager(this)
                        list.adapter = AudioAdapter(tracks, controller?.currentMediaItem?.mediaId) { index, shuffle ->
                            AudioPlayerActivity.start(this, tracks, index, shuffle)
                        }
                        if (animate) animateList(list)
                    }
                }
            }
            Tab.PROFILE -> {
                header.text = "Me"
                profile.visibility = View.VISIBLE
                findViewById<TextView>(R.id.statTotal).text = videos.size.toString()
                findViewById<TextView>(R.id.statMusic).text = audio.size.toString()
                findViewById<TextView>(R.id.statProtected).text = videos.count { it.isEncrypted }.toString()
                findViewById<TextView>(R.id.statFolders).text = folders.size.toString()
                bindScanAll()
                if (animate) profile.startAnimation(AnimationUtils.loadAnimation(this, R.anim.item_fade_up))
            }
        }
    }

    private fun showVideos(list: RecyclerView, items: List<LocalVideo>, watched: Set<String>, animate: Boolean) {
        if (items.isEmpty()) {
            showEmpty(emptyFor(tab))
            return
        }
        list.visibility = View.VISIBLE
        val cards = cardsFor()
        list.layoutManager = if (cards) GridLayoutManager(this, cardSpan()) else LinearLayoutManager(this)
        list.adapter = MxVideoAdapter(items, cards, watched) { openVideo(it, items) }
        if (animate) animateList(list)
    }

    private fun animateList(list: RecyclerView) {
        list.layoutAnimation = AnimationUtils.loadLayoutAnimation(this, R.anim.layout_fade_up)
        list.scheduleLayoutAnimation()
    }

    private data class Empty(val icon: Int, val title: String, val message: String, val action: String?, val onAction: (() -> Unit)?)

    private fun emptyFor(t: Tab): Empty {
        if (query.isNotEmpty()) {
            return Empty(R.drawable.ic_search, "No results", "Nothing matches “$query”.", "Clear search") { closeSearch() }
        }
        val needed = when {
            Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU -> Manifest.permission.READ_EXTERNAL_STORAGE
            t == Tab.MUSIC -> Manifest.permission.READ_MEDIA_AUDIO
            else -> Manifest.permission.READ_MEDIA_VIDEO
        }
        val missing = missingPermissions().filter { it == needed }
        if (missing.isNotEmpty() && t != Tab.PROTECTED) {
            return Empty(
                R.drawable.ic_lock_open, "Allow access to your media",
                "Xama needs permission to show the videos and music stored on this phone.",
                "Allow access"
            ) { requestOrOpenSettings(missing) }
        }
        return when (t) {
            Tab.MUSIC -> Empty(R.drawable.ic_music, "No music yet",
                "Songs on this phone (MP3, M4A, AAC, FLAC, WAV, OGG, OPUS) will appear here.", "Refresh") { reloadAsync(force = true) }
            Tab.PROTECTED -> Empty(R.drawable.ic_shield, "No protected videos",
                "Protected videos sent to this phone will appear here.", "Refresh") { reloadAsync(force = true) }
            else -> Empty(R.drawable.ic_folder, "No videos yet",
                "Folders with videos on this phone will appear here.", "Refresh") { reloadAsync(force = true) }
        }
    }

    private fun requestOrOpenSettings(missing: List<String>) {
        val canAsk = missing.any { shouldShowRequestPermissionRationale(it) }
        val asked = prefs.getBoolean("asked_media", false)
        if (canAsk || !asked) {
            prefs.edit().putBoolean("asked_media", true).apply()
            permissionLauncher.launch(missing.toTypedArray())
        } else {
            try {
                startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", packageName, null)))
            } catch (_: Exception) {
                permissionLauncher.launch(missing.toTypedArray())
            }
        }
    }

    private fun showEmpty(e: Empty) {
        findViewById<RecyclerView>(R.id.contentList).visibility = View.GONE
        findViewById<ImageView>(R.id.emptyIcon).setImageResource(e.icon)
        findViewById<TextView>(R.id.emptyTitle).text = e.title
        findViewById<TextView>(R.id.emptyMessage).text = e.message
        val action = findViewById<TextView>(R.id.emptyAction)
        if (e.action != null && e.onAction != null) {
            action.visibility = View.VISIBLE
            action.text = e.action
            action.setOnClickListener { e.onAction.invoke() }
        } else {
            action.visibility = View.GONE
        }
        findViewById<View>(R.id.emptyState).visibility = View.VISIBLE
    }

    /** Feed order: portrait videos first, then the rest. */
    private fun reelsFeed(source: List<LocalVideo>): List<LocalVideo> {
        val v = source.filter { !it.isAudio }
        return (v.filter { it.isVertical } + v.filter { !it.isVertical }).take(300)
    }

    /** TikTok-style vertical swipe feed over [feed], starting at [start]. */
    private fun openReels(feed: List<LocalVideo>, start: LocalVideo?) {
        if (feed.isEmpty()) {
            Toast.makeText(this, if (loading) "Still scanning…" else "No videos found for Reels", Toast.LENGTH_SHORT).show()
            return
        }
        val startIndex = start?.let { s -> feed.indexOfFirst { it.id == s.id } }?.coerceAtLeast(0) ?: 0
        startActivity(Intent(this, ReelsActivity::class.java).apply {
            putExtra(EXTRA_START_INDEX, startIndex)
            putStringArrayListExtra("video_ids", ArrayList(feed.map { it.id }))
        })
    }

    private fun updateModeBar() {
        val bar = findViewById<View>(R.id.modeBar)
        val show = (tab == Tab.REELS || (tab == Tab.MUSIC && openMusicFolder == null)) && !searchOpen
        bar.visibility = if (show) View.VISIBLE else View.GONE
        if (!show) return
        val byFolder = if (tab == Tab.REELS) reelsByFolder else musicByFolder
        fun style(id: Int, selected: Boolean) {
            val v = findViewById<TextView>(id)
            v.setBackgroundResource(if (selected) R.drawable.chip_selected else R.drawable.chip_unselected)
            v.setTextColor(if (selected) 0xFFFFFFFF.toInt() else ContextCompat.getColor(this, R.color.text_secondary))
        }
        style(R.id.modeAll, !byFolder)
        style(R.id.modeFolders, byFolder)
        findViewById<View>(R.id.modePlay).visibility = if (tab == Tab.REELS && !byFolder) View.VISIBLE else View.GONE
    }

    private fun setByFolder(value: Boolean) {
        if (tab == Tab.REELS) {
            reelsByFolder = value
            prefs.edit().putBoolean("reels_by_folder", value).apply()
        } else {
            musicByFolder = value
            openMusicFolder = null
            prefs.edit().putBoolean("music_by_folder", value).apply()
        }
        render(animate = true)
    }

    // ---------------------------------------------------------------- scan all folders

    private fun bindScanAll() {
        val on = com.shammapps.xama.storage.XamaStorage.scanAllEnabled(this)
        val access = com.shammapps.xama.storage.XamaStorage.canWalkStorage(this)
        findViewById<com.google.android.material.materialswitch.MaterialSwitch>(R.id.scanAllSwitch).isChecked = on && access
        findViewById<TextView>(R.id.scanAllValue).text = when {
            on && access -> "On · includes hidden folders and Android/media"
            on -> "Needs “All files access” – tap to allow"
            else -> "Off · only media folders"
        }
    }

    private fun toggleScanAll() {
        val store = com.shammapps.xama.storage.XamaStorage
        val on = store.scanAllEnabled(this)
        if (on && store.canWalkStorage(this)) {
            store.setScanAll(this, false)
            bindScanAll()
            reloadAsync(force = true)
            return
        }
        com.google.android.material.dialog.MaterialAlertDialogBuilder(this)
            .setTitle("Scan all folders")
            .setMessage(
                "Xama will also look through every folder on this phone, including hidden folders " +
                    "and Android/media, for videos and music.\n\n" +
                    "This needs “All files access”. Note: on Android 11 and newer, Android/data " +
                    "is blocked for all apps and can't be scanned."
            )
            .setPositiveButton("Allow & scan") { _, _ ->
                store.setScanAll(this, true)
                if (store.canWalkStorage(this)) {
                    bindScanAll()
                    rescan()
                } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    try {
                        startActivity(Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, Uri.parse("package:$packageName")))
                    } catch (_: Exception) {
                        try { startActivity(Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION)) } catch (_: Exception) {}
                    }
                } else {
                    ensurePermission()
                }
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun openVideo(video: LocalVideo, context: List<LocalVideo>) {
        val playlist = context.filter { !it.isAudio }.take(500)
        startActivity(Intent(this, PlayerActivity::class.java).apply {
            putExtra(EXTRA_VIDEO_ID, video.id)
            putExtra(EXTRA_TITLE, video.title)
            putExtra(EXTRA_FILE_PATH, video.filePath)
            putExtra(EXTRA_CONTENT_URI, video.contentUri)
            putExtra(EXTRA_IS_ENCRYPTED, video.isEncrypted)
            putExtra(EXTRA_IV_BASE64, video.ivBase64)
            putStringArrayListExtra(PlayerActivity.EXTRA_PLAYLIST_IDS, ArrayList(playlist.map { it.id }))
        })
    }

    // ---------------------------------------------------------------- mini player

    private fun connectMiniPlayer() {
        if (!PlaybackService.isRunning) {
            if (findViewById<View>(R.id.miniPlayer).visibility != View.GONE) {
                findViewById<View>(R.id.miniPlayer).visibility = View.GONE
                adjustBottomPadding(false)
            }
            return
        }
        val token = SessionToken(this, ComponentName(this, PlaybackService::class.java))
        val future = MediaController.Builder(this, token).buildAsync()
        controllerFuture = future
        future.addListener({
            val c = try { future.get() } catch (_: Exception) { null } ?: return@addListener
            controller = c
            c.addListener(miniListener)
            bindMiniPlayer()
            handler.post(miniTicker)
        }, ContextCompat.getMainExecutor(this))
    }

    private fun releaseMiniPlayer() {
        handler.removeCallbacks(miniTicker)
        controller?.removeListener(miniListener)
        controller = null
        controllerFuture?.let { MediaController.releaseFuture(it) }
        controllerFuture = null
    }

    private fun bindMiniPlayer() {
        val mini = findViewById<View>(R.id.miniPlayer)
        val c = controller
        val item = c?.currentMediaItem
        if (c == null || item == null || c.playbackState == Player.STATE_IDLE && c.mediaItemCount == 0) {
            if (mini.visibility != View.GONE) {
                mini.visibility = View.GONE
                adjustBottomPadding(false)
            }
            return
        }
        if (mini.visibility != View.VISIBLE) {
            mini.visibility = View.VISIBLE
            mini.startAnimation(AnimationUtils.loadAnimation(this, R.anim.item_fade_up))
            adjustBottomPadding(true)
        }
        val meta = c.mediaMetadata
        findViewById<TextView>(R.id.miniTitle).text = meta.title ?: item.mediaMetadata.title ?: "Playing"
        findViewById<TextView>(R.id.miniArtist).text = meta.artist?.toString()?.takeIf { it.isNotBlank() } ?: "Unknown artist"
        findViewById<ImageButton>(R.id.miniPlayPause).setImageResource(
            if (c.playWhenReady && c.playbackState != Player.STATE_ENDED) R.drawable.ic_pause else R.drawable.ic_play
        )
        val art = findViewById<ImageView>(R.id.miniArt)
        val data = meta.artworkData
        val key = item.mediaId + ":" + (data?.size ?: 0)
        if (key != miniArtKey) {
            miniArtKey = key
            art.setImageBitmap(data?.let { try { BitmapFactory.decodeByteArray(it, 0, it.size) } catch (_: Throwable) { null } })
        }
        updateMiniProgress()
    }

    /** Docked layout: the mini player takes its own row, nothing to pad. */
    @Suppress("UNUSED_PARAMETER")
    private fun adjustBottomPadding(miniVisible: Boolean) {}

    private fun updateMiniProgress() {
        val c = controller ?: return
        val dur = c.duration
        val bar = findViewById<ProgressBar>(R.id.miniProgress)
        bar.progress = if (dur != C.TIME_UNSET && dur > 0) ((c.currentPosition * 1000) / dur).toInt() else 0
    }
}
