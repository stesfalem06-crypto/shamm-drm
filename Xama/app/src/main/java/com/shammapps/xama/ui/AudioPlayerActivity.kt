package com.shammapps.xama.ui

import android.Manifest
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.RenderEffect
import android.graphics.Shader
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.View
import android.widget.ImageButton
import android.widget.ImageView
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.annotation.OptIn
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import androidx.media3.ui.DefaultTimeBar
import androidx.media3.ui.TimeBar
import com.google.common.util.concurrent.ListenableFuture
import com.shammapps.xama.R
import com.shammapps.xama.data.LibraryCache
import com.shammapps.xama.data.LocalVideo
import com.shammapps.xama.player.PlaybackService
import com.shammapps.xama.util.ThumbLoader
import java.io.File

/**
 * Music player screen. Playback itself lives in [PlaybackService] so music keeps
 * playing in the background with a media notification; this screen is a
 * MediaController UI on top of it.
 */
@OptIn(UnstableApi::class)
class AudioPlayerActivity : AppCompatActivity() {

    companion object {
        const val EXTRA_IDS = "audio_ids"
        const val EXTRA_INDEX = "audio_index"
        const val EXTRA_SHUFFLE = "audio_shuffle"
        private const val PREFS = "xama_audio"

        fun start(context: Context, tracks: List<LocalVideo>, index: Int, shuffle: Boolean) {
            if (tracks.isEmpty()) return
            context.startActivity(Intent(context, AudioPlayerActivity::class.java).apply {
                putStringArrayListExtra(EXTRA_IDS, ArrayList(tracks.map { it.id }))
                putExtra(EXTRA_INDEX, index)
                putExtra(EXTRA_SHUFFLE, shuffle)
                addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP)
            })
        }

