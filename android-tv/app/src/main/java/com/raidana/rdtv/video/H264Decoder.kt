package com.raidana.rdtv.video

import android.media.MediaCodec
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.view.Surface
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.atomic.AtomicLong

/** Reassembles fragmented "RDH2" Annex-B access units sent by the Windows host. */
class H264Assembler {
    class AccessUnit(val annexB: ByteArray, val width: Int, val height: Int, val keyframe: Boolean)

    private var seq = -1L
    private var parts: Array<ByteArray?> = emptyArray()
    private var received = 0
    private var width = 0
    private var height = 0
    private var key = false

    fun add(d: ByteArray, off: Int, len: Int): AccessUnit? {
        if (len < 24 || d[off] != 'R'.code.toByte() || d[off + 1] != 'D'.code.toByte() ||
            d[off + 2] != 'H'.code.toByte() || d[off + 3] != '2'.code.toByte() || d[off + 4].toInt() != 1
        ) return null
        val bb = ByteBuffer.wrap(d, off, len).order(ByteOrder.LITTLE_ENDIAN)
        val w = bb.getShort(off + 8).toInt() and 0xFFFF
        val h = bb.getShort(off + 10).toInt() and 0xFFFF
        val s = bb.getInt(off + 12).toLong() and 0xFFFFFFFFL
        val idx = bb.getShort(off + 16).toInt() and 0xFFFF
        val cnt = bb.getShort(off + 18).toInt() and 0xFFFF
        val plen = bb.getInt(off + 20)
        val isKey = (d[off + 5].toInt() and 1) != 0
        if (cnt == 0 || idx >= cnt || plen < 0 || 24 + plen > len) return null

        if (cnt == 1) {
            return AccessUnit(d.copyOfRange(off + 24, off + 24 + plen), w, h, isKey)
        }
        if (s != seq || cnt != parts.size) {
            seq = s
            parts = arrayOfNulls(cnt)
            received = 0
            width = w
            height = h
            key = isKey
        }
        if (parts[idx] == null) {
            parts[idx] = d.copyOfRange(off + 24, off + 24 + plen)
            received++
        }
        if (received < parts.size) return null

        var total = 0
        for (p in parts) total += p!!.size
        val out = ByteArray(total)
        var o = 0
        for (p in parts) {
            System.arraycopy(p!!, 0, out, o, p.size)
            o += p.size
        }
        parts = emptyArray()
        received = 0
        seq = -1
        return AccessUnit(out, width, height, key)
    }
}

/**
 * Low-latency hardware H.264 decoder rendering straight to a [Surface].
 * Frames are decoded and released to the display as soon as they leave the codec.
 */
