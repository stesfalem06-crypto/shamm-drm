package com.shammapps.xama.ui

import android.content.Context
import android.text.format.Formatter
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
import com.shammapps.xama.data.VideoFolder
import com.shammapps.xama.util.ThumbLoader

/** Shared helpers for the classic folder / video list screens. */
object MxFormat {
    private const val NEW_WINDOW_SEC = 7L * 24 * 3600

    /** "NEW" = added in the last 7 days and never opened. */
    fun isNew(context: Context, v: LocalVideo, watched: Set<String>): Boolean {
        if (v.id in watched) return false
        if (PlaybackPositions.get(context, v.id) != null) return false
        val now = System.currentTimeMillis() / 1000
        return v.dateAddedSec > 0 && now - v.dateAddedSec < NEW_WINDOW_SEC
    }

    fun resolution(v: LocalVideo): String {
        val shortSide = minOf(v.width, v.height)
        if (shortSide <= 0) return ""
        return when {
            shortSide >= 2160 -> "4K"
            shortSide >= 1440 -> "1440p"
            shortSide >= 1080 -> "1080p"
            shortSide >= 720 -> "720p"
            shortSide >= 480 -> "480p"
            shortSide >= 360 -> "360p"
            else -> "${shortSide}p"
        }
    }

    fun meta(context: Context, v: LocalVideo): String {
        val parts = ArrayList<String>(3)
        if (v.isEncrypted) parts.add(if (v.isPdf) "Protected PDF" else "Protected")
        if (v.sizeBytes > 0) parts.add(Formatter.formatShortFileSize(context, v.sizeBytes))
        resolution(v).takeIf { it.isNotEmpty() }?.let { parts.add(it) }
        return parts.joinToString("  •  ")
    }
}

class MxFolderAdapter(
    private val folders: List<VideoFolder>,
    private val grid: Boolean,
    private val watched: Set<String>,
    private val countLabel: String = "videos",
    private val onClick: (VideoFolder) -> Unit,
) : RecyclerView.Adapter<MxFolderAdapter.Holder>() {

    class Holder(v: View) : RecyclerView.ViewHolder(v) {
        val name: TextView = v.findViewById(R.id.folderName)
        val count: TextView = v.findViewById(R.id.folderCount)
        val badge: TextView = v.findViewById(R.id.folderNew)
        val icon: ImageView = v.findViewById(R.id.folderIcon)
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder = Holder(
        LayoutInflater.from(parent.context)
            .inflate(if (grid) R.layout.item_mx_folder_grid else R.layout.item_mx_folder, parent, false)
    )

    override fun onBindViewHolder(holder: Holder, position: Int) {
        val f = folders[position]
        val ctx = holder.itemView.context
        holder.name.text = f.name
        holder.count.text = if (f.videoCount == 1) "1 ${countLabel.removeSuffix("s")}" else "${f.videoCount} $countLabel"
        holder.icon.setImageResource(
            when {
                f.name == "Protected" -> R.drawable.ic_shield
                countLabel == "tracks" -> R.drawable.ic_music
                else -> R.drawable.ic_folder
            }
        )
        val newCount = f.videos.count { MxFormat.isNew(ctx, it, watched) }
        holder.badge.visibility = if (newCount > 0) View.VISIBLE else View.GONE
        holder.badge.text = if (grid || newCount <= 1) "NEW" else "$newCount NEW"
        holder.itemView.setOnClickListener { onClick(f) }
    }

    override fun getItemCount() = folders.size
}

class MxVideoAdapter(
    private val items: List<LocalVideo>,
    /** false = compact list rows, true = large 16:9 thumbnail cards. */
    private val cards: Boolean,
    private val watched: Set<String>,
    private val onLong: ((LocalVideo) -> Unit)? = null,
    private val onClick: (LocalVideo) -> Unit,
) : RecyclerView.Adapter<MxVideoAdapter.Holder>() {

    class Holder(v: View) : RecyclerView.ViewHolder(v) {
        val thumb: ImageView = v.findViewById(R.id.videoThumb)
        val duration: TextView = v.findViewById(R.id.videoDuration)
        val progress: ProgressBar = v.findViewById(R.id.videoProgress)
        val title: TextView = v.findViewById(R.id.videoTitle)
        val meta: TextView = v.findViewById(R.id.videoMeta)
        val badge: TextView = v.findViewById(R.id.videoNew)
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder = Holder(
        LayoutInflater.from(parent.context)
            .inflate(if (cards) R.layout.item_mx_video_card else R.layout.item_mx_video, parent, false)
    )

    override fun onBindViewHolder(holder: Holder, position: Int) {
        val v = items[position]
        val ctx = holder.itemView.context
        holder.title.text = v.title
        holder.meta.text = MxFormat.meta(ctx, v)
        if (v.isPdf) {
            holder.duration.visibility = View.VISIBLE
            holder.duration.text = "PDF"
        } else if (v.durationMs > 0) {
            holder.duration.visibility = View.VISIBLE
            holder.duration.text = Ui.formatDuration(v.durationMs)
        } else {
            holder.duration.visibility = View.GONE
        }
        val pos = PlaybackPositions.get(ctx, v.id)
        if (pos != null && pos.fraction > 0f) {
            holder.progress.visibility = View.VISIBLE
            holder.progress.progress = (pos.fraction * 1000).toInt()
        } else {
            holder.progress.visibility = View.GONE
        }
        holder.badge.visibility = if (MxFormat.isNew(ctx, v, watched)) View.VISIBLE else View.GONE
        ThumbLoader.load(ctx, v, holder.thumb, "mx-${v.id}")
        holder.itemView.setOnClickListener { onClick(v) }
        holder.itemView.setOnLongClickListener { onLong?.invoke(v); onLong != null }
    }

    override fun getItemCount() = items.size
}
