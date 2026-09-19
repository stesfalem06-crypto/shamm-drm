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
        val thumb2: ImageView = v.findViewById(R.id.folderThumb2)
        val emoji: TextView = v.findViewById(R.id.folderEmoji)
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder {
        val v = LayoutInflater.from(parent.context).inflate(R.layout.item_folder, parent, false)
        return Holder(v)
    }

    override fun onBindViewHolder(holder: Holder, position: Int) {
        val folder = folders[position]
        holder.name.text = folder.name
        holder.count.text = if (folder.videoCount == 1) "1 video" else "${folder.videoCount} videos"
        holder.emoji.visibility = View.VISIBLE
        val samples = folder.videos.filter { !it.isEncrypted }.take(2)
        if (samples.isNotEmpty()) {
            ThumbLoader.load(holder.itemView.context, samples[0], holder.thumb, folder.name + "-0")
            holder.emoji.visibility = View.GONE
            if (samples.size > 1) {
                ThumbLoader.load(holder.itemView.context, samples[1], holder.thumb2, folder.name + "-1")
            }
        } else {
            holder.thumb.setImageDrawable(null)
            holder.thumb.setBackgroundResource(R.drawable.thumb_placeholder)
            holder.thumb2.setImageDrawable(null)
            holder.thumb2.setBackgroundResource(R.drawable.thumb_placeholder)
        }
        holder.itemView.setOnClickListener { onClick(folder) }
    }

    override fun getItemCount() = folders.size
}