class H264Decoder(
    private val surface: Surface,
    private val onSizeKnown: (Int, Int) -> Unit,
    private val onUnsupported: (Int, Int) -> Unit = { _, _ -> }
) {
    private var codec: MediaCodec? = null
    private var width = 0
    private var height = 0
    private var outThread: Thread? = null

    @Volatile
    private var running = false
    private var waitingForKeyframe = true

    val rendered = AtomicLong()
    val fed = AtomicLong()
    val dropped = AtomicLong()
    val errors = AtomicLong()

    @Volatile var codecName = ""
    @Volatile var lastError = ""

    // Decoder attempts, tried in order when output stalls: default decoder with low-latency hints,
    // default decoder with a plain config, then every other AVC decoder on the device.
    private class Attempt(val name: String?, val plain: Boolean)

    private val attempts: List<Attempt> by lazy {
        val names = try {
            MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
                .filter { !it.isEncoder && it.supportedTypes.any { t -> t.equals(MediaFormat.MIMETYPE_VIDEO_AVC, true) } }
                .map { it.name }
                .filter { !it.contains("secure", true) }
        } catch (_: Exception) {
            emptyList()
        }
        val list = ArrayList<Attempt>()
        list.add(Attempt(names.firstOrNull(), false))
        list.add(Attempt(names.firstOrNull(), true))
        for (n in names.drop(1)) list.add(Attempt(n, true))
        list
    }
    private var attemptIndex = 0
    val plainConfig: Boolean get() = attempts[attemptIndex].plain

    /** Moves to the next decoder configuration. Returns false when every option has been tried. */
    fun escalate(): Boolean {
        if (attemptIndex + 1 >= attempts.size) return false
        attemptIndex++
        restartRequested = true
        lastError = "→ ${describe()}"
        return true
    }

    fun describe(): String {
        val a = attempts[attemptIndex]
        return (a.name ?: "default") + if (a.plain) " (plain)" else ""
    }

    private fun describeError(e: Exception): String {
        if (e is MediaCodec.CodecException) {
            val code = if (Build.VERSION.SDK_INT >= 23) e.errorCode.toString() else "?"
            return "CodecException code=$code recoverable=${e.isRecoverable} transient=${e.isTransient} ${e.diagnosticInfo}"
        }
        return "${e.javaClass.simpleName} ${e.message ?: ""}"
    }

    private var errorStreak = 0

    /** Two codec errors in a row without any picture: don't wait for the watchdog, switch decoder now. */
    private fun noteError() {
        errorStreak++
        if (errorStreak >= 2 && escalate()) errorStreak = 0
    }

    /** Set by the output thread after a non-recoverable codec error; the next keyframe restarts the codec. */
    @Volatile private var restartRequested = false

    /** Feed one access unit. Returns false when a keyframe is needed to continue. */
    @Synchronized
    fun feed(au: H264Assembler.AccessUnit): Boolean {
        if (restartRequested) {
            restartRequested = false
            stopCodec()
        }
        if (codec == null || au.width != width || au.height != height) {
            if (!au.keyframe) return false
            if (!startCodec(au)) return false
        }
        val c = codec ?: return false
        if (waitingForKeyframe && !au.keyframe) return false
        try {
            val idx = c.dequeueInputBuffer(8_000)
            if (idx < 0) {
                waitingForKeyframe = true
                dropped.incrementAndGet()
                return false
            }
            val buf = c.getInputBuffer(idx) ?: return false
            buf.clear()
            if (au.annexB.size > buf.capacity()) {
                c.queueInputBuffer(idx, 0, 0, 0, 0)
                waitingForKeyframe = true
                return false
            }
            buf.put(au.annexB)
            val flags = if (au.keyframe) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
            c.queueInputBuffer(idx, 0, au.annexB.size, System.nanoTime() / 1000, flags)
            if (au.keyframe) waitingForKeyframe = false
            fed.incrementAndGet()
            return true
        } catch (e: Exception) {
            lastError = "feed: ${describeError(e)}"
            errors.incrementAndGet()
            noteError()
            stopCodec()
            return false
        }
    }

    private fun startCodec(au: H264Assembler.AccessUnit): Boolean {
        stopCodec()
        return try {
            val fmt = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, au.width, au.height)
            val (sps, pps) = findParamSets(au.annexB)
            if (sps != null) fmt.setByteBuffer("csd-0", ByteBuffer.wrap(sps))
            if (pps != null) fmt.setByteBuffer("csd-1", ByteBuffer.wrap(pps))
            fmt.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 4 * 1024 * 1024)
            if (!attempts[attemptIndex].plain) {
                if (Build.VERSION.SDK_INT >= 30) fmt.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
                if (Build.VERSION.SDK_INT >= 23) {
                    fmt.setInteger(MediaFormat.KEY_PRIORITY, 0)
                    fmt.setInteger(MediaFormat.KEY_OPERATING_RATE, Short.MAX_VALUE.toInt())
                }
            }
            val chosen = attempts[attemptIndex].name
            val c = if (chosen != null) MediaCodec.createByCodecName(chosen)
            else MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            c.configure(fmt, surface, null, 0)
            c.start()
            codecName = try { c.name } catch (_: Exception) { "?" }
            codec = c
            width = au.width
            height = au.height
            waitingForKeyframe = false
            running = true
            outThread = Thread({ drain(c) }, "h264-out").also { it.start() }
            onSizeKnown(au.width, au.height)
            true
        } catch (e: Exception) {
            lastError = "start: ${describeError(e)}"
            errors.incrementAndGet()
            codec = null
            onUnsupported(au.width, au.height)
            false
        }
    }

    private fun drain(c: MediaCodec) {
        val info = MediaCodec.BufferInfo()
        while (running) {
            try {
                val i = c.dequeueOutputBuffer(info, 10_000)
                if (i >= 0) {
                    c.releaseOutputBuffer(i, true)
                    rendered.incrementAndGet()
                    errorStreak = 0
                }
            } catch (e: MediaCodec.CodecException) {
                errors.incrementAndGet()
                lastError = "codec: ${describeError(e)}"
                noteError()
                if (!e.isTransient) { restartRequested = true; break }
            } catch (e: Exception) {
                // stopCodec() racing with this thread is expected; anything else needs a restart.
                if (running) {
                    errors.incrementAndGet()
                    lastError = "out: ${e.javaClass.simpleName} ${e.message ?: ""}"
                    restartRequested = true
                }
                break
            }
        }
    }

    @Synchronized
    fun stopCodec() {
        running = false
        val c = codec
        codec = null
        width = 0
        height = 0
        waitingForKeyframe = true
        try { outThread?.join(300) } catch (_: Exception) {}
        outThread = null
        if (c != null) {
            try { c.stop() } catch (_: Exception) {}
            try { c.release() } catch (_: Exception) {}
        }
    }

    fun release() = stopCodec()

    private fun findParamSets(d: ByteArray): Pair<ByteArray?, ByteArray?> {
        var sps: ByteArray? = null
        var pps: ByteArray? = null
        var i = 0
        val starts = ArrayList<Pair<Int, Int>>() // (nal start incl. start code, header offset)
        while (i + 3 < d.size) {
            if (d[i].toInt() == 0 && d[i + 1].toInt() == 0) {
                if (d[i + 2].toInt() == 1) {
                    starts.add(i to i + 3); i += 3; continue
                }
                if (d[i + 2].toInt() == 0 && d[i + 3].toInt() == 1) {
                    starts.add(i to i + 4); i += 4; continue
                }
            }
            i++
        }
        for (k in starts.indices) {
            val (s, h) = starts[k]
            if (h >= d.size) continue
            val end = if (k + 1 < starts.size) starts[k + 1].first else d.size
            val type = d[h].toInt() and 0x1F
            if (type == 7 && sps == null) sps = d.copyOfRange(s, end)
            if (type == 8 && pps == null) pps = d.copyOfRange(s, end)
        }
        return sps to pps
    }
}
