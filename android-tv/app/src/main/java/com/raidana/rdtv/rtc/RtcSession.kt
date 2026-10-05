package com.raidana.rdtv.rtc

import android.content.Context
import com.raidana.rdtv.net.Signaling
import org.webrtc.DataChannel
import org.webrtc.IceCandidate
import org.webrtc.MediaConstraints
import org.webrtc.PeerConnection
import org.webrtc.PeerConnectionFactory
import org.webrtc.SdpObserver
import org.webrtc.SessionDescription
import java.nio.ByteBuffer
import java.util.UUID
import java.util.concurrent.ConcurrentLinkedQueue

/**
 * WebRTC offerer that talks to the Windows CustomerAgent through a single reliable "data" channel.
 * Signaling goes through [Signaling] (SignalR relay on the server).
 */
class RtcSession(
    private val context: Context,
    private val sessionId: String,
    private val signaling: Signaling,
    private val listener: Listener
) : Signaling.Listener, Link {

    interface Listener {
        fun onChannelOpen()
        fun onChannelClosed(reason: String)
        fun onMessage(data: ByteArray)
        fun onLog(message: String)
    }

    companion object {
        @Volatile
        private var factory: PeerConnectionFactory? = null

        @Synchronized
        private fun factory(ctx: Context): PeerConnectionFactory {
            factory?.let { return it }
            PeerConnectionFactory.initialize(
                PeerConnectionFactory.InitializationOptions.builder(ctx.applicationContext).createInitializationOptions()
            )
            val f = PeerConnectionFactory.builder().createPeerConnectionFactory()
            factory = f
            return f
        }
    }

    private var pc: PeerConnection? = null
    private var channel: DataChannel? = null
    private val peerId = UUID.randomUUID().toString().replace("-", "")

    @Volatile
    private var remoteSet = false

    @Volatile
    private var offerSent = false

    @Volatile
    private var closed = false
    private val pendingRemote = ConcurrentLinkedQueue<IceCandidate>()
    private val pendingLocal = ConcurrentLinkedQueue<IceCandidate>()

    override val isOpen: Boolean get() = channel?.state() == DataChannel.State.OPEN

    fun start() {
        signaling.listener = this
        val f = factory(context)
        val cfg = PeerConnection.RTCConfiguration(
            listOf(PeerConnection.IceServer.builder("stun:stun.l.google.com:19302").createIceServer())
        ).apply {
            sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
            continualGatheringPolicy = PeerConnection.ContinualGatheringPolicy.GATHER_CONTINUALLY
            iceCandidatePoolSize = 0
            disableIPv6OnWifi = true
        }

        val conn = f.createPeerConnection(cfg, object : PeerConnection.Observer {
            override fun onSignalingChange(s: PeerConnection.SignalingState?) {}
            override fun onIceConnectionChange(s: PeerConnection.IceConnectionState?) {
                listener.onLog("ICE: $s")
            }

            override fun onConnectionChange(s: PeerConnection.PeerConnectionState?) {
                listener.onLog("Peer: $s")
                if (s == PeerConnection.PeerConnectionState.FAILED || s == PeerConnection.PeerConnectionState.CLOSED) {
                    notifyClosed("Connection $s")
                }
            }

            override fun onIceConnectionReceivingChange(b: Boolean) {}
            override fun onIceGatheringChange(s: PeerConnection.IceGatheringState?) {
                listener.onLog("ICE gathering: $s")
            }
            override fun onIceCandidate(c: IceCandidate) {
                listener.onLog("local cand: " + c.sdp.substringAfter("candidate:").take(70))
                if (offerSent) sendCandidate(c) else pendingLocal.add(c)
            }

            override fun onIceCandidatesRemoved(c: Array<out IceCandidate>?) {}
            override fun onAddStream(s: org.webrtc.MediaStream?) {}
            override fun onRemoveStream(s: org.webrtc.MediaStream?) {}
            override fun onDataChannel(dc: DataChannel?) {}
            override fun onRenegotiationNeeded() {}
            override fun onAddTrack(r: org.webrtc.RtpReceiver?, s: Array<out org.webrtc.MediaStream>?) {}
        }) ?: throw IllegalStateException("Could not create PeerConnection")
        pc = conn

        val dc = conn.createDataChannel("data", DataChannel.Init())
        channel = dc
        dc.registerObserver(object : DataChannel.Observer {
            override fun onBufferedAmountChange(previous: Long) {}
            override fun onStateChange() {
                listener.onLog("DataChannel: ${dc.state()}")
                when (dc.state()) {
                    DataChannel.State.OPEN -> listener.onChannelOpen()
                    DataChannel.State.CLOSED -> notifyClosed("Data channel closed")
                    else -> {}
                }
            }

            override fun onMessage(buffer: DataChannel.Buffer) {
                val bb = buffer.data
                val bytes = ByteArray(bb.remaining())
                bb.get(bytes)
                listener.onMessage(bytes)
            }
        })

        conn.createOffer(object : SimpleSdp() {
            override fun onCreateSuccess(desc: SessionDescription) {
                conn.setLocalDescription(object : SimpleSdp() {
                    override fun onSetSuccess() {
                        try {
                            signaling.requestOffer(sessionId)
                            signaling.submitOffer(sessionId, peerId, desc.description)
                            offerSent = true
                            listener.onLog("Offer sent")
                            // The PC side creates its peer asynchronously after the offer arrives;
                            // give it a moment before the first candidates so none are dropped.
                            Thread {
                                Thread.sleep(350)
                                flushLocalCandidates()
                            }.start()
                        } catch (e: Exception) {
                            notifyClosed("Signaling failed: ${e.message}")
                        }
                    }

                    override fun onSetFailure(error: String?) {
                        notifyClosed("setLocalDescription failed: $error")
                    }
                }, desc)
            }

            override fun onCreateFailure(error: String?) {
                notifyClosed("createOffer failed: $error")
            }
        }, MediaConstraints())
    }

    private fun flushLocalCandidates() {
        while (true) {
            val c = pendingLocal.poll() ?: break
            sendCandidate(c)
        }
    }

    private fun sendCandidate(c: IceCandidate) {
        try {
            signaling.submitIceCandidate(sessionId, peerId, c.sdp, c.sdpMid ?: "0", c.sdpMLineIndex)
        } catch (_: Exception) {
        }
    }

    override fun send(bytes: ByteArray): Boolean {
        val dc = channel ?: return false
        if (dc.state() != DataChannel.State.OPEN) return false
        return dc.send(DataChannel.Buffer(ByteBuffer.wrap(bytes), true))
    }

    // ------------------------------------------------------------ Signaling.Listener

    override fun onAnswer(peerId: String, sdp: String) {
        if (peerId != this.peerId) return
        val conn = pc ?: return
        conn.setRemoteDescription(object : SimpleSdp() {
            override fun onSetSuccess() {
                remoteSet = true
                listener.onLog("Answer applied")
                while (true) {
                    val c = pendingRemote.poll() ?: break
                    conn.addIceCandidate(c)
                }
            }

            override fun onSetFailure(error: String?) {
                notifyClosed("setRemoteDescription failed: $error")
            }
        }, SessionDescription(SessionDescription.Type.ANSWER, sdp))
    }

    override fun onIceCandidate(peerId: String, candidate: String, sdpMid: String, sdpMLineIndex: Int) {
        if (peerId != this.peerId) return
        listener.onLog("remote cand: " + candidate.substringAfter("candidate:").take(70))
        val c = IceCandidate(sdpMid.ifEmpty { "0" }, sdpMLineIndex, candidate)
        if (remoteSet) pc?.addIceCandidate(c) else pendingRemote.add(c)
    }

    override fun onPeerClosed(peerId: String) {
        if (peerId == this.peerId) notifyClosed("The PC closed the connection")
    }

    override fun onError(message: String) {
        listener.onLog("Signaling error: $message")
    }

    override fun onClosed(message: String?) {
        listener.onLog("Signaling closed: ${message ?: ""}")
    }

    private fun notifyClosed(reason: String) {
        if (closed) return
        listener.onChannelClosed(reason)
    }

    override fun close() {
        if (closed) return
        closed = true
        try {
            signaling.closePeer(sessionId, peerId)
        } catch (_: Exception) {
        }
        try {
            channel?.unregisterObserver()
            channel?.close()
            channel?.dispose()
        } catch (_: Exception) {
        }
        try {
            pc?.close()
            pc?.dispose()
        } catch (_: Exception) {
        }
        channel = null
        pc = null
    }

    private open class SimpleSdp : SdpObserver {
        override fun onCreateSuccess(desc: SessionDescription) {}
        override fun onSetSuccess() {}
        override fun onCreateFailure(error: String?) {}
        override fun onSetFailure(error: String?) {}
    }
}
