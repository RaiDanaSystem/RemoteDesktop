package com.raidana.rdtv.net

import com.raidana.rdtv.rtc.Link
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.DataInputStream
import java.io.EOFException
import java.io.IOException
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Server-less LAN link (see src/Shared/Transport/Direct on the Windows side).
 *
 * Framing: [uint32 LE length][byte tag][body]; tags 1 Hello, 2 Accept, 3 Reject, 0x10 Data (TransportEnvelope).
 */
class DirectLink private constructor(
    private val socket: Socket,
    val hostName: String,
    val viewOnly: Boolean,
    private val listener: Listener
) : Link {

    interface Listener {
        fun onMessage(data: ByteArray)
        fun onClosed(reason: String)
    }

    class Result(val link: DirectLink?, val error: String?)

    private val out: OutputStream = socket.getOutputStream()
    private val sender = Executors.newSingleThreadExecutor { Thread(it, "direct-send") }
    private val closed = AtomicBoolean(false)

    @Volatile
    private var open = true
    override val isOpen: Boolean get() = open && !closed.get()

    fun startReading() {
        Thread({
            try {
                val input = DataInputStream(BufferedInputStream(socket.getInputStream(), 256 * 1024))
                val lenBuf = ByteArray(4)
                while (!closed.get()) {
                    input.readFully(lenBuf)
                    val len = ByteBuffer.wrap(lenBuf).order(ByteOrder.LITTLE_ENDIAN).int
                    if (len < 1 || len > MAX_FRAME) throw IOException("bad frame length $len")
                    val tag = input.readUnsignedByte()
                    val body = ByteArray(len - 1)
                    input.readFully(body)
                    if (tag == TAG_DATA) listener.onMessage(body)
                }
            } catch (e: EOFException) {
                finish("The PC closed the connection")
            } catch (e: Exception) {
                finish(if (closed.get()) "" else (e.message ?: "Connection lost"))
            }
        }, "direct-recv").start()
    }

    override fun send(bytes: ByteArray): Boolean {
        if (!isOpen) return false
        val frame = frame(TAG_DATA, bytes)
        sender.execute {
            try {
                synchronized(out) { out.write(frame); out.flush() }
            } catch (e: Exception) {
                finish(e.message ?: "Send failed")
            }
        }
        return true
    }

    private fun finish(reason: String) {
        open = false
        if (closed.compareAndSet(false, true)) {
            try { socket.close() } catch (_: Exception) {}
            sender.shutdown()
            if (reason.isNotEmpty()) listener.onClosed(reason)
        }
    }

    override fun close() {
        if (closed.compareAndSet(false, true)) {
            open = false
            try { socket.close() } catch (_: Exception) {}
            sender.shutdownNow()
        }
    }

    companion object {
        const val PROTOCOL = "RDLAN1"
        const val DEFAULT_PORT = 45870
        const val DISCOVERY_PORT = 45871
        private const val TAG_HELLO = 1
        private const val TAG_ACCEPT = 2
        private const val TAG_REJECT = 3
        private const val TAG_DATA = 0x10
        private const val MAX_FRAME = 64 * 1024 * 1024

        fun frame(tag: Int, body: ByteArray): ByteArray {
            val bb = ByteBuffer.allocate(5 + body.size).order(ByteOrder.LITTLE_ENDIAN)
            bb.putInt(1 + body.size)
            bb.put(tag.toByte())
            bb.put(body)
            return bb.array()
        }

        /** Blocking: connects, says hello and waits for the PC user to accept. Call off the main thread. */
        fun connect(
            host: String, port: Int, viewerId: String, viewerName: String,
            listener: Listener, onWaiting: () -> Unit = {}
        ): Result {
            val socket = Socket()
            try {
                socket.tcpNoDelay = true
                socket.receiveBufferSize = 4 * 1024 * 1024
                socket.connect(InetSocketAddress(host, port), 6000)
                val hello = JSONObject()
                    .put("protocol", PROTOCOL)
                    .put("viewerId", viewerId)
                    .put("viewerName", viewerName)
                    .put("platform", "Android")
                socket.getOutputStream().write(frame(TAG_HELLO, hello.toString().toByteArray()))
                socket.getOutputStream().flush()
                onWaiting()

                socket.soTimeout = 75_000
                val input = DataInputStream(socket.getInputStream())
                val lenBuf = ByteArray(4)
                input.readFully(lenBuf)
                val len = ByteBuffer.wrap(lenBuf).order(ByteOrder.LITTLE_ENDIAN).int
                if (len < 1 || len > 1_000_000) throw IOException("bad reply")
                val tag = input.readUnsignedByte()
                val body = ByteArray(len - 1)
                input.readFully(body)
                socket.soTimeout = 0

                return when (tag) {
                    TAG_ACCEPT -> {
                        val j = JSONObject(String(body))
                        val link = DirectLink(socket, j.optString("hostName", host), j.optBoolean("viewOnly", false), listener)
                        Result(link, null)
                    }
                    TAG_REJECT -> {
                        socket.close()
                        val r = JSONObject(String(body)).optString("reason", "Rejected")
                        Result(null, if (r == "busy") "The PC is already in a session." else r)
                    }
                    else -> { socket.close(); Result(null, "Unexpected reply from the PC.") }
                }
            } catch (e: java.net.SocketTimeoutException) {
                try { socket.close() } catch (_: Exception) {}
                return Result(null, "The PC did not answer in time.")
            } catch (e: Exception) {
                try { socket.close() } catch (_: Exception) {}
                return Result(null, "Cannot reach $host:$port (${e.message ?: e.javaClass.simpleName})")
            }
        }
    }
}
