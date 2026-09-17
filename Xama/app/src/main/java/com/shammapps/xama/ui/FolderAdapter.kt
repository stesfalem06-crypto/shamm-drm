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
        holder.thumb.setBackgroundResource(R.drawable.thumb_placeholder)

        val sample = folder.videos.firstOrNull { !it.isEncrypted }
        if (sample != null) {
            val ctx = appContext ?: holder.itemView.context.applicationContext
            executor.execute {
                val r = MediaMetadataRetriever()
                val bmp: Bitmap? = try {
                    when {
                        !sample.contentUri.isNullOrBlank() -> r.setDataSource(ctx, Uri.parse(sample.contentUri))
                        sample.filePath.isNotBlank() -> r.setDataSource(sample.filePath)
                        else -> null
                    }
                    r.getFrameAtTime(500_000, MediaMetadataRetriever.OPTION_CLOSEST_SYNC)
                } catch (_: Exception) {
                    null
                } finally {
                    try { r.release() } catch (_: Exception) {}
                }
                main.post {
                    if (holder.itemView.tag == folder.name && bmp != null) {
                        holder.thumb.setImageBitmap(bmp)
                    }
                }
            }
        }

        holder.itemView.setOnClickListener { onClick(folder) }
    }

    override fun getItemCount() = folders.size
}
