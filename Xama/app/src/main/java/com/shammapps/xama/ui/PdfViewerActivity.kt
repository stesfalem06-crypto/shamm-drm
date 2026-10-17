package com.shammapps.xama.ui

import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.pdf.PdfRenderer
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.ParcelFileDescriptor
import android.system.Os
import android.util.LruCache
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.recyclerview.widget.RecyclerView
import androidx.viewpager2.widget.ViewPager2
import com.shammapps.xama.R
import com.shammapps.xama.crypto.NativeCrypto
import com.shammapps.xama.crypto.ShammKeyResolver
import com.shammapps.xama.data.WatchHistory
import com.shammapps.xama.security.SecurityGuard
import java.io.File
import java.io.FileDescriptor
import java.io.FileInputStream
import java.io.FileOutputStream
import java.util.UUID
import java.util.concurrent.Executors

/**
 * In-app viewer for protected PDFs.
 *
 * Uses the existing, unchanged key path (ShammKeyResolver -> device-bound content key)
 * and the same AES-CTR native routine the video DataSource uses. The plaintext lives
 * only in an anonymous memory file (memfd, Android 11+) or, on older versions, in an
 * app-private temp file that is unlinked immediately after opening (never visible by
 * name). Screens are FLAG_SECURE; there is no share / export / print. Everything is
 * closed and the key wiped when the viewer closes.
 */
class PdfViewerActivity : AppCompatActivity() {

    companion object {
        const val EXTRA_ID = "pdf_id"
        const val EXTRA_TITLE = "pdf_title"
        const val EXTRA_IV = "pdf_iv"
        private const val CHUNK = 1 shl 20
    }

    private val io = Executors.newSingleThreadExecutor() // PdfRenderer is single-threaded
    private val main = Handler(Looper.getMainLooper())
    private var pfd: ParcelFileDescriptor? = null
    private var renderer: PdfRenderer? = null
    private var contentKey: ByteArray? = null
    private var pageCount = 0
    private var renderWidth = 1080
    private val pages = object : LruCache<Int, Bitmap>(4) {}
    @Volatile private var closed = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        SecurityGuard.enableScreenshotProtection(this)
        setContentView(R.layout.activity_pdf_viewer)
        if (SecurityGuard.isEnvironmentCompromised(this)) {
            Toast.makeText(this, "This device can't open protected documents.", Toast.LENGTH_LONG).show()
            finish()
            return
        }
        val id = intent.getStringExtra(EXTRA_ID).orEmpty()
        val iv = intent.getStringExtra(EXTRA_IV).orEmpty()
        findViewById<TextView>(R.id.pdfTitle).text = intent.getStringExtra(EXTRA_TITLE) ?: "Document"
        findViewById<View>(R.id.pdfBack).setOnClickListener { finish() }
        renderWidth = (resources.displayMetrics.widthPixels * 1.5f).toInt().coerceIn(800, 2000)
        WatchHistory.record(this, id)

