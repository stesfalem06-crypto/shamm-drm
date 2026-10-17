package com.shammapps.xama.ui

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.TextView
import androidx.core.content.ContextCompat
import androidx.recyclerview.widget.RecyclerView
import com.shammapps.xama.R
import com.shammapps.xama.data.LocalVideo
import com.shammapps.xama.util.ThumbLoader

/** Music list with a "Play all / Shuffle" header. */
class AudioAdapter(
    private val tracks: List<LocalVideo>,
    private val nowPlayingId: String?,
    private val onPlay: (index: Int, shuffle: Boolean) -> Unit,
) : RecyclerView.Adapter<RecyclerView.ViewHolder>() {

    private class HeaderHolder(v: View) : RecyclerView.ViewHolder(v) {
        val count: TextView = v.findViewById(R.id.audioHeaderCount)
        val playAll: View = v.findViewById(R.id.audioPlayAll)
        val shuffle: View = v.findViewById(R.id.audioShuffle)
    }

    private class TrackHolder(v: View) : RecyclerView.ViewHolder(v) {
        val art: ImageView = v.findViewById(R.id.audioArt)
        val title: TextView = v.findViewById(R.id.audioTitle)
        val meta: TextView = v.findViewById(R.id.audioMeta)
        val duration: TextView = v.findViewById(R.id.audioDuration)
    }

    override fun getItemViewType(position: Int) = if (position == 0) 0 else 1

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): RecyclerView.ViewHolder {
        val inflater = LayoutInflater.from(parent.context)
        return if (viewType == 0) HeaderHolder(inflater.inflate(R.layout.item_audio_header, parent, false))
        else TrackHolder(inflater.inflate(R.layout.item_audio, parent, false))
    }

    override fun onBindViewHolder(holder: RecyclerView.ViewHolder, position: Int) {
        if (holder is HeaderHolder) {
            val totalMs = tracks.sumOf { it.durationMs }
            holder.count.text = buildString {
                append(if (tracks.size == 1) "1 track" else "${tracks.size} tracks")
                if (totalMs > 0) append(" · ").append(Ui.formatDuration(totalMs))
            }
            holder.playAll.setOnClickListener { onPlay(0, false) }
            holder.shuffle.setOnClickListener { onPlay(0, true) }
            return
        }
        holder as TrackHolder
        val index = position - 1
        val track = tracks[index]
        val ctx = holder.itemView.context
        holder.title.text = track.title
        val playing = track.id == nowPlayingId
        holder.title.setTextColor(
            ContextCompat.getColor(ctx, if (playing) R.color.accent_light else R.color.text_primary)
        )
        holder.meta.text = listOf(track.artist, track.album).filter { it.isNotBlank() }
            .joinToString(" · ").ifBlank { track.folderName }
        holder.duration.text = if (track.durationMs > 0) Ui.formatDuration(track.durationMs) else ""
        ThumbLoader.load(ctx, track, holder.art, "art-" + track.id)
        holder.itemView.setOnClickListener { onPlay(index, false) }
    }

    override fun getItemCount() = if (tracks.isEmpty()) 0 else tracks.size + 1
}
