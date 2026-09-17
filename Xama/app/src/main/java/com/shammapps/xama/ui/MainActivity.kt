package com.shammapps.xama.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.media.MediaMetadataRetriever
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.view.View
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.widget.NestedScrollView
import androidx.recyclerview.widget.GridLayoutManager
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.shammapps.xama.R
import com.shammapps.xama.data.LocalVideo
import com.shammapps.xama.data.VideoFolder
import com.shammapps.xama.data.VideoRepository
import com.shammapps.xama.data.WatchHistory
import java.util.concurrent.Executors

class MainActivity : AppCompatActivity() {

    companion object {
        const val EXTRA_VIDEO_ID = "video_id"
        const val EXTRA_TITLE = "title"
        const val EXTRA_FILE_PATH = "file_path"
        const val EXTRA_CONTENT_URI = "content_uri"
        const val EXTRA_IS_ENCRYPTED = "is_encrypted"
        const val EXTRA_IV_BASE64 = "iv_base64"
        const val EXTRA_START_INDEX = "start_index"
    }

    private enum class Tab { HOME, FOLDERS, PROTECTED, PROFILE }
    private enum class Category { ALL, RECENT, REELS, LONG, PROTECTED }

    private lateinit var repository: VideoRepository
    private var allVideos: List<LocalVideo> = emptyList()
    private var folders: List<VideoFolder> = emptyList()
    private var tab = Tab.HOME
    private var category = Category.ALL
    private var query = ""
    private var openFolder: VideoFolder? = null
    private var featured: LocalVideo? = null
    private val thumbExecutor = Executors.newSingleThreadExecutor()

    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        if (!granted) {
            Toast.makeText(this, "Allow video access to browse folders on this phone.", Toast.LENGTH_LONG).show()
        }
        reload()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        repository = VideoRepository(this)

