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
import com.shammapps.xama.data.VideoFolder
import java.util.concurrent.Executors

class FolderAdapter(
    private val folders: List<VideoFolder>,
    private val onClick: (VideoFolder) -> Unit,
) : RecyclerView.Adapter<FolderAdapter.Holder>() {

    private val executor = Executors.newFixedThreadPool(2)
    private val main = Handler(Looper.getMainLooper())
    private var appContext: Context? = null

    class Holder(v: View) : RecyclerView.ViewHolder(v) {
        val name: TextView = v.findViewById(R.id.folderName)
        val count: TextView = v.findViewById(R.id.folderCount)
        val thumb: ImageView = v.findViewById(R.id.folderThumb)
        val thumb2: ImageView = v.findViewById(R.id.folderThumb2)
        val emoji: TextView = v.findViewById(R.id.folderEmoji)
    }

    override fun onAttachedToRecyclerView(recyclerView: RecyclerView) {
        super.onAttachedToRecyclerView(recyclerView)
        appContext = recyclerView.context.applicationContext
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder {
        val v = LayoutInflater.from(parent.context).inflate(R.layout.item_folder, parent, false)
        return Holder(v)
    }

    override fun onBindViewHolder(holder: Holder, position: Int) {
        val folder = folders[position]
        holder.name.text = folder.name
        holder.count.text = if (folder.videoCount == 1) "1 video" else "${folder.videoCount} videos"
        holder.itemView.tag = folder.name
        holder.thumb.setImageDrawable(null)
        holder.thumb2.setImageDrawable(null)
        holder.thumb.setBackgroundResource(R.drawable.thumb_placeholder)
        holder.thumb2.setBackgroundResource(R.drawable.thumb_placeholder)
        holder.emoji.visibility = View.VISIBLE

        val samples = folder.videos.filter { !it.isEncrypted }.take(2)
        if (samples.isNotEmpty()) {
            val ctx = appContext ?: holder.itemView.context.applicationContext
            executor.execute {
                val b0 = load(ctx, samples[0])
                val b1 = if (samples.size > 1) load(ctx, samples[1]) else null
                main.post {
                    if (holder.itemView.tag != folder.name) return@post
                    if (b0 != null) {
                        holder.thumb.setImageBitmap(b0)
                        holder.thumb.background = null
                        holder.emoji.visibility = View.GONE
                    }
                    if (b1 != null) {
                        holder.thumb2.setImageBitmap(b1)
                        holder.thumb2.background = null
                    }
                }
            }
        }

        holder.itemView.setOnClickListener { onClick(folder) }
    }

    private fun load(ctx: Context, video: LocalVideo): Bitmap? {
        val r = MediaMetadataRetriever()
        return try {
            when {
                !video.contentUri.isNullOrBlank() -> r.setDataSource(ctx, Uri.parse(video.contentUri))
                video.filePath.isNotBlank() -> r.setDataSource(video.filePath)
                else -> return null
            }
            r.getFrameAtTime(500_000, MediaMetadataRetriever.OPTION_CLOSEST_SYNC)
        } catch (_: Exception) {
            null
        } finally {
            try { r.release() } catch (_: Exception) {}
        }
    }

    override fun getItemCount() = folders.size
}
