package com.shammapps.xama.ui

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.ProgressBar
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView
import com.shammapps.xama.R
import com.shammapps.xama.data.LocalVideo
import com.shammapps.xama.data.PlaybackPositions
import com.shammapps.xama.util.ThumbLoader

/** Video list row: 16:9 thumbnail, title, meta line, badge and resume progress. */
class PosterAdapter(
    private val videos: List<LocalVideo>,
    private val onClick: (LocalVideo) -> Unit,
) : RecyclerView.Adapter<PosterAdapter.Holder>() {

    class Holder(v: View) : RecyclerView.ViewHolder(v) {
        val image: ImageView = v.findViewById(R.id.posterImage)
        val glyph: ImageView = v.findViewById(R.id.posterGlyph)
        val title: TextView = v.findViewById(R.id.posterTitle)
        val meta: TextView = v.findViewById(R.id.posterMeta)
        val duration: TextView = v.findViewById(R.id.posterDuration)
        val badge: TextView = v.findViewById(R.id.posterBadge)
        val progress: ProgressBar = v.findViewById(R.id.posterProgress)
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder {
        val v = LayoutInflater.from(parent.context).inflate(R.layout.item_poster, parent, false)
        return Holder(v)
    }

    override fun onBindViewHolder(holder: Holder, position: Int) {
        val video = videos[position]
        val ctx = holder.itemView.context
        holder.title.text = video.title
        holder.meta.text = buildString {
            append(video.folderName.ifBlank { "Video" })
            if (video.isEncrypted) append(" · Licensed to this device")
        }
        if (video.durationMs > 0) {
            holder.duration.text = Ui.formatDuration(video.durationMs)
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
                holder.badge.text = "REEL"
                holder.badge.visibility = View.VISIBLE
            }
            else -> holder.badge.visibility = View.GONE
        }
        holder.glyph.visibility = if (video.isEncrypted) View.VISIBLE else View.GONE
        holder.glyph.setImageResource(if (video.isEncrypted) R.drawable.ic_shield else R.drawable.ic_movie)

        val saved = PlaybackPositions.get(ctx, video.id)
        if (saved != null && saved.fraction > 0.01f) {
            holder.progress.visibility = View.VISIBLE
            holder.progress.progress = (saved.fraction * 1000).toInt()
        } else {
            holder.progress.visibility = View.GONE
        }
        ThumbLoader.load(ctx, video, holder.image, video.id)
        holder.itemView.setOnClickListener { onClick(video) }
    }

    override fun getItemCount() = videos.size
}
