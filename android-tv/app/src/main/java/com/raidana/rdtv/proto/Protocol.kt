package com.raidana.rdtv.proto

import org.json.JSONObject
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID

/**
 * Wire format shared with the Windows agents (see src/Shared/Transport).
 *
 * Envelope : [int32 LE headerLen][JSON header][payload]
 * ScreenFrame payload : [int32 LE headerLen][JSON header][frame bytes]
 */
object Proto {
    // TransportMessageType names (serialized as strings by the Windows side)
    const val T_PING = "Ping"
    const val T_PONG = "Pong"
    const val T_SCREEN = "ScreenFrame"
    const val T_INPUT = "Input"
    const val T_CLIPBOARD = "Clipboard"
    const val T_CONTROL = "Control"

    // ControlAction (numeric)
    const val CTL_DISCONNECT = 0
    const val CTL_SHOW_REMOTE_CURSOR = 6
    const val CTL_STREAM_SETTINGS = 7

    // InputEventType (numeric)
    const val IN_MOVE = 1
    const val IN_BUTTON = 2
    const val IN_WHEEL = 3
    const val IN_KEY = 4
    const val IN_CHAR = 5
    const val IN_DBLCLICK = 6

    // MouseButton / KeyAction (numeric)
    const val BTN_LEFT = 0
    const val BTN_RIGHT = 1
    const val BTN_MIDDLE = 2
    const val ACT_DOWN = 0
    const val ACT_UP = 1

    // FrameFormat (numeric)
    const val FMT_JPEG = 1
    const val FMT_H264 = 3
    const val FMT_TILES = 4

    fun envelope(type: String, payload: ByteArray): ByteArray {
        val header = JSONObject()
            .put("version", 1)
            .put("messageType", type)
            .put("messageId", UUID.randomUUID().toString().replace("-", ""))
            .put("timestamp", System.currentTimeMillis())
            .put("payloadLength", payload.size)
        val hb = header.toString().toByteArray(Charsets.UTF_8)
        val out = ByteBuffer.allocate(4 + hb.size + payload.size).order(ByteOrder.LITTLE_ENDIAN)
        out.putInt(hb.size)
        out.put(hb)
        out.put(payload)
        return out.array()
    }

    class Envelope(val type: String, val data: ByteArray, val offset: Int) {
        val length: Int get() = data.size - offset
    }

