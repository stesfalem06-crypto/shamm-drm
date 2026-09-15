package com.shammapps.xama.ui

import android.content.Intent
import android.os.Bundle
import android.view.View
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
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
        const val EXTRA_IS_ENCRYPTED = "is_encrypted"
        const val EXTRA_IV_BASE64 = "iv_base64"
        const val EXTRA_START_INDEX = "start_index"
    }

    private lateinit var repository: VideoRepository

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        repository = VideoRepository(this)
        loadLibrary()
    }

    override fun onResume() {
        super.onResume()
        loadLibrary()
    }

    private fun loadLibrary() {
        val all = repository.loadIncoming() + repository.loadPlain()
        val list = findViewById<RecyclerView>(R.id.videoList)
        val empty = findViewById<View>(R.id.emptyState)
        val countText = findViewById<TextView>(R.id.videoCountText)

        countText.text = when (all.size) {
            0 -> ""
            1 -> "1 video"
            else -> "${all.size} videos"
        }

        if (all.isEmpty()) {
            list.visibility = View.GONE
            empty.visibility = View.VISIBLE
            return
        }

        list.visibility = View.VISIBLE
        empty.visibility = View.GONE
        list.layoutManager = LinearLayoutManager(this)
        list.adapter = VideoAdapter(all) { video -> openVideo(video, all) }
    }

    private fun openVideo(video: LocalVideo, all: List<LocalVideo>) {
        if (video.isVertical) {
            val verticalOnly = all.filter { it.isVertical }
            val startIndex = verticalOnly.indexOf(video).coerceAtLeast(0)
            val intent = Intent(this, ReelsActivity::class.java)
            intent.putExtra(EXTRA_START_INDEX, startIndex)
            intent.putStringArrayListExtra("video_ids", ArrayList(verticalOnly.map { it.id }))
            startActivity(intent)
        } else {
            val intent = Intent(this, PlayerActivity::class.java)
            intent.putExtra(EXTRA_VIDEO_ID, video.id)
            intent.putExtra(EXTRA_TITLE, video.title)
            intent.putExtra(EXTRA_FILE_PATH, video.filePath)
            intent.putExtra(EXTRA_IS_ENCRYPTED, video.isEncrypted)
            intent.putExtra(EXTRA_IV_BASE64, video.ivBase64)
            startActivity(intent)
        }
    }
}
