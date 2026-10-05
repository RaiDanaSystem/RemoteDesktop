package com.raidana.rdtv.video

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Software fallback: decodes "RDRT" JPEG dirty-tile packets (and plain JPEG frames) into a backing bitmap.
 * Decoding happens on the caller's thread; the owning view just draws [bitmap].
 */
class TileCanvas(private val onUpdated: () -> Unit) {
    @Volatile
    var bitmap: Bitmap? = null
        private set
    private var canvas: Canvas? = null

    fun isTilePacket(d: ByteArray, off: Int, len: Int) =
        len >= 24 && d[off] == 'R'.code.toByte() && d[off + 1] == 'D'.code.toByte() &&
            d[off + 2] == 'R'.code.toByte() && d[off + 3] == 'T'.code.toByte()

    fun applyTiles(d: ByteArray, off: Int, len: Int): IntArray? {
        val bb = ByteBuffer.wrap(d, off, len).order(ByteOrder.LITTLE_ENDIAN)
        val w = bb.getShort(off + 8).toInt() and 0xFFFF
        val h = bb.getShort(off + 10).toInt() and 0xFFFF
        val tileSize = bb.getShort(off + 12).toInt() and 0xFFFF
        val count = bb.getShort(off + 22).toInt() and 0xFFFF
        if (w <= 0 || h <= 0) return null
        ensure(w, h)
        val cv = canvas ?: return null
        var p = off + 24
        val opts = BitmapFactory.Options().apply { inPreferredConfig = Bitmap.Config.ARGB_8888 }
        for (i in 0 until count) {
            if (p + 8 > off + len) break
            val col = bb.getShort(p).toInt() and 0xFFFF
            val row = bb.getShort(p + 2).toInt() and 0xFFFF
            val n = bb.getInt(p + 4)
            p += 8
            if (n < 0 || p + n > off + len) break
            val tile = BitmapFactory.decodeByteArray(d, p, n, opts)
            if (tile != null) {
                cv.drawBitmap(tile, (col * tileSize).toFloat(), (row * tileSize).toFloat(), null)
                tile.recycle()
            }
            p += n
        }
        onUpdated()
        return intArrayOf(w, h)
    }

    fun applyJpeg(d: ByteArray, off: Int, len: Int): IntArray? {
        val frame = BitmapFactory.decodeByteArray(d, off, len) ?: return null
        ensure(frame.width, frame.height)
        canvas?.drawBitmap(frame, 0f, 0f, null)
        val size = intArrayOf(frame.width, frame.height)
        frame.recycle()
        onUpdated()
        return size
    }

    private fun ensure(w: Int, h: Int) {
        val b = bitmap
        if (b != null && b.width == w && b.height == h) return
        val nb = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
        bitmap = nb
        canvas = Canvas(nb)
    }

    fun clear() {
        bitmap = null
        canvas = null
        onUpdated()
    }
}
