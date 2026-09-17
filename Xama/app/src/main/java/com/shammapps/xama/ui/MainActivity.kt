package com.shammapps.xama.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.view.View
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.recyclerview.widget.GridLayoutManager
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.shammapps.xama.R
import com.shammapps.xama.data.LocalVideo
import com.shammapps.xama.data.VideoFolder
import com.shammapps.xama.data.VideoRepository

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

    private enum class Tab { HOME, FOLDERS, PROTECTED }

    private lateinit var repository: VideoRepository
    private var allVideos: List<LocalVideo> = emptyList()
    private var folders: List<VideoFolder> = emptyList()
    private var tab = Tab.HOME
    private var query = ""
    private var openFolder: VideoFolder? = null

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

        findViewById<LinearLayout>(R.id.navHome).setOnClickListener {
            tab = Tab.HOME
            openFolder = null
            updateNav()
            render()
        }
        findViewById<LinearLayout>(R.id.navFolders).setOnClickListener {
            tab = Tab.FOLDERS
            openFolder = null
            updateNav()
            render()
        }
        findViewById<LinearLayout>(R.id.navProtected).setOnClickListener {
            tab = Tab.PROTECTED
            openFolder = null
            updateNav()
            render()
        }
        findViewById<TextView>(R.id.btnBackFolder).setOnClickListener {
            openFolder = null
            render()
        }

        ensurePermission()
    }

    override fun onResume() {
        super.onResume()
        if (::repository.isInitialized) reload()
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
        updateNav()
        render()
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
    }

    private fun render() {
        val list = findViewById<RecyclerView>(R.id.contentList)
        val empty = findViewById<View>(R.id.emptyState)
        val backBar = findViewById<View>(R.id.folderBackBar)
        val header = findViewById<TextView>(R.id.headerTitle)
        val countText = findViewById<TextView>(R.id.videoCountText)

        val protectedCount = allVideos.count { it.isEncrypted }
        countText.text = when {
            allVideos.isEmpty() -> ""
            protectedCount > 0 -> "${allVideos.size} videos · $protectedCount protected"
            else -> "${allVideos.size} videos"
        }

        // Folder detail mode
        if (openFolder != null) {
            val folder = openFolder!!
            header.text = folder.name
            backBar.visibility = View.VISIBLE
            findViewById<TextView>(R.id.folderDetailTitle).text =
                if (folder.videoCount == 1) "1 video" else "${folder.videoCount} videos"
            list.setPadding(list.paddingLeft, (56 * resources.displayMetrics.density).toInt(), list.paddingRight, list.paddingBottom)
            var videos = folder.videos
            if (query.isNotEmpty()) videos = videos.filter { it.title.contains(query, true) }
            showGrid(list, empty, videos)
            return
        }

        backBar.visibility = View.GONE
        list.setPadding(list.paddingLeft, (12 * resources.displayMetrics.density).toInt(), list.paddingRight, list.paddingBottom)

        when (tab) {
            Tab.HOME -> {
                header.text = "Xama"
                var videos = allVideos
                if (query.isNotEmpty()) videos = videos.filter {
                    it.title.contains(query, true) || it.folderName.contains(query, true)
                }
                showGrid(list, empty, videos)
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
                    list.visibility = View.GONE
                    empty.visibility = View.VISIBLE
                } else {
                    empty.visibility = View.GONE
                    list.visibility = View.VISIBLE
                    list.layoutManager = LinearLayoutManager(this)
                    list.adapter = FolderAdapter(folderList) { folder ->
                        openFolder = folder
                        render()
                    }
                }
            }
        }
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
