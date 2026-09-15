package com.shammapps.xama.ui

import android.graphics.Bitmap
import android.media.MediaMetadataRetriever
import android.os.Handler
import android.os.Looper
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView
import com.shammapps.xama.R
import com.shammapps.xama.data.LocalVideo
import java.util.concurrent.Executors

class VideoAdapter(
    private val videos: List<LocalVideo>,
    private val onClick: (LocalVideo) -> Unit,
) : RecyclerView.Adapter<VideoAdapter.VideoHolder>() {

    private val executor = Executors.newFixedThreadPool(2)
    private val mainHandler = Handler(Looper.getMainLooper())

    class VideoHolder(itemView: View) : RecyclerView.ViewHolder(itemView) {
        val titleText: TextView = itemView.findViewById(R.id.titleText)
        val subtitleText: TextView = itemView.findViewById(R.id.subtitleText)
        val badgeText: TextView = itemView.findViewById(R.id.badgeText)
        val thumbImage: ImageView = itemView.findViewById(R.id.thumbImage)
        val durationText: TextView = itemView.findViewById(R.id.durationText)
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): VideoHolder {
        val view = LayoutInflater.from(parent.context).inflate(R.layout.item_video, parent, false)
        return VideoHolder(view)
    }

    override fun onBindViewHolder(holder: VideoHolder, position: Int) {
        val video = videos[position]
        holder.titleText.text = video.title

        val kind = when {
            video.isEncrypted && video.isVertical -> "PROTECTED · REELS"
            video.isEncrypted -> "PROTECTED"
            video.isVertical -> "REELS"
            else -> "VIDEO"
        }
        holder.badgeText.text = kind
        holder.subtitleText.text = if (video.isEncrypted) {
            "Licensed to this device"
        } else {
            "Local file"
        }

        // Reset thumb while loading
        holder.thumbImage.setImageDrawable(null)
        holder.thumbImage.setBackgroundResource(R.drawable.thumb_placeholder)
        holder.durationText.visibility = View.GONE
        holder.itemView.tag = video.id

        // Thumbnails only for plain files (encrypted bodies can't be probed)
        if (!video.isEncrypted) {
            executor.execute {
                val pair = extractThumbAndDuration(video.filePath)
                mainHandler.post {
                    if (holder.itemView.tag == video.id && pair != null) {
                        pair.first?.let {
                            holder.thumbImage.setImageBitmap(it)
                            holder.thumbImage.background = null
                        }
                        if (pair.second.isNotEmpty()) {
                            holder.durationText.text = pair.second
                            holder.durationText.visibility = View.VISIBLE
                        }
                    }
                }
            }
        }

        holder.itemView.setOnClickListener { onClick(video) }
    }

    private fun extractThumbAndDuration(path: String): Pair<Bitmap?, String>? {
        val r = MediaMetadataRetriever()
        return try {
            r.setDataSource(path)
            val bmp = r.getFrameAtTime(1_000_000, MediaMetadataRetriever.OPTION_CLOSEST_SYNC)
            val durMs = r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull() ?: 0L
            val dur = if (durMs > 0) formatDuration(durMs) else ""
            Pair(bmp, dur)
        } catch (_: Exception) {
            null
        } finally {
            try { r.release() } catch (_: Exception) {}
        }
    }

    private fun formatDuration(ms: Long): String {
        val totalSec = ms / 1000
        val h = totalSec / 3600
        val m = (totalSec % 3600) / 60
        val s = totalSec % 60
        return if (h > 0) String.format("%d:%02d:%02d", h, m, s)
        else String.format("%d:%02d", m, s)
    }

    override fun getItemCount() = videos.size
}
