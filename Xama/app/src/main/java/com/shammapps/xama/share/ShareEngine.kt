package com.shammapps.xama.share

import android.content.Context
import com.shammapps.xama.crypto.ShammKeyResolver
import com.shammapps.xama.data.LocalVideo
import com.shammapps.xama.storage.XamaStorage
import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.File
import java.io.InputStream
import java.io.OutputStream
import java.io.RandomAccessFile
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException
import javax.net.SocketFactory

/**
 * Xama-to-Xama offline transfer over a local socket (local-only hotspot or same Wi-Fi).
 * Line protocol + raw bytes, resumable per file via byte offsets (receiver keeps *.part files).
 *
 *  R -> S  HELLO {"d":deviceId,"n":name,"c":code}
 *  S -> R  OK {"d":..,"n":..,"files":[{"n":name,"s":size,"g":"plain|protected"}]}   |  NO <reason>
 *  R -> S  GET <index> <offset>          S -> R  DATA <len>\n<bytes>
 *  R -> S  BYE
 */
object ShareProto {
    const val PORT = 47321

    fun readLine(inp: InputStream, max: Int = 64 * 1024): String {
        val sb = StringBuilder()
        while (true) {
            val b = inp.read()
            if (b < 0) throw java.io.EOFException()
            if (b == '\n'.code) return sb.toString()
            sb.append(b.toChar())
            if (sb.length > max) throw java.io.IOException("line too long")
        }
    }

    fun writeLine(out: OutputStream, s: String) {
        out.write((s + "\n").toByteArray(Charsets.UTF_8)); out.flush()
    }

    fun safeName(n: String): String = File(n).name.replace(Regex("[\\\\/:*?\"<>|\\u0000]"), "_").ifBlank { "file" }

    val PROTECTED_EXTS = setOf("shammvid", "shammkey", "shammthumb", "shammmeta")
    private val ORDER = listOf("shammvid", "shammkey", "shammthumb", "shammmeta")

    data class OutFile(val file: File, val name: String, val protectedPkg: Boolean, val videoId: String?)

    /** Builds the send list. Protected items become their full package, meta LAST. */
    fun buildList(ctx: Context, items: List<LocalVideo>): List<OutFile> {
        val out = ArrayList<OutFile>()
        val dirs = ShammKeyResolver.incomingDirs(ctx)
        for (v in items) {
            if (v.isEncrypted) {
                val files = ORDER.mapNotNull { ext -> dirs.map { File(it, "${v.id}.$ext") }.firstOrNull { it.isFile } }
                if (files.none { it.extension == "shammvid" } || files.none { it.extension == "shammmeta" } ||
                    files.none { it.extension == "shammkey" }) continue
                files.forEach { out += OutFile(it, it.name, true, v.id) }
            } else {
                val f = File(v.filePath)
                if (v.filePath.isBlank() || !f.isFile) continue
                if (f.extension.lowercase() in PROTECTED_EXTS) continue
                out += OutFile(f, safeName(f.name), false, null)
            }
        }
        return out
    }
}

interface ShareListener {
    fun onStatus(text: String)
    fun onProgress(done: Long, total: Long, file: String)
    fun onFinished(ok: Boolean, text: String)
}

