package com.shammapps.xama.share

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Color
import android.media.MediaScannerConnection
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiManager
import android.net.wifi.WifiNetworkSpecifier
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.shammapps.xama.R
import com.shammapps.xama.data.LibraryCache
import com.shammapps.xama.data.LocalVideo
import com.shammapps.xama.storage.XamaStorage
import org.json.JSONObject
import java.net.Inet4Address
import java.net.NetworkInterface
import java.text.DateFormat
import java.util.Date
import javax.net.SocketFactory

/** Offline Xama-to-Xama sharing: send (hotspot + QR), receive (scan QR / enter code), partners. */
class ShareActivity : AppCompatActivity() {

    companion object {
        private const val EXTRA_MODE = "mode"
        private const val EXTRA_IDS = "ids"

        fun send(ctx: Context, items: List<LocalVideo>) =
            ctx.startActivity(Intent(ctx, ShareActivity::class.java).putExtra(EXTRA_MODE, "send")
                .putStringArrayListExtra(EXTRA_IDS, ArrayList(items.map { it.id })))

        fun receive(ctx: Context) = ctx.startActivity(Intent(ctx, ShareActivity::class.java).putExtra(EXTRA_MODE, "receive"))
        fun partners(ctx: Context) = ctx.startActivity(Intent(ctx, ShareActivity::class.java).putExtra(EXTRA_MODE, "partners"))
    }

    private val main = Handler(Looper.getMainLooper())
    private lateinit var root: LinearLayout
    private lateinit var status: TextView
    private lateinit var progress: ProgressBar
    private lateinit var progressText: TextView
    private var server: ShareServer? = null
    private var client: ShareClient? = null
    private var reservation: WifiManager.LocalOnlyHotspotReservation? = null
    private var netCallback: ConnectivityManager.NetworkCallback? = null
    private val myId by lazy { XamaStorage.deviceIdHex(this) }
    private val myName get() = "${Build.MANUFACTURER} ${Build.MODEL}".trim()
    private var pendingAfterPerms: (() -> Unit)? = null

    private val perms = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { res ->
        val cb = pendingAfterPerms; pendingAfterPerms = null
        if (res.values.all { it }) cb?.invoke() else setStatus("Permission needed for offline Wi-Fi sharing.")
    }

