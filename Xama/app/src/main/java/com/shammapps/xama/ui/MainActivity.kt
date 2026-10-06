package com.shammapps.xama.ui

import android.Manifest
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.Configuration
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
import androidx.core.widget.NestedScrollView
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
import com.shammapps.xama.util.ThumbLoader
import java.util.Calendar
import java.util.concurrent.Executors

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
        private const val STATE_CATEGORY = "category"
        private const val STATE_FOLDER = "folder"
        private const val STATE_QUERY = "query"
    }

    private enum class Tab { HOME, FOLDERS, MUSIC, PROTECTED, PROFILE }
    private enum class Category { ALL, RECENT, REELS, LONG, PROTECTED }

    private lateinit var repository: VideoRepository
    private var allItems: List<LocalVideo> = emptyList()
    private var videos: List<LocalVideo> = emptyList()
    private var audio: List<LocalVideo> = emptyList()
    private var folders: List<VideoFolder> = emptyList()
    private var tab = Tab.HOME
    private var category = Category.ALL
    private var query = ""
    private var openFolder: VideoFolder? = null
    private var pendingFolderName: String? = null
    private var featured: LocalVideo? = null
    private var loading = false
    private var hasLoadedOnce = false
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

    // ---------------------------------------------------------------- lifecycle

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        repository = VideoRepository(this)

        savedInstanceState?.let { s ->
            tab = Tab.values().getOrElse(s.getInt(STATE_TAB, 0)) { Tab.HOME }
            category = Category.values().getOrElse(s.getInt(STATE_CATEGORY, 0)) { Category.ALL }
            pendingFolderName = s.getString(STATE_FOLDER)
            query = s.getString(STATE_QUERY).orEmpty()
        }

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                when {
                    openFolder != null -> {
                        openFolder = null
                        tab = Tab.FOLDERS
                        updateNav()
                        render(animate = true)
                    }
                    query.isNotEmpty() -> findViewById<EditText>(R.id.searchInput).setText("")
                    tab != Tab.HOME -> switchTab(Tab.HOME)
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
                findViewById<View>(R.id.searchClear).visibility = if (query.isEmpty()) View.GONE else View.VISIBLE
                render()
            }
            override fun afterTextChanged(s: Editable?) {}
        })
        findViewById<View>(R.id.searchClear).setOnClickListener { search.setText("") }

        findViewById<View>(R.id.navHome).setOnClickListener { switchTab(Tab.HOME) }
        findViewById<View>(R.id.navFolders).setOnClickListener { switchTab(Tab.FOLDERS) }
        findViewById<View>(R.id.navMusic).setOnClickListener { switchTab(Tab.MUSIC) }
        findViewById<View>(R.id.navProtected).setOnClickListener { switchTab(Tab.PROTECTED) }
        findViewById<View>(R.id.navProfile).setOnClickListener { switchTab(Tab.PROFILE) }
        findViewById<View>(R.id.btnBackFolder).setOnClickListener {
            openFolder = null
            render(animate = true)
        }
        findViewById<View>(R.id.btnRefresh).setOnClickListener {
            Toast.makeText(this, "Rescanning library…", Toast.LENGTH_SHORT).show()
            reloadAsync(force = true)
        }
        findViewById<View>(R.id.btnAbout).setOnClickListener { openAbout() }
        findViewById<View>(R.id.rowAbout).setOnClickListener { openAbout() }
        findViewById<View>(R.id.rowRescan).setOnClickListener {
            Toast.makeText(this, "Rescanning library…", Toast.LENGTH_SHORT).show()
            reloadAsync(force = true)
        }
        findViewById<View>(R.id.rowClearHistory).setOnClickListener {
            WatchHistory.clear(this)
            Toast.makeText(this, "Continue watching cleared", Toast.LENGTH_SHORT).show()
            render()
        }
        findViewById<TextView>(R.id.profileVersion).text = "v${BuildConfig.VERSION_NAME}"

        bindCategory(R.id.catAll, Category.ALL)
        bindCategory(R.id.catRecent, Category.RECENT)
        bindCategory(R.id.catReels, Category.REELS)
        bindCategory(R.id.catLong, Category.LONG)
        bindCategory(R.id.catProtected, Category.PROTECTED)

        findViewById<View>(R.id.miniBody).setOnClickListener {
            startActivity(Intent(this, AudioPlayerActivity::class.java))
        }
        findViewById<View>(R.id.miniPlayPause).setOnClickListener {
            controller?.let { if (it.playWhenReady) it.pause() else { if (it.playbackState == Player.STATE_IDLE) it.prepare(); it.play() } }
        }
        findViewById<View>(R.id.miniNext).setOnClickListener { controller?.seekToNext() }

        updateNav()
        styleCategories()

        // After rotation / recreation show the cached library instantly.
        if (LibraryCache.items.isNotEmpty()) {
            applyLibrary(LibraryCache.items)
        }
        ensurePermission()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        outState.putInt(STATE_TAB, tab.ordinal)
        outState.putInt(STATE_CATEGORY, category.ordinal)
        outState.putString(STATE_FOLDER, openFolder?.name)
        outState.putString(STATE_QUERY, query)
    }

    override fun onStart() {
        super.onStart()
        connectMiniPlayer()
    }

    override fun onResume() {
        super.onResume()
        // Pick up files that arrived while we were away (incoming / plain folders,
        // MediaStore). Cheap and off the main thread; re-render only on change.
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
        if (missing.isEmpty()) {
            reloadAsync(force = true)
        } else {
            // Load what we can right away (protected + app folder), then ask.
            reloadAsync(force = true)
            permissionLauncher.launch(missing.toTypedArray())
        }
    }

    /** Heavy MediaStore work off the main thread to avoid freezes. */
    private var reloadQueued = false

    private fun reloadAsync(force: Boolean) {
        if (loading) {
            if (force) reloadQueued = true
            return
        }
        loading = true
        if (allItems.isEmpty()) render()
        loadExecutor.execute {
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
        folders = try { repository.loadFolders(items) } catch (_: Exception) { emptyList() }
        featured = pickFeatured(videos)
        openFolder = openFolder?.let { f -> folders.find { it.name == f.name } }
        pendingFolderName?.let { name ->
            openFolder = folders.find { it.name == name }
            pendingFolderName = null
        }
        updateNav()
        styleCategories()
        render()
    }

    /** Refresh progress bars / continue row without rebuilding lists (keeps scroll). */
    private fun refreshLight() {
        if (tab == Tab.HOME && openFolder == null) bindContinue()
        findViewById<RecyclerView>(R.id.homeGrid).adapter?.notifyDataSetChanged()
        findViewById<RecyclerView>(R.id.contentList).adapter?.notifyDataSetChanged()
    }

    private fun pickFeatured(list: List<LocalVideo>): LocalVideo? {
        if (list.isEmpty()) return null
        val recent = WatchHistory.resolve(this, list).firstOrNull { !it.isVertical }
        return recent
            ?: list.firstOrNull { it.isEncrypted }
            ?: list.filter { !it.isVertical }.maxByOrNull { it.durationMs }
            ?: list.first()
    }

    // ---------------------------------------------------------------- navigation

    private fun switchTab(t: Tab) {
        if (t == tab && openFolder == null) {
            // Re-tap scrolls to top
            findViewById<NestedScrollView>(R.id.homeScroll).smoothScrollTo(0, 0)
            findViewById<RecyclerView>(R.id.contentList).smoothScrollToPosition(0)
            return
        }
        tab = t
        openFolder = null
        updateNav()
        render(animate = true)
    }

    private fun bindCategory(id: Int, c: Category) {
        findViewById<TextView>(id).setOnClickListener {
            if (category == c) return@setOnClickListener
            category = c
            styleCategories()
            render(animate = true)
        }
    }

    private fun openAbout() = startActivity(Intent(this, AboutActivity::class.java))

    private fun updateNav() {
        fun paint(item: Int, icon: Int, label: Int, selected: Boolean) {
            val color = ContextCompat.getColor(this, if (selected) R.color.accent else R.color.text_muted)
            findViewById<View>(item).apply {
                setBackgroundResource(if (selected) R.drawable.nav_indicator else 0)
                isSelected = selected
            }
            findViewById<ImageView>(icon).setColorFilter(color)
            findViewById<TextView>(label).setTextColor(
                if (selected) ContextCompat.getColor(this, R.color.accent_light) else color
            )
        }
        paint(R.id.navHome, R.id.navHomeIcon, R.id.navHomeLabel, tab == Tab.HOME)
        paint(R.id.navFolders, R.id.navFoldersIcon, R.id.navFoldersLabel, tab == Tab.FOLDERS)
        paint(R.id.navMusic, R.id.navMusicIcon, R.id.navMusicLabel, tab == Tab.MUSIC)
        paint(R.id.navProtected, R.id.navProtectedIcon, R.id.navProtectedLabel, tab == Tab.PROTECTED)
        paint(R.id.navProfile, R.id.navProfileIcon, R.id.navProfileLabel, tab == Tab.PROFILE)
    }

    private fun styleCategories() {
        fun style(id: Int, selected: Boolean) {
            val v = findViewById<TextView>(id)
            v.setBackgroundResource(if (selected) R.drawable.chip_selected else R.drawable.chip_unselected)
            v.setTextColor(if (selected) 0xFFFFFFFF.toInt() else ContextCompat.getColor(this, R.color.text_secondary))
        }
        style(R.id.catAll, category == Category.ALL)
        style(R.id.catRecent, category == Category.RECENT)
        style(R.id.catReels, category == Category.REELS)
        style(R.id.catLong, category == Category.LONG)
        style(R.id.catProtected, category == Category.PROTECTED)
    }

    // ---------------------------------------------------------------- rendering

    private fun isLandscape() = resources.configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
    private fun columns(): Int {
        val widthDp = resources.configuration.screenWidthDp
        return when {
            widthDp >= 900 -> 3
            widthDp >= 600 || isLandscape() -> 2
            else -> 1
        }
    }

    private fun greeting(): String = when (Calendar.getInstance().get(Calendar.HOUR_OF_DAY)) {
        in 5..11 -> "Good morning"
        in 12..17 -> "Good afternoon"
        else -> "Good evening"
    }

    private fun matches(v: LocalVideo): Boolean =
        query.isEmpty() || v.title.contains(query, true) || v.folderName.contains(query, true) ||
            v.artist.contains(query, true) || v.album.contains(query, true)

    private fun render(animate: Boolean = false) {
        val list = findViewById<RecyclerView>(R.id.contentList)
        val homeScroll = findViewById<NestedScrollView>(R.id.homeScroll)
        val profile = findViewById<View>(R.id.profilePanel)
        val empty = findViewById<View>(R.id.emptyState)
        val loadingView = findViewById<View>(R.id.loadingState)
        val backBar = findViewById<View>(R.id.folderBackBar)
        val categoryScroll = findViewById<View>(R.id.categoryScroll)
        val searchBar = findViewById<View>(R.id.searchBar)
        val header = findViewById<TextView>(R.id.headerTitle)
        val countText = findViewById<TextView>(R.id.videoCountText)

        val protectedCount = videos.count { it.isEncrypted }
        countText.text = when {
            loading && allItems.isEmpty() -> "Scanning…"
            allItems.isEmpty() -> "Your private video & music player"
            else -> buildString {
                append(if (videos.size == 1) "1 video" else "${videos.size} videos")
                if (audio.isNotEmpty()) append(" · ${audio.size} tracks")
                if (protectedCount > 0) append(" · $protectedCount protected")
            }
        }

        homeScroll.visibility = View.GONE
        list.visibility = View.GONE
        profile.visibility = View.GONE
        empty.visibility = View.GONE
        loadingView.visibility = View.GONE
        backBar.visibility = View.GONE
        categoryScroll.visibility = if (tab == Tab.HOME && openFolder == null) View.VISIBLE else View.GONE
        searchBar.visibility = if (tab == Tab.PROFILE) View.GONE else View.VISIBLE
        val searchInput = findViewById<EditText>(R.id.searchInput)
        searchInput.hint = when (tab) {
            Tab.MUSIC -> "Search songs, artists, albums"
            Tab.FOLDERS -> "Search folders"
            else -> "Search videos"
        }
        list.setPadding(list.paddingLeft, Ui.dp(this, 8), list.paddingRight, list.paddingBottom)

        if (loading && allItems.isEmpty() && tab != Tab.PROFILE) {
            header.text = if (tab == Tab.HOME) greeting() else tabTitle()
            loadingView.visibility = View.VISIBLE
            return
        }

        val folder = openFolder
        if (folder != null) {
            header.text = folder.name
            backBar.visibility = View.VISIBLE
            categoryScroll.visibility = View.GONE
            val totalMs = folder.videos.sumOf { it.durationMs }
            findViewById<TextView>(R.id.folderDetailTitle).text = buildString {
                append(if (folder.videoCount == 1) "1 video" else "${folder.videoCount} videos")
                if (totalMs > 0) append(" · ").append(Ui.formatDuration(totalMs))
            }
            list.setPadding(list.paddingLeft, Ui.dp(this, 56), list.paddingRight, list.paddingBottom)
            showVideoList(list, empty, folder.videos.filter { matches(it) }.take(300), animate)
            return
        }

        when (tab) {
            Tab.HOME -> {
                header.text = greeting()
                var items = filterByCategory(videos).filter { matches(it) }
                val feat = featured?.takeIf { query.isEmpty() && category == Category.ALL }
                if (feat != null) items = items.filter { it.id != feat.id }
                items = items.take(80)
                findViewById<TextView>(R.id.sectionTitle).text = when {
                    query.isNotEmpty() -> "Results"
                    else -> when (category) {
                        Category.ALL -> "Suggested for you"
                        Category.RECENT -> "Recently added"
                        Category.REELS -> "Reels & vertical"
                        Category.LONG -> "Movies & long videos"
                        Category.PROTECTED -> "Protected titles"
                    }
                }
                if (items.isEmpty() && feat == null) {
                    showEmpty(emptyFor(Tab.HOME))
                } else {
                    homeScroll.visibility = View.VISIBLE
                    bindHero(feat)
                    if (query.isEmpty()) bindContinue() else findViewById<View>(R.id.continueSection).visibility = View.GONE
                    val grid = findViewById<RecyclerView>(R.id.homeGrid)
                    grid.layoutManager = GridLayoutManager(this, columns())
                    grid.adapter = PosterAdapter(items) { openVideo(it, items) }
                    if (animate) animateList(grid)
                }
            }
            Tab.PROTECTED -> {
                header.text = "Protected"
                showVideoList(list, empty, videos.filter { it.isEncrypted && matches(it) }, animate)
            }
            Tab.FOLDERS -> {
                header.text = "Folders"
                val folderList = folders.filter { f ->
                    query.isEmpty() || f.name.contains(query, true) || f.videos.any { it.title.contains(query, true) }
                }
                if (folderList.isEmpty()) {
                    showEmpty(emptyFor(Tab.FOLDERS))
                } else {
                    list.visibility = View.VISIBLE
                    list.layoutManager = GridLayoutManager(this, columns())
                    list.adapter = FolderAdapter(folderList) { f ->
                        openFolder = f
                        render(animate = true)
                    }
                    if (animate) animateList(list)
                }
            }
            Tab.MUSIC -> {
                header.text = "Music"
                val tracks = audio.filter { matches(it) }
                if (tracks.isEmpty()) {
                    showEmpty(emptyFor(Tab.MUSIC))
                } else {
                    list.visibility = View.VISIBLE
                    val cols = columns()
                    list.layoutManager = GridLayoutManager(this, cols).apply {
                        spanSizeLookup = object : GridLayoutManager.SpanSizeLookup() {
                            override fun getSpanSize(position: Int) = if (position == 0) cols else 1
                        }
                    }
                    list.adapter = AudioAdapter(tracks, controller?.currentMediaItem?.mediaId) { index, shuffle ->
                        AudioPlayerActivity.start(this, tracks, index, shuffle)
                    }
                    if (animate) animateList(list)
                }
            }
            Tab.PROFILE -> {
                header.text = "Profile"
                profile.visibility = View.VISIBLE
                findViewById<TextView>(R.id.statTotal).text = videos.size.toString()
                findViewById<TextView>(R.id.statMusic).text = audio.size.toString()
                findViewById<TextView>(R.id.statProtected).text = protectedCount.toString()
                findViewById<TextView>(R.id.statFolders).text = folders.size.toString()
                if (animate) profile.startAnimation(AnimationUtils.loadAnimation(this, R.anim.item_fade_up))
            }
        }
    }

    private fun tabTitle() = when (tab) {
        Tab.HOME -> "Home"
        Tab.FOLDERS -> "Folders"
        Tab.MUSIC -> "Music"
        Tab.PROTECTED -> "Protected"
        Tab.PROFILE -> "Profile"
    }

    private fun animateList(list: RecyclerView) {
        list.layoutAnimation = AnimationUtils.loadLayoutAnimation(this, R.anim.layout_fade_up)
        list.scheduleLayoutAnimation()
    }

    private data class Empty(val icon: Int, val title: String, val message: String, val action: String?, val onAction: (() -> Unit)?)

    private fun emptyFor(t: Tab): Empty {
        if (query.isNotEmpty()) {
            return Empty(R.drawable.ic_search, "No results", "Nothing matches “$query”.", "Clear search") {
                findViewById<EditText>(R.id.searchInput).setText("")
            }
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
            Tab.MUSIC -> Empty(
                R.drawable.ic_music, "No music yet",
                "Songs on this phone (MP3, M4A, AAC, FLAC, WAV, OGG, OPUS) will appear here.",
                "Rescan"
            ) { reloadAsync(force = true) }
            Tab.PROTECTED -> Empty(
                R.drawable.ic_shield, "No protected videos",
                "Protected videos sent to this phone will appear here.", "Rescan"
            ) { reloadAsync(force = true) }
            Tab.FOLDERS -> Empty(
                R.drawable.ic_folder, "No folders yet",
                "Folders with videos on this phone will appear here.", "Rescan"
            ) { reloadAsync(force = true) }
            else -> Empty(
                R.drawable.ic_movie,
                if (category == Category.ALL) "No videos yet" else "Nothing in this category",
                "Videos on this phone and files sent to Xama will appear here.", "Rescan"
            ) { reloadAsync(force = true) }
        }
    }

    private fun requestOrOpenSettings(missing: List<String>) {
        val canAsk = missing.any { shouldShowRequestPermissionRationale(it) }
        val asked = getSharedPreferences("xama_ui", MODE_PRIVATE).getBoolean("asked_media", false)
        if (canAsk || !asked) {
            getSharedPreferences("xama_ui", MODE_PRIVATE).edit().putBoolean("asked_media", true).apply()
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
        val empty = findViewById<View>(R.id.emptyState)
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
        empty.visibility = View.VISIBLE
    }

    private fun filterByCategory(list: List<LocalVideo>): List<LocalVideo> = when (category) {
        Category.ALL -> list
        Category.RECENT -> list.sortedByDescending { it.dateAddedSec }.take(40)
        Category.REELS -> list.filter { it.isVertical }
        Category.LONG -> list.filter { !it.isVertical && it.durationMs >= 10 * 60 * 1000 }
        Category.PROTECTED -> list.filter { it.isEncrypted }
    }

    private fun bindContinue() {
        val section = findViewById<View>(R.id.continueSection)
        val row = findViewById<RecyclerView>(R.id.continueRow)
        val recent = WatchHistory.resolve(this, videos).take(12)
        if (recent.isEmpty() || query.isNotEmpty()) {
            section.visibility = View.GONE
            return
        }
        section.visibility = View.VISIBLE
        if (row.layoutManager == null) {
            row.layoutManager = LinearLayoutManager(this, LinearLayoutManager.HORIZONTAL, false)
        }
        row.adapter = ContinueAdapter(recent) { openVideo(it, recent) }
    }

    private fun bindHero(feat: LocalVideo?) {
        val heroCard = findViewById<View>(R.id.heroCard)
        if (feat == null) {
            heroCard.visibility = View.GONE
            return
        }
        heroCard.visibility = View.VISIBLE
        findViewById<TextView>(R.id.heroKicker).text = when {
            feat.isEncrypted -> "Protected · Featured"
            WatchHistory.recentIds(this).firstOrNull() == feat.id -> "Pick up where you left off"
            else -> "Featured"
        }
        findViewById<TextView>(R.id.heroTitle).text = feat.title
        findViewById<TextView>(R.id.heroMeta).text = buildString {
            if (feat.durationMs > 0) append(Ui.formatDuration(feat.durationMs))
            if (feat.folderName.isNotBlank()) {
                if (isNotEmpty()) append(" · ")
                append(feat.folderName)
            }
        }
        findViewById<TextView>(R.id.heroPlay).setOnClickListener { openVideo(feat, videos) }
        findViewById<TextView>(R.id.heroFolder).setOnClickListener {
            openFolder = folders.find { it.name == feat.folderName }
                ?: VideoFolder(feat.folderName, 1, listOf(feat))
            tab = Tab.FOLDERS
            updateNav()
            render(animate = true)
        }
        heroCard.setOnClickListener { openVideo(feat, videos) }
        ThumbLoader.load(this, feat, findViewById(R.id.heroImage), "hero-${feat.id}")
    }

    private fun showVideoList(list: RecyclerView, empty: View, items: List<LocalVideo>, animate: Boolean) {
        if (items.isEmpty()) {
            list.visibility = View.GONE
            showEmpty(emptyFor(tab))
            return
        }
        empty.visibility = View.GONE
        list.visibility = View.VISIBLE
        list.layoutManager = GridLayoutManager(this, columns())
        list.adapter = PosterAdapter(items) { openVideo(it, items) }
        if (animate) animateList(list)
    }

    private fun openVideo(video: LocalVideo, context: List<LocalVideo>) {
        if (video.isVertical) {
            val verticalOnly = videos.filter { it.isVertical }
            val startIndex = verticalOnly.indexOfFirst { it.id == video.id }.coerceAtLeast(0)
            startActivity(Intent(this, ReelsActivity::class.java).apply {
                putExtra(EXTRA_START_INDEX, startIndex)
                putStringArrayListExtra("video_ids", ArrayList(verticalOnly.map { it.id }.take(100)))
            })
        } else {
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

    /** Keeps the last list rows clear of the floating nav (+ mini player). */
    private fun adjustBottomPadding(miniVisible: Boolean) {
        val bottom = Ui.dp(this, if (miniVisible) 188 else 120)
        for (id in intArrayOf(R.id.contentList, R.id.homeScroll, R.id.profilePanel)) {
            val v = findViewById<View>(id)
            v.setPadding(v.paddingLeft, v.paddingTop, v.paddingRight, bottom)
        }
    }

    private fun updateMiniProgress() {
        val c = controller ?: return
        val dur = c.duration
        val bar = findViewById<ProgressBar>(R.id.miniProgress)
        bar.progress = if (dur != C.TIME_UNSET && dur > 0) ((c.currentPosition * 1000) / dur).toInt() else 0
    }
}