/** Sender side. Accepts reconnects (resume) until the receiver says BYE or [stop] is called. */
class ShareServer(
    private val ctx: Context,
    private val all: List<ShareProto.OutFile>,
    private val code: String,
    private val myId: String,
    private val myName: String,
    private val listener: ShareListener,
) : Thread("xama-share-server") {
    @Volatile private var stopped = false
    private var server: ServerSocket? = null

    fun stopServer() { stopped = true; runCatching { server?.close() } }

    override fun run() {
        try {
            val ss = ServerSocket()
            ss.reuseAddress = true
            ss.bind(InetSocketAddress(ShareProto.PORT))
            server = ss
            while (!stopped) {
                val s = try { ss.accept() } catch (e: Exception) { if (stopped) break else continue }
                try {
                    if (serve(s)) { listener.onFinished(true, "Sent"); break }
                } catch (e: Exception) {
                    if (!stopped) listener.onStatus("Connection dropped – waiting for the other phone to resume…")
                } finally { runCatching { s.close() } }
            }
        } catch (e: Exception) {
            if (!stopped) listener.onFinished(false, "Couldn't start sharing: ${e.message}")
        } finally { runCatching { server?.close() } }
    }

    /** true when the receiver finished (BYE). */
    private fun serve(s: Socket): Boolean {
        s.soTimeout = 60_000
        s.tcpNoDelay = true
        val inp = BufferedInputStream(s.getInputStream())
        val out = s.getOutputStream().buffered(256 * 1024)
        val hello = ShareProto.readLine(inp)
        if (!hello.startsWith("HELLO ")) return false
        val h = JSONObject(hello.removePrefix("HELLO "))
        val peerId = h.optString("d").lowercase()
        val peerName = h.optString("n", "Phone")
        if (peerId.length != 64) { ShareProto.writeLine(out, "NO bad device"); return false }

        // Plain files: fixed partners (max 2, QR code on first share). Protected: family members only.
        val family = Family.load(ctx, myId)
        val plain = all.filter { !it.protectedPkg }
        var prot = all.filter { it.protectedPkg }
        val notes = ArrayList<String>()
        if (prot.isNotEmpty()) {
            if (family?.isMember(peerId) != true || !family.isMember(myId)) {
                notes += "protected items skipped (that phone isn't in this phone's family group)"
                prot = emptyList()
            } else {
                // Only packages whose key actually includes the receiver (v2 SHK2 recipients).
                val okIds = prot.mapNotNull { it.videoId }.toSet().filter { id ->
                    val key = prot.firstOrNull { it.videoId == id && it.file.extension == "shammkey" }?.file
                    val rec = key?.let { runCatching { ShammKeyResolver.recipients(it.readBytes()) }.getOrNull() }
                    rec?.any { it.equals(peerId, true) } == true
                }.toSet()
                val skipped = prot.mapNotNull { it.videoId }.toSet().size - okIds.size
                if (skipped > 0) notes += "$skipped protected item(s) skipped (not licensed for that phone – ask the shop to resend for the family)"
                prot = prot.filter { it.videoId in okIds }
            }
        }
        var plainOk = plain
        if (plain.isNotEmpty()) {
            val paired = Pairing.isPaired(ctx, peerId)
            val familyPeer = family?.isMember(peerId) == true
            when {
                paired || familyPeer -> Unit
                h.optString("c") != code -> { plainOk = emptyList(); notes += "wrong pairing code" }
                !Pairing.add(ctx, peerId, peerName) -> {
                    plainOk = emptyList(); notes += "this phone already has ${Pairing.MAX} share partners"
                }
            }
        }
        val files = prot + plainOk
        if (files.isEmpty()) {
            ShareProto.writeLine(out, "NO " + (notes.joinToString("; ").ifBlank { "nothing to send" }))
            listener.onFinished(false, "Not sent: " + notes.joinToString("; "))
            return true
        }
        val arr = JSONArray()
        files.forEach { arr.put(JSONObject().put("n", it.name).put("s", it.file.length()).put("g", if (it.protectedPkg) "protected" else "plain")) }
        ShareProto.writeLine(out, "OK " + JSONObject().put("d", myId).put("n", myName).put("files", arr)
            .put("note", notes.joinToString("; ")).toString())
        listener.onStatus("Connected to $peerName")
        val total = files.sumOf { it.file.length() }
        val buf = ByteArray(128 * 1024)
        while (true) {
            val line = ShareProto.readLine(inp)
            if (line == "BYE") return true
            val parts = line.split(' ')
            if (parts.size != 3 || parts[0] != "GET") return false
            val idx = parts[1].toInt(); val off = parts[2].toLong()
            val f = files.getOrNull(idx) ?: return false
            val len = (f.file.length() - off).coerceAtLeast(0)
            ShareProto.writeLine(out, "DATA $len")
            val before = files.take(idx).sumOf { it.file.length() }
            RandomAccessFile(f.file, "r").use { raf ->
                raf.seek(off)
                var left = len; var sent = off
                while (left > 0) {
                    val n = raf.read(buf, 0, minOf(buf.size.toLong(), left).toInt())
                    if (n < 0) break
                    out.write(buf, 0, n)
                    left -= n; sent += n
                    listener.onProgress(before + sent, total, f.name)
                }
            }
            out.flush()
        }
    }
}

