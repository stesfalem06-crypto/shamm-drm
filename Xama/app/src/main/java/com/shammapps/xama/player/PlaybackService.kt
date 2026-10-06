package com.shammapps.xama.player

import android.app.PendingIntent
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import androidx.annotation.OptIn
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import com.shammapps.xama.ui.AudioPlayerActivity

/**
 * Background music playback with a system media notification / lock-screen
 * controls (Media3 MediaSessionService).
 *
 * Only plain audio files are played here. Protected video never goes through
 * this service; it stays inside PlayerActivity with FLAG_SECURE.
 */
@OptIn(UnstableApi::class)
class PlaybackService : MediaSessionService() {

    private var session: MediaSession? = null
    private val handler = Handler(Looper.getMainLooper())
    private val sleepRunnable = Runnable {
        session?.player?.pause()
        sleepAtElapsed = 0L
    }

    override fun onCreate() {
        super.onCreate()
        val player = ExoPlayer.Builder(this)
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(C.USAGE_MEDIA)
                    .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
                    .build(),
                /* handleAudioFocus = */ true
            )
            .setHandleAudioBecomingNoisy(true)
            .setWakeMode(C.WAKE_MODE_LOCAL)
            .build()

        val openPlayer = PendingIntent.getActivity(
            this,
            0,
            Intent(this, AudioPlayerActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        session = MediaSession.Builder(this, player)
            .setSessionActivity(openPlayer)
            .setCallback(object : MediaSession.Callback {
                override fun onAddMediaItems(
                    mediaSession: MediaSession,
                    controller: MediaSession.ControllerInfo,
                    mediaItems: MutableList<MediaItem>,
                ): ListenableFuture<MutableList<MediaItem>> {
                    // Items from our own controller keep their URI; items coming from
                    // other controllers only carry requestMetadata.mediaUri.
                    val resolved = mediaItems.mapNotNull { item ->
                        when {
                            item.localConfiguration != null -> item
                            item.requestMetadata.mediaUri != null ->
                                item.buildUpon().setUri(item.requestMetadata.mediaUri).build()
                            else -> null
                        }
                    }.toMutableList()
                    return Futures.immediateFuture(resolved)
                }
            })
            .build()
        instance = this
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? = session

    override fun onTaskRemoved(rootIntent: Intent?) {
        val player = session?.player
        if (player == null || !player.playWhenReady || player.mediaItemCount == 0) {
            stopSelf()
        }
    }

    override fun onDestroy() {
        handler.removeCallbacks(sleepRunnable)
        sleepAtElapsed = 0L
        session?.run {
            player.release()
            release()
        }
        session = null
        if (instance === this) instance = null
        super.onDestroy()
    }

    private fun scheduleSleep(minutes: Int) {
        handler.removeCallbacks(sleepRunnable)
        if (minutes <= 0) {
            sleepAtElapsed = 0L
            return
        }
        val delay = minutes * 60_000L
        sleepAtElapsed = SystemClock.elapsedRealtime() + delay
        handler.postDelayed(sleepRunnable, delay)
    }

    companion object {
        @Volatile
        var instance: PlaybackService? = null
            private set

        /** elapsedRealtime when the music sleep timer fires, 0 = off. */
        @Volatile
        var sleepAtElapsed: Long = 0L
            private set

        val isRunning: Boolean get() = instance != null

        fun setSleepTimer(minutes: Int) {
            instance?.scheduleSleep(minutes)
        }
    }
}
