package com.shammapps.xama.ui

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView
import com.shammapps.xama.R
import com.shammapps.xama.data.LocalVideo
import com.shammapps.xama.util.ThumbLoader

class PosterAdapter(
    private val videos: List<LocalVideo>,
    private val onClick: (LocalVideo) -> Unit,
) : RecyclerView.Adapter<PosterAdapter.Holder>() {

    class Holder(v: View) : RecyclerView.ViewHolder(v) {
        val image: ImageView = v.findViewById(R.id.posterImage)
        val title: TextView = v.findViewById(R.id.posterTitle)
        val duration: TextView = v.findViewById(R.id.posterDuration)
        val badge: TextView = v.findViewById(R.id.posterBadge)
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder {
        val v = LayoutInflater.from(parent.context).inflate(R.layout.item_poster, parent, false)
        return Holder(v)
    }

    override fun onBindViewHolder(holder: Holder, position: Int) {
        val video = videos[position]
        holder.title.text = video.title
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
        ThumbLoader.load(holder.itemView.context, video, holder.image, video.id)
        holder.itemView.setOnClickListener { onClick(video) }
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
