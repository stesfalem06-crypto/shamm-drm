package com.shammapps.xama.ui

import android.os.Bundle
import android.view.View
import android.view.WindowManager
import android.widget.ImageButton
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.media3.common.MediaItem
import androidx.media3.datasource.DataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.ProgressiveMediaSource
import androidx.media3.ui.PlayerView
import com.shammapps.xama.R
import com.shammapps.xama.crypto.NativeCrypto
import com.shammapps.xama.crypto.ShammKeyResolver
import com.shammapps.xama.player.ShammDataSource
import com.shammapps.xama.security.SecurityGuard
import java.io.File

/**
 * Full-screen video player with MX Player-style chrome:
 * top title bar, large center play/pause, bottom seek + time.
 */
class PlayerActivity : AppCompatActivity() {

    private var player: ExoPlayer? = null
    private var contentKey: ByteArray? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Immersive edge-to-edge player
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_player)
        hideSystemBars()

        val title = intent.getStringExtra(MainActivity.EXTRA_TITLE) ?: "Video"
        val filePath = intent.getStringExtra(MainActivity.EXTRA_FILE_PATH) ?: ""
        val isEncrypted = intent.getBooleanExtra(MainActivity.EXTRA_IS_ENCRYPTED, false)
        val ivBase64 = intent.getStringExtra(MainActivity.EXTRA_IV_BASE64)
        val videoId = intent.getStringExtra(MainActivity.EXTRA_VIDEO_ID) ?: ""

        val playerView = findViewById<PlayerView>(R.id.playerView)
        // Wire title + close into the custom controller once it inflates
        playerView.post {
            playerView.findViewById<TextView>(R.id.exo_title)?.text = title
            playerView.findViewById<ImageButton>(R.id.exo_close)?.setOnClickListener { finish() }
        }

        if (isEncrypted) {
            SecurityGuard.enableScreenshotProtection(this)
            if (SecurityGuard.isEnvironmentCompromised(this)) {
                Toast.makeText(this, "This device can't play protected videos.", Toast.LENGTH_LONG).show()
                finish()
                return
            }
            playProtected(videoId, ivBase64 ?: "", playerView)
        } else {
            playPlain(filePath, playerView)
        }
    }

    private fun hideSystemBars() {
        val c = WindowInsetsControllerCompat(window, window.decorView)
        c.hide(WindowInsetsCompat.Type.systemBars())
        c.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
    }

    private fun playProtected(videoId: String, ivBase64: String, playerView: PlayerView) {
        val resolver = ShammKeyResolver(this)
        val resolved = resolver.resolve(videoId, ivBase64)
        if (resolved == null) {
            Toast.makeText(this, "This video isn't licensed for this device.", Toast.LENGTH_LONG).show()
            finish()
            return
        }
        contentKey = resolved.contentKey

        val exo = ExoPlayer.Builder(this).build()
        player = exo
        playerView.player = exo

        val factory = DataSource.Factory { ShammDataSource(resolved.contentKey, resolved.iv) }
        val mediaSource = ProgressiveMediaSource.Factory(factory)
            .createMediaSource(MediaItem.fromUri(android.net.Uri.fromFile(resolved.videoFile)))

        exo.setMediaSource(mediaSource)
        exo.prepare()
        exo.playWhenReady = true
    }

    private fun playPlain(filePath: String, playerView: PlayerView) {
        val exo = ExoPlayer.Builder(this).build()
        player = exo
        playerView.player = exo
        exo.setMediaItem(MediaItem.fromUri(android.net.Uri.fromFile(File(filePath))))
        exo.prepare()
        exo.playWhenReady = true
    }

    override fun onStop() {
        super.onStop()
        player?.pause()
    }

    override fun onDestroy() {
        super.onDestroy()
        player?.release()
        player = null
        contentKey?.let { NativeCrypto.shamm_wipe(it, NativeCrypto.KEY_LEN) }
        contentKey = null
    }
}
