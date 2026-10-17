package com.shammapps.xama.ui

import android.content.Context
import android.graphics.Matrix
import android.graphics.RectF
import android.util.AttributeSet
import android.view.GestureDetector
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import androidx.appcompat.widget.AppCompatImageView

/** ImageView with pinch-zoom (1x-5x), pan, and double-tap zoom. Fits width at 1x. */
class ZoomImageView @JvmOverloads constructor(
    context: Context, attrs: AttributeSet? = null,
) : AppCompatImageView(context, attrs) {

    private val m = Matrix()
    private var base = 1f
    private var scale = 1f
    private val maxScale = 5f

    val isZoomed: Boolean get() = scale > 1.01f

    private val scaleDetector = ScaleGestureDetector(context, object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
        override fun onScale(d: ScaleGestureDetector): Boolean {
            val target = (scale * d.scaleFactor).coerceIn(1f, maxScale)
            val f = target / scale
            scale = target
            m.postScale(f, f, d.focusX, d.focusY)
            clamp()
            return true
        }
    })

    private val gestures = GestureDetector(context, object : GestureDetector.SimpleOnGestureListener() {
        override fun onScroll(e1: MotionEvent?, e2: MotionEvent, dx: Float, dy: Float): Boolean {
            if (!isZoomed) return false
            m.postTranslate(-dx, -dy)
            clamp()
            return true
        }

        override fun onDoubleTap(e: MotionEvent): Boolean {
            if (isZoomed) reset() else {
                scale = 2.5f
                m.postScale(2.5f, 2.5f, e.x, e.y)
                clamp()
            }
            return true
        }
    })

    init {
        scaleType = ScaleType.MATRIX
    }

    fun reset() {
        scale = 1f
        fit()
    }

    private fun fit() {
        val d = drawable ?: return
        if (width == 0 || d.intrinsicWidth <= 0) return
        base = width.toFloat() / d.intrinsicWidth
        m.reset()
        m.postScale(base, base)
        val h = d.intrinsicHeight * base
        if (h < height) m.postTranslate(0f, (height - h) / 2f)
        imageMatrix = m
    }

    private fun clamp() {
        val d = drawable ?: return
        val r = RectF(0f, 0f, d.intrinsicWidth.toFloat(), d.intrinsicHeight.toFloat())
        m.mapRect(r)
        var dx = 0f
        var dy = 0f
        if (r.width() <= width) dx = (width - r.width()) / 2f - r.left
        else if (r.left > 0) dx = -r.left else if (r.right < width) dx = width - r.right
        if (r.height() <= height) dy = (height - r.height()) / 2f - r.top
        else if (r.top > 0) dy = -r.top else if (r.bottom < height) dy = height - r.bottom
        m.postTranslate(dx, dy)
        imageMatrix = m
    }

    override fun setImageBitmap(bm: android.graphics.Bitmap?) {
        super.setImageBitmap(bm)
        scale = 1f
        post { fit() }
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        reset()
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        scaleDetector.onTouchEvent(event)
        gestures.onTouchEvent(event)
        // While zoomed (or pinching), keep the pager from stealing the gesture.
        parent?.requestDisallowInterceptTouchEvent(isZoomed || event.pointerCount > 1)
        return true
    }
}
