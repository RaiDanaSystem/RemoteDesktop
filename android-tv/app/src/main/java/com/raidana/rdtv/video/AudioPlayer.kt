package com.raidana.rdtv.video

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.os.Build
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicInteger

/** Plays the PC's system audio (16-bit PCM, ~20 ms packets) with a small prebuffer and a latency cap. */
class AudioPlayer {
    private val exec = Executors.newSingleThreadExecutor { Thread(it, "audio-play") }
    private val queued = AtomicInteger()
    private var track: AudioTrack? = null
    private var rate = 0
    private var channels = 0
    private var started = false
    private var writtenBytes = 0

    @Volatile
    var released = false
        private set

    /** Handles one serialized AudioPacket (see src/Shared/Audio/AudioProtocol.cs). */
    fun onPacket(d: ByteArray, off: Int, len: Int) {
        if (released || len < 26) return
        val bb = ByteBuffer.wrap(d, off, len).order(ByteOrder.LITTLE_ENDIAN)
        if (d[off].toInt() != 1) return // AudioMessageType.AudioData
        val sr = bb.getInt(off + 14)
        val ch = bb.getInt(off + 18)
        val n = bb.getInt(off + 22)
        if (sr < 8000 || sr > 96000 || ch !in 1..2 || n <= 0 || 26 + n > len) return

        // Keep latency bounded: drop when more than ~300 ms is waiting.
        if (queued.get() > sr * ch * 2 * 3 / 10) return
        val pcm = d.copyOfRange(off + 26, off + 26 + n)
        queued.addAndGet(n)
        exec.execute {
            try {
                if (released) return@execute
                ensure(sr, ch)
                val t = track ?: return@execute
                t.write(pcm, 0, pcm.size)
                writtenBytes += pcm.size
                if (!started && writtenBytes >= sr * ch * 2 / 12) { // ~80 ms prebuffer
                    t.play()
                    started = true
                }
            } catch (_: Exception) {
            } finally {
                queued.addAndGet(-n)
            }
        }
    }

    private fun ensure(sr: Int, ch: Int) {
        if (track != null && rate == sr && channels == ch) return
        track?.let { try { it.stop() } catch (_: Exception) {}; it.release() }
        started = false
        writtenBytes = 0
        rate = sr
        channels = ch
        val mask = if (ch == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
        val min = AudioTrack.getMinBufferSize(sr, mask, AudioFormat.ENCODING_PCM_16BIT)
        val size = maxOf(min * 2, sr * ch * 2 / 5) // at least 200 ms
        track = if (Build.VERSION.SDK_INT >= 26) {
            AudioTrack.Builder()
                .setAudioAttributes(
                    AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_MEDIA)
                        .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
                        .build()
                )
                .setAudioFormat(
                    AudioFormat.Builder()
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setSampleRate(sr)
                        .setChannelMask(mask)
                        .build()
                )
                .setBufferSizeInBytes(size)
                .setTransferMode(AudioTrack.MODE_STREAM)
                .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
                .build()
        } else {
            @Suppress("DEPRECATION")
            AudioTrack(AudioManager.STREAM_MUSIC, sr, mask, AudioFormat.ENCODING_PCM_16BIT, size, AudioTrack.MODE_STREAM)
        }
    }

    fun release() {
        released = true
        exec.execute {
            try { track?.stop() } catch (_: Exception) {}
            try { track?.release() } catch (_: Exception) {}
            track = null
        }
        exec.shutdown()
    }
}
