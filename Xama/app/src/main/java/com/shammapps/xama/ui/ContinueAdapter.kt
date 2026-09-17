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

class ContinueAdapter(
    private val videos: List<LocalVideo>,
    private val onClick: (LocalVideo) -> Unit,
) : RecyclerView.Adapter<ContinueAdapter.Holder>() {

    private val executor = Executors.newFixedThreadPool(2)
    private val main = Handler(Looper.getMainLooper())
    private var appContext: Context? = null

    class Holder(v: View) : RecyclerView.ViewHolder(v) {
        val image: ImageView = v.findViewById(R.id.continueImage)
        val title: TextView = v.findViewById(R.id.continueTitle)
    }

    override fun onAttachedToRecyclerView(recyclerView: RecyclerView) {
        super.onAttachedToRecyclerView(recyclerView)
        appContext = recyclerView.context.applicationContext
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder {
        val v = LayoutInflater.from(parent.context).inflate(R.layout.item_continue, parent, false)
        return Holder(v)
    }

    override fun onBindViewHolder(holder: Holder, position: Int) {
        val video = videos[position]
        holder.title.text = video.title
        holder.itemView.tag = video.id
        holder.image.setImageDrawable(null)
        holder.image.setBackgroundResource(R.drawable.thumb_placeholder)
        if (!video.isEncrypted) {
            val ctx = appContext ?: holder.itemView.context.applicationContext
            executor.execute {
                val r = MediaMetadataRetriever()
                val bmp = try {
                    when {
                        !video.contentUri.isNullOrBlank() -> r.setDataSource(ctx, Uri.parse(video.contentUri))
                        video.filePath.isNotBlank() -> r.setDataSource(video.filePath)
                        else -> null
                    }
                    r.getFrameAtTime(800_000, MediaMetadataRetriever.OPTION_CLOSEST_SYNC)
                } catch (_: Exception) {
                    null
                } finally {
                    try { r.release() } catch (_: Exception) {}
                }
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

    override fun getItemCount() = videos.size
}
