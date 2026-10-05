package com.raidana.rdtv.net

import com.google.gson.JsonObject
import com.microsoft.signalr.HubConnection
import com.microsoft.signalr.HubConnectionBuilder
import com.microsoft.signalr.HubConnectionState
import com.microsoft.signalr.TransportEnum
import io.reactivex.rxjava3.core.Single

/** SignalR client for the server's /hubs/webrtc signaling relay (offerer side). */
class Signaling(private val baseUrl: String, private val token: String) {

    interface Listener {
        fun onAnswer(peerId: String, sdp: String)
        fun onIceCandidate(peerId: String, candidate: String, sdpMid: String, sdpMLineIndex: Int)
        fun onPeerClosed(peerId: String)
        fun onError(message: String)
        fun onClosed(message: String?)
    }

    var listener: Listener? = null
    private var hub: HubConnection? = null

    val isConnected: Boolean get() = hub?.connectionState == HubConnectionState.CONNECTED

    fun connect() {
        val h = HubConnectionBuilder.create("$baseUrl/hubs/webrtc")
            .withAccessTokenProvider(Single.defer { Single.just(token) })
            .withTransport(TransportEnum.WEBSOCKETS)
            .build()

        h.on("AnswerReceived", { d: JsonObject ->
            listener?.onAnswer(d.str("peerId"), d.str("sdpAnswer"))
        }, JsonObject::class.java)

        h.on("IceCandidateReceived", { d: JsonObject ->
            listener?.onIceCandidate(
                d.str("peerId"), d.str("candidate"), d.str("sdpMid"),
                if (d.has("sdpMLineIndex") && !d.get("sdpMLineIndex").isJsonNull) d.get("sdpMLineIndex").asInt else 0
            )
        }, JsonObject::class.java)

        h.on("PeerClosed", { d: JsonObject ->
            listener?.onPeerClosed(d.str("peerId"))
        }, JsonObject::class.java)

        h.on("Error", { d: JsonObject ->
            listener?.onError(d.str("message").ifEmpty { "Signaling error" })
        }, JsonObject::class.java)

        h.onClosed { e -> listener?.onClosed(e?.message) }

        hub = h
        h.start().blockingAwait()
    }

    fun requestOffer(sessionId: String) {
        hub?.send("RequestOffer", sessionId)
    }

    fun submitOffer(sessionId: String, peerId: String, sdp: String) {
        hub?.send("SubmitOffer", sessionId, peerId, sdp)
    }

    fun submitIceCandidate(sessionId: String, peerId: String, candidate: String, sdpMid: String, sdpMLineIndex: Int) {
        hub?.send("SubmitIceCandidate", sessionId, peerId, candidate, sdpMid, sdpMLineIndex)
    }

    fun closePeer(sessionId: String, peerId: String) {
        try {
            if (isConnected) hub?.send("CloseConnection", sessionId, peerId)
        } catch (_: Exception) {
        }
    }

    fun close() {
        try {
            hub?.stop()?.blockingAwait()
        } catch (_: Exception) {
        }
        hub = null
    }

    private fun JsonObject.str(name: String): String =
        if (has(name) && !get(name).isJsonNull) get(name).asString else ""
}
