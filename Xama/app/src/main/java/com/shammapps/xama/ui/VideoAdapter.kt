package com.shammapps.xama.ui

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView
import com.shammapps.xama.R
import com.shammapps.xama.data.LocalVideo

class VideoAdapter(
    private val videos: List<LocalVideo>,
    private val onClick: (LocalVideo) -> Unit,
) : RecyclerView.Adapter<VideoAdapter.VideoHolder>() {

    class VideoHolder(itemView: View) : RecyclerView.ViewHolder(itemView) {
        val titleText: TextView = itemView.findViewById(R.id.titleText)
        val subtitleText: TextView = itemView.findViewById(R.id.subtitleText)
        val badgeText: TextView = itemView.findViewById(R.id.badgeText)
        val accentBar: View = itemView.findViewById(R.id.accentBar)
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): VideoHolder {
        val view = LayoutInflater.from(parent.context).inflate(R.layout.item_video, parent, false)
        return VideoHolder(view)
    }

    override fun onBindViewHolder(holder: VideoHolder, position: Int) {
        val video = videos[position]
        holder.titleText.text = video.title

        val kind = when {
            video.isEncrypted && video.isVertical -> "Protected · Reels"
            video.isEncrypted -> "Protected"
            video.isVertical -> "Reels"
            else -> "Standard"
        }
        holder.badgeText.text = kind.uppercase()
        holder.subtitleText.text = if (video.isEncrypted) "Licensed to this device only" else "Open file"

        // Dim accent for plain videos
        holder.accentBar.alpha = if (video.isEncrypted) 1f else 0.35f

        holder.itemView.setOnClickListener { onClick(video) }
    }

    override fun getItemCount() = videos.size
}
