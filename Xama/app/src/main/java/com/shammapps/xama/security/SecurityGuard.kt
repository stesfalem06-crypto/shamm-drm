package com.shammapps.xama.security

import android.app.Activity
import android.view.WindowManager
import com.scottyab.rootbeer.RootBeer

/**
 * Central place for every anti-piracy control. Called from both
 * PlayerActivity and ReelsActivity before any protected video starts.
 *
 * Being upfront about limits: none of this makes piracy impossible. A
 * determined attacker with a rooted device and tools like Frida can still
 * defeat client-side checks, and a second camera pointed at the screen
 * defeats ALL software protection, on any DRM system anywhere. What this
 * layer does is close the easy, casual paths (screenshot, screen-record
 * apps, an emulator running unmodified) so that only sophisticated,
 * deliberate attackers get further - which is the realistic bar for a
 * commercial content-protection system.
 */
object SecurityGuard {

    /** Blocks screenshots and screen recording at the OS level for this screen. */
    fun enableScreenshotProtection(activity: Activity) {
        activity.window.setFlags(
            WindowManager.LayoutParams.FLAG_SECURE,
            WindowManager.LayoutParams.FLAG_SECURE
        )
    }

    /**
     * Returns true if the environment looks compromised: rooted device,
     * running inside an emulator, or common root-cloaking/hooking
     * frameworks detected. Playback should refuse to start when this is true.
     */
    fun isEnvironmentCompromised(activity: Activity): Boolean {
        val rootBeer = RootBeer(activity)
        return rootBeer.isRooted || rootBeer.isRootedWithoutBusyBoxCheck || isLikelyEmulator()
    }

    private fun isLikelyEmulator(): Boolean {
        val fp = android.os.Build.FINGERPRINT
        val model = android.os.Build.MODEL
        val manufacturer = android.os.Build.MANUFACTURER
        return fp.startsWith("generic") || fp.startsWith("unknown") ||
                model.contains("google_sdk") || model.contains("Emulator") ||
                model.contains("Android SDK built for x86") ||
                manufacturer.contains("Genymotion") ||
                (android.os.Build.BRAND.startsWith("generic") && android.os.Build.DEVICE.startsWith("generic")) ||
                "google_sdk" == android.os.Build.PRODUCT
    }
}
