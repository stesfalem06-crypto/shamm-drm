package com.shammapps.xama.ui

import android.Manifest
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.view.View
import android.widget.TextView
import android.widget.Toast
import androidx.activity.OnBackPressedCallback
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.shammapps.xama.R
import com.shammapps.xama.storage.XamaStorage
import java.util.concurrent.Executors

/**
 * One-time "Connect to shop PC" setup for the MTP (File transfer) workflow:
 *  1. ask for every permission in one go (media, notifications, then All files access),
 *  2. write the device identity file X Seller reads over MTP,
 *  3. tell the customer: plug in, choose File transfer, tap Allow.
 * The old USB-debugging guide stays available under "Advanced".
 * Skippable; re-open from Me > Connect to shop PC.
 */
class OnboardingActivity : AppCompatActivity() {

    companion object {
        private const val PREFS = "xama_ui"
        private const val KEY_DONE = "onboarding_done_v2"

        fun isDone(context: Context): Boolean =
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_DONE, false)

        private fun markDone(context: Context) =
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY_DONE, true).apply()
    }

    private val io = Executors.newSingleThreadExecutor()
    private var askedAllFiles = false

    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) {
        writeIdentityAsync()
        if (!XamaStorage.hasAllFilesAccess()) explainAllFiles() else refreshStatus()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_onboarding)

        findViewById<View>(R.id.onbSkip).setOnClickListener { finishGuide() }
        findViewById<View>(R.id.onbDone).setOnClickListener { finishGuide() }
        findViewById<View>(R.id.onbSetup).setOnClickListener { startSetup() }
        findViewById<View>(R.id.onbAllFilesRow).setOnClickListener { explainAllFiles() }
        findViewById<View>(R.id.onbOpenAbout).setOnClickListener { openAboutPhone() }
        findViewById<View>(R.id.onbOpenDev).setOnClickListener { openDeveloperOptions() }
        val advanced = findViewById<View>(R.id.onbAdvanced)
        val advToggle = findViewById<TextView>(R.id.onbAdvancedToggle)
        advToggle.setOnClickListener {
            val show = advanced.visibility != View.VISIBLE
            advanced.visibility = if (show) View.VISIBLE else View.GONE
            advToggle.text = if (show) "Hide advanced: USB debugging" else "Advanced: USB debugging (optional)"
        }

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() = finishGuide()
        })
    }

    override fun onResume() {
        super.onResume()
        writeIdentityAsync()
        refreshStatus()
    }

    override fun onDestroy() {
        super.onDestroy()
        io.shutdown()
    }

    private fun finishGuide() {
        markDone(this)
        finish()
    }

    // ------------------------------------------------------------ permissions

    private fun runtimePermissions(): List<String> {
        val list = ArrayList<String>()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            list += Manifest.permission.READ_MEDIA_VIDEO
            list += Manifest.permission.READ_MEDIA_AUDIO
            list += Manifest.permission.POST_NOTIFICATIONS
        } else {
            list += Manifest.permission.READ_EXTERNAL_STORAGE
            if (Build.VERSION.SDK_INT <= Build.VERSION_CODES.Q) list += Manifest.permission.WRITE_EXTERNAL_STORAGE
        }
        return list
    }

    private fun granted(p: String) =
        ContextCompat.checkSelfPermission(this, p) == PackageManager.PERMISSION_GRANTED

    private fun mediaGranted(): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU)
            granted(Manifest.permission.READ_MEDIA_VIDEO) && granted(Manifest.permission.READ_MEDIA_AUDIO)
        else granted(Manifest.permission.READ_EXTERNAL_STORAGE)

    private fun notificationsGranted(): Boolean =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU || granted(Manifest.permission.POST_NOTIFICATIONS)

    private fun startSetup() {
        val missing = runtimePermissions().filter { !granted(it) }
        if (missing.isNotEmpty()) {
            permissionLauncher.launch(missing.toTypedArray())
        } else if (!XamaStorage.hasAllFilesAccess()) {
            explainAllFiles()
        } else {
            writeIdentityAsync()
            Toast.makeText(this, "All set. Plug into the shop PC, choose File transfer, tap Allow.", Toast.LENGTH_LONG).show()
        }
    }

    private fun explainAllFiles() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R || XamaStorage.hasAllFilesAccess()) {
            refreshStatus()
            return
        }
        MaterialAlertDialogBuilder(this)
            .setTitle("Allow access to all files")
            .setMessage(
                "Xama needs “All files access” so it can pick up the videos the shop PC copies " +
                    "into Download and Android/media, and (optionally) scan every folder on this phone.\n\n" +
                    "On the next screen, switch on “Allow access to manage all files” for Xama, then come back."
            )
            .setPositiveButton("Open settings") { _, _ -> openAllFilesSettings() }
            .setNegativeButton("Not now") { _, _ -> refreshStatus() }
            .show()
    }

    private fun openAllFilesSettings() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) return
        askedAllFiles = true
        try {
            startActivity(
                Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, Uri.parse("package:$packageName"))
            )
        } catch (_: Exception) {
            try {
                startActivity(Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION))
            } catch (_: Exception) {
                Toast.makeText(this, "Open Settings > Apps > Xama > Files and media manually.", Toast.LENGTH_LONG).show()
            }
        }
    }

    private fun writeIdentityAsync() {
        if (io.isShutdown) return
        io.execute {
            try { XamaStorage.writeDeviceIdentity(applicationContext) } catch (_: Exception) {}
            runOnUiThread { if (!isFinishing && !isDestroyed) refreshStatus() }
        }
    }

    // ------------------------------------------------------------ status

    private fun setStatus(id: Int, ok: Boolean, okText: String, todoText: String) {
        findViewById<TextView>(id).apply {
            text = if (ok) "✓  $okText" else "•  $todoText"
            setTextColor(ContextCompat.getColor(this@OnboardingActivity, if (ok) R.color.accent_light else R.color.text_muted))
        }
    }

    private fun refreshStatus() {
        val media = mediaGranted()
        val allFiles = XamaStorage.hasAllFilesAccess()
        val notif = notificationsGranted()
        val id = XamaStorage.identityWritten(this)
        setStatus(R.id.onbStatusMedia, media, "Videos & music access", "Videos & music access needed")
        setStatus(R.id.onbStatusAllFiles, allFiles, "All files access", "All files access needed (tap here)")
        findViewById<View>(R.id.onbAllFilesRow).visibility =
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) View.VISIBLE else View.GONE
        setStatus(R.id.onbStatusNotif, notif, "Notifications", "Notifications off (optional)")
        setStatus(R.id.onbStatusId, id, "Shop PC can recognise this phone", "Phone ID not written yet")
        val ready = media && allFiles && id
        findViewById<View>(R.id.onbReady).visibility = if (ready) View.VISIBLE else View.GONE
        findViewById<TextView>(R.id.onbSetup).text = if (ready) "Set up again" else "Set up now"
        findViewById<TextView>(R.id.onbDone).text = if (ready) "Done" else "Finish later"

        val dev = devOptionsEnabled()
        val adb = usbDebuggingEnabled()
        setStatus(R.id.onbStatusDev, dev, "Developer options are on", "Developer options are off")
        setStatus(R.id.onbStatusAdb, adb, "USB debugging is on", "USB debugging is off")
    }

    // ------------------------------------------------------------ advanced (USB debugging)

    private fun devOptionsEnabled(): Boolean = try {
        Settings.Global.getInt(contentResolver, Settings.Global.DEVELOPMENT_SETTINGS_ENABLED, 0) == 1
    } catch (_: Exception) { false }

    private fun usbDebuggingEnabled(): Boolean = try {
        Settings.Global.getInt(contentResolver, Settings.Global.ADB_ENABLED, 0) == 1
    } catch (_: Exception) { false }

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
            Toast.makeText(this, "Turn on Developer options first.", Toast.LENGTH_LONG).show()
        }
        if (!tryStart(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS, Settings.ACTION_SETTINGS)) {
            Toast.makeText(this, "Open Settings > System > Developer options manually.", Toast.LENGTH_LONG).show()
        }
    }
}
