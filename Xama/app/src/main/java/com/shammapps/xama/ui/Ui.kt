package com.shammapps.xama.ui

import android.content.Context
import android.view.LayoutInflater
import android.view.View
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import androidx.core.content.ContextCompat
import com.google.android.material.bottomsheet.BottomSheetBehavior
import com.google.android.material.bottomsheet.BottomSheetDialog
import com.shammapps.xama.R

/** Small shared UI helpers. */
object Ui {
    fun formatDuration(ms: Long): String {
        if (ms <= 0) return "0:00"
        val s = ms / 1000
        val h = s / 3600
        val m = (s % 3600) / 60
        val sec = s % 60
        return if (h > 0) String.format("%d:%02d:%02d", h, m, sec) else String.format("%d:%02d", m, sec)
    }

    fun dp(context: Context, value: Int): Int = (value * context.resources.displayMetrics.density).toInt()

    fun speedLabel(speed: Float): String {
        val s = if (speed == speed.toInt().toFloat()) speed.toInt().toString() else speed.toString().trimEnd('0')
        return "${s}x"
    }

    val SPEEDS = floatArrayOf(0.25f, 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f)
}

/**
 * Consistent, elegant bottom-sheet menu used by the players
 * (speed, aspect, tracks, sleep timer, overflow…).
 */
class OptionSheet(private val context: Context, title: String, subtitle: String? = null) {

    data class Option(
        val label: String,
        val iconRes: Int = 0,
        val value: String? = null,
        val checked: Boolean = false,
        val onClick: () -> Unit,
    )

    private val dialog = BottomSheetDialog(context)
    private val root: View = LayoutInflater.from(context).inflate(R.layout.sheet_options, null, false)
    private val rows: LinearLayout = root.findViewById(R.id.sheetRows)

    init {
        root.findViewById<TextView>(R.id.sheetTitle).text = title
        root.findViewById<TextView>(R.id.sheetSubtitle).apply {
            if (subtitle.isNullOrBlank()) visibility = View.GONE else {
                visibility = View.VISIBLE
                text = subtitle
            }
        }
        dialog.setContentView(root)
        dialog.behavior.state = BottomSheetBehavior.STATE_EXPANDED
        dialog.behavior.skipCollapsed = true
    }

    fun add(option: Option): OptionSheet {
        val row = LayoutInflater.from(context).inflate(R.layout.item_sheet_row, rows, false)
        val icon = row.findViewById<ImageView>(R.id.rowIcon)
        if (option.iconRes != 0) icon.setImageResource(option.iconRes) else icon.visibility = View.GONE
        val label = row.findViewById<TextView>(R.id.rowLabel)
        label.text = option.label
        if (option.checked) {
            label.setTextColor(ContextCompat.getColor(context, R.color.accent_light))
            if (option.iconRes != 0) icon.setColorFilter(ContextCompat.getColor(context, R.color.accent))
        }
        row.findViewById<TextView>(R.id.rowValue).apply {
            if (option.value.isNullOrBlank()) visibility = View.GONE else text = option.value
        }
        row.findViewById<ImageView>(R.id.rowCheck).visibility = if (option.checked) View.VISIBLE else View.GONE
        row.setOnClickListener {
            dialog.dismiss()
            option.onClick()
        }
        rows.addView(row)
        return this
    }

    fun add(label: String, iconRes: Int = 0, value: String? = null, checked: Boolean = false, onClick: () -> Unit) =
        add(Option(label, iconRes, value, checked, onClick))

    fun setOnDismiss(block: () -> Unit): OptionSheet {
        dialog.setOnDismissListener { block() }
        return this
    }

    fun show() = dialog.show()
}
