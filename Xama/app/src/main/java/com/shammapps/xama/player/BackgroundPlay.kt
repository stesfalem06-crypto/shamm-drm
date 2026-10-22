package com.shammapps.xama.player

import android.content.ComponentName
import android.content.Context
import android.os.Bundle
import android.widget.Toast
import androidx.core.content.ContextCompat
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import com.shammapps.xama.data.LocalVideo
import com.shammapps.xama.ui.AudioPlayerActivity

/**
 * MX-style "play in background": hands a plain video (and the rest of its playlist)
 * to the existing PlaybackService as audio-only playback with a media notification
 * and lock-screen controls.
 *
 * Protected videos are refused: their decryption is bound to PlayerActivity
 * (FLAG_SECURE screen) and moving it into the service would mean changing the DRM
 * playback path, which we don't do.
 */
object BackgroundPlay {
    const val KEY_KIND = "xama_kind"
    const val KIND_VIDEO = "video"
    const val PREF_CONTINUE = "bg_continue_on_leave"

    fun continueOnLeave(context: Context): Boolean =
        context.getSharedPreferences("xama_ui", Context.MODE_PRIVATE).getBoolean(PREF_CONTINUE, false)

    fun setContinueOnLeave(context: Context, value: Boolean) =
        context.getSharedPreferences("xama_ui", Context.MODE_PRIVATE).edit().putBoolean(PREF_CONTINUE, value).apply()

    fun isAllowed(v: LocalVideo?): Boolean = v != null && !v.isEncrypted && !v.isPdf && !v.isAudio

    fun refuseToast(context: Context) = Toast.makeText(
        context, "Background play isn't available for protected videos.", Toast.LENGTH_SHORT
    ).show()

    private fun toItem(v: LocalVideo): MediaItem? {
        val base = AudioPlayerActivity.toMediaItem(v) ?: return null
        val meta = base.mediaMetadata.buildUpon()
            .setArtist(v.folderName.ifBlank { null })
            .setExtras(Bundle().apply { putString(KEY_KIND, KIND_VIDEO) })
            .build()
        return base.buildUpon().setMediaMetadata(meta).build()
    }

    /**
     * Starts background playback of [playlist] at [index] / [positionMs].
     * [onStarted] runs on the main thread once the service took over (close the video UI there).
     */
    fun start(context: Context, playlist: List<LocalVideo>, index: Int, positionMs: Long, onStarted: () -> Unit) {
        val plain = playlist.filter { isAllowed(it) }
        val current = playlist.getOrNull(index)
        if (!isAllowed(current)) {
            refuseToast(context)
            return
        }
        val items = plain.mapNotNull { toItem(it) }
        val start = plain.indexOfFirst { it.id == current!!.id }.coerceAtLeast(0)
        if (items.isEmpty()) return
        val app = context.applicationContext
        val token = SessionToken(app, ComponentName(app, PlaybackService::class.java))
        val future = MediaController.Builder(app, token).buildAsync()
        future.addListener({
            val c = try { future.get() } catch (_: Exception) { null }
            if (c == null) {
                Toast.makeText(app, "Couldn't start background play.", Toast.LENGTH_SHORT).show()
                return@addListener
            }
            c.setMediaItems(items, start, positionMs.coerceAtLeast(0))
            c.prepare()
            c.play()
            onStarted()
            MediaController.releaseFuture(future)
        }, ContextCompat.getMainExecutor(app))
    }
}
