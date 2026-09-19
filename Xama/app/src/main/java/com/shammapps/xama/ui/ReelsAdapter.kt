package com.shammapps.xama.ui

import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.view.GestureDetector
import android.view.LayoutInflater
import android.view.MotionEvent
import android.view.View
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

    inner class ReelHolder(itemView: View) : RecyclerView.ViewHolder(itemView) {
        val playerView: PlayerView = itemView.findViewById(R.id.reelPlayerView)
        val titleText: TextView = itemView.findViewById(R.id.reelTitleText)
        val hintText: TextView = itemView.findViewById(R.id.reelHintText)
        val progress: ProgressBar = itemView.findViewById(R.id.reelProgress)
        val seekHint: TextView = itemView.findViewById(R.id.reelSeekHint)
        var player: ExoPlayer? = null
        var contentKey: ByteArray? = null
        private var progressRunnable: Runnable? = null

        fun startProgressUpdates() {
            stopProgressUpdates()
            val r = object : Runnable {
                override fun run() {
                    val p = player
                    if (p != null && p.duration > 0) {
                        progress.progress = ((p.currentPosition * 1000) / p.duration).toInt().coerceIn(0, 1000)
                    }
                    mainHandler.postDelayed(this, 250)
                }
            }
            progressRunnable = r
            mainHandler.post(r)
        }

        fun stopProgressUpdates() {
            progressRunnable?.let { mainHandler.removeCallbacks(it) }
            progressRunnable = null
        }

        fun showSeek(text: String) {
            seekHint.text = text
            seekHint.visibility = View.VISIBLE
            mainHandler.removeCallbacks(hideSeek)
            mainHandler.postDelayed(hideSeek, 600)
        }

        private val hideSeek = Runnable { seekHint.visibility = View.GONE }
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ReelHolder {
        val view = LayoutInflater.from(parent.context).inflate(R.layout.item_reel, parent, false)
        return ReelHolder(view)
    }

    override fun onBindViewHolder(holder: ReelHolder, position: Int) {
        // Always release previous player for this holder first
        releaseHolder(holder)

        val video = videos[position]
        holder.titleText.text = video.title
        holder.hintText.text = if (position < videos.size - 1)
            "Double-tap sides to seek · Swipe up for next"
        else
            "Double-tap sides to seek · End of feed"
        holder.progress.progress = 0

        val ctx = holder.itemView.context
        val exo = ExoPlayer.Builder(ctx).build()
        holder.player = exo
        holder.playerView.player = exo

        // Gestures: tap pause, double-tap seek ±10s
        val detector = GestureDetector(ctx, object : GestureDetector.SimpleOnGestureListener() {
            override fun onSingleTapConfirmed(e: MotionEvent): Boolean {
                val p = holder.player ?: return true
                p.playWhenReady = !p.playWhenReady
                return true
            }

            override fun onDoubleTap(e: MotionEvent): Boolean {
                val p = holder.player ?: return true
                val w = holder.playerView.width.coerceAtLeast(1)
                if (e.x < w / 2f) {
                    val target = (p.currentPosition - 10_000).coerceAtLeast(0)
                    p.seekTo(target)
                    holder.showSeek("−10s")
                } else {
                    val dur = p.duration.coerceAtLeast(0)
                    val target = (p.currentPosition + 10_000).coerceAtMost(if (dur > 0) dur else Long.MAX_VALUE / 4)
                    p.seekTo(target)
                    holder.showSeek("+10s")
                }
                return true
            }
        })
        holder.playerView.setOnTouchListener { _, ev ->
            detector.onTouchEvent(ev)
            true
        }

        try {
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
        } catch (_: Exception) {
            holder.titleText.text = "${video.title}  ·  can't play"
        }
    }

    fun setPlaying(holder: ReelHolder, playing: Boolean) {
        holder.player?.playWhenReady = playing
        if (playing) holder.startProgressUpdates() else holder.stopProgressUpdates()
    }

    private fun releaseHolder(holder: ReelHolder) {
        holder.stopProgressUpdates()
        holder.playerView.player = null
        holder.player?.release()
        holder.player = null
        holder.contentKey?.let {
            try { NativeCrypto.shamm_wipe(it, NativeCrypto.KEY_LEN) } catch (_: Exception) {}
        }
        holder.contentKey = null
    }

    override fun onViewRecycled(holder: ReelHolder) {
        super.onViewRecycled(holder)
        releaseHolder(holder)
    }

    override fun getItemCount() = videos.size
}