        findViewById<EditText>(R.id.searchInput).addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {
                query = s?.toString()?.trim().orEmpty()
                render()
            }
            override fun afterTextChanged(s: Editable?) {}
        })

        findViewById<LinearLayout>(R.id.navHome).setOnClickListener { switchTab(Tab.HOME) }
        findViewById<LinearLayout>(R.id.navFolders).setOnClickListener { switchTab(Tab.FOLDERS) }
        findViewById<LinearLayout>(R.id.navProtected).setOnClickListener { switchTab(Tab.PROTECTED) }
        findViewById<LinearLayout>(R.id.navProfile).setOnClickListener { switchTab(Tab.PROFILE) }
        findViewById<TextView>(R.id.btnBackFolder).setOnClickListener {
            openFolder = null
            render()
        }

        bindCategory(R.id.catAll, Category.ALL)
        bindCategory(R.id.catRecent, Category.RECENT)
        bindCategory(R.id.catReels, Category.REELS)
        bindCategory(R.id.catLong, Category.LONG)
        bindCategory(R.id.catProtected, Category.PROTECTED)

        ensurePermission()
    }

    override fun onResume() {
        super.onResume()
        if (::repository.isInitialized) reload()
    }

    private fun switchTab(t: Tab) {
        tab = t
        openFolder = null
        updateNav()
        render()
    }

    private fun bindCategory(id: Int, c: Category) {
        findViewById<TextView>(id).setOnClickListener {
            category = c
            styleCategories()
            render()
        }
    }

    private fun ensurePermission() {
        val permission = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            Manifest.permission.READ_MEDIA_VIDEO
        } else {
            Manifest.permission.READ_EXTERNAL_STORAGE
        }
        if (ContextCompat.checkSelfPermission(this, permission) == PackageManager.PERMISSION_GRANTED) {
            reload()
        } else {
            permissionLauncher.launch(permission)
        }
    }

    private fun reload() {
        allVideos = repository.loadAll()
        folders = repository.loadFolders(allVideos)
        featured = pickFeatured(allVideos)
        updateNav()
        styleCategories()
        render()
    }

    private fun pickFeatured(videos: List<LocalVideo>): LocalVideo? {
        if (videos.isEmpty()) return null
        // Prefer protected, else longest, else first (most recent from MediaStore order)
        return videos.firstOrNull { it.isEncrypted }
            ?: videos.maxByOrNull { it.durationMs }
            ?: videos.first()
    }

    private fun updateNav() {
        fun paint(icon: Int, label: Int, selected: Boolean) {
            val c = if (selected) R.color.accent else R.color.text_muted
            findViewById<TextView>(icon).setTextColor(ContextCompat.getColor(this, c))
            findViewById<TextView>(label).setTextColor(ContextCompat.getColor(this, c))
        }
        paint(R.id.navHomeIcon, R.id.navHomeLabel, tab == Tab.HOME)
        paint(R.id.navFoldersIcon, R.id.navFoldersLabel, tab == Tab.FOLDERS)
        paint(R.id.navProtectedIcon, R.id.navProtectedLabel, tab == Tab.PROTECTED)
        paint(R.id.navProfileIcon, R.id.navProfileLabel, tab == Tab.PROFILE)
    }

    private fun styleCategories() {
        fun style(id: Int, selected: Boolean) {
            val v = findViewById<TextView>(id)
            v.setBackgroundResource(if (selected) R.drawable.chip_selected else R.drawable.chip_unselected)
            v.setTextColor(if (selected) 0xFFFFFFFF.toInt() else ContextCompat.getColor(this, R.color.text_muted))
        }
        style(R.id.catAll, category == Category.ALL)
        style(R.id.catRecent, category == Category.RECENT)
        style(R.id.catReels, category == Category.REELS)
        style(R.id.catLong, category == Category.LONG)
        style(R.id.catProtected, category == Category.PROTECTED)
    }

    private fun render() {
        val list = findViewById<RecyclerView>(R.id.contentList)
        val homeScroll = findViewById<NestedScrollView>(R.id.homeScroll)
        val profile = findViewById<View>(R.id.profilePanel)
        val empty = findViewById<View>(R.id.emptyState)
        val backBar = findViewById<View>(R.id.folderBackBar)
        val categoryScroll = findViewById<View>(R.id.categoryScroll)
        val searchBar = findViewById<View>(R.id.searchBar)
        val header = findViewById<TextView>(R.id.headerTitle)
        val countText = findViewById<TextView>(R.id.videoCountText)

        val protectedCount = allVideos.count { it.isEncrypted }
        countText.text = when {
            allVideos.isEmpty() -> ""
            protectedCount > 0 -> "${allVideos.size} · $protectedCount protected"
            else -> "${allVideos.size} videos"
        }

        // defaults
        homeScroll.visibility = View.GONE
        list.visibility = View.GONE
        profile.visibility = View.GONE
        empty.visibility = View.GONE
        backBar.visibility = View.GONE
        categoryScroll.visibility = if (tab == Tab.HOME) View.VISIBLE else View.GONE
        searchBar.visibility = if (tab == Tab.PROFILE) View.GONE else View.VISIBLE

        if (openFolder != null) {
            val folder = openFolder!!
            header.text = folder.name
            backBar.visibility = View.VISIBLE
            categoryScroll.visibility = View.GONE
            findViewById<TextView>(R.id.folderDetailTitle).text =
                if (folder.videoCount == 1) "1 video" else "${folder.videoCount} videos"
            list.setPadding(list.paddingLeft, (56 * resources.displayMetrics.density).toInt(), list.paddingRight, list.paddingBottom)
            var videos = folder.videos
            if (query.isNotEmpty()) videos = videos.filter { it.title.contains(query, true) }
            showGrid(list, empty, videos)
            return
        }

        list.setPadding(list.paddingLeft, (12 * resources.displayMetrics.density).toInt(), list.paddingRight, list.paddingBottom)

        when (tab) {
            Tab.HOME -> {
                header.text = "Xama"
                homeScroll.visibility = View.VISIBLE
                bindHero()
                bindContinue()
                var videos = filterByCategory(allVideos)
                if (query.isNotEmpty()) {
                    videos = videos.filter {
                        it.title.contains(query, true) || it.folderName.contains(query, true)
                    }
                }
                // Don't duplicate featured in the grid
                val featId = featured?.id
                val grid = if (featId != null) videos.filter { it.id != featId } else videos
                findViewById<TextView>(R.id.sectionTitle).text = when (category) {
                    Category.ALL -> "On this device"
                    Category.RECENT -> "Recently added"
                    Category.REELS -> "Reels & vertical"
                    Category.LONG -> "Movies & long form"
                    Category.PROTECTED -> "Protected titles"
                }
                val homeGrid = findViewById<RecyclerView>(R.id.homeGrid)
                if (grid.isEmpty() && featured == null) {
                    homeScroll.visibility = View.GONE
                    empty.visibility = View.VISIBLE
                } else {
                    homeGrid.layoutManager = GridLayoutManager(this, 2)
                    homeGrid.adapter = PosterAdapter(grid) { openVideo(it) }
                }
            }
            Tab.PROTECTED -> {
                header.text = "Protected"
                var videos = allVideos.filter { it.isEncrypted }
                if (query.isNotEmpty()) videos = videos.filter { it.title.contains(query, true) }
                showGrid(list, empty, videos)
            }
            Tab.FOLDERS -> {
                header.text = "Folders"
                var folderList = folders
                if (query.isNotEmpty()) {
                    folderList = folderList.filter {
                        it.name.contains(query, true) ||
                            it.videos.any { v -> v.title.contains(query, true) }
                    }
                }
                if (folderList.isEmpty()) {
                    empty.visibility = View.VISIBLE
                } else {
                    list.visibility = View.VISIBLE
                    list.layoutManager = LinearLayoutManager(this)
                    list.adapter = FolderAdapter(folderList) { folder ->
                        openFolder = folder
                        render()
                    }
                }
            }
            Tab.PROFILE -> {
                header.text = "Profile"
                profile.visibility = View.VISIBLE
                findViewById<TextView>(R.id.statTotal).text = allVideos.size.toString()
                findViewById<TextView>(R.id.statProtected).text = protectedCount.toString()
                findViewById<TextView>(R.id.statFolders).text = folders.size.toString()
            }
        }
    }

    private fun filterByCategory(videos: List<LocalVideo>): List<LocalVideo> {
        return when (category) {
            Category.ALL -> videos
            Category.RECENT -> videos.take(40) // already newest-first from MediaStore
            Category.REELS -> videos.filter { it.isVertical }
            Category.LONG -> videos.filter { !it.isVertical && it.durationMs >= 10 * 60 * 1000 }
            Category.PROTECTED -> videos.filter { it.isEncrypted }
        }
    }


    private fun bindContinue() {
        val section = findViewById<View>(R.id.continueSection)
        val row = findViewById<RecyclerView>(R.id.continueRow)
        val recent = WatchHistory.resolve(this, allVideos)
        if (recent.isEmpty()) {
            section.visibility = View.GONE
            return
        }
        section.visibility = View.VISIBLE
        row.layoutManager = LinearLayoutManager(this, LinearLayoutManager.HORIZONTAL, false)
        row.adapter = ContinueAdapter(recent) { openVideo(it) }
    }

    private fun bindHero() {
        val heroCard = findViewById<View>(R.id.heroCard)
        val feat = featured
        if (feat == null) {
            heroCard.visibility = View.GONE
            return
        }
        heroCard.visibility = View.VISIBLE
        findViewById<TextView>(R.id.heroTitle).text = feat.title
        val meta = buildString {
            if (feat.durationMs > 0) append(formatDuration(feat.durationMs))
            if (feat.folderName.isNotBlank()) {
                if (isNotEmpty()) append(" · ")
                append(feat.folderName)
            }
            if (feat.isEncrypted) {
                if (isNotEmpty()) append(" · ")
                append("Protected")
            }
        }
        findViewById<TextView>(R.id.heroMeta).text = meta
        findViewById<TextView>(R.id.heroPlay).setOnClickListener { openVideo(feat) }
        findViewById<TextView>(R.id.heroFolder).setOnClickListener {
            openFolder = folders.find { it.name == feat.folderName }
                ?: VideoFolder(feat.folderName, 1, listOf(feat))
            tab = Tab.FOLDERS
            updateNav()
            render()
        }
        heroCard.setOnClickListener { openVideo(feat) }

        val image = findViewById<ImageView>(R.id.heroImage)
        image.setImageDrawable(null)
        image.setBackgroundResource(R.drawable.thumb_placeholder)
        if (!feat.isEncrypted) {
            thumbExecutor.execute {
                val bmp = loadThumb(feat)
                runOnUiThread {
                    if (bmp != null) {
                        image.setImageBitmap(bmp)
                        image.background = null
                    }
                }
            }
        }
    }

    private fun loadThumb(video: LocalVideo): Bitmap? {
        val r = MediaMetadataRetriever()
        return try {
            when {
                !video.contentUri.isNullOrBlank() -> r.setDataSource(this, Uri.parse(video.contentUri))
                video.filePath.isNotBlank() -> r.setDataSource(video.filePath)
                else -> return null
            }
            r.getFrameAtTime(1_500_000, MediaMetadataRetriever.OPTION_CLOSEST_SYNC)
        } catch (_: Exception) {
            null
        } finally {
            try { r.release() } catch (_: Exception) {}
        }
    }

    private fun formatDuration(ms: Long): String {
        val s = ms / 1000
        val h = s / 3600
        val m = (s % 3600) / 60
        val sec = s % 60
        return if (h > 0) String.format("%d:%02d:%02d", h, m, sec) else String.format("%d:%02d", m, sec)
    }

    private fun showGrid(list: RecyclerView, empty: View, videos: List<LocalVideo>) {
        if (videos.isEmpty()) {
            list.visibility = View.GONE
            empty.visibility = View.VISIBLE
            return
        }
        empty.visibility = View.GONE
        list.visibility = View.VISIBLE
        list.layoutManager = GridLayoutManager(this, 2)
        list.adapter = PosterAdapter(videos) { openVideo(it) }
    }

    private fun openVideo(video: LocalVideo) {
        if (video.isVertical) {
            val verticalOnly = allVideos.filter { it.isVertical }
            val startIndex = verticalOnly.indexOfFirst { it.id == video.id }.coerceAtLeast(0)
            startActivity(Intent(this, ReelsActivity::class.java).apply {
                putExtra(EXTRA_START_INDEX, startIndex)
                putStringArrayListExtra("video_ids", ArrayList(verticalOnly.map { it.id }))
            })
        } else {
            startActivity(Intent(this, PlayerActivity::class.java).apply {
                putExtra(EXTRA_VIDEO_ID, video.id)
                putExtra(EXTRA_TITLE, video.title)
                putExtra(EXTRA_FILE_PATH, video.filePath)
                putExtra(EXTRA_CONTENT_URI, video.contentUri)
                putExtra(EXTRA_IS_ENCRYPTED, video.isEncrypted)
                putExtra(EXTRA_IV_BASE64, video.ivBase64)
            })
        }
    }
}
