package com.shammapps.xama.ui

import android.content.ActivityNotFoundException
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.view.View
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import com.shammapps.xama.BuildConfig
import com.shammapps.xama.R

/** About Xama: app name, version and developer credit with a tappable phone number. */
class AboutActivity : AppCompatActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_about)

        findViewById<View>(R.id.aboutBack).setOnClickListener { finish() }
        findViewById<TextView>(R.id.aboutVersion).text = "Version ${BuildConfig.VERSION_NAME}"

        val phone = getString(R.string.about_phone)
        findViewById<View>(R.id.aboutPhoneRow).setOnClickListener {
            try {
                startActivity(Intent(Intent.ACTION_DIAL, Uri.parse("tel:$phone")))
            } catch (_: ActivityNotFoundException) {
                Toast.makeText(this, "No phone app found", Toast.LENGTH_SHORT).show()
            }
        }
    }
}
