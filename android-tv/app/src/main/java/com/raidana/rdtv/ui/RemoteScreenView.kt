package com.raidana.rdtv.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.graphics.Rect
import android.util.AttributeSet
import android.view.SurfaceView
import android.view.View
import android.widget.FrameLayout
import com.raidana.rdtv.video.TileCanvas

/**
 * Hosts the video surface (hardware H.264), the software tile view and a local cursor overlay.
 * The video is letterboxed to keep the PC's aspect ratio; the cursor lives in normalized (0..1)
 * coordinates of the video area so it maps 1:1 to PC pixels.
 */
class RemoteScreenView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : FrameLayout(context, attrs) {

    val surfaceView = SurfaceView(context)
    private val tileView = TileView(context)
    private val cursorView = CursorView(context)

    private var videoW = 0
    private var videoH = 0
    private val content = Rect()

    /** Normalized cursor position. */
    var cursorX = 0.5f
        private set
    var cursorY = 0.5f
        private set

    init {
        setBackgroundColor(Color.BLACK)
        addView(surfaceView)
        addView(tileView)
        addView(cursorView)
    }

    fun attachTiles(tiles: TileCanvas) {
        tileView.tiles = tiles
    }

    fun setVideoSize(w: Int, h: Int) {
        if (w <= 0 || h <= 0 || (w == videoW && h == videoH)) return
        videoW = w
        videoH = h
        requestLayout()
    }

    fun invalidateTiles() = tileView.postInvalidateOnAnimation()

    fun setCursor(nx: Float, ny: Float) {
        cursorX = nx.coerceIn(0f, 1f)
        cursorY = ny.coerceIn(0f, 1f)
        cursorView.invalidate()
    }

    fun setCursorVisible(visible: Boolean) {
        cursorView.visibility = if (visible) VISIBLE else INVISIBLE
    }

    /** Converts view pixels to normalized coordinates of the video area. */
    fun toNormalized(x: Float, y: Float): Pair<Float, Float> {
        val w = content.width().coerceAtLeast(1)
        val h = content.height().coerceAtLeast(1)
        return ((x - content.left) / w).coerceIn(0f, 1f) to ((y - content.top) / h).coerceIn(0f, 1f)
    }

    val contentWidth: Int get() = content.width().coerceAtLeast(1)
    val contentHeight: Int get() = content.height().coerceAtLeast(1)

    override fun onLayout(changed: Boolean, l: Int, t: Int, r: Int, b: Int) {
        val w = r - l
        val h = b - t
        if (videoW > 0 && videoH > 0 && w > 0 && h > 0) {
            val scale = minOf(w.toFloat() / videoW, h.toFloat() / videoH)
            val cw = (videoW * scale).toInt()
            val ch = (videoH * scale).toInt()
            val cl = (w - cw) / 2
            val ct = (h - ch) / 2
            content.set(cl, ct, cl + cw, ct + ch)
        } else {
            content.set(0, 0, w, h)
        }
        surfaceView.layout(content.left, content.top, content.right, content.bottom)
        tileView.layout(content.left, content.top, content.right, content.bottom)
        cursorView.layout(0, 0, w, h)
    }

    private class TileView(context: Context) : View(context) {
        var tiles: TileCanvas? = null
        private val paint = Paint(Paint.FILTER_BITMAP_FLAG)
        private val dst = Rect()

        override fun onDraw(canvas: Canvas) {
            val bmp = tiles?.bitmap ?: return
            dst.set(0, 0, width, height)
            canvas.drawBitmap(bmp, null, dst, paint)
        }
    }

    private inner class CursorView(context: Context) : View(context) {
        private val fill = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE; style = Paint.Style.FILL }
        private val stroke = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.BLACK; style = Paint.Style.STROKE; strokeWidth = 3f
        }
        private val path = Path()

        override fun onDraw(canvas: Canvas) {
            val px = content.left + cursorX * content.width()
            val py = content.top + cursorY * content.height()
            val s = resources.displayMetrics.density * 1.0f
            path.reset()
            path.moveTo(px, py)
            path.lineTo(px, py + 22 * s)
            path.lineTo(px + 5.5f * s, py + 17 * s)
            path.lineTo(px + 10 * s, py + 26 * s)
            path.lineTo(px + 14 * s, py + 24 * s)
            path.lineTo(px + 9.5f * s, py + 15.5f * s)
            path.lineTo(px + 16 * s, py + 15.5f * s)
            path.close()
            canvas.drawPath(path, fill)
            canvas.drawPath(path, stroke)
        }
    }
}
