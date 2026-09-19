package com.shammapps.xama.ui

import android.media.AudioManager
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.view.GestureDetector
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.ImageButton
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.GestureDetectorCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackParameters
import androidx.media3.common.Player
import androidx.media3.datasource.DataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.ProgressiveMediaSource
import androidx.media3.ui.AspectRatioFrameLayout
import androidx.media3.ui.PlayerView
import com.shammapps.xama.R
import com.shammapps.xama.crypto.NativeCrypto
import com.shammapps.xama.crypto.ShammKeyResolver
import com.shammapps.xama.player.ShammDataSource
import com.shammapps.xama.security.SecurityGuard
import com.shammapps.xama.data.WatchHistory
import java.io.File
import kotlin.math.abs

/**
 * Competitive full-screen player:
 * - Brightness (left vertical drag) / Volume (right vertical drag)
 * - Double-tap seek ±10s
 * - Lock, speed menu, aspect ratio cycle
 * - Custom chrome with seek bar + transport
 */
class PlayerActivity : AppCompatActivity() {

    private var player: ExoPlayer? = null
    private var contentKey: ByteArray? = null
    private var locked = false
    private var aspectIndex = 0
    private val aspectModes = intArrayOf(
        AspectRatioFrameLayout.RESIZE_MODE_FIT,
        AspectRatioFrameLayout.RESIZE_MODE_ZOOM,
        AspectRatioFrameLayout.RESIZE_MODE_FILL,
        AspectRatioFrameLayout.RESIZE_MODE_FIXED_WIDTH,
    )
    private val aspectLabels = arrayOf("Fit", "Zoom", "Fill", "Stretch")
    private val speeds = floatArrayOf(0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f)
    private var speedIndex = 2

    private lateinit var playerView: PlayerView
    private lateinit var gestureHint: TextView
    private lateinit var lockOverlay: View
    private lateinit var audioManager: AudioManager
    private val hideHint = Handler(Looper.getMainLooper())

