package com.shammapps.xama.ui

import android.os.Bundle
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.viewpager2.widget.ViewPager2
import com.shammapps.xama.R
import com.shammapps.xama.crypto.ShammKeyResolver
import com.shammapps.xama.data.LocalVideo
import com.shammapps.xama.data.VideoRepository
import com.shammapps.xama.security.SecurityGuard

class ReelsActivity : AppCompatActivity() {

    private lateinit var pager: ViewPager2
    private lateinit var adapter: ReelsAdapter
    private var videos: List<LocalVideo> = emptyList()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_reels)

        // Any protected clip in the feed means the whole feed screen gets
        // the same protections a single protected video would - simplest
        // and safest rule, rather than trying to toggle protection per swipe.
        SecurityGuard.enableScreenshotProtection(this)
        if (SecurityGuard.isEnvironmentCompromised(this)) {
            Toast.makeText(this, "This device can't play protected videos.", Toast.LENGTH_LONG).show()
            finish()
            return
        }

        val ids = intent.getStringArrayListExtra("video_ids") ?: arrayListOf()
        val startIndex = intent.getIntExtra(MainActivity.EXTRA_START_INDEX, 0)

        val repository = VideoRepository(this)
        val all = repository.loadIncoming() + repository.loadPlain()
        videos = ids.mapNotNull { id -> all.find { it.id == id } }

        if (videos.isEmpty()) {
            finish()
            return
        }

        val resolver = ShammKeyResolver(this)
        adapter = ReelsAdapter(videos, resolver)

        pager = findViewById(R.id.reelsPager)
        pager.adapter = adapter
        pager.setCurrentItem(startIndex.coerceIn(0, videos.size - 1), false)

        pager.registerOnPageChangeCallback(object : ViewPager2.OnPageChangeCallback() {
            override fun onPageSelected(position: Int) {
                // Pause every page except the one now in view - only one
                // clip should ever be playing audio at a time.
                for (i in videos.indices) {
                    val holder = pager.findViewHolderForAdapterPosition(i)
                    if (holder is ReelsAdapter.ReelHolder) {
                        adapter.setPlaying(holder, i == position)
                    }
                }
            }
        })
    }

    override fun onPause() {
        super.onPause()
        val holder = pager.findViewHolderForAdapterPosition(pager.currentItem)
        if (holder is ReelsAdapter.ReelHolder) adapter.setPlaying(holder, false)
    }

    override fun onResume() {
        super.onResume()
        val holder = pager.findViewHolderForAdapterPosition(pager.currentItem)
        if (holder is ReelsAdapter.ReelHolder) adapter.setPlaying(holder, true)
    }
}
