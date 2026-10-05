package com.raidana.rdtv.rtc

/** A message pipe to the PC: either a WebRTC data channel (server mode) or a direct TCP link (LAN mode). */
interface Link {
    val isOpen: Boolean
    fun send(bytes: ByteArray): Boolean
    fun close()
}
