package com.shammapps.xama.ui

import android.os.Bundle
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.recyclerview.widget.RecyclerView
import androidx.viewpager2.widget.ViewPager2
import com.shammapps.xama.R
import com.shammapps.xama.crypto.ShammKeyResolver
import com.shammapps.xama.data.LocalVideo
import com.shammapps.xama.data.VideoRepository
import com.shammapps.xama.security.SecurityGuard
import com.shammapps.xama.data.WatchHistory

class ReelsActivity : AppCompatActivity() {

    private lateinit var pager: ViewPager2
    private lateinit var adapter: ReelsAdapter
    private var videos: List<LocalVideo> = emptyList()

    private fun recycler(): RecyclerView? =
        if (pager.childCount > 0) pager.getChildAt(0) as? RecyclerView else null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_reels)

        val ids = intent.getStringArrayListExtra("video_ids") ?: arrayListOf()
        val startIndex = intent.getIntExtra(MainActivity.EXTRA_START_INDEX, 0)

        val repository = VideoRepository(this)
        val all = repository.loadAll()
        videos = ids.mapNotNull { id -> all.find { it.id == id } }

        if (videos.isEmpty()) {
            finish()
            return
        }

        // Security only when the feed includes protected content
        if (videos.any { it.isEncrypted }) {
            SecurityGuard.enableScreenshotProtection(this)
            if (SecurityGuard.isEnvironmentCompromised(this)) {
                Toast.makeText(this, "This device can't play protected videos.", Toast.LENGTH_LONG).show()
                finish()
                return
            }
        }

        val resolver = ShammKeyResolver(this)
        adapter = ReelsAdapter(videos, resolver)

        pager = findViewById(R.id.reelsPager)
        pager.adapter = adapter
        val start = startIndex.coerceIn(0, videos.size - 1)
        pager.setCurrentItem(start, false)
        WatchHistory.record(this, videos[start].id)

        pager.registerOnPageChangeCallback(object : ViewPager2.OnPageChangeCallback() {
            override fun onPageSelected(position: Int) {
                val rv = recycler() ?: return
                for (i in videos.indices) {
                    val holder = rv.findViewHolderForAdapterPosition(i)
                    if (holder is ReelsAdapter.ReelHolder) {
                        adapter.setPlaying(holder, i == position)
                    }
                }
                if (position in videos.indices) {
                    WatchHistory.record(this@ReelsActivity, videos[position].id)
                }
            }
        })
    }

    override fun onPause() {
        super.onPause()
        if (!::pager.isInitialized) return
        val holder = recycler()?.findViewHolderForAdapterPosition(pager.currentItem)
        if (holder is ReelsAdapter.ReelHolder) adapter.setPlaying(holder, false)
    }

    override fun onResume() {
        super.onResume()
        if (!::pager.isInitialized) return
        val holder = recycler()?.findViewHolderForAdapterPosition(pager.currentItem)
        if (holder is ReelsAdapter.ReelHolder) adapter.setPlaying(holder, true)
    }
}
