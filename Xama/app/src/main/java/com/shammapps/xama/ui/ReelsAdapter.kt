package com.shammapps.xama.ui

import android.net.Uri
import android.view.LayoutInflater
import android.view.ViewGroup
import android.widget.TextView
import androidx.media3.common.MediaItem
import androidx.media3.datasource.DataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.ProgressiveMediaSource
import androidx.media3.ui.PlayerView
import androidx.recyclerview.widget.RecyclerView
import com.shammapps.xama.R
import com.shammapps.xama.crypto.NativeCrypto
import com.shammapps.xama.crypto.ShammKeyResolver
import com.shammapps.xama.data.LocalVideo
import com.shammapps.xama.player.ShammDataSource
import java.io.File

/**
 * TikTok/Reels-style vertical feed: one video per full-screen page. Each
 * page owns its own ExoPlayer instance (created on bind, released on
 * unbind) rather than sharing one player across pages - simpler to reason
 * about correctly with ViewPager2's page recycling, at the cost of a brief
 * re-buffer when swiping, which is an acceptable trade for reliability.
 */
class ReelsAdapter(
    private val videos: List<LocalVideo>,
    private val keyResolver: ShammKeyResolver,
) : RecyclerView.Adapter<ReelsAdapter.ReelHolder>() {

    inner class ReelHolder(itemView: android.view.View) : RecyclerView.ViewHolder(itemView) {
        val playerView: PlayerView = itemView.findViewById(R.id.reelPlayerView)
        val titleText: TextView = itemView.findViewById(R.id.reelTitleText)
        var player: ExoPlayer? = null
        var contentKey: ByteArray? = null
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ReelHolder {
        val view = LayoutInflater.from(parent.context).inflate(R.layout.item_reel, parent, false)
        return ReelHolder(view)
    }

    override fun onBindViewHolder(holder: ReelHolder, position: Int) {
        val video = videos[position]
        holder.titleText.text = video.title

        val exo = ExoPlayer.Builder(holder.itemView.context).build()
        holder.player = exo
        holder.playerView.player = exo

        if (video.isEncrypted) {
            val resolved = keyResolver.resolve(video.id, video.ivBase64 ?: "")
            if (resolved == null) {
                holder.titleText.text = "${video.title}  ·  not licensed for this device"
                return
            }
            holder.contentKey = resolved.contentKey
            val factory = DataSource.Factory { ShammDataSource(resolved.contentKey, resolved.iv) }
            val mediaSource = ProgressiveMediaSource.Factory(factory)
                .createMediaSource(MediaItem.fromUri(Uri.fromFile(resolved.videoFile)))
            exo.setMediaSource(mediaSource)
        } else {
            exo.setMediaItem(MediaItem.fromUri(Uri.fromFile(File(video.filePath))))
        }

        exo.repeatMode = ExoPlayer.REPEAT_MODE_ONE // reels loop, like the real apps
        exo.prepare()
    }

    /** Called by ReelsActivity's page-change callback: only the currently
     * visible page should actually be playing audio/video. */
    fun setPlaying(holder: ReelHolder, playing: Boolean) {
        holder.player?.playWhenReady = playing
    }

    override fun onViewRecycled(holder: ReelHolder) {
        super.onViewRecycled(holder)
        holder.player?.release()
        holder.player = null
        holder.contentKey?.let { NativeCrypto.shamm_wipe(it, NativeCrypto.KEY_LEN) }
        holder.contentKey = null
    }

    override fun getItemCount() = videos.size
}