        fun toMediaItem(track: LocalVideo): MediaItem? {
            val uri = when {
                !track.contentUri.isNullOrBlank() -> Uri.parse(track.contentUri)
                track.filePath.isNotBlank() -> Uri.fromFile(File(track.filePath))
                else -> return null
            }
            val meta = MediaMetadata.Builder()
                .setTitle(track.title)
                .setArtist(track.artist.ifBlank { null })
                .setAlbumTitle(track.album.ifBlank { null })
                .setIsPlayable(true)
                .setIsBrowsable(false)
                .build()
            return MediaItem.Builder()
                .setMediaId(track.id)
                .setUri(uri)
                .setRequestMetadata(MediaItem.RequestMetadata.Builder().setMediaUri(uri).build())
                .setMediaMetadata(meta)
                .build()
        }
    }

    private var controllerFuture: ListenableFuture<MediaController>? = null
    private var controller: MediaController? = null
    private var pendingQueue: Intent? = null
    private var artKey: String? = null

    private lateinit var art: ImageView
    private lateinit var backdrop: ImageView
    private lateinit var title: TextView
    private lateinit var artist: TextView
    private lateinit var timeBar: DefaultTimeBar
    private lateinit var position: TextView
    private lateinit var duration: TextView
    private lateinit var playPause: ImageButton
    private lateinit var shuffle: ImageButton
    private lateinit var repeat: ImageButton
    private lateinit var speedBtn: TextView
    private lateinit var sleepBtn: TextView
    private var scrubbing = false

    private val handler = Handler(Looper.getMainLooper())
    private val ticker = object : Runnable {
        override fun run() {
            updateProgress()
            updateSleepLabel()
            handler.postDelayed(this, 500)
        }
    }

    private val notificationPermission =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    private val listener = object : Player.Listener {
        override fun onEvents(player: Player, events: Player.Events) {
            bindAll()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_audio_player)
        art = findViewById(R.id.audioArt)
        backdrop = findViewById(R.id.audioBackdrop)
        title = findViewById(R.id.audioTitle)
        artist = findViewById(R.id.audioArtist)
        timeBar = findViewById(R.id.audioTimeBar)
        position = findViewById(R.id.audioPosition)
        duration = findViewById(R.id.audioDuration)
        playPause = findViewById(R.id.audioPlayPause)
        shuffle = findViewById(R.id.audioShuffle)
        repeat = findViewById(R.id.audioRepeat)
        speedBtn = findViewById(R.id.audioSpeed)
        sleepBtn = findViewById(R.id.audioSleep)
        title.isSelected = true // marquee

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            backdrop.setRenderEffect(RenderEffect.createBlurEffect(80f, 80f, Shader.TileMode.CLAMP))
        }

        findViewById<View>(R.id.audioClose).setOnClickListener { finish() }
        findViewById<View>(R.id.audioMore).setOnClickListener { showMoreSheet() }
        playPause.setOnClickListener {
            val c = controller ?: return@setOnClickListener
            when {
                c.playbackState == Player.STATE_ENDED -> { c.seekToDefaultPosition(0); c.play() }
                c.playbackState == Player.STATE_IDLE -> { c.prepare(); c.play() }
                c.playWhenReady -> c.pause()
                else -> c.play()
            }
        }
        findViewById<View>(R.id.audioPrev).setOnClickListener { controller?.seekToPrevious() }
        findViewById<View>(R.id.audioNext).setOnClickListener { controller?.seekToNext() }
        shuffle.setOnClickListener {
            controller?.let { it.shuffleModeEnabled = !it.shuffleModeEnabled }
        }
        repeat.setOnClickListener {
            controller?.let {
                it.repeatMode = when (it.repeatMode) {
                    Player.REPEAT_MODE_OFF -> Player.REPEAT_MODE_ALL
                    Player.REPEAT_MODE_ALL -> Player.REPEAT_MODE_ONE
                    else -> Player.REPEAT_MODE_OFF
                }
            }
        }
        speedBtn.setOnClickListener { showSpeedSheet() }
        sleepBtn.setOnClickListener { showSleepSheet() }
        timeBar.addListener(object : TimeBar.OnScrubListener {
            override fun onScrubStart(timeBar: TimeBar, position: Long) { scrubbing = true }
            override fun onScrubMove(timeBar: TimeBar, position: Long) {
                this@AudioPlayerActivity.position.text = Ui.formatDuration(position)
            }
            override fun onScrubStop(timeBar: TimeBar, position: Long, canceled: Boolean) {
                scrubbing = false
                if (!canceled) controller?.seekTo(position)
            }
        })

        if (savedInstanceState == null && intent.hasExtra(EXTRA_IDS)) pendingQueue = intent
        maybeAskNotificationPermission()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        if (intent.hasExtra(EXTRA_IDS)) {
            pendingQueue = intent
            controller?.let { applyPendingQueue(it) }
        }
    }

    override fun onStart() {
        super.onStart()
        val token = SessionToken(this, ComponentName(this, PlaybackService::class.java))
        val future = MediaController.Builder(this, token).buildAsync()
        controllerFuture = future
        future.addListener({
            val c = try { future.get() } catch (_: Exception) { null } ?: run {
                Toast.makeText(this, "Couldn't start the music player", Toast.LENGTH_SHORT).show()
                return@addListener
            }
            controller = c
            c.addListener(listener)
            applyPendingQueue(c)
            bindAll()
        }, ContextCompat.getMainExecutor(this))
        handler.post(ticker)
    }

    override fun onStop() {
        super.onStop()
        handler.removeCallbacks(ticker)
        controller?.removeListener(listener)
        controller = null
        controllerFuture?.let { MediaController.releaseFuture(it) }
        controllerFuture = null
    }

    private fun applyPendingQueue(c: MediaController) {
        val queueIntent = pendingQueue ?: return
        pendingQueue = null
        val ids = queueIntent.getStringArrayListExtra(EXTRA_IDS) ?: return
        val tracks = LibraryCache.byIds(ids)
        val items = tracks.mapNotNull { toMediaItem(it) }
        if (items.isEmpty()) {
            Toast.makeText(this, "Couldn't open this track", Toast.LENGTH_SHORT).show()
            return
        }
        val shuffleOn = queueIntent.getBooleanExtra(EXTRA_SHUFFLE, false)
        val start = if (shuffleOn) (items.indices).random()
        else queueIntent.getIntExtra(EXTRA_INDEX, 0).coerceIn(0, items.size - 1)
        c.setMediaItems(items, start, C.TIME_UNSET)
        c.shuffleModeEnabled = shuffleOn
        c.prepare()
        c.play()
    }

    private fun bindAll() {
        val c = controller ?: return
        val meta = c.mediaMetadata
        title.text = meta.title ?: c.currentMediaItem?.mediaMetadata?.title ?: "Not playing"
        artist.text = listOfNotNull(meta.artist?.toString()?.takeIf { it.isNotBlank() },
            meta.albumTitle?.toString()?.takeIf { it.isNotBlank() }).joinToString(" · ").ifBlank { "Unknown artist" }
        playPause.setImageResource(
            if (c.playWhenReady && c.playbackState != Player.STATE_ENDED && c.playbackState != Player.STATE_IDLE)
                R.drawable.ic_pause else R.drawable.ic_play
        )
        val accent = ContextCompat.getColor(this, R.color.accent)
        val muted = ContextCompat.getColor(this, R.color.text_secondary)
        shuffle.setColorFilter(if (c.shuffleModeEnabled) accent else muted)
        repeat.setImageResource(if (c.repeatMode == Player.REPEAT_MODE_ONE) R.drawable.ic_repeat_one else R.drawable.ic_repeat)
        repeat.setColorFilter(if (c.repeatMode == Player.REPEAT_MODE_OFF) muted else accent)
        speedBtn.text = Ui.speedLabel(c.playbackParameters.speed)
        bindArtwork(c)
        updateProgress()
    }

    private fun bindArtwork(c: MediaController) {
        val item = c.currentMediaItem ?: return
        val data = c.mediaMetadata.artworkData
        val key = item.mediaId + ":" + (data?.size ?: 0)
        if (key == artKey) return
        artKey = key
        if (data != null) {
            val bmp = try { BitmapFactory.decodeByteArray(data, 0, data.size) } catch (_: Throwable) { null }
            if (bmp != null) { showArt(bmp); return }
        }
        showArt(null)
        val uri = item.localConfiguration?.uri ?: item.requestMetadata.mediaUri ?: return
        val probe = LocalVideo(
            id = item.mediaId, title = "", filePath = if (uri.scheme == "file") uri.path.orEmpty() else "",
            contentUri = if (uri.scheme == "file") null else uri.toString(), isEncrypted = false, isAudio = true,
        )
        val expected = key
        ThumbLoader.loadLarge(this, probe) { bmp -> if (artKey == expected && bmp != null) showArt(bmp) }
    }

    private fun showArt(bmp: Bitmap?) {
        art.setImageBitmap(bmp)
        backdrop.setImageBitmap(bmp)
        if (bmp != null) {
            art.alpha = 0f
            art.animate().alpha(1f).setDuration(250).start()
        }
    }

    private fun updateProgress() {
        val c = controller ?: return
        val dur = if (c.duration == C.TIME_UNSET) 0L else c.duration
        timeBar.setDuration(dur)
        timeBar.setBufferedPosition(c.bufferedPosition)
        if (!scrubbing) {
            timeBar.setPosition(c.currentPosition)
            position.text = Ui.formatDuration(c.currentPosition)
        }
        duration.text = Ui.formatDuration(dur)
    }

    private fun updateSleepLabel() {
        val at = PlaybackService.sleepAtElapsed
        val remaining = if (at > 0) at - SystemClock.elapsedRealtime() else 0L
        sleepBtn.text = if (remaining > 0) Ui.formatDuration(remaining) else "Sleep"
        sleepBtn.setTextColor(ContextCompat.getColor(this, if (remaining > 0) R.color.accent_light else R.color.text_primary))
    }

    private fun showSpeedSheet() {
        val c = controller ?: return
        val currentSpeed = c.playbackParameters.speed
        val sheet = OptionSheet(this, "Playback speed")
        for (s in Ui.SPEEDS) {
            sheet.add(if (s == 1f) "Normal" else Ui.speedLabel(s), R.drawable.ic_speed, checked = s == currentSpeed) {
                controller?.setPlaybackSpeed(s)
            }
        }
        sheet.show()
    }

    private fun showSleepSheet() {
        val sheet = OptionSheet(this, "Sleep timer", "Music pauses automatically, even in the background")
        sheet.add("Off", R.drawable.ic_timer, checked = PlaybackService.sleepAtElapsed == 0L) {
            PlaybackService.setSleepTimer(0)
        }
        for (m in intArrayOf(15, 30, 45, 60, 90)) {
            sheet.add("$m minutes", R.drawable.ic_timer) {
                PlaybackService.setSleepTimer(m)
                Toast.makeText(this, "Music will pause in $m minutes", Toast.LENGTH_SHORT).show()
            }
        }
        sheet.show()
    }

    private fun showMoreSheet() {
        val c = controller
        val sheet = OptionSheet(this, "Music")
        sheet.add("Playback speed", R.drawable.ic_speed, c?.let { Ui.speedLabel(it.playbackParameters.speed) }) { showSpeedSheet() }
        sheet.add("Sleep timer", R.drawable.ic_timer) { showSleepSheet() }
        sheet.add("Stop playback", R.drawable.ic_close) {
            controller?.let {
                it.stop()
                it.clearMediaItems()
            }
            PlaybackService.setSleepTimer(0)
            finish()
        }
        sheet.show()
    }

    private fun maybeAskNotificationPermission() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) ==
            PackageManager.PERMISSION_GRANTED
        ) return
        val prefs = getSharedPreferences(PREFS, MODE_PRIVATE)
        if (prefs.getBoolean("asked_notifications", false)) return
        prefs.edit().putBoolean("asked_notifications", true).apply()
        notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
    }
}
