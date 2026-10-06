package com.shammapps.xama.ui

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView
import com.shammapps.xama.R
import com.shammapps.xama.data.VideoFolder
import com.shammapps.xama.util.ThumbLoader

class FolderAdapter(
    private val folders: List<VideoFolder>,
    private val onClick: (VideoFolder) -> Unit,
) : RecyclerView.Adapter<FolderAdapter.Holder>() {

    class Holder(v: View) : RecyclerView.ViewHolder(v) {
        val name: TextView = v.findViewById(R.id.folderName)
        val count: TextView = v.findViewById(R.id.folderCount)
        val thumb: ImageView = v.findViewById(R.id.folderThumb)
        val icon: ImageView = v.findViewById(R.id.folderIcon)
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder {
        val v = LayoutInflater.from(parent.context).inflate(R.layout.item_folder, parent, false)
        return Holder(v)
    }

    override fun onBindViewHolder(holder: Holder, position: Int) {
        val folder = folders[position]
        holder.name.text = folder.name
        val totalMs = folder.videos.sumOf { it.durationMs }
        holder.count.text = buildString {
            append(if (folder.videoCount == 1) "1 video" else "${folder.videoCount} videos")
            if (totalMs > 0) append(" · ").append(Ui.formatDuration(totalMs))
        }
        val isProtected = folder.videos.isNotEmpty() && folder.videos.all { it.isEncrypted }
        holder.icon.setImageResource(if (isProtected) R.drawable.ic_shield else R.drawable.ic_folder)
        val sample = folder.videos.firstOrNull { !it.isEncrypted }
        if (sample != null) {
            holder.icon.visibility = View.GONE
            ThumbLoader.load(holder.itemView.context, sample, holder.thumb, "folder-" + folder.name)
        } else {
            holder.icon.visibility = View.VISIBLE
            holder.thumb.tag = null
            holder.thumb.setImageDrawable(null)
            holder.thumb.setBackgroundResource(if (isProtected) R.drawable.thumb_protected else R.drawable.thumb_placeholder)
        }
        holder.itemView.setOnClickListener { onClick(folder) }
    }

    override fun getItemCount() = folders.size
}
