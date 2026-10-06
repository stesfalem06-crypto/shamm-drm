package com.shammapps.xama.ui

import android.app.PendingIntent
import android.app.PictureInPictureParams
import android.app.RemoteAction
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ActivityInfo
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.graphics.drawable.Icon
import android.media.AudioManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.OpenableColumns
import android.provider.Settings
import android.util.Rational
import android.view.GestureDetector
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.ImageButton
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
import androidx.activity.OnBackPressedCallback
import androidx.activity.result.contract.ActivityResultContracts
import androidx.annotation.OptIn
import androidx.annotation.RequiresApi
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.Lifecycle
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.Format
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.TrackSelectionOverride
import androidx.media3.common.Tracks
import androidx.media3.common.VideoSize
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DataSource
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.exoplayer.source.MediaSource
import androidx.media3.exoplayer.source.MergingMediaSource
import androidx.media3.exoplayer.source.ProgressiveMediaSource
import androidx.media3.extractor.DefaultExtractorsFactory
import androidx.media3.extractor.ExtractorsFactory
import androidx.media3.extractor.text.DefaultSubtitleParserFactory
import androidx.media3.extractor.text.SubtitleExtractor
import androidx.media3.ui.AspectRatioFrameLayout
import androidx.media3.ui.DefaultTimeBar
import androidx.media3.ui.PlayerView
import androidx.media3.ui.TimeBar
import com.google.android.material.progressindicator.CircularProgressIndicator
import com.shammapps.xama.R
import com.shammapps.xama.crypto.NativeCrypto
import com.shammapps.xama.crypto.ShammKeyResolver
import com.shammapps.xama.data.LibraryCache
import com.shammapps.xama.data.LocalVideo
import com.shammapps.xama.data.PlaybackPositions
import com.shammapps.xama.data.WatchHistory
import com.shammapps.xama.player.ShammDataSource
import com.shammapps.xama.security.SecurityGuard
import java.io.File
import java.util.Locale
import kotlin.math.abs

/**
 * Full-featured video player (plain + protected):
 * - Custom overlay: play/pause, ±10 s, previous/next, seek bar with buffered progress
 * - Gestures: double-tap seek, brightness (left swipe), volume (right swipe), horizontal seek
 * - Speed 0.25x–2x, aspect modes, rotation, controls lock, audio/subtitle tracks
 *   (+ external .srt/.vtt), picture-in-picture, sleep timer, repeat, resume position
 * - Friendly error screen instead of a black screen
 *
 * Protected playback is unchanged: same SecurityGuard checks, FLAG_SECURE,
 * ShammKeyResolver and ShammDataSource; keys are wiped when a player is released.
 */
@OptIn(UnstableApi::class)
class PlayerActivity : AppCompatActivity() {

    companion object {
        const val EXTRA_PLAYLIST_IDS = "playlist_ids"
        private const val ACTION_PIP_CONTROL = "com.shammapps.xama.PIP_CONTROL"
        private const val EXTRA_PIP_CMD = "cmd"
        private const val CMD_PLAY_PAUSE = 1
        private const val CMD_REWIND = 2
        private const val CMD_FORWARD = 3
        private const val AUTO_HIDE_MS = 3500L
        private const val SEEK_STEP_MS = 10_000L
    }

    private enum class Aspect(val label: String) {
        FIT("Fit"), FILL("Fill"), CROP("Crop"), RATIO_16_9("16:9"), RATIO_4_3("4:3")
    }

    private enum class Repeat(val label: String) { OFF("Off"), ONE("Repeat one"), ALL("Repeat all") }

    private enum class Rotation(val label: String) {
        AUTO_VIDEO("Match video"), LANDSCAPE("Landscape"), PORTRAIT("Portrait"), SENSOR("Auto-rotate")
    }

    private data class ExternalSub(val uri: Uri, val mimeType: String, val label: String)

    // Player state
    private var player: ExoPlayer? = null
    private var contentKey: ByteArray? = null
    private var playlist: List<LocalVideo> = emptyList()
    private var index = 0
    private val current: LocalVideo? get() = playlist.getOrNull(index)
    private val externalSubs = HashMap<String, ExternalSub>()
    private var speed = 1.0f
    private var aspect = Aspect.FIT
    private var repeat = Repeat.OFF
    private var rotation = Rotation.AUTO_VIDEO
    private var subtitlesDisabled = false
    private var locked = false
    private var scrubbing = false
    private var controlsVisible = true
    private var sleepAtElapsed = 0L
    private var sleepAtEnd = false
    private var lastSavedAt = 0L
    private var videoAspect = 16f / 9f

    // Views
    private lateinit var root: FrameLayout
    private lateinit var playerView: PlayerView
    private lateinit var touchLayer: View
    private lateinit var controlsRoot: View
    private lateinit var topBar: View
    private lateinit var bottomBar: View
    private lateinit var titleText: TextView
    private lateinit var subtitleText: TextView
    private lateinit var btnPlayPause: ImageButton
    private lateinit var btnPrev: ImageButton
    private lateinit var btnNext: ImageButton
    private lateinit var btnSpeed: TextView
    private lateinit var btnRepeat: ImageButton
    private lateinit var btnSleep: ImageButton
    private lateinit var btnAudioTrack: ImageButton
    private lateinit var btnSubtitles: ImageButton
    private lateinit var btnPip: ImageButton
    private lateinit var timeBar: DefaultTimeBar
    private lateinit var timePosition: TextView
    private lateinit var timeDuration: TextView
    private lateinit var spinner: CircularProgressIndicator
    private lateinit var levelIndicator: View
    private lateinit var levelIcon: ImageView
    private lateinit var levelBar: ProgressBar
    private lateinit var levelText: TextView
    private lateinit var seekIndicator: View
    private lateinit var seekIndicatorDelta: TextView
    private lateinit var seekIndicatorPos: TextView
    private lateinit var rippleLeft: LinearLayout
    private lateinit var rippleRight: LinearLayout
    private lateinit var rippleLeftText: TextView
    private lateinit var rippleRightText: TextView
    private lateinit var lockOverlay: View
    private lateinit var btnUnlock: View
    private lateinit var resumeChip: View
    private lateinit var errorView: View
    private lateinit var audioManager: AudioManager

    private val handler = Handler(Looper.getMainLooper())
    private val hideControlsRunnable = Runnable { hideControls() }
    private val hideIndicatorsRunnable = Runnable {
        levelIndicator.visibility = View.GONE
        seekIndicator.visibility = View.GONE
    }
    private val hideUnlockRunnable = Runnable { btnUnlock.animate().alpha(0f).setDuration(250).start() }
    private val hideResumeRunnable = Runnable { resumeChip.visibility = View.GONE }
    private val sleepRunnable = Runnable {
        player?.pause()
        sleepAtElapsed = 0L
        updateSleepButton()
        Toast.makeText(this, "Sleep timer ended — playback paused", Toast.LENGTH_SHORT).show()
    }
    private val progressRunnable = object : Runnable {
        override fun run() {
            updateProgress()
            val p = player
            if (p != null && p.isPlaying) {
                val now = SystemClock.elapsedRealtime()
                if (now - lastSavedAt > 10_000) {
                    lastSavedAt = now
                    savePosition()
                }
            }
            handler.postDelayed(this, if (controlsVisible) 250 else 1000)
        }
    }