        io.execute {
            val ok = try { open(id, iv) } catch (_: Throwable) { false }
            main.post {
                if (isFinishing || isDestroyed) return@post
                findViewById<ProgressBar>(R.id.pdfLoading).visibility = View.GONE
                if (!ok) {
                    Toast.makeText(this, "This document isn't licensed for this device.", Toast.LENGTH_LONG).show()
                    finish()
                } else {
                    setupPager()
                }
            }
        }
    }

    /** Decrypts into an anonymous fd and opens PdfRenderer on it. Runs on [io]. */
    private fun open(id: String, ivBase64: String): Boolean {
        val resolved = ShammKeyResolver(this).resolve(id, ivBase64) ?: return false
        contentKey = resolved.contentKey
        val fd = createPrivateFd()
        try {
            FileInputStream(resolved.videoFile).use { input ->
                FileOutputStream(fd.fileDescriptor).use { out ->
                    val buf = ByteArray(CHUNK)
                    var offset = 0L
                    while (true) {
                        val n = input.read(buf)
                        if (n <= 0) break
                        val rc = NativeCrypto.shamm_ctr_crypt(
                            resolved.contentKey, NativeCrypto.KEY_LEN,
                            resolved.iv, NativeCrypto.IV_LEN, offset, buf, n
                        )
                        if (rc != 0) throw IllegalStateException("decrypt")
                        out.write(buf, 0, n)
                        offset += n
                    }
                    out.flush()
                    java.util.Arrays.fill(buf, 0)
                }
            }
            Os.lseek(fd.fileDescriptor, 0, android.system.OsConstants.SEEK_SET)
            pfd = fd
            val r = PdfRenderer(fd)
            renderer = r
            pageCount = r.pageCount
            return pageCount > 0
        } catch (t: Throwable) {
            try { fd.close() } catch (_: Exception) {}
            pfd = null
            return false
        }
    }

    /**
     * Read/write fd with no name on disk: memfd on Android 11+; otherwise an
     * app-private file that is unlinked right away (the fd stays valid).
     */
    private fun createPrivateFd(): ParcelFileDescriptor {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            try {
                val fd: FileDescriptor = Os.memfd_create("xama-doc", 0)
                return ParcelFileDescriptor.dup(fd).also { Os.close(fd) }
            } catch (_: Throwable) {
            }
        }
        val dir = File(noBackupFilesDir, "d").apply { mkdirs() }
        dir.listFiles()?.forEach { it.delete() } // leftovers from a crash
        val f = File(dir, UUID.randomUUID().toString())
        val p = ParcelFileDescriptor.open(
            f, ParcelFileDescriptor.MODE_READ_WRITE or ParcelFileDescriptor.MODE_CREATE or ParcelFileDescriptor.MODE_TRUNCATE
        )
        f.delete()
        return p
    }

    private fun setupPager() {
        val pager = findViewById<ViewPager2>(R.id.pdfPager)
        val indicator = findViewById<TextView>(R.id.pdfPage)
        pager.offscreenPageLimit = 1
        pager.adapter = PageAdapter()
        indicator.text = "1 / $pageCount"
        pager.registerOnPageChangeCallback(object : ViewPager2.OnPageChangeCallback() {
            override fun onPageSelected(position: Int) {
                indicator.text = "${position + 1} / $pageCount"
            }
        })
    }

    private fun renderPage(index: Int, done: (Bitmap?) -> Unit) {
        pages.get(index)?.let { done(it); return }
        io.execute {
            val r = renderer
            val bmp = if (r == null || closed) null else try {
                r.openPage(index).use { page ->
                    val w = renderWidth
                    val h = (page.height.toFloat() * w / page.width).toInt().coerceAtLeast(1)
                    Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888).also {
                        it.eraseColor(Color.WHITE)
                        page.render(it, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
                    }
                }
            } catch (_: Throwable) { null }
            main.post {
                if (closed) return@post
                if (bmp != null) pages.put(index, bmp)
                done(bmp)
            }
        }
    }

    private inner class PageAdapter : RecyclerView.Adapter<PageAdapter.Holder>() {
        inner class Holder(val image: ZoomImageView) : RecyclerView.ViewHolder(image)

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder {
            val v = ZoomImageView(parent.context).apply {
                layoutParams = FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
                setBackgroundColor(0xFF1A1A1A.toInt())
            }
            return Holder(v)
        }

        override fun onBindViewHolder(holder: Holder, position: Int) {
            holder.image.setImageDrawable(null)
            holder.image.tag = position
            renderPage(position) { bmp ->
                if (holder.image.tag == position && bmp != null) holder.image.setImageBitmap(bmp)
            }
        }

        override fun getItemCount() = pageCount
    }

    override fun onDestroy() {
        super.onDestroy()
        closed = true
        main.removeCallbacksAndMessages(null)
        io.execute {
            try { renderer?.close() } catch (_: Exception) {}
            renderer = null
            try { pfd?.close() } catch (_: Exception) {}
            pfd = null
            contentKey?.let { try { NativeCrypto.shamm_wipe(it, NativeCrypto.KEY_LEN) } catch (_: Throwable) {} }
            contentKey = null
        }
        io.shutdown()
        pages.evictAll()
    }
}
