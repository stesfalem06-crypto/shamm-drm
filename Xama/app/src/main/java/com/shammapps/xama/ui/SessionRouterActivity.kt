package com.shammapps.xama.ui

import android.content.Intent
import android.os.Bundle
import androidx.annotation.OptIn
import android.app.Activity
import androidx.media3.common.util.UnstableApi
import com.shammapps.xama.data.LibraryCache
import com.shammapps.xama.player.BackgroundPlay
import com.shammapps.xama.player.PlaybackService

/**
 * Target of the media notification tap. Music opens the audio player; a video that
 * was sent to background play re-opens the video player at the current position
 * (and stops the background audio so it doesn't play twice).
 */
@OptIn(UnstableApi::class)
class SessionRouterActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val player = PlaybackService.instance?.currentPlayer()
        val item = player?.currentMediaItem
        val isVideo = item?.mediaMetadata?.extras?.getString(BackgroundPlay.KEY_KIND) == BackgroundPlay.KIND_VIDEO
        if (player != null && item != null && isVideo) {
            val ids = ArrayList<String>()
            for (i in 0 until player.mediaItemCount) ids.add(player.getMediaItemAt(i).mediaId)
            val pos = player.currentPosition
            val video = LibraryCache.byIds(listOf(item.mediaId)).firstOrNull()
            player.pause()
            player.clearMediaItems()
            startActivity(Intent(this, PlayerActivity::class.java).apply {
                putExtra(MainActivity.EXTRA_VIDEO_ID, item.mediaId)
                putExtra(MainActivity.EXTRA_TITLE, video?.title ?: item.mediaMetadata.title?.toString())
                putExtra(MainActivity.EXTRA_FILE_PATH, video?.filePath ?: "")
                putExtra(MainActivity.EXTRA_CONTENT_URI, video?.contentUri ?: item.localConfiguration?.uri?.toString())
                putExtra(MainActivity.EXTRA_IS_ENCRYPTED, false)
                putExtra(PlayerActivity.EXTRA_START_POSITION, pos)
                putStringArrayListExtra(PlayerActivity.EXTRA_PLAYLIST_IDS, ids)
            })
        } else {
            startActivity(Intent(this, AudioPlayerActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP))
        }
        finish()
    }
}
