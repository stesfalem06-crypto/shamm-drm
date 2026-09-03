package com.shammapps.xama.ui

import android.os.Bundle
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
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
 * Plays exactly one video. Handles both cases:
 *   - Plain (unencrypted) file: normal ExoPlayer file playback, no restrictions.
 *   - Protected (.shammvid) file: resolves + unwraps the content key for THIS
 *     phone, then streams decrypted bytes through ShammDataSource. Screenshot/
 *     recording protection and root/emulator checks are enforced only for
 *     protected content - a plain video has none of these restrictions, per
 *     the product requirement.
 */
class PlayerActivity : AppCompatActivity() {

    private var player: ExoPlayer? = null
    private var contentKey: ByteArray? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_player)

        val title = intent.getStringExtra(MainActivity.EXTRA_TITLE) ?: ""
        val filePath = intent.getStringExtra(MainActivity.EXTRA_FILE_PATH) ?: return finishWithError("Missing file")
        val isEncrypted = intent.getBooleanExtra(MainActivity.EXTRA_IS_ENCRYPTED, false)
        val ivBase64 = intent.getStringExtra(MainActivity.EXTRA_IV_BASE64)
        val videoId = intent.getStringExtra(MainActivity.EXTRA_VIDEO_ID) ?: ""

        title.let { supportActionBar?.title = it }

        if (isEncrypted) {
            SecurityGuard.enableScreenshotProtection(this)
            if (SecurityGuard.isEnvironmentCompromised(this)) {
                Toast.makeText(this, "This device can't play protected videos.", Toast.LENGTH_LONG).show()
                finish()
                return
            }
            playProtected(videoId, ivBase64 ?: "")
        } else {
            playPlain(filePath)
        }
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

        val exo = ExoPlayer.Builder(this).build()
        player = exo
        findViewById<PlayerView>(R.id.playerView).player = exo

        val factory = DataSource.Factory { ShammDataSource(resolved.contentKey, resolved.iv) }
        val mediaSource = ProgressiveMediaSource.Factory(factory)
            .createMediaSource(MediaItem.fromUri(android.net.Uri.fromFile(resolved.videoFile)))

        exo.setMediaSource(mediaSource)
        exo.prepare()
        exo.playWhenReady = true
    }

    private fun playPlain(filePath: String) {
        val exo = ExoPlayer.Builder(this).build()
        player = exo
        findViewById<PlayerView>(R.id.playerView).player = exo
        exo.setMediaItem(MediaItem.fromUri(android.net.Uri.fromFile(File(filePath))))
        exo.prepare()
        exo.playWhenReady = true
    }

    private fun finishWithError(message: String) {
        Toast.makeText(this, message, Toast.LENGTH_LONG).show()
        finish()
    }

    override fun onStop() {
        super.onStop()
        player?.pause()
    }

    override fun onDestroy() {
        super.onDestroy()
        player?.release()
        player = null
        // Wipe the content key from memory the moment playback is done -
        // no reason for decrypted key material to linger any longer than it must.
        contentKey?.let { NativeCrypto.shamm_wipe(it, NativeCrypto.KEY_LEN) }
        contentKey = null
    }
}
