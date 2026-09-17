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
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.shammapps.xama.R
import com.shammapps.xama.data.LocalVideo
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

    private enum class Filter { ALL, PROTECTED, REELS, PHONE }

    private lateinit var repository: VideoRepository
    private var allVideos: List<LocalVideo> = emptyList()
    private var filter = Filter.ALL
    private var query = ""

    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        if (!granted) {
            Toast.makeText(
                this,
                "Allow video access to browse phone videos. Protected USB titles still work.",
                Toast.LENGTH_LONG
            ).show()
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
                applyFilter()
            }
            override fun afterTextChanged(s: Editable?) {}
        })

        bindChip(R.id.chipAll, Filter.ALL)
        bindChip(R.id.chipProtected, Filter.PROTECTED)
        bindChip(R.id.chipReels, Filter.REELS)
        bindChip(R.id.chipPhone, Filter.PHONE)

        ensureMediaPermissionThenLoad()
    }

    override fun onResume() {
        super.onResume()
        if (::repository.isInitialized) reload()
    }

    private fun bindChip(id: Int, f: Filter) {
        findViewById<TextView>(id).setOnClickListener {
            filter = f
            styleChips()
            applyFilter()
        }
    }

    private fun styleChips() {
        fun style(id: Int, selected: Boolean) {
            val v = findViewById<TextView>(id)
            v.setBackgroundResource(if (selected) R.drawable.chip_selected else R.drawable.chip_unselected)
            v.setTextColor(
                if (selected) 0xFFFFFFFF.toInt()
                else ContextCompat.getColor(this, R.color.text_muted)
            )
        }
        style(R.id.chipAll, filter == Filter.ALL)
        style(R.id.chipProtected, filter == Filter.PROTECTED)
        style(R.id.chipReels, filter == Filter.REELS)
        style(R.id.chipPhone, filter == Filter.PHONE)
    }

    private fun ensureMediaPermissionThenLoad() {
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
        styleChips()
        applyFilter()
    }

    private fun applyFilter() {
        var list = allVideos
        list = when (filter) {
            Filter.ALL -> list
            Filter.PROTECTED -> list.filter { it.isEncrypted }
            Filter.REELS -> list.filter { it.isVertical }
            Filter.PHONE -> list.filter { !it.isEncrypted }
        }
        if (query.isNotEmpty()) {
            list = list.filter { it.title.contains(query, ignoreCase = true) }
        }

        val recycler = findViewById<RecyclerView>(R.id.videoList)
        val empty = findViewById<View>(R.id.emptyState)
        val countText = findViewById<TextView>(R.id.videoCountText)

        val protectedCount = allVideos.count { it.isEncrypted }
        countText.text = when {
            allVideos.isEmpty() -> ""
            protectedCount > 0 -> "${allVideos.size} · $protectedCount protected"
            else -> "${allVideos.size} videos"
        }

        if (list.isEmpty()) {
            recycler.visibility = View.GONE
            empty.visibility = View.VISIBLE
        } else {
            recycler.visibility = View.VISIBLE
            empty.visibility = View.GONE
            recycler.layoutManager = LinearLayoutManager(this)
            recycler.adapter = VideoAdapter(list) { video -> openVideo(video, list) }
        }
    }

    private fun openVideo(video: LocalVideo, visible: List<LocalVideo>) {
        if (video.isVertical) {
            val verticalOnly = allVideos.filter { it.isVertical }
            val startIndex = verticalOnly.indexOfFirst { it.id == video.id }.coerceAtLeast(0)
            val intent = Intent(this, ReelsActivity::class.java)
            intent.putExtra(EXTRA_START_INDEX, startIndex)
            intent.putStringArrayListExtra("video_ids", ArrayList(verticalOnly.map { it.id }))
            startActivity(intent)
        } else {
            val intent = Intent(this, PlayerActivity::class.java)
            intent.putExtra(EXTRA_VIDEO_ID, video.id)
            intent.putExtra(EXTRA_TITLE, video.title)
            intent.putExtra(EXTRA_FILE_PATH, video.filePath)
            intent.putExtra(EXTRA_CONTENT_URI, video.contentUri)
            intent.putExtra(EXTRA_IS_ENCRYPTED, video.isEncrypted)
            intent.putExtra(EXTRA_IV_BASE64, video.ivBase64)
            startActivity(intent)
        }
    }
}
