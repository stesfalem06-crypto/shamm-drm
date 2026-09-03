package com.shammapps.xama.ui

import android.view.LayoutInflater
import android.view.ViewGroup
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView
import com.shammapps.xama.R
import com.shammapps.xama.data.LocalVideo

class VideoAdapter(
    private val videos: List<LocalVideo>,
    private val onClick: (LocalVideo) -> Unit,
) : RecyclerView.Adapter<VideoAdapter.VideoHolder>() {

    class VideoHolder(itemView: android.view.View) : RecyclerView.ViewHolder(itemView) {
        val title: TextView = itemView.findViewById(R.id.titleText)
        val badge: TextView = itemView.findViewById(R.id.badgeText)
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): VideoHolder {
        val view = LayoutInflater.from(parent.context).inflate(R.layout.item_video, parent, false)
        return VideoHolder(view)
    }

    override fun onBindViewHolder(holder: VideoHolder, position: Int) {
        val video = videos[position]
        holder.title.text = video.title
        holder.badge.text = when {
            video.isEncrypted && video.isVertical -> "PROTECTED · REEL"
            video.isEncrypted -> "PROTECTED"
            video.isVertical -> "REEL"
            else -> ""
        }
        holder.itemView.setOnClickListener { onClick(video) }
    }

    override fun getItemCount() = videos.size
}