    fun parseEnvelope(data: ByteArray): Envelope? {
        if (data.size < 4) return null
        val bb = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN)
        val hl = bb.getInt(0)
        if (hl <= 0 || hl > 64 * 1024 || data.size < 4 + hl) return null
        return try {
            val json = JSONObject(String(data, 4, hl, Charsets.UTF_8))
            Envelope(json.optString("messageType"), data, 4 + hl)
        } catch (e: Exception) {
            null
        }
    }

    class ScreenFrame(
        val width: Int,
        val height: Int,
        val format: Int,
        val nativeWidth: Int,
        val nativeHeight: Int,
        val data: ByteArray,
        val offset: Int
    ) {
        val length: Int get() = data.size - offset
    }

    fun parseScreenFrame(env: Envelope): ScreenFrame? {
        val d = env.data
        val base = env.offset
        if (d.size - base < 4) return null
        val hl = ByteBuffer.wrap(d).order(ByteOrder.LITTLE_ENDIAN).getInt(base)
        if (hl <= 0 || hl > 64 * 1024 || d.size - base < 4 + hl) return null
        return try {
            val json = JSONObject(String(d, base + 4, hl, Charsets.UTF_8))
            ScreenFrame(
                width = json.optInt("width"),
                height = json.optInt("height"),
                format = json.optInt("format"),
                nativeWidth = json.optInt("nativeWidth"),
                nativeHeight = json.optInt("nativeHeight"),
                data = d,
                offset = base + 4 + hl
            )
        } catch (e: Exception) {
            null
        }
    }

    // ---------------------------------------------------------------- outgoing

    fun ping(): ByteArray {
        val p = JSONObject()
            .put("PingId", UUID.randomUUID().toString().replace("-", ""))
            .put("TimestampMs", System.currentTimeMillis())
        return envelope(T_PING, p.toString().toByteArray())
    }

    fun pong(pingId: String, timestampMs: Long): ByteArray {
        val p = JSONObject().put("PingId", pingId).put("TimestampMs", timestampMs)
        return envelope(T_PONG, p.toString().toByteArray())
    }

    private fun input(
        type: Int,
        x: Int = 0,
        y: Int = 0,
        button: Int = 0,
        action: Int = 0,
        wheel: Int = 0,
        vk: Int = 0,
        ch: Char? = null
    ): ByteArray {
        val j = JSONObject()
            .put("type", type)
            .put("timestampMs", System.currentTimeMillis())
            .put("mouseX", x)
            .put("mouseY", y)
            .put("button", button)
            .put("action", action)
            .put("wheelDelta", wheel)
            .put("virtualKeyCode", vk)
        if (ch != null) j.put("character", ch.toString())
        return envelope(T_INPUT, j.toString().toByteArray(Charsets.UTF_8))
    }

    fun mouseMove(x: Int, y: Int) = input(IN_MOVE, x, y)
    fun mouseButton(x: Int, y: Int, button: Int, down: Boolean) =
        input(IN_BUTTON, x, y, button = button, action = if (down) ACT_DOWN else ACT_UP)

    fun mouseDoubleClick(x: Int, y: Int) = input(IN_DBLCLICK, x, y, button = BTN_LEFT)
    fun mouseWheel(x: Int, y: Int, delta: Int) = input(IN_WHEEL, x, y, wheel = delta)
    fun key(vk: Int, down: Boolean) = input(IN_KEY, vk = vk, action = if (down) ACT_DOWN else ACT_UP)
    fun char(c: Char) = input(IN_CHAR, ch = c)

    /** @param maxWidth 0 = automatic (follows quality); up to 3840 for 4K. */
    fun streamSettings(fps: Int, quality: Int, maxWidth: Int = 0, codec: String = "h264"): ByteArray {
        val meta = JSONObject()
            .put("Fps", fps.toString()).put("fps", fps.toString())
            .put("Quality", quality.toString()).put("quality", quality.toString())
            .put("MaxWidth", maxWidth.toString()).put("maxWidth", maxWidth.toString())
            .put("Codec", codec)
            .put("ColorFix", "1") // encode with correct R/B order (hardware decoders show the stream as-is)
        val j = JSONObject()
            .put("action", CTL_STREAM_SETTINGS)
            .put("fps", fps)
            .put("quality", quality)
            .put("metadata", meta)
        return envelope(T_CONTROL, j.toString().toByteArray())
    }

    fun showRemoteCursor(enabled: Boolean): ByteArray {
        val meta = JSONObject().put("Enabled", if (enabled) "true" else "false")
        val j = JSONObject().put("action", CTL_SHOW_REMOTE_CURSOR).put("metadata", meta)
        return envelope(T_CONTROL, j.toString().toByteArray())
    }

    fun disconnect(): ByteArray {
        val j = JSONObject().put("action", CTL_DISCONNECT).put("reason", "Android TV viewer disconnected")
        return envelope(T_CONTROL, j.toString().toByteArray())
    }
}

/** Windows virtual-key codes used by the on-screen shortcuts. */
object VK {
    const val BACK = 0x08
    const val TAB = 0x09
    const val ENTER = 0x0D
    const val SHIFT = 0x10
    const val CTRL = 0x11
    const val ALT = 0x12
    const val ESC = 0x1B
    const val SPACE = 0x20
    const val PGUP = 0x21
    const val PGDN = 0x22
    const val END = 0x23
    const val HOME = 0x24
    const val LEFT = 0x25
    const val UP = 0x26
    const val RIGHT = 0x27
    const val DOWN = 0x28
    const val INSERT = 0x2D
    const val DELETE = 0x2E
    const val LWIN = 0x5B
    const val F1 = 0x70
}