    // Gesture state
    private enum class GestureMode { NONE, SEEK, BRIGHTNESS, VOLUME }
    private var gestureMode = GestureMode.NONE
    private var downX = 0f
    private var downY = 0f
    private var startBrightness = 0.5f
    private var startVolume = 0f
    private var startPositionMs = 0L
    private var seekTargetMs = 0L
    private var lastSeekTapAt = 0L
    private var lastSeekZone = 0
    private var seekAccumSec = 0
    private var consumedTap = false
    private var touchSlop = 24

    private val pickSubtitle = registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) attachSubtitle(uri)
        hideSystemBars()
    }

    private val pipReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            if (intent?.action != ACTION_PIP_CONTROL) return
            val p = player ?: return
            when (intent.getIntExtra(EXTRA_PIP_CMD, 0)) {
                CMD_PLAY_PAUSE -> togglePlayPause()
                CMD_REWIND -> p.seekTo((p.currentPosition - SEEK_STEP_MS).coerceAtLeast(0))
                CMD_FORWARD -> seekForward(p)
            }
        }
    }

    private val playerListener = object : Player.Listener {
        override fun onIsPlayingChanged(isPlaying: Boolean) {
            updatePlayPause()
            if (isPlaying) {
                window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                scheduleHide()
            } else {
                window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                handler.removeCallbacks(hideControlsRunnable)
                savePosition()
            }
            updatePipParams()
        }

        override fun onPlaybackStateChanged(state: Int) {
            spinner.visibility = if (state == Player.STATE_BUFFERING) View.VISIBLE else View.GONE
            if (state == Player.STATE_ENDED) handleEnded()
            updatePlayPause()
            updateProgress()
        }

        override fun onPlayerError(error: PlaybackException) {
            spinner.visibility = View.GONE
            showError(error)
        }

        override fun onVideoSizeChanged(videoSize: VideoSize) {
            if (videoSize.width <= 0 || videoSize.height <= 0) return
            videoAspect = videoSize.width * videoSize.pixelWidthHeightRatio / videoSize.height
            if (rotation == Rotation.AUTO_VIDEO) applyRotation()
            updatePipParams()
        }

        override fun onTracksChanged(tracks: Tracks) {
            val audioCount = tracks.groups.filter { it.type == C.TRACK_TYPE_AUDIO }.sumOf { it.length }
            btnAudioTrack.alpha = if (audioCount > 1) 1f else 0.5f
            val hasText = tracks.groups.any { it.type == C.TRACK_TYPE_TEXT && it.isSelected }
            btnSubtitles.setColorFilter(
                ContextCompat.getColor(this@PlayerActivity, if (hasText) R.color.accent else android.R.color.white)
            )
        }
    }

    // ---------------------------------------------------------------- lifecycle

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        setContentView(R.layout.activity_player)
        hideSystemBars()
        bindViews()
        audioManager = getSystemService(AUDIO_SERVICE) as AudioManager
        touchSlop = ViewConfiguration.get(this).scaledTouchSlop

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (locked) showUnlockHint() else finish()
            }
        })

        playlist = buildPlaylist()
        if (playlist.isEmpty()) {
            Toast.makeText(this, "Video file not found.", Toast.LENGTH_LONG).show()
            finish()
            return
        }

        // Protected content: block screenshots / screen recording for this screen and
        // refuse to start on emulators (same checks as before).
        if (playlist.any { it.isEncrypted }) {
            SecurityGuard.enableScreenshotProtection(this)
        }
        if (current?.isEncrypted == true && SecurityGuard.isEnvironmentCompromised(this)) {
            Toast.makeText(this, "This device can't play protected videos.", Toast.LENGTH_LONG).show()
            finish()
            return
        }

        ContextCompat.registerReceiver(
            this, pipReceiver, IntentFilter(ACTION_PIP_CONTROL), ContextCompat.RECEIVER_NOT_EXPORTED
        )
        receiverRegistered = true

        wireControls()
        setupGestures()
        applyInsets()
        root.addOnLayoutChangeListener { _, l, t, r, b, ol, ot, or, ob ->
            if (r - l != or - ol || b - t != ob - ot) root.post { applyAspect() }
        }

        loadItem(index, initial = true)
        showControls()
        handler.post(progressRunnable)
    }

    private var receiverRegistered = false

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()
    }

    override fun onStop() {
        super.onStop()
        savePosition()
        player?.pause()
    }

    override fun onDestroy() {
        super.onDestroy()
        handler.removeCallbacksAndMessages(null)
        if (receiverRegistered) {
            try { unregisterReceiver(pipReceiver) } catch (_: Exception) {}
            receiverRegistered = false
        }
        releasePlayer()
    }

    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        // Android 12+ auto-enters via setAutoEnterEnabled; older versions enter here.
        if (Build.VERSION.SDK_INT in Build.VERSION_CODES.O until Build.VERSION_CODES.S &&
            player?.isPlaying == true && errorView.visibility != View.VISIBLE
        ) {
            enterPip(silent = true)
        }
    }

    override fun onPictureInPictureModeChanged(isInPictureInPictureMode: Boolean, newConfig: Configuration) {
        super.onPictureInPictureModeChanged(isInPictureInPictureMode, newConfig)
        if (isInPictureInPictureMode) {
            hideControls(animate = false)
            levelIndicator.visibility = View.GONE
            seekIndicator.visibility = View.GONE
            resumeChip.visibility = View.GONE
            touchLayer.visibility = View.GONE
        } else {
            touchLayer.visibility = View.VISIBLE
            // PiP window dismissed (activity no longer visible): close the player.
            if (lifecycle.currentState == Lifecycle.State.CREATED) {
                finish()
            } else {
                showControls()
            }
        }
    }

    // ---------------------------------------------------------------- setup

    private fun bindViews() {
        root = findViewById(R.id.playerRoot)
        playerView = findViewById(R.id.playerView)
        touchLayer = findViewById(R.id.touchLayer)
        controlsRoot = findViewById(R.id.controlsRoot)
        topBar = findViewById(R.id.topBar)
        bottomBar = findViewById(R.id.bottomBar)
        titleText = findViewById(R.id.videoTitle)
        subtitleText = findViewById(R.id.videoSubtitle)
        btnPlayPause = findViewById(R.id.btnPlayPause)
        btnPrev = findViewById(R.id.btnPrev)
        btnNext = findViewById(R.id.btnNext)
        btnSpeed = findViewById(R.id.btnSpeed)
        btnRepeat = findViewById(R.id.btnRepeat)
        btnSleep = findViewById(R.id.btnSleep)
        btnAudioTrack = findViewById(R.id.btnAudioTrack)
        btnSubtitles = findViewById(R.id.btnSubtitles)
        btnPip = findViewById(R.id.btnPip)
        timeBar = findViewById(R.id.timeBar)
        timePosition = findViewById(R.id.timePosition)
        timeDuration = findViewById(R.id.timeDuration)
        spinner = findViewById(R.id.bufferingSpinner)
        levelIndicator = findViewById(R.id.levelIndicator)
        levelIcon = findViewById(R.id.levelIcon)
        levelBar = findViewById(R.id.levelBar)
        levelText = findViewById(R.id.levelText)
        seekIndicator = findViewById(R.id.seekIndicator)
        seekIndicatorDelta = findViewById(R.id.seekIndicatorDelta)
        seekIndicatorPos = findViewById(R.id.seekIndicatorPos)
        rippleLeft = findViewById(R.id.seekRippleLeft)
        rippleRight = findViewById(R.id.seekRippleRight)
        rippleLeftText = findViewById(R.id.seekRippleLeftText)
        rippleRightText = findViewById(R.id.seekRippleRightText)
        lockOverlay = findViewById(R.id.lockOverlay)
        btnUnlock = findViewById(R.id.btnUnlock)
        resumeChip = findViewById(R.id.resumeChip)
        errorView = findViewById(R.id.errorView)
    }

    /** Playlist = the list the user opened the video from (for previous / next). */
    private fun buildPlaylist(): List<LocalVideo> {
        val title = intent.getStringExtra(MainActivity.EXTRA_TITLE) ?: "Video"
        val filePath = intent.getStringExtra(MainActivity.EXTRA_FILE_PATH) ?: ""
        val contentUri = intent.getStringExtra(MainActivity.EXTRA_CONTENT_URI)
        val isEncrypted = intent.getBooleanExtra(MainActivity.EXTRA_IS_ENCRYPTED, false)
        val ivBase64 = intent.getStringExtra(MainActivity.EXTRA_IV_BASE64)
        val videoId = intent.getStringExtra(MainActivity.EXTRA_VIDEO_ID) ?: ""

        val ids = intent.getStringArrayListExtra(EXTRA_PLAYLIST_IDS) ?: arrayListOf()
        val list = LibraryCache.byIds(ids).filter { !it.isAudio }
        val pos = list.indexOfFirst { it.id == videoId }
        if (videoId.isNotBlank() && pos >= 0) {
            index = pos
            return list
        }
        if (filePath.isBlank() && contentUri.isNullOrBlank() && !isEncrypted) return emptyList()
        val historyId = when {
            videoId.isNotBlank() -> videoId
            !contentUri.isNullOrBlank() -> contentUri
            else -> filePath
        }
        index = 0
        return listOf(
            LocalVideo(
                id = historyId,
                title = title,
                filePath = filePath,
                isEncrypted = isEncrypted,
                ivBase64 = ivBase64,
                contentUri = contentUri,
            )
        )
    }

    private fun wireControls() {
        findViewById<View>(R.id.btnBack).setOnClickListener { finish() }
        btnPlayPause.setOnClickListener { togglePlayPause(); scheduleHide() }
        findViewById<View>(R.id.btnRewind).setOnClickListener {
            player?.let { it.seekTo((it.currentPosition - SEEK_STEP_MS).coerceAtLeast(0)) }
            scheduleHide()
        }
        findViewById<View>(R.id.btnForward).setOnClickListener {
            player?.let { seekForward(it) }
            scheduleHide()
        }
        btnPrev.setOnClickListener { playPrevious() }
        btnNext.setOnClickListener { playNext(userInitiated = true) }
        findViewById<View>(R.id.btnLock).setOnClickListener { setLocked(true) }
        findViewById<View>(R.id.btnAspect).setOnClickListener { cycleAspect() }
        findViewById<View>(R.id.btnAspect).setOnLongClickListener { showAspectSheet(); true }
        btnSpeed.setOnClickListener { showSpeedSheet() }
        btnRepeat.setOnClickListener { cycleRepeat() }
        btnSleep.setOnClickListener { showSleepSheet() }
        findViewById<View>(R.id.btnRotate).setOnClickListener { toggleRotation() }
        findViewById<View>(R.id.btnRotate).setOnLongClickListener { showRotationSheet(); true }
        btnPip.setOnClickListener { enterPip(silent = false) }
        btnAudioTrack.setOnClickListener { showAudioTrackSheet() }
        btnSubtitles.setOnClickListener { showSubtitleSheet() }
        findViewById<View>(R.id.btnMore).setOnClickListener { showMoreSheet() }
        btnUnlock.setOnClickListener { setLocked(false) }
        findViewById<View>(R.id.resumeStartOver).setOnClickListener {
            player?.seekTo(0)
            current?.let { PlaybackPositions.clear(this, it.id) }
            resumeChip.visibility = View.GONE
        }
        findViewById<View>(R.id.errorBack).setOnClickListener { finish() }
        findViewById<View>(R.id.errorRetry).setOnClickListener { loadItem(index, startPositionMs = lastKnownPosition) }
        findViewById<View>(R.id.errorNext).setOnClickListener { playNext(userInitiated = true) }

        if (!supportsPip()) btnPip.visibility = View.GONE

        timeBar.addListener(object : TimeBar.OnScrubListener {
            override fun onScrubStart(timeBar: TimeBar, position: Long) {
                scrubbing = true
                handler.removeCallbacks(hideControlsRunnable)
            }

            override fun onScrubMove(timeBar: TimeBar, position: Long) {
                timePosition.text = Ui.formatDuration(position)
            }

            override fun onScrubStop(timeBar: TimeBar, position: Long, canceled: Boolean) {
                scrubbing = false
                if (!canceled) player?.seekTo(position)
                scheduleHide()
            }
        })
        updateRepeatButton()
        updateSleepButton()
        btnSpeed.text = Ui.speedLabel(speed)
    }

    private fun applyInsets() {
        val topPad = topBar.paddingTop
        val bottomPad = bottomBar.paddingBottom
        ViewCompat.setOnApplyWindowInsetsListener(controlsRoot) { _, insets ->
            val cut = insets.getInsets(WindowInsetsCompat.Type.displayCutout())
            val bars = insets.getInsetsIgnoringVisibility(WindowInsetsCompat.Type.systemBars())
            val left = maxOf(cut.left, if (isLandscape()) bars.left else 0)
            val right = maxOf(cut.right, if (isLandscape()) bars.right else 0)
            topBar.setPadding(Ui.dp(this, 8) + left, topPad + cut.top, Ui.dp(this, 8) + right, topBar.paddingBottom)
            bottomBar.setPadding(
                Ui.dp(this, 16) + left, bottomBar.paddingTop, Ui.dp(this, 16) + right,
                bottomPad + if (isLandscape()) 0 else bars.bottom / 2
            )
            insets
        }
    }

    private fun isLandscape() = resources.configuration.orientation == Configuration.ORIENTATION_LANDSCAPE

    // ---------------------------------------------------------------- playback

    private var lastKnownPosition = 0L

    private fun buildPlayer(): ExoPlayer {
        val exo = ExoPlayer.Builder(this)
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(C.USAGE_MEDIA)
                    .setContentType(C.AUDIO_CONTENT_TYPE_MOVIE)
                    .build(),
                /* handleAudioFocus = */ true
            )
            .setHandleAudioBecomingNoisy(true)
            .setSeekBackIncrementMs(SEEK_STEP_MS)
            .setSeekForwardIncrementMs(SEEK_STEP_MS)
            .build()
        exo.addListener(playerListener)
        exo.setPlaybackSpeed(speed)
        exo.repeatMode = if (repeat == Repeat.ONE) Player.REPEAT_MODE_ONE else Player.REPEAT_MODE_OFF
        if (subtitlesDisabled) {
            exo.trackSelectionParameters = exo.trackSelectionParameters.buildUpon()
                .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true).build()
        }
        player = exo
        playerView.player = exo
        return exo
    }

    /** Releases the current player and wipes the protected content key, if any. */
    private fun releasePlayer() {
        player?.let {
            lastKnownPosition = it.currentPosition
            it.removeListener(playerListener)
            it.release()
        }
        player = null
        playerView.player = null
        contentKey?.let { NativeCrypto.shamm_wipe(it, NativeCrypto.KEY_LEN) }
        contentKey = null
    }

    private fun loadItem(newIndex: Int, initial: Boolean = false, startPositionMs: Long? = null) {
        if (!initial) savePosition()
        releasePlayer()
        index = newIndex.coerceIn(0, playlist.size - 1)
        val item = playlist[index]
        errorView.visibility = View.GONE
        resumeChip.visibility = View.GONE
        bindTitle(item)
        WatchHistory.record(this, item.id)

        val source: MediaSource = if (item.isEncrypted) {
            if (SecurityGuard.isEnvironmentCompromised(this)) {
                showErrorMessage("Protected video", "This device can't play protected videos.")
                return
            }
            val resolved = ShammKeyResolver(this).resolve(item.id, item.ivBase64 ?: "")
            if (resolved == null) {
                if (initial) {
                    Toast.makeText(this, "This video isn't licensed for this device.", Toast.LENGTH_LONG).show()
                    finish()
                } else {
                    showErrorMessage("Not licensed", "This video isn't licensed for this device.")
                }
                return
            }
            contentKey = resolved.contentKey
            val factory = DataSource.Factory { ShammDataSource(resolved.contentKey, resolved.iv) }
            // Embedded subtitle tracks are parsed during extraction so they can be selected
            // (same behaviour DefaultMediaSourceFactory applies to plain files).
            @Suppress("DEPRECATION")
            val extractors = DefaultExtractorsFactory().setTextTrackTranscodingEnabled(true)
            ProgressiveMediaSource.Factory(factory, extractors)
                .createMediaSource(MediaItem.fromUri(Uri.fromFile(resolved.videoFile)))
        } else {
            val uri = when {
                !item.contentUri.isNullOrBlank() -> Uri.parse(item.contentUri)
                item.filePath.isNotBlank() -> Uri.fromFile(File(item.filePath))
                else -> {
                    showErrorMessage("File not found", "This video file couldn't be found.")
                    return
                }
            }
            DefaultMediaSourceFactory(this).createMediaSource(MediaItem.fromUri(uri))
        }

        val withSubs = externalSubs[item.id]?.let { sub ->
            buildSubtitleSource(sub)?.let { MergingMediaSource(source, it) }
        } ?: source

        val exo = buildPlayer()
        exo.setMediaSource(withSubs)

        if (startPositionMs != null) {
            exo.seekTo(startPositionMs)
        } else {
            val saved = PlaybackPositions.get(this, item.id)
            if (saved != null && saved.positionMs > 5_000 &&
                (saved.durationMs <= 0 || saved.positionMs < saved.durationMs - 10_000)
            ) {
                exo.seekTo(saved.positionMs)
                showResumeChip(saved.positionMs)
            }
        }
        exo.prepare()
        exo.playWhenReady = true
        updatePrevNext()
        applyAspect()
    }

    private fun buildSubtitleSource(sub: ExternalSub): MediaSource? {
        val format = Format.Builder()
            .setSampleMimeType(sub.mimeType)
            .setSelectionFlags(C.SELECTION_FLAG_DEFAULT)
            .setLabel(sub.label)
            .setId("external:" + sub.uri)
            .build()
        val parserFactory = DefaultSubtitleParserFactory()
        if (!parserFactory.supportsFormat(format)) return null
        val extractors = ExtractorsFactory { arrayOf(SubtitleExtractor(parserFactory.create(format), format)) }
        return ProgressiveMediaSource.Factory(DefaultDataSource.Factory(this), extractors)
            .createMediaSource(MediaItem.fromUri(sub.uri))
    }

    private fun bindTitle(item: LocalVideo) {
        titleText.text = item.title
        subtitleText.text = buildString {
            if (item.isEncrypted) append("Protected") else append(item.folderName.takeIf { it.isNotBlank() && it != "Other" } ?: "Video")
            if (playlist.size > 1) append(" · ${index + 1} of ${playlist.size}")
        }
    }

    private fun savePosition() {
        val p = player ?: return
        val item = current ?: return
        val dur = p.duration
        if (dur == C.TIME_UNSET || dur <= 0) return
        PlaybackPositions.save(this, item.id, p.currentPosition, dur)
    }

    private fun togglePlayPause() {
        val p = player ?: return
        when {
            p.playbackState == Player.STATE_ENDED -> {
                p.seekTo(0)
                p.play()
            }
            p.playbackState == Player.STATE_IDLE -> {
                p.prepare()
                p.play()
            }
            p.isPlaying || p.playWhenReady -> p.pause()
            else -> p.play()
        }
    }

    private fun seekForward(p: Player) {
        val dur = p.duration
        val target = p.currentPosition + SEEK_STEP_MS
        p.seekTo(if (dur != C.TIME_UNSET && dur > 0) target.coerceAtMost(dur) else target)
    }

    private fun hasPrevious() = index > 0 || (repeat == Repeat.ALL && playlist.size > 1)
    private fun hasNext() = index < playlist.size - 1 || (repeat == Repeat.ALL && playlist.size > 1)

    private fun playPrevious() {
        val p = player
        if (p != null && p.currentPosition > 5_000) {
            p.seekTo(0)
            return
        }
        if (!hasPrevious()) return
        loadItem(if (index > 0) index - 1 else playlist.size - 1)
        showControls()
    }

    private fun playNext(userInitiated: Boolean) {
        if (!hasNext()) {
            if (userInitiated) Toast.makeText(this, "This is the last video", Toast.LENGTH_SHORT).show()
            return
        }
        loadItem(if (index < playlist.size - 1) index + 1 else 0)
        showControls()
    }

    private fun handleEnded() {
        current?.let { PlaybackPositions.clear(this, it.id) }
        if (sleepAtEnd) {
            sleepAtEnd = false
            updateSleepButton()
            showControls(autoHide = false)
            return
        }
        when {
            repeat == Repeat.ONE -> player?.let { it.seekTo(0); it.play() }
            hasNext() -> playNext(userInitiated = false)
            else -> showControls(autoHide = false)
        }
    }

    private fun updatePrevNext() {
        val prev = hasPrevious() || playlist.size > 1
        btnPrev.isEnabled = prev
        btnPrev.alpha = if (prev) 1f else 0.35f
        val next = hasNext()
        btnNext.isEnabled = next
        btnNext.alpha = if (next) 1f else 0.35f
        val showNav = playlist.size > 1
        btnPrev.visibility = if (showNav) View.VISIBLE else View.GONE
        btnNext.visibility = if (showNav) View.VISIBLE else View.GONE
    }

    private fun updatePlayPause() {
        val p = player
        val icon = when {
            p == null -> R.drawable.ic_play
            p.playbackState == Player.STATE_ENDED -> R.drawable.ic_replay
            p.playWhenReady && p.playbackState != Player.STATE_IDLE -> R.drawable.ic_pause
            else -> R.drawable.ic_play
        }
        btnPlayPause.setImageResource(icon)
    }

    private fun updateProgress() {
        val p = player ?: return
        val dur = if (p.duration == C.TIME_UNSET) 0L else p.duration
        lastKnownPosition = p.currentPosition
        timeBar.setDuration(dur)
        timeBar.setBufferedPosition(p.bufferedPosition)
        if (!scrubbing) {
            timeBar.setPosition(p.currentPosition)
            timePosition.text = Ui.formatDuration(p.currentPosition)
        }
        timeDuration.text = Ui.formatDuration(dur)
    }

    // ---------------------------------------------------------------- errors

    private fun showError(error: PlaybackException) {
        val isProtected = current?.isEncrypted == true
        val (title, message) = when (error.errorCode) {
            PlaybackException.ERROR_CODE_IO_FILE_NOT_FOUND ->
                "File not found" to "This file was moved or deleted. Try rescanning your library."
            PlaybackException.ERROR_CODE_IO_NO_PERMISSION ->
                "No access" to "Xama doesn't have permission to read this file."
            PlaybackException.ERROR_CODE_DECODER_INIT_FAILED,
            PlaybackException.ERROR_CODE_DECODER_QUERY_FAILED,
            PlaybackException.ERROR_CODE_DECODING_FAILED,
            PlaybackException.ERROR_CODE_DECODING_FORMAT_UNSUPPORTED,
            PlaybackException.ERROR_CODE_DECODING_FORMAT_EXCEEDS_CAPABILITIES ->
                "Format not supported" to "This phone can't decode this video's format or resolution."
            PlaybackException.ERROR_CODE_PARSING_CONTAINER_UNSUPPORTED,
            PlaybackException.ERROR_CODE_PARSING_CONTAINER_MALFORMED ->
                if (isProtected) "Can't open protected video" to
                    "The protected file couldn't be read. It may be damaged or licensed to another device."
                else "Can't read this file" to "The file looks damaged or uses an unsupported format."
            PlaybackException.ERROR_CODE_AUDIO_TRACK_INIT_FAILED,
            PlaybackException.ERROR_CODE_AUDIO_TRACK_WRITE_FAILED ->
                "Audio problem" to "The audio output couldn't be started. Try again."
            else ->
                if (isProtected) "Can't play protected video" to
                    "Something went wrong while playing this protected video."
                else "Can't play this video" to "Something went wrong while playing this file."
        }
        showErrorMessage(title, message)
    }

    private fun showErrorMessage(title: String, message: String) {
        hideControls(animate = false)
        findViewById<TextView>(R.id.errorTitle).text = title
        findViewById<TextView>(R.id.errorMessage).text = message
        findViewById<View>(R.id.errorNext).visibility = if (hasNext()) View.VISIBLE else View.GONE
        errorView.alpha = 0f
        errorView.visibility = View.VISIBLE
        errorView.animate().alpha(1f).setDuration(200).start()
        window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }

    // ---------------------------------------------------------------- controls visibility

    private fun showControls(autoHide: Boolean = true) {
        if (locked || isInPipCompat()) return
        controlsVisible = true
        controlsRoot.animate().cancel()
        if (controlsRoot.visibility != View.VISIBLE) {
            controlsRoot.alpha = 0f
            controlsRoot.visibility = View.VISIBLE
        }
        controlsRoot.animate().alpha(1f).setDuration(180).start()
        updateProgress()
        if (autoHide) scheduleHide() else handler.removeCallbacks(hideControlsRunnable)
    }

    private fun hideControls(animate: Boolean = true) {
        controlsVisible = false
        handler.removeCallbacks(hideControlsRunnable)
        controlsRoot.animate().cancel()
        if (animate) {
            controlsRoot.animate().alpha(0f).setDuration(220)
                .withEndAction { if (!controlsVisible) controlsRoot.visibility = View.GONE }.start()
        } else {
            controlsRoot.alpha = 0f
            controlsRoot.visibility = View.GONE
        }
    }

    private fun scheduleHide() {
        handler.removeCallbacks(hideControlsRunnable)
        if (player?.isPlaying == true && !scrubbing) handler.postDelayed(hideControlsRunnable, AUTO_HIDE_MS)
    }

    private fun toggleControls() {
        if (controlsVisible) hideControls() else showControls()
    }

    private fun setLocked(value: Boolean) {
        locked = value
        if (value) {
            hideControls()
            lockOverlay.visibility = View.VISIBLE
            showUnlockHint()
        } else {
            handler.removeCallbacks(hideUnlockRunnable)
            lockOverlay.visibility = View.GONE
            showControls()
        }
    }

    private fun showUnlockHint() {
        btnUnlock.animate().cancel()
        btnUnlock.alpha = 1f
        handler.removeCallbacks(hideUnlockRunnable)
        handler.postDelayed(hideUnlockRunnable, 2500)
    }

    private fun showResumeChip(positionMs: Long) {
        findViewById<TextView>(R.id.resumeText).text = "Resumed from ${Ui.formatDuration(positionMs)}"
        resumeChip.visibility = View.VISIBLE
        handler.removeCallbacks(hideResumeRunnable)
        handler.postDelayed(hideResumeRunnable, 6000)
    }

    // ---------------------------------------------------------------- gestures

    private fun setupGestures() {
        val detector = GestureDetector(this, object : GestureDetector.SimpleOnGestureListener() {
            override fun onDown(e: MotionEvent): Boolean = true

            override fun onSingleTapUp(e: MotionEvent): Boolean {
                // Rapid taps after a double-tap keep seeking (YouTube-style).
                val zone = zoneOf(e.x)
                if (zone != 0 && zone == lastSeekZone && SystemClock.uptimeMillis() - lastSeekTapAt < 700) {
                    seekByTap(zone)
                    consumedTap = true
                }
                return false
            }

            override fun onSingleTapConfirmed(e: MotionEvent): Boolean {
                if (consumedTap) {
                    consumedTap = false
                    return true
                }
                toggleControls()
                return true
            }

            override fun onDoubleTap(e: MotionEvent): Boolean {
                consumedTap = false
                val zone = zoneOf(e.x)
                if (zone == 0) {
                    togglePlayPause()
                    showControls()
                } else {
                    seekByTap(zone)
                }
                return true
            }
        })

        touchLayer.setOnTouchListener { _, ev ->
            if (locked) {
                if (ev.actionMasked == MotionEvent.ACTION_UP) showUnlockHint()
                return@setOnTouchListener true
            }
            detector.onTouchEvent(ev)
            handleSwipe(ev)
            true
        }
    }

    /** -1 = left (rewind), 0 = center, 1 = right (forward). */
    private fun zoneOf(x: Float): Int {
        val w = root.width.coerceAtLeast(1)
        return when {
            x < w * 0.38f -> -1
            x > w * 0.62f -> 1
            else -> 0
        }
    }

    private fun seekByTap(zone: Int) {
        val p = player ?: return
        val now = SystemClock.uptimeMillis()
        seekAccumSec = if (zone == lastSeekZone && now - lastSeekTapAt < 900) seekAccumSec + 10 else 10
        lastSeekZone = zone
        lastSeekTapAt = now
        if (zone < 0) {
            p.seekTo((p.currentPosition - SEEK_STEP_MS).coerceAtLeast(0))
            showRipple(rippleLeft, rippleLeftText)
        } else {
            seekForward(p)
            showRipple(rippleRight, rippleRightText)
        }
    }

    private fun showRipple(ripple: LinearLayout, label: TextView) {
        label.text = "$seekAccumSec seconds"
        val lp = ripple.layoutParams
        lp.width = (root.width * 0.38f).toInt()
        ripple.layoutParams = lp
        ripple.visibility = View.VISIBLE
        ripple.animate().cancel()
        ripple.alpha = 1f
        ripple.animate().alpha(0f).setStartDelay(550).setDuration(250)
            .withEndAction { ripple.visibility = View.GONE }.start()
    }

    private fun handleSwipe(ev: MotionEvent) {
        val p = player
        when (ev.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = ev.x
                downY = ev.y
                gestureMode = GestureMode.NONE
                startVolume = audioManager.getStreamVolume(AudioManager.STREAM_MUSIC).toFloat()
                startBrightness = window.attributes.screenBrightness.let {
                    if (it < 0) {
                        try {
                            Settings.System.getInt(contentResolver, Settings.System.SCREEN_BRIGHTNESS, 128) / 255f
                        } catch (_: Exception) {
                            0.5f
                        }
                    } else it
                }.coerceIn(0.01f, 1f)
                startPositionMs = p?.currentPosition ?: 0L
            }
            MotionEvent.ACTION_MOVE -> {
                val dx = ev.x - downX
                val dy = downY - ev.y
                if (gestureMode == GestureMode.NONE) {
                    val edge = Ui.dp(this, 40)
                    if (downY < edge || downY > root.height - edge) return
                    val slop = touchSlop * 1.5f
                    if (abs(dx) > slop && abs(dx) > abs(dy) * 1.3f) {
                        val dur = p?.duration ?: C.TIME_UNSET
                        if (dur != C.TIME_UNSET && dur > 0) gestureMode = GestureMode.SEEK
                    } else if (abs(dy) > slop && abs(dy) > abs(dx) * 1.3f) {
                        gestureMode = if (downX < root.width / 2f) GestureMode.BRIGHTNESS else GestureMode.VOLUME
                    }
                    if (gestureMode != GestureMode.NONE) handler.removeCallbacks(hideIndicatorsRunnable)
                }
                when (gestureMode) {
                    GestureMode.BRIGHTNESS -> {
                        val b = (startBrightness + dy / (root.height * 0.8f)).coerceIn(0.01f, 1f)
                        val lp = window.attributes
                        lp.screenBrightness = b
                        window.attributes = lp
                        showLevel(R.drawable.ic_brightness, (b * 100).toInt())
                    }
                    GestureMode.VOLUME -> {
                        val max = audioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC).coerceAtLeast(1)
                        val v = (startVolume + dy / (root.height * 0.8f) * max).coerceIn(0f, max.toFloat())
                        val iv = v.toInt()
                        if (iv != audioManager.getStreamVolume(AudioManager.STREAM_MUSIC)) {
                            audioManager.setStreamVolume(AudioManager.STREAM_MUSIC, iv, 0)
                        }
                        showLevel(R.drawable.ic_volume, (v * 100 / max).toInt())
                    }
                    GestureMode.SEEK -> {
                        val dur = p?.duration ?: return
                        val range = minOf(dur, 120_000L).coerceAtLeast(30_000L)
                        seekTargetMs = (startPositionMs + (dx / root.width.coerceAtLeast(1)) * range).toLong()
                            .coerceIn(0L, dur)
                        val delta = seekTargetMs - startPositionMs
                        seekIndicatorDelta.text = (if (delta >= 0) "+" else "−") + Ui.formatDuration(abs(delta))
                        seekIndicatorPos.text = "${Ui.formatDuration(seekTargetMs)} / ${Ui.formatDuration(dur)}"
                        levelIndicator.visibility = View.GONE
                        seekIndicatorPos.visibility = View.VISIBLE
                        seekIndicator.visibility = View.VISIBLE
                        timeBar.setPosition(seekTargetMs)
                    }
                    GestureMode.NONE -> Unit
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                if (gestureMode == GestureMode.SEEK) p?.seekTo(seekTargetMs)
                if (gestureMode != GestureMode.NONE) {
                    handler.removeCallbacks(hideIndicatorsRunnable)
                    handler.postDelayed(hideIndicatorsRunnable, 600)
                }
                gestureMode = GestureMode.NONE
            }
        }
    }

    private fun showLevel(icon: Int, percent: Int) {
        levelIcon.setImageResource(icon)
        levelBar.progress = percent.coerceIn(0, 100)
        levelText.text = "$percent%"
        seekIndicator.visibility = View.GONE
        levelIndicator.visibility = View.VISIBLE
    }

    private fun showHint(text: String) {
        seekIndicatorDelta.text = text
        seekIndicatorPos.text = ""
        seekIndicatorPos.visibility = View.GONE
        levelIndicator.visibility = View.GONE
        seekIndicator.visibility = View.VISIBLE
        handler.removeCallbacks(hideIndicatorsRunnable)
        handler.postDelayed({
            seekIndicator.visibility = View.GONE
            seekIndicatorPos.visibility = View.VISIBLE
        }, 900)
    }

    // ---------------------------------------------------------------- aspect / rotation

    private fun cycleAspect() {
        aspect = Aspect.values()[(aspect.ordinal + 1) % Aspect.values().size]
        applyAspect()
        showHint(aspect.label)
        scheduleHide()
    }

    private fun applyAspect() {
        val w = root.width
        val h = root.height
        val lp = playerView.layoutParams as FrameLayout.LayoutParams
        when (aspect) {
            Aspect.FIT, Aspect.FILL, Aspect.CROP -> {
                lp.width = FrameLayout.LayoutParams.MATCH_PARENT
                lp.height = FrameLayout.LayoutParams.MATCH_PARENT
                playerView.resizeMode = when (aspect) {
                    Aspect.FILL -> AspectRatioFrameLayout.RESIZE_MODE_FILL
                    Aspect.CROP -> AspectRatioFrameLayout.RESIZE_MODE_ZOOM
                    else -> AspectRatioFrameLayout.RESIZE_MODE_FIT
                }
            }
            Aspect.RATIO_16_9, Aspect.RATIO_4_3 -> {
                if (w <= 0 || h <= 0) return
                val ratio = if (aspect == Aspect.RATIO_16_9) 16f / 9f else 4f / 3f
                if (w.toFloat() / h > ratio) {
                    lp.height = h
                    lp.width = (h * ratio).toInt()
                } else {
                    lp.width = w
                    lp.height = (w / ratio).toInt()
                }
                playerView.resizeMode = AspectRatioFrameLayout.RESIZE_MODE_FILL
            }
        }
        lp.gravity = android.view.Gravity.CENTER
        playerView.layoutParams = lp
    }

    private fun toggleRotation() {
        rotation = if (isLandscape()) Rotation.PORTRAIT else Rotation.LANDSCAPE
        applyRotation()
        showHint(rotation.label)
        scheduleHide()
    }

    private fun applyRotation() {
        requestedOrientation = when (rotation) {
            Rotation.LANDSCAPE -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
            Rotation.PORTRAIT -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
            Rotation.SENSOR -> ActivityInfo.SCREEN_ORIENTATION_FULL_SENSOR
            Rotation.AUTO_VIDEO -> when {
                videoAspect > 1.05f -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                videoAspect < 0.95f -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
                else -> ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
            }
        }
    }

    // ---------------------------------------------------------------- sheets

    private fun showSpeedSheet() {
        val sheet = OptionSheet(this, "Playback speed")
        for (s in Ui.SPEEDS) {
            sheet.add(if (s == 1f) "Normal" else Ui.speedLabel(s), R.drawable.ic_speed, checked = s == speed) {
                speed = s
                player?.setPlaybackSpeed(s)
                btnSpeed.text = Ui.speedLabel(s)
            }
        }
        sheet.setOnDismiss { hideSystemBars() }.show()
    }

    private fun showAspectSheet() {
        val sheet = OptionSheet(this, "Aspect ratio")
        for (a in Aspect.values()) {
            sheet.add(a.label, R.drawable.ic_aspect, checked = a == aspect) {
                aspect = a
                applyAspect()
            }
        }
        sheet.setOnDismiss { hideSystemBars() }.show()
    }

    private fun cycleRepeat() {
        repeat = Repeat.values()[(repeat.ordinal + 1) % Repeat.values().size]
        player?.repeatMode = if (repeat == Repeat.ONE) Player.REPEAT_MODE_ONE else Player.REPEAT_MODE_OFF
        updateRepeatButton()
        updatePrevNext()
        showHint(repeat.label.let { if (repeat == Repeat.OFF) "Repeat off" else it })
        scheduleHide()
    }

    private fun updateRepeatButton() {
        btnRepeat.setImageResource(if (repeat == Repeat.ONE) R.drawable.ic_repeat_one else R.drawable.ic_repeat)
        btnRepeat.setColorFilter(
            ContextCompat.getColor(this, if (repeat == Repeat.OFF) android.R.color.white else R.color.accent)
        )
        btnRepeat.alpha = if (repeat == Repeat.OFF) 0.85f else 1f
    }

    private fun showSleepSheet() {
        val remaining = if (sleepAtElapsed > 0) sleepAtElapsed - SystemClock.elapsedRealtime() else 0L
        val subtitle = when {
            sleepAtEnd -> "Stops at the end of this video"
            remaining > 0 -> "Pauses in ${Ui.formatDuration(remaining)}"
            else -> null
        }
        val sheet = OptionSheet(this, "Sleep timer", subtitle)
        sheet.add("Off", R.drawable.ic_timer, checked = sleepAtElapsed == 0L && !sleepAtEnd) { setSleep(0) }
        for (m in intArrayOf(15, 30, 45, 60, 90)) {
            sheet.add("$m minutes", R.drawable.ic_timer) { setSleep(m) }
        }
        sheet.add("End of this video", R.drawable.ic_timer, checked = sleepAtEnd) {
            handler.removeCallbacks(sleepRunnable)
            sleepAtElapsed = 0L
            sleepAtEnd = true
            updateSleepButton()
            Toast.makeText(this, "Playback will stop at the end of this video", Toast.LENGTH_SHORT).show()
        }
        sheet.setOnDismiss { hideSystemBars() }.show()
    }

    private fun setSleep(minutes: Int) {
        handler.removeCallbacks(sleepRunnable)
        sleepAtEnd = false
        if (minutes <= 0) {
            sleepAtElapsed = 0L
        } else {
            sleepAtElapsed = SystemClock.elapsedRealtime() + minutes * 60_000L
            handler.postDelayed(sleepRunnable, minutes * 60_000L)
            Toast.makeText(this, "Playback will pause in $minutes minutes", Toast.LENGTH_SHORT).show()
        }
        updateSleepButton()
    }

    private fun updateSleepButton() {
        val active = sleepAtElapsed > 0 || sleepAtEnd
        btnSleep.setColorFilter(ContextCompat.getColor(this, if (active) R.color.accent else android.R.color.white))
    }

    private fun showRotationSheet() {
        val sheet = OptionSheet(this, "Screen rotation")
        for (r in Rotation.values()) {
            sheet.add(r.label, R.drawable.ic_rotate, checked = r == rotation) {
                rotation = r
                applyRotation()
            }
        }
        sheet.setOnDismiss { hideSystemBars() }.show()
    }

    private fun trackLabel(format: Format, n: Int): String {
        val lang = format.language?.takeIf { it.isNotBlank() && it != "und" && it != C.LANGUAGE_UNDETERMINED }
            ?.let { Locale(it).displayLanguage.replaceFirstChar { c -> c.titlecase(Locale.getDefault()) } }
        val base = format.label?.takeIf { it.isNotBlank() } ?: lang ?: "Track $n"
        val extra = when (format.channelCount) {
            1 -> "Mono"
            2 -> "Stereo"
            6 -> "5.1"
            8 -> "7.1"
            else -> null
        }
        return if (extra != null && format.sampleMimeType?.startsWith("audio") == true) "$base · $extra" else base
    }

    private fun showAudioTrackSheet() {
        val p = player ?: return
        val groups = p.currentTracks.groups.filter { it.type == C.TRACK_TYPE_AUDIO }
        val sheet = OptionSheet(this, "Audio track")
        var n = 0
        for (g in groups) {
            for (i in 0 until g.length) {
                if (!g.isTrackSupported(i)) continue
                n++
                sheet.add(trackLabel(g.getTrackFormat(i), n), R.drawable.ic_headset, checked = g.isTrackSelected(i)) {
                    p.trackSelectionParameters = p.trackSelectionParameters.buildUpon()
                        .setTrackTypeDisabled(C.TRACK_TYPE_AUDIO, false)
                        .setOverrideForType(TrackSelectionOverride(g.mediaTrackGroup, i))
                        .build()
                }
            }
        }
        if (n == 0) {
            Toast.makeText(this, "No audio tracks in this video", Toast.LENGTH_SHORT).show()
            return
        }
        sheet.setOnDismiss { hideSystemBars() }.show()
    }

    private fun showSubtitleSheet() {
        val p = player ?: return
        val groups = p.currentTracks.groups.filter { it.type == C.TRACK_TYPE_TEXT }
        val anySelected = groups.any { it.isSelected }
        val sheet = OptionSheet(this, "Subtitles")
        sheet.add("Off", R.drawable.ic_subtitles, checked = subtitlesDisabled || !anySelected) {
            subtitlesDisabled = true
            p.trackSelectionParameters = p.trackSelectionParameters.buildUpon()
                .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true).build()
        }
        var n = 0
        for (g in groups) {
            for (i in 0 until g.length) {
                if (!g.isTrackSupported(i)) continue
                n++
                sheet.add(
                    trackLabel(g.getTrackFormat(i), n), R.drawable.ic_subtitles,
                    checked = !subtitlesDisabled && g.isTrackSelected(i)
                ) {
                    subtitlesDisabled = false
                    p.trackSelectionParameters = p.trackSelectionParameters.buildUpon()
                        .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, false)
                        .setOverrideForType(TrackSelectionOverride(g.mediaTrackGroup, i))
                        .build()
                }
            }
        }
        sheet.add("Load subtitle file (.srt, .vtt)", R.drawable.ic_add) {
            try {
                pickSubtitle.launch(arrayOf("application/x-subrip", "text/*", "application/octet-stream"))
            } catch (_: Exception) {
                Toast.makeText(this, "No file picker available", Toast.LENGTH_SHORT).show()
            }
        }
        sheet.setOnDismiss { hideSystemBars() }.show()
    }

    private fun attachSubtitle(uri: Uri) {
        val item = current ?: return
        var name = uri.lastPathSegment ?: "Subtitles"
        try {
            contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
                if (c.moveToFirst()) c.getString(0)?.let { name = it }
            }
        } catch (_: Exception) {}
        val lower = name.lowercase(Locale.ROOT)
        val mime = when {
            lower.endsWith(".vtt") -> MimeTypes.TEXT_VTT
            lower.endsWith(".ass") || lower.endsWith(".ssa") -> MimeTypes.TEXT_SSA
            lower.endsWith(".ttml") || lower.endsWith(".dfxp") || lower.endsWith(".xml") -> MimeTypes.APPLICATION_TTML
            else -> MimeTypes.APPLICATION_SUBRIP
        }
        externalSubs[item.id] = ExternalSub(uri, mime, name.substringBeforeLast('.'))
        subtitlesDisabled = false
        val pos = player?.currentPosition ?: 0L
        loadItem(index, startPositionMs = pos)
        Toast.makeText(this, "Subtitles loaded: $name", Toast.LENGTH_SHORT).show()
    }

    private fun showMoreSheet() {
        val remaining = if (sleepAtElapsed > 0) sleepAtElapsed - SystemClock.elapsedRealtime() else 0L
        val sheet = OptionSheet(this, current?.title ?: "Options")
        sheet.add("Playback speed", R.drawable.ic_speed, Ui.speedLabel(speed)) { showSpeedSheet() }
        sheet.add("Aspect ratio", R.drawable.ic_aspect, aspect.label) { showAspectSheet() }
        sheet.add("Audio track", R.drawable.ic_headset) { showAudioTrackSheet() }
        sheet.add("Subtitles", R.drawable.ic_subtitles) { showSubtitleSheet() }
        sheet.add(
            "Sleep timer", R.drawable.ic_timer,
            when {
                sleepAtEnd -> "End of video"
                remaining > 0 -> Ui.formatDuration(remaining)
                else -> "Off"
            }
        ) { showSleepSheet() }
        sheet.add("Repeat", if (repeat == Repeat.ONE) R.drawable.ic_repeat_one else R.drawable.ic_repeat, repeat.label) {
            cycleRepeat()
        }
        sheet.add("Screen rotation", R.drawable.ic_rotate, rotation.label) { showRotationSheet() }
        if (supportsPip()) sheet.add("Picture-in-picture", R.drawable.ic_pip) { enterPip(silent = false) }
        sheet.add("Lock controls", R.drawable.ic_lock) { setLocked(true) }
        sheet.setOnDismiss { hideSystemBars() }.show()
    }

    // ---------------------------------------------------------------- picture-in-picture

    private fun supportsPip(): Boolean =
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.O &&
            packageManager.hasSystemFeature(PackageManager.FEATURE_PICTURE_IN_PICTURE)

    private fun isInPipCompat(): Boolean =
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.N && isInPictureInPictureMode

    private fun enterPip(silent: Boolean) {
        if (!supportsPip()) {
            if (!silent) Toast.makeText(this, "Picture-in-picture isn't available on this device", Toast.LENGTH_SHORT).show()
            return
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            try {
                enterPictureInPictureMode(buildPipParams())
            } catch (_: Exception) {
                if (!silent) Toast.makeText(this, "Couldn't start picture-in-picture", Toast.LENGTH_SHORT).show()
            }
        }
    }

    private fun updatePipParams() {
        if (!supportsPip() || Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        try {
            setPictureInPictureParams(buildPipParams())
        } catch (_: Exception) {}
    }

    @RequiresApi(Build.VERSION_CODES.O)
    private fun buildPipParams(): PictureInPictureParams {
        val ratio = videoAspect.coerceIn(0.42f, 2.38f)
        val builder = PictureInPictureParams.Builder()
            .setAspectRatio(Rational((ratio * 1000).toInt(), 1000))
            .setActions(
                listOf(
                    pipAction(CMD_REWIND, R.drawable.ic_replay, "Back 10 seconds"),
                    if (player?.isPlaying == true) pipAction(CMD_PLAY_PAUSE, R.drawable.ic_pause, "Pause")
                    else pipAction(CMD_PLAY_PAUSE, R.drawable.ic_play, "Play"),
                    pipAction(CMD_FORWARD, R.drawable.ic_forward, "Forward 10 seconds"),
                )
            )
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            builder.setAutoEnterEnabled(player?.isPlaying == true)
            builder.setSeamlessResizeEnabled(true)
        }
        return builder.build()
    }

    @RequiresApi(Build.VERSION_CODES.O)
    private fun pipAction(cmd: Int, icon: Int, title: String): RemoteAction {
        val intent = Intent(ACTION_PIP_CONTROL).setPackage(packageName).putExtra(EXTRA_PIP_CMD, cmd)
        val pi = PendingIntent.getBroadcast(
            this, cmd, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        return RemoteAction(Icon.createWithResource(this, icon), title, title, pi)
    }

    // ---------------------------------------------------------------- system UI

    private fun hideSystemBars() {
        val c = WindowInsetsControllerCompat(window, window.decorView)
        c.hide(WindowInsetsCompat.Type.systemBars())
        c.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
    }
}
