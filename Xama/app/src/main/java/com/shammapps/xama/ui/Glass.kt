package com.shammapps.xama.ui

import android.app.Activity
import android.graphics.RenderEffect
import android.graphics.Shader
import android.os.Build
import android.view.View
import android.view.WindowManager

object Glass {
    fun applyWindow(activity: Activity) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            try {
                activity.window.addFlags(WindowManager.LayoutParams.FLAG_BLUR_BEHIND)
                activity.window.attributes.blurBehindRadius = 28
            } catch (_: Exception) { }
        }
    }

    fun frost(view: View, radius: Float = 22f) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            try {
                view.setRenderEffect(
                    RenderEffect.createBlurEffect(radius, radius, Shader.TileMode.CLAMP)
                )
            } catch (_: Exception) { }
        }
    }
}