/** Receiver side. Reconnects and resumes from *.part sizes after a drop. */
class ShareClient(
    private val ctx: Context,
    private val factory: SocketFactory,
    private val host: String,
    private val port: Int,
    private val code: String,
    private val myId: String,
    private val myName: String,
    private val listener: ShareListener,
) : Thread("xama-share-client") {
    @Volatile var stopped = false
    val received = ArrayList<File>()

    override fun run() {
        var attempts = 0
        while (!stopped && attempts < 40) {
            try {
                val r = session()
                if (r != null) { listener.onFinished(r.first, r.second); return }
            } catch (e: Exception) {
                attempts++
                if (stopped) return
                listener.onStatus("Connection dropped – resuming (${attempts})…")
                sleep(2000)
            }
        }
        if (!stopped) listener.onFinished(false, "Couldn't reach the other phone")
    }

    private fun destFor(name: String, protectedPkg: Boolean): File {
        val dir = if (protectedPkg) File(ctx.getExternalFilesDir(null), "incoming")
        else File(XamaStorage.mediaDir(ctx), "Received")
        dir.mkdirs()
        return File(dir, ShareProto.safeName(name))
    }

    /** null = retry, else (ok, message). */
    private fun session(): Pair<Boolean, String>? {
        val s = factory.createSocket()
        s.use { sock ->
            sock.connect(InetSocketAddress(host, port), 8000)
            sock.soTimeout = 60_000
            sock.tcpNoDelay = true
            val inp = BufferedInputStream(sock.getInputStream(), 256 * 1024)
            val out = sock.getOutputStream()
            ShareProto.writeLine(out, "HELLO " + JSONObject().put("d", myId).put("n", myName).put("c", code))
            val resp = ShareProto.readLine(inp)
            if (resp.startsWith("NO")) return false to ("Refused: " + resp.removePrefix("NO").trim())
            if (!resp.startsWith("OK ")) return null
            val o = JSONObject(resp.removePrefix("OK "))
            val senderId = o.optString("d").lowercase()
            val senderName = o.optString("n", "Phone")
            val arr = o.getJSONArray("files")
            val files = (0 until arr.length()).map { arr.getJSONObject(it) }
            val anyPlain = files.any { it.optString("g") != "protected" }
            if (anyPlain) {
                val fam = Family.load(ctx, myId)
                if (fam?.isMember(senderId) != true && !Pairing.add(ctx, senderId, senderName)) {
                    ShareProto.writeLine(out, "BYE")
                    return false to "This phone already has ${Pairing.MAX} share partners. Unpair one first (after 30 days)."
                }
            }
            val total = files.sumOf { it.optLong("s") }
            var doneBefore = 0L
            val buf = ByteArray(128 * 1024)
            for ((i, f) in files.withIndex()) {
                val size = f.optLong("s"); val prot = f.optString("g") == "protected"
                val name = f.optString("n")
                val target = destFor(name, prot)
                if (target.isFile && target.length() == size) { doneBefore += size; continue }
                val part = File(target.path + ".part")
                if (part.length() > size) part.delete()
                val off = part.length()
                ShareProto.writeLine(out, "GET $i $off")
                val hdr = ShareProto.readLine(inp)
                if (!hdr.startsWith("DATA ")) return null
                var left = hdr.removePrefix("DATA ").trim().toLong()
                java.io.FileOutputStream(part, true).use { fo ->
                    var got = off
                    while (left > 0) {
                        val n = try { inp.read(buf, 0, minOf(buf.size.toLong(), left).toInt()) } catch (e: SocketTimeoutException) { throw e }
                        if (n < 0) throw java.io.EOFException()
                        fo.write(buf, 0, n); left -= n; got += n
                        listener.onProgress(doneBefore + got, total, name)
                    }
                }
                if (part.length() != size) return null
                if (target.exists()) target.delete()
                if (!part.renameTo(target)) { part.copyTo(target, true); part.delete() }
                received += target
                doneBefore += size
            }
            ShareProto.writeLine(out, "BYE")
            val note = o.optString("note")
            return true to ("Received ${files.size} file(s) from $senderName" + if (note.isNotBlank()) " · $note" else "")
        }
    }
}