    private val scan = registerForActivityResult(ScanContract()) { r ->
        val txt = r.contents ?: return@registerForActivityResult
        runCatching { JSONObject(txt) }.getOrNull()?.let { connectFromQr(it) } ?: setStatus("That QR code isn't a Xama share code.")
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        val scroll = ScrollView(this).apply { setBackgroundColor(ContextCompat.getColor(this@ShareActivity, R.color.bg_dark)) }
        root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20), dp(28), dp(20), dp(28)) }
        scroll.addView(root)
        setContentView(scroll)
        when (intent.getStringExtra(EXTRA_MODE)) {
            "send" -> buildSend()
            "receive" -> buildReceive()
            else -> buildPartners()
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        server?.stopServer()
        client?.stopped = true
        runCatching { reservation?.close() }
        netCallback?.let { cb ->
            val cm = getSystemService(ConnectivityManager::class.java)
            runCatching { cm.bindProcessToNetwork(null) }
            runCatching { cm.unregisterNetworkCallback(cb) }
        }
    }

    // ------------------------------------------------------------------ UI helpers
    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()
    private fun text(t: String, size: Float = 15f, bold: Boolean = false, color: Int = Color.WHITE) = TextView(this).apply {
        text = t; textSize = size; setTextColor(color); if (bold) setTypeface(typeface, android.graphics.Typeface.BOLD)
        setPadding(0, dp(6), 0, dp(6)); root.addView(this)
    }
    private fun button(t: String, onClick: () -> Unit) = Button(this).apply {
        text = t; isAllCaps = false; setOnClickListener { onClick() }; root.addView(this)
    }
    private fun progressViews() {
        progress = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply { max = 1000; visibility = View.GONE }
        root.addView(progress, LinearLayout.LayoutParams(-1, dp(10)).apply { topMargin = dp(12) })
        progressText = text("", 13f, color = 0xFFB0B0B0.toInt())
        status = text("", 14f, color = 0xFFE0E0E0.toInt())
    }
    private fun setStatus(t: String) { main.post { status.text = t } }

    private val listener = object : ShareListener {
        private var lastUi = 0L
        private var startMs = 0L
        override fun onStatus(text: String) = setStatus(text)
        override fun onProgress(done: Long, total: Long, file: String) {
            val now = System.currentTimeMillis()
            if (startMs == 0L) startMs = now
            if (now - lastUi < 150 && done < total) return
            lastUi = now
            val speed = done / ((now - startMs).coerceAtLeast(1) / 1000.0) // approx (resumes inflate it)
            main.post {
                progress.visibility = View.VISIBLE
                progress.progress = if (total > 0) (done * 1000 / total).toInt() else 0
                progressText.text = "$file\n${mb(done)} / ${mb(total)} MB · ${"%.1f".format(speed / 1048576)} MB/s"
            }
        }
        override fun onFinished(ok: Boolean, text: String) {
            main.post {
                status.text = text
                if (ok) progress.progress = 1000
                client?.received?.takeIf { it.isNotEmpty() }?.let { files ->
                    MediaScannerConnection.scanFile(this@ShareActivity, files.map { it.absolutePath }.toTypedArray(), null, null)
                }
                Toast.makeText(this@ShareActivity, text, Toast.LENGTH_LONG).show()
            }
        }
    }
    private fun mb(b: Long) = "%.1f".format(b / 1048576.0)

    private fun wifiPerms(): Array<String> =
        if (Build.VERSION.SDK_INT >= 33) arrayOf(Manifest.permission.NEARBY_WIFI_DEVICES)
        else arrayOf(Manifest.permission.ACCESS_FINE_LOCATION)

    private fun withPerms(block: () -> Unit) {
        val missing = wifiPerms().filter { ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED }
        if (missing.isEmpty()) block() else { pendingAfterPerms = block; perms.launch(missing.toTypedArray()) }
    }

    // ------------------------------------------------------------------ SEND
    private lateinit var qr: ImageView
    private lateinit var codeText: TextView
    private val code = (100000 + java.security.SecureRandom().nextInt(900000)).toString()

    private fun buildSend() {
        val ids = intent.getStringArrayListExtra(EXTRA_IDS) ?: arrayListOf()
        val items = LibraryCache.byIds(ids)
        val list = ShareProto.buildList(this, items)
        text("Share nearby (offline)", 22f, true)
        if (list.isEmpty()) {
            text("Nothing to send. This file can't be shared (it may be missing).")
            return
        }
        val prot = list.any { it.protectedPkg }
        text("${items.joinToString { it.title }}\n${list.size} file(s), ${mb(list.sumOf { it.file.length() })} MB", 14f, color = 0xFFB0B0B0.toInt())
        if (prot) {
            val fam = Family.load(this, myId)
            text(if (fam?.active == true && fam.others(myId).isNotEmpty())
                "Protected: can only go to your family phones: " + fam.others(myId).joinToString { it.model }
            else "Protected movies can only be shared inside a family group registered at the shop. This phone has no family group.",
                13f, color = 0xFFFFC107.toInt())
        }
        qr = ImageView(this)
        root.addView(qr, LinearLayout.LayoutParams(dp(260), dp(260)).apply { gravity = Gravity.CENTER_HORIZONTAL; topMargin = dp(12) })
        codeText = text("", 14f)
        progressViews()
        button("Use same Wi-Fi instead of hotspot") { startSameWifi(list) }
        withPerms { startHotspot(list) }
    }

    private fun startServer(list: List<ShareProto.OutFile>) {
        if (server != null) return
        server = ShareServer(this, list, code, myId, myName, listener).also { it.start() }
    }

    @SuppressLint("MissingPermission")
    private fun startHotspot(list: List<ShareProto.OutFile>) {
        if (Build.VERSION.SDK_INT < 26) { startSameWifi(list); return }
        setStatus("Starting offline hotspot…")
        val wm = applicationContext.getSystemService(WifiManager::class.java)
        try {
            wm.startLocalOnlyHotspot(object : WifiManager.LocalOnlyHotspotCallback() {
                override fun onStarted(res: WifiManager.LocalOnlyHotspotReservation) {
                    reservation = res
                    var ssid = ""; var pass = ""; var wpa3 = false
                    if (Build.VERSION.SDK_INT >= 30) {
                        val c = res.softApConfiguration
                        ssid = c.ssid ?: ""; pass = c.passphrase ?: ""
                        wpa3 = c.securityType == android.net.wifi.SoftApConfiguration.SECURITY_TYPE_WPA3_SAE
                    } else {
                        @Suppress("DEPRECATION") val c = res.wifiConfiguration
                        @Suppress("DEPRECATION") run { ssid = c?.SSID ?: ""; pass = c?.preSharedKey ?: "" }
                    }
                    ssid = ssid.trim('"')
                    startServer(list)
                    main.postDelayed({ showCode(ssid, pass, wpa3, localIp(preferHotspot = true)) }, 800)
                }
                override fun onFailed(reason: Int) {
                    setStatus("Hotspot couldn't start (turn off your normal hotspot / turn on Wi-Fi). Trying same Wi-Fi…")
                    main.post { startSameWifi(list) }
                }
            }, main)
        } catch (e: Exception) {
            setStatus("Hotspot unavailable: ${e.message}. Using same Wi-Fi.")
            startSameWifi(list)
        }
    }

    private fun startSameWifi(list: List<ShareProto.OutFile>) {
        runCatching { reservation?.close() }; reservation = null
        startServer(list)
        val ip = localIp(preferHotspot = false)
        if (ip == null) { setStatus("Connect both phones to the same Wi-Fi (no internet needed), then try again."); return }
        showCode("", "", false, ip)
    }

    private fun showCode(ssid: String, pass: String, wpa3: Boolean, ip: String?) {
        val payload = JSONObject().put("x", "xama-share").put("v", 1).put("s", ssid).put("p", pass)
            .put("t", if (wpa3) "wpa3" else "wpa2").put("h", ip ?: "").put("o", ShareProto.PORT).put("c", code).put("d", myId)
        qr.setImageBitmap(qrBitmap(payload.toString(), dp(260)))
        codeText.text = buildString {
            append("On the other phone: Me → Receive from nearby Xama → Scan QR.\n")
            if (ssid.isNotEmpty()) append("Hotspot: $ssid\nPassword: $pass\n") else append("Same Wi-Fi mode\n")
            append("Address: ${ip ?: "?"}\nPairing code: $code")
        }
        setStatus("Waiting for the other phone…")
    }

    private fun localIp(preferHotspot: Boolean): String? {
        val addrs = runCatching {
            NetworkInterface.getNetworkInterfaces().toList().filter { it.isUp && !it.isLoopback }
                .flatMap { ni -> ni.inetAddresses.toList().filterIsInstance<Inet4Address>().map { ni.name to it.hostAddress } }
        }.getOrDefault(emptyList())
        val hot = addrs.firstOrNull { (n, _) -> n.startsWith("ap") || n.startsWith("swlan") || n.startsWith("softap") || n.startsWith("wlan1") }
        val wlan = addrs.firstOrNull { (n, _) -> n.startsWith("wlan") }
        return (if (preferHotspot) hot ?: wlan else wlan ?: hot)?.second ?: addrs.firstOrNull()?.second
    }

    private fun qrBitmap(s: String, size: Int): Bitmap {
        val m = QRCodeWriter().encode(s, BarcodeFormat.QR_CODE, size, size, mapOf(EncodeHintType.MARGIN to 1))
        val bmp = Bitmap.createBitmap(size, size, Bitmap.Config.RGB_565)
        for (x in 0 until size) for (y in 0 until size) bmp.setPixel(x, y, if (m[x, y]) Color.BLACK else Color.WHITE)
        return bmp
    }

    // ------------------------------------------------------------------ RECEIVE
    private fun buildReceive() {
        text("Receive from nearby Xama", 22f, true)
        text("Works without internet. On the sending phone choose “Share nearby” on a video or song, then scan its QR code here.\n" +
            "Protected movies arrive only if both phones are in the same family group registered at the shop.", 13f, color = 0xFFB0B0B0.toInt())
        button("Scan QR code") {
            scan.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE).setPrompt("Scan the QR on the sending phone")
                .setBeepEnabled(false).setOrientationLocked(false))
        }
        button("Enter hotspot name & code") { manualDialog() }
        progressViews()
    }

    private fun manualDialog() {
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20), dp(8), dp(20), 0) }
        fun field(h: String) = EditText(this).apply { hint = h; box.addView(this) }
        val ssid = field("Hotspot name (empty = same Wi-Fi)")
        val pass = field("Hotspot password")
        val host = field("Address (shown on the sender)")
        val code = field("Pairing code")
        MaterialAlertDialogBuilder(this).setTitle("Connect manually").setView(box)
            .setPositiveButton("Connect") { _, _ ->
                connectFromQr(JSONObject().put("s", ssid.text.toString().trim()).put("p", pass.text.toString())
                    .put("h", host.text.toString().trim()).put("o", ShareProto.PORT).put("c", code.text.toString().trim()))
            }.setNegativeButton("Cancel", null).show()
    }

    private fun connectFromQr(o: JSONObject) {
        val ssid = o.optString("s"); val pass = o.optString("p"); val host = o.optString("h")
        val port = o.optInt("o", ShareProto.PORT); val code = o.optString("c")
        val senderId = o.optString("d").lowercase()
        if (senderId.length == 64 && !Pairing.hasRoomFor(this, senderId) && Family.load(this, myId)?.isMember(senderId) != true) {
            setStatus("This phone already has ${Pairing.MAX} share partners. Protected family shares still work; plain files need a free partner slot.")
        }
        if (ssid.isEmpty()) { startClient(SocketFactory.getDefault(), host, port, code); return }
        withPerms { joinHotspot(ssid, pass, o.optString("t") == "wpa3") { factory, gateway ->
            startClient(factory, host.ifBlank { gateway ?: "" }, port, code)
        } }
    }

    @SuppressLint("MissingPermission")
    private fun joinHotspot(ssid: String, pass: String, wpa3: Boolean, then: (SocketFactory, String?) -> Unit) {
        setStatus("Joining $ssid…")
        val cm = getSystemService(ConnectivityManager::class.java)
        if (Build.VERSION.SDK_INT >= 29) {
            val spec = WifiNetworkSpecifier.Builder().setSsid(ssid).apply {
                if (wpa3) setWpa3Passphrase(pass) else setWpa2Passphrase(pass)
            }.build()
            val req = NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
                .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).setNetworkSpecifier(spec).build()
            val cb = object : ConnectivityManager.NetworkCallback() {
                private var started = false
                override fun onAvailable(network: Network) {
                    if (started) return; started = true
                    val gw = runCatching {
                        val lp = cm.getLinkProperties(network)
                        (if (Build.VERSION.SDK_INT >= 30) lp?.dhcpServerAddress?.hostAddress else null)
                            ?: lp?.routes?.firstOrNull { it.isDefaultRoute }?.gateway?.hostAddress
                    }.getOrNull()
                    main.post { then(network.socketFactory, gw) }
                }
                override fun onUnavailable() = setStatus("Couldn't join $ssid. Check the name/password and stay close.")
                override fun onLost(network: Network) = setStatus("Wi-Fi link lost – the transfer will resume when it's back.")
            }
            netCallback = cb
            cm.requestNetwork(req, cb)
        } else {
            @Suppress("DEPRECATION") run {
                val wm = applicationContext.getSystemService(WifiManager::class.java)
                val conf = android.net.wifi.WifiConfiguration().apply { SSID = "\"$ssid\""; preSharedKey = "\"$pass\"" }
                val id = wm.addNetwork(conf)
                wm.disconnect(); wm.enableNetwork(id, true); wm.reconnect()
                main.postDelayed({
                    val gw = runCatching { val g = wm.dhcpInfo.gateway; "%d.%d.%d.%d".format(g and 0xff, g shr 8 and 0xff, g shr 16 and 0xff, g shr 24 and 0xff) }.getOrNull()
                    then(SocketFactory.getDefault(), gw)
                }, 6000)
            }
        }
    }

    private fun startClient(factory: SocketFactory, host: String, port: Int, code: String) {
        if (host.isBlank()) { setStatus("Missing address of the sending phone."); return }
        client?.stopped = true
        setStatus("Connecting…")
        client = ShareClient(this, factory, host, port, code, myId, myName, listener).also { it.start() }
    }

    // ------------------------------------------------------------------ PARTNERS
    private fun buildPartners() {
        root.removeAllViews()
        text("Share partners & family", 22f, true)
        text("This phone's ID: ${myId.take(12)}…", 12f, color = 0xFF909090.toInt())
        val fam = Family.load(this, myId)
        text("Family group (protected movies)", 16f, true)
        if (fam == null || !fam.active) {
            text(if (fam?.status == "removed") "This phone was removed from its family group." else
                "No family group. Ask your shop to register up to 3 family phones.", 13f, color = 0xFFB0B0B0.toInt())
        } else {
            text("${fam.shopName} · ${fam.members.size}/${fam.phonesMax} phones" + if (fam.source == "weekly-pass") " · from weekly pass" else "", 13f)
            fam.members.forEach { m -> text("• ${m.model}" + if (m.deviceId == myId) " (this phone)" else "", 14f) }
        }
        text("Fixed partners (normal videos & music) – max ${Pairing.MAX}", 16f, true)
        val peers = Pairing.peers(this)
        if (peers.isEmpty()) text("None yet. The first phone you share with by QR becomes a partner.", 13f, color = 0xFFB0B0B0.toInt())
        val df = DateFormat.getDateInstance(DateFormat.MEDIUM)
        peers.forEach { p ->
            text("• ${p.name}  (paired ${df.format(Date(p.pairedAt))})", 14f)
            if (Pairing.canUnpair(p)) button("Unpair ${p.name}") {
                MaterialAlertDialogBuilder(this).setTitle("Unpair ${p.name}?")
                    .setMessage("You can pair a new phone afterwards; it will also stay fixed for 30 days.")
                    .setPositiveButton("Unpair") { _, _ -> Pairing.remove(this, p.deviceId); buildPartners() }
                    .setNegativeButton("Cancel", null).show()
            } else text("   Can be unpaired from ${df.format(Date(Pairing.unpairAvailableAt(p)))}", 12f, color = 0xFF909090.toInt())
        }
    }
}
