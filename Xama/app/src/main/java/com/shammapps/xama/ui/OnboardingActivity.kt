package com.shammapps.xama.ui

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.provider.Settings
import android.view.View
import android.widget.TextView
import android.widget.Toast
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.shammapps.xama.R

/**
 * First-run guide that prepares the phone for the shop PC (X Seller):
 * enable Developer options, turn on USB debugging, then just tap "Allow"
 * when the phone is plugged in. Skippable; re-open it from Me > USB setup.
 *
 * Nothing here changes system settings itself - it only explains the steps
 * and deep-links into the relevant Settings screens.
 */
class OnboardingActivity : AppCompatActivity() {

    companion object {
        private const val PREFS = "xama_ui"
        private const val KEY_DONE = "onboarding_done"

        fun isDone(context: Context): Boolean =
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_DONE, false)

        private fun markDone(context: Context) =
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY_DONE, true).apply()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_onboarding)

        findViewById<View>(R.id.onbSkip).setOnClickListener { finishGuide() }
        findViewById<View>(R.id.onbDone).setOnClickListener { finishGuide() }
        findViewById<View>(R.id.onbOpenAbout).setOnClickListener { openAboutPhone() }
        findViewById<View>(R.id.onbOpenDev).setOnClickListener { openDeveloperOptions() }

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() = finishGuide()
        })
    }

    override fun onResume() {
        super.onResume()
        refreshStatus()
    }

    private fun finishGuide() {
        markDone(this)
        finish()
    }

    private fun devOptionsEnabled(): Boolean = try {
        Settings.Global.getInt(contentResolver, Settings.Global.DEVELOPMENT_SETTINGS_ENABLED, 0) == 1
    } catch (_: Exception) { false }

    private fun usbDebuggingEnabled(): Boolean = try {
        Settings.Global.getInt(contentResolver, Settings.Global.ADB_ENABLED, 0) == 1
    } catch (_: Exception) { false }

    private fun refreshStatus() {
        val dev = devOptionsEnabled()
        val adb = usbDebuggingEnabled()
        val ok = ContextCompat.getColor(this, R.color.accent_light)
        val pending = ContextCompat.getColor(this, R.color.text_muted)
        findViewById<TextView>(R.id.onbStatusDev).apply {
            text = if (dev) "✓ Developer options are on" else "• Developer options are off"
            setTextColor(if (dev) ok else pending)
        }
        findViewById<TextView>(R.id.onbStatusAdb).apply {
            text = if (adb) "✓ USB debugging is on" else "• USB debugging is off"
            setTextColor(if (adb) ok else pending)
        }
        findViewById<TextView>(R.id.onbDone).text = if (dev && adb) "Done" else "Finish later"
        findViewById<View>(R.id.onbReady).visibility = if (dev && adb) View.VISIBLE else View.GONE
    }

    private fun tryStart(vararg actions: String): Boolean {
        for (action in actions) {
            try {
                startActivity(Intent(action).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                return true
            } catch (_: ActivityNotFoundException) {
            } catch (_: SecurityException) {
            }
        }
        return false
    }

    private fun openAboutPhone() {
        if (!tryStart(Settings.ACTION_DEVICE_INFO_SETTINGS, Settings.ACTION_SETTINGS)) {
            Toast.makeText(this, "Open Settings > About phone manually.", Toast.LENGTH_LONG).show()
        } else {
            Toast.makeText(this, "Tap “Build number” 7 times", Toast.LENGTH_LONG).show()
        }
    }

    private fun openDeveloperOptions() {
        if (!devOptionsEnabled()) {
            Toast.makeText(this, "Turn on Developer options first (step 1).", Toast.LENGTH_LONG).show()
        }
        if (!tryStart(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS, Settings.ACTION_SETTINGS)) {
            Toast.makeText(this, "Open Settings > System > Developer options manually.", Toast.LENGTH_LONG).show()
        }
    }
}
