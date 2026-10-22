package com.shammapps.xama.ui

import android.app.Activity
import android.content.Context
import android.media.AudioManager
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.view.GestureDetector
import android.view.LayoutInflater
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.SeekBar
import android.widget.TextView
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.datasource.DataSource
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.ProgressiveMediaSource
import androidx.media3.ui.PlayerView
import androidx.recyclerview.widget.RecyclerView
import com.shammapps.xama.R
import com.shammapps.xama.crypto.NativeCrypto
import com.shammapps.xama.crypto.ShammKeyResolver
import com.shammapps.xama.data.Favorites
import com.shammapps.xama.data.LocalVideo
import com.shammapps.xama.player.ShammDataSource
import java.io.File
import kotlin.math.abs

/**
 * Vertical reels. Only the CURRENT page owns an ExoPlayer (one hardware video decoder);
 * neighbours are created lazily on selection and released when they leave. This avoids
 * exhausting the phone's decoder instances (the "Format not supported" error seen when
 * switching between the Videos player and Reels).
 */
class ReelsAdapter(
    private val videos: List<LocalVideo>,
    private val keyResolver: ShammKeyResolver,
    /** Headphones button: (position, currentPositionMs) -> hand off to background audio. */
    private val onBackground: (Int, Long) -> Unit = { _, _ -> },
) : RecyclerView.Adapter<ReelsAdapter.ReelHolder>() {

    private val mainHandler = Handler(Looper.getMainLooper())
    private val bound = HashSet<ReelHolder>()

    inner class ReelHolder(itemView: View) : RecyclerView.ViewHolder(itemView) {
        val playerView: PlayerView = itemView.findViewById(R.id.reelPlayerView)
        val titleText: TextView = itemView.findViewById(R.id.reelTitleText)
        val hintText: TextView = itemView.findViewById(R.id.reelHintText)
        val seek: SeekBar = itemView.findViewById(R.id.reelSeek)
        val timePos: TextView = itemView.findViewById(R.id.reelTimePos)
        val timeDur: TextView = itemView.findViewById(R.id.reelTimeDur)
        val centerIcon: ImageView = itemView.findViewById(R.id.reelCenterIcon)
        var dragging = false
        val seekHint: TextView = itemView.findViewById(R.id.reelSeekHint)
        var player: ExoPlayer? = null
        var video: LocalVideo? = null
        var retried = false
        var resumeAt = 0L
        var wantPlaying = false
        val bigHeart: ImageView = itemView.findViewById(R.id.reelBigHeart)
        val like: ImageView = itemView.findViewById(R.id.reelLike)
        val fav: ImageView = itemView.findViewById(R.id.reelFav)
        val level: View = itemView.findViewById(R.id.reelLevel)
        val levelIcon: ImageView = itemView.findViewById(R.id.reelLevelIcon)
        val levelBar: android.widget.ProgressBar = itemView.findViewById(R.id.reelLevelBar)
        val levelText: TextView = itemView.findViewById(R.id.reelLevelText)
        val sliderBright: SeekBar = itemView.findViewById(R.id.reelSliderBright)
        val sliderVol: SeekBar = itemView.findViewById(R.id.reelSliderVol)
        var contentKey: ByteArray? = null
        private var progressRunnable: Runnable? = null

        fun startProgressUpdates() {
            stopProgressUpdates()
            val r = object : Runnable {
                override fun run() {
                    val p = player
                    if (p != null && p.duration > 0 && !dragging) {
                        seek.progress = ((p.currentPosition * 1000) / p.duration).toInt().coerceIn(0, 1000)
                        timePos.text = Ui.formatDuration(p.currentPosition)
                        timeDur.text = Ui.formatDuration(p.duration)
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
        private val hideLevel = Runnable { level.visibility = View.GONE }

        fun showLevel(icon: Int, pct: Int) {
            levelIcon.setImageResource(icon)
            levelBar.progress = pct.coerceIn(0, 100)
            levelText.text = "$pct"
            level.visibility = View.VISIBLE
            mainHandler.removeCallbacks(hideLevel)
            mainHandler.postDelayed(hideLevel, 900)
        }

        fun bindMarks() {
            val v = video ?: return
            val ctx = itemView.context
            like.setImageResource(if (Favorites.isLiked(ctx, v.id)) R.drawable.ic_heart else R.drawable.ic_heart_outline)
            like.setColorFilter(if (Favorites.isLiked(ctx, v.id)) 0xFFFF2D55.toInt() else 0xFFFFFFFF.toInt())
            fav.setImageResource(if (Favorites.isFav(ctx, v.id)) R.drawable.ic_bookmark else R.drawable.ic_bookmark_outline)
            fav.setColorFilter(if (Favorites.isFav(ctx, v.id)) 0xFFFFC107.toInt() else 0xFFFFFFFF.toInt())
        }

        fun popHeart() {
            bigHeart.animate().cancel()
            bigHeart.alpha = 1f; bigHeart.scaleX = 0.4f; bigHeart.scaleY = 0.4f; bigHeart.rotation = -12f
            bigHeart.animate().scaleX(1.1f).scaleY(1.1f).rotation(0f).setDuration(220)
                .setInterpolator(android.view.animation.OvershootInterpolator()).withEndAction {
                    bigHeart.animate().alpha(0f).scaleX(1.4f).scaleY(1.4f).translationY(-80f).setDuration(380)
                        .withEndAction { bigHeart.translationY = 0f }.start()
                }.start()
        }

        /** Center play/pause icon: stays while paused, flashes away on resume. */
        fun showPlayState(playing: Boolean) {
            centerIcon.setImageResource(if (playing) R.drawable.ic_pause else R.drawable.ic_play)
            centerIcon.animate().cancel()
            centerIcon.alpha = 1f
            centerIcon.scaleX = 0.8f
            centerIcon.scaleY = 0.8f
            centerIcon.animate().scaleX(1f).scaleY(1f).setDuration(150).withEndAction {
                if (playing) centerIcon.animate().alpha(0f).setStartDelay(350).setDuration(250).start()
            }.start()
        }
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ReelHolder {
        val view = LayoutInflater.from(parent.context).inflate(R.layout.item_reel, parent, false)
        return ReelHolder(view)
    }

    override fun onBindViewHolder(holder: ReelHolder, position: Int) {
        releaseHolder(holder)
        bound += holder
        val video = videos[position]
        holder.video = video
        holder.retried = false
        holder.resumeAt = 0L
        holder.titleText.text = video.title
        holder.hintText.text = if (position < videos.size - 1)
            "Double-tap to like · Swipe up for next"
        else
            "Double-tap to like · End of feed"
        holder.seek.progress = 0
        holder.itemView.findViewById<View>(R.id.reelBackground).apply {
            alpha = if (com.shammapps.xama.player.BackgroundPlay.isAllowed(video)) 1f else 0.4f
            setOnClickListener {
                val pos = holder.bindingAdapterPosition
                if (pos != RecyclerView.NO_POSITION) onBackground(pos, holder.player?.currentPosition ?: 0L)
            }
        }
        val ctx = holder.itemView.context
        holder.bindMarks()
        holder.like.setOnClickListener {
            val on = Favorites.toggle(ctx, Favorites.LIKED, video.id)
            holder.bindMarks()
            if (on) holder.popHeart()
        }
        holder.fav.setOnClickListener {
            val on = Favorites.toggle(ctx, Favorites.FAV, video.id)
            holder.bindMarks()
            android.widget.Toast.makeText(ctx, if (on) "Added to Favorites" else "Removed from Favorites", android.widget.Toast.LENGTH_SHORT).show()
        }
        bindSliders(holder)
        holder.timePos.text = "0:00"
        holder.timeDur.text = if (video.durationMs > 0) Ui.formatDuration(video.durationMs) else "0:00"
        holder.centerIcon.animate().cancel()
        holder.centerIcon.alpha = 0f
        holder.dragging = false
        holder.seek.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar, value: Int, fromUser: Boolean) {
                val dur = holder.player?.duration ?: 0L
                if (fromUser && dur > 0) holder.timePos.text = Ui.formatDuration(dur * value / 1000)
            }
            override fun onStartTrackingTouch(sb: SeekBar) { holder.dragging = true }
            override fun onStopTrackingTouch(sb: SeekBar) {
                holder.dragging = false
                val p = holder.player ?: return
                if (p.duration > 0) p.seekTo(p.duration * sb.progress / 1000)
            }
        })

        // Gestures: tap = pause/play; double-tap = like (edges = seek ±10s);
        // horizontal drag: left half = brightness, right half = volume. Vertical drag = next/prev (pager).
        val detector = GestureDetector(ctx, object : GestureDetector.SimpleOnGestureListener() {
            override fun onSingleTapConfirmed(e: MotionEvent): Boolean {
                val p = holder.player ?: return true
                p.playWhenReady = !p.playWhenReady
                holder.showPlayState(p.playWhenReady)
                return true
            }

            override fun onDoubleTap(e: MotionEvent): Boolean {
                val p = holder.player
                val w = holder.playerView.width.coerceAtLeast(1)
                when {
                    p != null && e.x < w * 0.2f -> {
                        p.seekTo((p.currentPosition - 10_000).coerceAtLeast(0)); holder.showSeek("−10s")
                    }
                    p != null && e.x > w * 0.8f -> {
                        val dur = p.duration.coerceAtLeast(0)
                        p.seekTo((p.currentPosition + 10_000).coerceAtMost(if (dur > 0) dur else Long.MAX_VALUE / 4))
                        holder.showSeek("+10s")
                    }
                    else -> {
                        Favorites.set(ctx, Favorites.LIKED, video.id, true)
                        holder.bindMarks()
                        holder.popHeart()
                    }
                }
                return true
            }
        })
        val slop = android.view.ViewConfiguration.get(ctx).scaledTouchSlop
        var downX = 0f; var downY = 0f; var horiz = false; var leftSide = true; var startLevel = 0f
        holder.playerView.setOnTouchListener { v, ev ->
            when (ev.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    downX = ev.x; downY = ev.y; horiz = false
                    leftSide = ev.x < v.width / 2f
                    startLevel = if (leftSide) brightness(ctx) else volumeFraction(ctx)
                }
                MotionEvent.ACTION_MOVE -> {
                    val dx = ev.x - downX; val dy = ev.y - downY
                    if (!horiz && abs(dx) > slop * 1.5f && abs(dx) > abs(dy) * 1.3f) {
                        horiz = true
                        v.parent?.requestDisallowInterceptTouchEvent(true)
                        val cancel = MotionEvent.obtain(ev).apply { action = MotionEvent.ACTION_CANCEL }
                        detector.onTouchEvent(cancel); cancel.recycle()
                    }
                    if (horiz) {
                        val frac = (startLevel + dx / (v.width.coerceAtLeast(1) * 0.8f)).coerceIn(0f, 1f)
                        if (leftSide) {
                            setBrightness(ctx, frac); holder.showLevel(R.drawable.ic_brightness, (frac * 100).toInt())
                            holder.sliderBright.progress = (frac * 100).toInt()
                        } else {
                            setVolumeFraction(ctx, frac); holder.showLevel(R.drawable.ic_volume, (frac * 100).toInt())
                            holder.sliderVol.progress = (frac * 100).toInt()
                        }
                        return@setOnTouchListener true
                    }
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    if (horiz) { horiz = false; v.parent?.requestDisallowInterceptTouchEvent(false); return@setOnTouchListener true }
                }
            }
            detector.onTouchEvent(ev)
            true
        }
        if (holder.wantPlaying) activate(holder)
    }

    // ---- tiny sliders option ----
    private fun slidersOn(ctx: Context) = ctx.getSharedPreferences("xama_ui", Context.MODE_PRIVATE).getBoolean("reel_sliders", false)

    private fun bindSliders(holder: ReelHolder) {
        val ctx = holder.itemView.context
        fun apply() {
            val on = slidersOn(ctx)
            holder.sliderBright.visibility = if (on) View.VISIBLE else View.GONE
            holder.sliderVol.visibility = if (on) View.VISIBLE else View.GONE
            holder.sliderBright.progress = (brightness(ctx) * 100).toInt()
            holder.sliderVol.progress = (volumeFraction(ctx) * 100).toInt()
            holder.itemView.findViewById<ImageView>(R.id.reelSliders).setColorFilter(if (on) 0xFFFFC107.toInt() else 0xFFFFFFFF.toInt())
        }
        apply()
        holder.itemView.findViewById<View>(R.id.reelSliders).setOnClickListener {
            ctx.getSharedPreferences("xama_ui", Context.MODE_PRIVATE).edit().putBoolean("reel_sliders", !slidersOn(ctx)).apply()
            bound.forEach { bindSliders(it) }
        }
        holder.sliderBright.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar, value: Int, fromUser: Boolean) {
                if (fromUser) { setBrightness(ctx, value / 100f); holder.showLevel(R.drawable.ic_brightness, value) }
            }
            override fun onStartTrackingTouch(sb: SeekBar) {}
            override fun onStopTrackingTouch(sb: SeekBar) {}
        })
        holder.sliderVol.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar, value: Int, fromUser: Boolean) {
                if (fromUser) { setVolumeFraction(ctx, value / 100f); holder.showLevel(R.drawable.ic_volume, value) }
            }
            override fun onStartTrackingTouch(sb: SeekBar) {}
            override fun onStopTrackingTouch(sb: SeekBar) {}
        })
    }

    private fun brightness(ctx: Context): Float {
        val a = ctx as? Activity ?: return 0.5f
        val b = a.window.attributes.screenBrightness
        if (b >= 0f) return b
        return try {
            android.provider.Settings.System.getInt(a.contentResolver, android.provider.Settings.System.SCREEN_BRIGHTNESS) / 255f
        } catch (_: Exception) { 0.5f }
    }

    private fun setBrightness(ctx: Context, f: Float) {
        val a = ctx as? Activity ?: return
        val lp = a.window.attributes
        lp.screenBrightness = f.coerceIn(0.01f, 1f)
        a.window.attributes = lp
    }

    private fun volumeFraction(ctx: Context): Float {
        val am = ctx.getSystemService(Context.AUDIO_SERVICE) as AudioManager
        val max = am.getStreamMaxVolume(AudioManager.STREAM_MUSIC).coerceAtLeast(1)
        return am.getStreamVolume(AudioManager.STREAM_MUSIC).toFloat() / max
    }

    private fun setVolumeFraction(ctx: Context, f: Float) {
        val am = ctx.getSystemService(Context.AUDIO_SERVICE) as AudioManager
        val max = am.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
        try { am.setStreamVolume(AudioManager.STREAM_MUSIC, (f * max).toInt().coerceIn(0, max), 0) } catch (_: Exception) {}
    }

    // ---- single-owner player ----
    private fun activate(holder: ReelHolder, decoderFallback: Boolean = false, startAt: Long = holder.resumeAt) {
        if (holder.player != null) return
        val video = holder.video ?: return
        val ctx = holder.itemView.context
        val renderers = DefaultRenderersFactory(ctx).setEnableDecoderFallback(true)
        val exo = ExoPlayer.Builder(ctx, renderers).build()
        holder.player = exo
        holder.playerView.player = exo
        exo.addListener(object : Player.Listener {
            override fun onPlayerError(error: PlaybackException) {
                // Retry once with a fresh player (frees a stuck/contended decoder).
                if (!holder.retried && holder.player === exo) {
                    holder.retried = true
                    val pos = exo.currentPosition
                    releasePlayerOnly(holder)
                    mainHandler.postDelayed({ if (holder.wantPlaying) activate(holder, true, pos) }, 300)
                } else {
                    holder.titleText.text = "${video.title}  ·  can't play"
                }
            }
        })
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
            if (startAt > 0) exo.seekTo(startAt)
            exo.prepare()
            exo.playWhenReady = holder.wantPlaying
        } catch (_: Exception) {
            holder.titleText.text = "${video.title}  ·  can't play"
        }
    }

    fun setPlaying(holder: ReelHolder, playing: Boolean) {
        holder.wantPlaying = playing
        if (playing) {
            activate(holder)
            holder.player?.playWhenReady = true
            holder.startProgressUpdates()
        } else {
            holder.stopProgressUpdates()
            releasePlayerOnly(holder)
        }
    }

    private fun releasePlayerOnly(holder: ReelHolder) {
        holder.playerView.player = null
        holder.player?.let { p -> if (p.currentPosition > 0) holder.resumeAt = p.currentPosition }
        holder.player?.let { runCatching { it.stop(); it.clearMediaItems() }; it.release() }
        holder.player = null
        holder.contentKey?.let {
            try { NativeCrypto.shamm_wipe(it, NativeCrypto.KEY_LEN) } catch (_: Exception) {}
        }
        holder.contentKey = null
    }

    private fun releaseHolder(holder: ReelHolder) {
        holder.stopProgressUpdates()
        releasePlayerOnly(holder)
    }

    /** Must be called from the activity's onDestroy: holders are not recycled when the activity dies. */
    fun releaseAll() {
        bound.forEach { it.wantPlaying = false; releaseHolder(it) }
        bound.clear()
        mainHandler.removeCallbacksAndMessages(null)
    }

    override fun onViewRecycled(holder: ReelHolder) {
        super.onViewRecycled(holder)
        holder.wantPlaying = false
        releaseHolder(holder)
        bound -= holder
    }

    override fun getItemCount() = videos.size
}
