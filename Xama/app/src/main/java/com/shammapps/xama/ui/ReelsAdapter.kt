package com.shammapps.xama.ui

import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.view.LayoutInflater
import android.view.ViewGroup
import android.widget.ProgressBar
import android.widget.TextView
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
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

class ReelsAdapter(
    private val videos: List<LocalVideo>,
    private val keyResolver: ShammKeyResolver,
) : RecyclerView.Adapter<ReelsAdapter.ReelHolder>() {

    private val mainHandler = Handler(Looper.getMainLooper())

    inner class ReelHolder(itemView: android.view.View) : RecyclerView.ViewHolder(itemView) {
        val playerView: PlayerView = itemView.findViewById(R.id.reelPlayerView)
        val titleText: TextView = itemView.findViewById(R.id.reelTitleText)
        val hintText: TextView = itemView.findViewById(R.id.reelHintText)
        val progress: ProgressBar = itemView.findViewById(R.id.reelProgress)
        var player: ExoPlayer? = null
        var contentKey: ByteArray? = null
        private var progressRunnable: Runnable? = null

        fun startProgressUpdates() {
            stopProgressUpdates()
            val r = object : Runnable {
                override fun run() {
                    val p = player
                    if (p != null && p.duration > 0) {
                        val pct = ((p.currentPosition * 1000) / p.duration).toInt().coerceIn(0, 1000)
                        progress.progress = pct
                    }
                    mainHandler.postDelayed(this, 200)
                }
            }
            progressRunnable = r
            mainHandler.post(r)
        }

        fun stopProgressUpdates() {
            progressRunnable?.let { mainHandler.removeCallbacks(it) }
            progressRunnable = null
        }
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ReelHolder {
        val view = LayoutInflater.from(parent.context).inflate(R.layout.item_reel, parent, false)
        return ReelHolder(view)
    }

    override fun onBindViewHolder(holder: ReelHolder, position: Int) {
        val video = videos[position]
        holder.titleText.text = video.title
        holder.hintText.text = if (position < videos.size - 1) "Swipe up for next" else "End of feed"
        holder.progress.progress = 0

        val exo = ExoPlayer.Builder(holder.itemView.context).build()
        holder.player = exo
        holder.playerView.player = exo

        // Tap to play/pause
        holder.playerView.setOnClickListener {
            val p = holder.player ?: return@setOnClickListener
            p.playWhenReady = !p.playWhenReady
        }

        if (video.isEncrypted) {
            val resolved = keyResolver.resolve(video.id, video.ivBase64 ?: "")
            if (resolved == null) {
                holder.titleText.text = "${video.title}  ·  not licensed"
                return
            }
            holder.contentKey = resolved.contentKey
            val factory = DataSource.Factory { ShammDataSource(resolved.contentKey, resolved.iv) }
            val mediaSource = ProgressiveMediaSource.Factory(factory)
                .createMediaSource(MediaItem.fromUri(Uri.fromFile(resolved.videoFile)))
            exo.setMediaSource(mediaSource)
        } else {
            val uri = when {
                !video.contentUri.isNullOrBlank() -> Uri.parse(video.contentUri)
                video.filePath.isNotBlank() -> Uri.fromFile(File(video.filePath))
                else -> return
            }
            exo.setMediaItem(MediaItem.fromUri(uri))
        }

        exo.repeatMode = Player.REPEAT_MODE_ONE
        exo.prepare()
        holder.startProgressUpdates()
    }

    fun setPlaying(holder: ReelHolder, playing: Boolean) {
        holder.player?.playWhenReady = playing
        if (playing) holder.startProgressUpdates() else holder.stopProgressUpdates()
    }

    override fun onViewRecycled(holder: ReelHolder) {
        super.onViewRecycled(holder)
        holder.stopProgressUpdates()
        holder.player?.release()
        holder.player = null
        holder.contentKey?.let { NativeCrypto.shamm_wipe(it, NativeCrypto.KEY_LEN) }
        holder.contentKey = null
    }

    override fun getItemCount() = videos.size
}