    private var gestureStartY = 0f
    private var gestureStartX = 0f
    private var gestureMode = 0 // 0 none, 1 brightness, 2 volume
    private var startBrightness = 0f
    private var startVolume = 0

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_player)
        hideSystemBars()
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() { finish() }
        })

        playerView = findViewById(R.id.playerView)
        gestureHint = findViewById(R.id.gestureHint)
        lockOverlay = findViewById(R.id.lockOverlay)
        audioManager = getSystemService(AUDIO_SERVICE) as AudioManager

        val title = intent.getStringExtra(MainActivity.EXTRA_TITLE) ?: "Video"
        val filePath = intent.getStringExtra(MainActivity.EXTRA_FILE_PATH) ?: ""
        val contentUri = intent.getStringExtra(MainActivity.EXTRA_CONTENT_URI)
        val isEncrypted = intent.getBooleanExtra(MainActivity.EXTRA_IS_ENCRYPTED, false)
        val ivBase64 = intent.getStringExtra(MainActivity.EXTRA_IV_BASE64)
        val videoId = intent.getStringExtra(MainActivity.EXTRA_VIDEO_ID) ?: ""

        playerView.post { wireController(title) }
        setupGestures()

        val historyId = when {
            videoId.isNotBlank() -> videoId
            !contentUri.isNullOrBlank() -> contentUri
            filePath.isNotBlank() -> filePath
            else -> null
        }
        if (historyId != null) WatchHistory.record(this, historyId)

        findViewById<ImageButton>(R.id.btn_unlock).setOnClickListener { setLocked(false) }

        if (isEncrypted) {
            SecurityGuard.enableScreenshotProtection(this)
            if (SecurityGuard.isEnvironmentCompromised(this)) {
                Toast.makeText(this, "This device can't play protected videos.", Toast.LENGTH_LONG).show()
                finish()
                return
            }
            playProtected(videoId, ivBase64 ?: "")
        } else {
            playPlain(filePath, contentUri)
        }
    }

    private fun wireController(title: String) {
        playerView.findViewById<TextView>(R.id.exo_title)?.text = title
        playerView.findViewById<ImageButton>(R.id.exo_close)?.setOnClickListener { finish() }
        playerView.findViewById<ImageButton>(R.id.btn_lock)?.setOnClickListener { setLocked(true) }
        playerView.findViewById<ImageButton>(R.id.btn_speed)?.setOnClickListener { showSpeedDialog() }
        playerView.findViewById<ImageButton>(R.id.btn_aspect)?.setOnClickListener { cycleAspect() }
        playerView.findViewById<TextView>(R.id.speed_label)?.text = "1.0x"
    }

    private fun setLocked(value: Boolean) {
        locked = value
        lockOverlay.visibility = if (value) View.VISIBLE else View.GONE
        if (value) playerView.hideController() else playerView.showController()
    }

    private fun showSpeedDialog() {
        val labels = speeds.map { String.format("%.2gx", it).replace("0.", ".") }.toTypedArray()
        // cleaner labels
        val nice = arrayOf("0.5x", "0.75x", "1.0x", "1.25x", "1.5x", "1.75x", "2.0x")
        AlertDialog.Builder(this, com.google.android.material.R.style.ThemeOverlay_MaterialComponents_Dialog_Alert)
            .setTitle("Playback speed")
            .setSingleChoiceItems(nice, speedIndex) { dialog, which ->
                speedIndex = which
                player?.playbackParameters = PlaybackParameters(speeds[which])
                playerView.findViewById<TextView>(R.id.speed_label)?.text = nice[which]
                dialog.dismiss()
            }
            .show()
    }

    private fun cycleAspect() {
        aspectIndex = (aspectIndex + 1) % aspectModes.size
        playerView.resizeMode = aspectModes[aspectIndex]
        showHint(aspectLabels[aspectIndex])
    }

    private fun setupGestures() {
        val detector = GestureDetectorCompat(this, object : GestureDetector.SimpleOnGestureListener() {
            override fun onDoubleTap(e: MotionEvent): Boolean {
                if (locked) return true
                val p = player ?: return true
                val w = playerView.width
                if (e.x < w / 2f) {
                    p.seekTo((p.currentPosition - 10_000).coerceAtLeast(0))
                    showHint("−10s")
                } else {
                    p.seekTo((p.currentPosition + 10_000).coerceAtMost(p.duration.coerceAtLeast(0)))
                    showHint("+10s")
                }
                return true
            }

            override fun onSingleTapConfirmed(e: MotionEvent): Boolean {
                if (locked) return true
                if (playerView.isControllerFullyVisible) playerView.hideController()
                else playerView.showController()
                return true
            }
        })

        playerView.setOnTouchListener { _, event ->
            if (locked) {
                detector.onTouchEvent(event)
                return@setOnTouchListener true
            }
            detector.onTouchEvent(event)
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    gestureStartY = event.y
                    gestureStartX = event.x
                    gestureMode = 0
                    startVolume = audioManager.getStreamVolume(AudioManager.STREAM_MUSIC)
                    startBrightness = window.attributes.screenBrightness.let {
                        if (it < 0) Settings.System.getInt(contentResolver, Settings.System.SCREEN_BRIGHTNESS, 128) / 255f
                        else it
                    }
                }
                MotionEvent.ACTION_MOVE -> {
                    val dy = gestureStartY - event.y
                    val dx = event.x - gestureStartX
                    if (gestureMode == 0 && abs(dy) > 24 && abs(dy) > abs(dx) * 1.2f) {
                        gestureMode = if (gestureStartX < playerView.width / 2f) 1 else 2
                    }
                    when (gestureMode) {
                        1 -> { // brightness
                            val delta = dy / playerView.height
                            val b = (startBrightness + delta).coerceIn(0.01f, 1f)
                            val lp = window.attributes
                            lp.screenBrightness = b
                            window.attributes = lp
                            showHint("☀ ${(b * 100).toInt()}%")
                        }
                        2 -> { // volume
                            val max = audioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
                            val delta = (dy / playerView.height * max).toInt()
                            val v = (startVolume + delta).coerceIn(0, max)
                            audioManager.setStreamVolume(AudioManager.STREAM_MUSIC, v, 0)
                            showHint("🔊 ${(v * 100 / max.coerceAtLeast(1))}%")
                        }
                    }
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> gestureMode = 0
            }
            true
        }
    }

    private fun showHint(text: String) {
        gestureHint.text = text
        gestureHint.visibility = View.VISIBLE
        hideHint.removeCallbacksAndMessages(null)
        hideHint.postDelayed({ gestureHint.visibility = View.GONE }, 700)
    }

    private fun hideSystemBars() {
        val c = WindowInsetsControllerCompat(window, window.decorView)
        c.hide(WindowInsetsCompat.Type.systemBars())
        c.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
    }

    private fun playProtected(videoId: String, ivBase64: String) {
        val resolver = ShammKeyResolver(this)
        val resolved = resolver.resolve(videoId, ivBase64)
        if (resolved == null) {
            Toast.makeText(this, "This video isn't licensed for this device.", Toast.LENGTH_LONG).show()
            finish()
            return
        }
        contentKey = resolved.contentKey
        val exo = ExoPlayer.Builder(this).build().also { player = it }
        playerView.player = exo
        val factory = DataSource.Factory { ShammDataSource(resolved.contentKey, resolved.iv) }
        val mediaSource = ProgressiveMediaSource.Factory(factory)
            .createMediaSource(MediaItem.fromUri(Uri.fromFile(resolved.videoFile)))
        exo.setMediaSource(mediaSource)
        exo.prepare()
        exo.playWhenReady = true
        exo.addListener(object : Player.Listener {
            override fun onPlaybackStateChanged(state: Int) {
                if (state == Player.STATE_ENDED) exo.seekTo(0)
            }
        })
    }

    private fun playPlain(filePath: String, contentUri: String?) {
        val exo = ExoPlayer.Builder(this).build().also { player = it }
        playerView.player = exo
        val uri = when {
            !contentUri.isNullOrBlank() -> Uri.parse(contentUri)
            filePath.isNotBlank() -> Uri.fromFile(File(filePath))
            else -> {
                Toast.makeText(this, "Video file not found.", Toast.LENGTH_LONG).show()
                finish()
                return
            }
        }
        exo.setMediaItem(MediaItem.fromUri(uri))
        exo.prepare()
        exo.playWhenReady = true
    }

    override fun onStop() {
        super.onStop()
        player?.pause()
    }

    override fun onDestroy() {
        super.onDestroy()
        hideHint.removeCallbacksAndMessages(null)
        player?.release()
        player = null
        contentKey?.let { NativeCrypto.shamm_wipe(it, NativeCrypto.KEY_LEN) }
        contentKey = null
    }
}
