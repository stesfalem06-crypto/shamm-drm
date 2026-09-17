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

class PosterAdapter(
    private val videos: List<LocalVideo>,
    private val onClick: (LocalVideo) -> Unit,
) : RecyclerView.Adapter<PosterAdapter.Holder>() {

    private val executor = Executors.newFixedThreadPool(3)
    private val main = Handler(Looper.getMainLooper())
    private var appContext: Context? = null

    class Holder(v: View) : RecyclerView.ViewHolder(v) {
        val image: ImageView = v.findViewById(R.id.posterImage)
        val title: TextView = v.findViewById(R.id.posterTitle)
        val duration: TextView = v.findViewById(R.id.posterDuration)
        val badge: TextView = v.findViewById(R.id.posterBadge)
    }

    override fun onAttachedToRecyclerView(recyclerView: RecyclerView) {
        super.onAttachedToRecyclerView(recyclerView)
        appContext = recyclerView.context.applicationContext
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder {
        val v = LayoutInflater.from(parent.context).inflate(R.layout.item_poster, parent, false)
        return Holder(v)
    }

    override fun onBindViewHolder(holder: Holder, position: Int) {
        val video = videos[position]
        holder.title.text = video.title
        holder.itemView.tag = video.id
        holder.image.setImageDrawable(null)
        holder.image.setBackgroundResource(R.drawable.thumb_placeholder)

        if (video.durationMs > 0) {
            holder.duration.text = formatDuration(video.durationMs)
            holder.duration.visibility = View.VISIBLE
        } else {
            holder.duration.visibility = View.GONE
        }

        when {
            video.isEncrypted -> {
                holder.badge.text = "PROTECTED"
                holder.badge.visibility = View.VISIBLE
            }
            video.isVertical -> {
                holder.badge.text = "REELS"
                holder.badge.visibility = View.VISIBLE
            }
            else -> holder.badge.visibility = View.GONE
        }

        if (!video.isEncrypted) {
            val ctx = appContext ?: holder.itemView.context.applicationContext
            executor.execute {
                val bmp = thumb(ctx, video)
                main.post {
                    if (holder.itemView.tag == video.id && bmp != null) {
                        holder.image.setImageBitmap(bmp)
                        holder.image.background = null
                    }
                }
            }
        }

        holder.itemView.setOnClickListener { onClick(video) }
    }

    private fun thumb(ctx: Context, video: LocalVideo): Bitmap? {
        val r = MediaMetadataRetriever()
        return try {
            when {
                !video.contentUri.isNullOrBlank() -> r.setDataSource(ctx, Uri.parse(video.contentUri))
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
        val s = ms / 1000
        val h = s / 3600
        val m = (s % 3600) / 60
        val sec = s % 60
        return if (h > 0) String.format("%d:%02d:%02d", h, m, sec) else String.format("%d:%02d", m, sec)
    }

    override fun getItemCount() = videos.size
}
