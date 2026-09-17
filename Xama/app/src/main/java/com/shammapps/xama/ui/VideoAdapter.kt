package com.shammapps.xama.ui

import android.content.Context
import android.graphics.Bitmap
import android.media.MediaMetadataRetriever
import android.net.Uri
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
    private var appContext: Context? = null

    class VideoHolder(itemView: View) : RecyclerView.ViewHolder(itemView) {
        val titleText: TextView = itemView.findViewById(R.id.titleText)
        val subtitleText: TextView = itemView.findViewById(R.id.subtitleText)
        val badgeText: TextView = itemView.findViewById(R.id.badgeText)
        val thumbImage: ImageView = itemView.findViewById(R.id.thumbImage)
        val durationText: TextView = itemView.findViewById(R.id.durationText)
    }

    override fun onAttachedToRecyclerView(recyclerView: RecyclerView) {
        super.onAttachedToRecyclerView(recyclerView)
        appContext = recyclerView.context.applicationContext
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
        holder.subtitleText.text = when {
            video.isEncrypted -> "Licensed to this device"
            video.contentUri != null -> "On this phone"
            else -> "Local file"
        }

        holder.thumbImage.setImageDrawable(null)
        holder.thumbImage.setBackgroundResource(R.drawable.thumb_placeholder)
        holder.itemView.tag = video.id

        if (video.durationMs > 0) {
            holder.durationText.text = formatDuration(video.durationMs)
            holder.durationText.visibility = View.VISIBLE
        } else {
            holder.durationText.visibility = View.GONE
        }

        if (!video.isEncrypted) {
            val ctx = appContext ?: holder.itemView.context.applicationContext
            executor.execute {
                val bmp = extractThumb(ctx, video)
                mainHandler.post {
                    if (holder.itemView.tag == video.id && bmp != null) {
                        holder.thumbImage.setImageBitmap(bmp)
                        holder.thumbImage.background = null
                    }
                }
            }
        }

        holder.itemView.setOnClickListener { onClick(video) }
    }

    private fun extractThumb(context: Context, video: LocalVideo): Bitmap? {
        val r = MediaMetadataRetriever()
        return try {
            when {
                !video.contentUri.isNullOrBlank() ->
                    r.setDataSource(context, Uri.parse(video.contentUri))
                video.filePath.isNotBlank() -> r.setDataSource(video.filePath)
                else -> return null
            }
            r.getFrameAtTime(1_000_000, MediaMetadataRetriever.OPTION_CLOSEST_SYNC)
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
