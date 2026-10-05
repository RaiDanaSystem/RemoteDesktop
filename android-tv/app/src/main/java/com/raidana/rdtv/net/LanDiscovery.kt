package com.raidana.rdtv.net

import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.util.concurrent.ConcurrentHashMap

/** Finds PCs that run the Windows app on the local network (UDP broadcast beacons). */
class LanDiscovery(private val selfId: String) {

    class Peer(val id: String, val name: String, val platform: String, val address: String, val port: Int, val seen: Long)

    private val peers = ConcurrentHashMap<String, Peer>()

    @Volatile
    private var running = false
    private var socket: DatagramSocket? = null
    var onChanged: (() -> Unit)? = null

    fun start() {
        if (running) return
        running = true
        try {
            val s = DatagramSocket(null)
            s.reuseAddress = true
            s.broadcast = true
            s.bind(InetSocketAddress(DirectLink.DISCOVERY_PORT))
            socket = s
        } catch (e: Exception) {
            running = false
            return
        }
        Thread({ listen() }, "lan-listen").start()
        Thread({
            while (running) {
                probe()
                try { Thread.sleep(3000) } catch (_: InterruptedException) { break }
            }
        }, "lan-probe").start()
    }

    fun stop() {
        running = false
        try { socket?.close() } catch (_: Exception) {}
        socket = null
    }

    fun peers(): List<Peer> {
        val now = System.currentTimeMillis()
        return peers.values.filter { now - it.seen < 7000 }.sortedBy { it.name.lowercase() }
    }

    fun probe() {
        val msg = JSONObject()
            .put("app", DirectLink.PROTOCOL).put("id", selfId).put("name", "Android")
            .put("platform", "Android").put("port", 0).put("sharing", false).put("probe", true)
            .toString().toByteArray()
        for (target in broadcastTargets()) {
            try {
                socket?.send(DatagramPacket(msg, msg.size, target, DirectLink.DISCOVERY_PORT))
            } catch (_: Exception) {
            }
        }
    }

    private fun broadcastTargets(): List<InetAddress> {
        val list = LinkedHashSet<InetAddress>()
        try { list.add(InetAddress.getByName("255.255.255.255")) } catch (_: Exception) {}
        try {
            for (ni in NetworkInterface.getNetworkInterfaces()) {
                if (!ni.isUp || ni.isLoopback) continue
                for (ia in ni.interfaceAddresses) ia.broadcast?.let { list.add(it) }
            }
        } catch (_: Exception) {
        }
        return list.toList()
    }

    private fun listen() {
        val buf = ByteArray(2048)
        while (running) {
            try {
                val p = DatagramPacket(buf, buf.size)
                socket?.receive(p) ?: break
                val j = JSONObject(String(p.data, 0, p.length))
                if (j.optString("app") != DirectLink.PROTOCOL) continue
                val id = j.optString("id")
                if (id.isEmpty() || id == selfId || j.optBoolean("probe", false)) continue
                if (!j.optBoolean("sharing", true)) { peers.remove(id); onChanged?.invoke(); continue }
                val isNew = !peers.containsKey(id)
                peers[id] = Peer(
                    id, j.optString("name", id), j.optString("platform", "Windows"),
                    p.address.hostAddress ?: continue, j.optInt("port", DirectLink.DEFAULT_PORT), System.currentTimeMillis()
                )
                if (isNew) onChanged?.invoke()
            } catch (e: Exception) {
                if (!running) break
            }
        }
    }
}
