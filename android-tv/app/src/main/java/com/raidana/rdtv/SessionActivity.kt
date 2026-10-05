package com.raidana.rdtv

import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.content.Intent
import android.net.wifi.WifiManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.os.SystemClock
import android.app.UiModeManager
import android.content.res.Configuration
import android.view.InputDevice
import android.view.ViewConfiguration
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.View
import android.view.WindowManager
import android.view.inputmethod.EditorInfo
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import com.raidana.rdtv.net.Api
import com.raidana.rdtv.net.ApiException
import com.raidana.rdtv.net.Signaling
import com.raidana.rdtv.proto.Proto
import com.raidana.rdtv.proto.VK
import com.raidana.rdtv.net.DirectLink
import com.raidana.rdtv.rtc.Link
import com.raidana.rdtv.rtc.RtcSession
import com.raidana.rdtv.ui.KeyMap
import com.raidana.rdtv.ui.RemoteScreenView
import com.raidana.rdtv.video.AudioPlayer
import com.raidana.rdtv.video.H264Assembler
import com.raidana.rdtv.video.H264Decoder
import com.raidana.rdtv.video.TileCanvas
import org.json.JSONObject
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicLong

class SessionActivity : Activity(), RtcSession.Listener {

    private val ui = Handler(Looper.getMainLooper())
    private val net = Executors.newSingleThreadExecutor { Thread(it, "session-net") }
    private val decode = Executors.newSingleThreadExecutor { Thread(it, "frame-decode") }

    private lateinit var screen: RemoteScreenView
    private lateinit var statusText: TextView
    private lateinit var statsText: TextView
    private lateinit var hintText: TextView
    private lateinit var menuPanel: View
    private lateinit var menuList: LinearLayout

    private var api: Api? = null
    private var signaling: Signaling? = null
    @Volatile private var rtc: Link? = null
    private var unsupportedHandled = false
    private var maxWidth = 0
    private var codecMode = "h264"
    private var stuckSeconds = 0
    private var lastAu = 0L
    private var lastRenderedForWatchdog = 0L
    private val auCount = AtomicLong()
    private var tilesRequested = false
    private var sessionId: String? = null

    @Volatile private var ended = false
    @Volatile private var channelOpen = false
    @Volatile private var attempt = 0

    // video
    private val assembler = H264Assembler()
    @Volatile private var decoder: H264Decoder? = null
    private lateinit var tiles: TileCanvas
    private var streamW = 0
    private var streamH = 0

    // stats
    private val bytesIn = AtomicLong()
    private val framesTiles = AtomicLong()
    private var lastRendered = 0L
    private var lastTiles = 0L
    private var lastBytes = 0L
    private var lastStatsAt = SystemClock.elapsedRealtime()
    @Volatile private var rttMs = -1L
    private var showStats = true
    private var soundOn = true
    private val muteHost = false // muting the PC's speakers is a setting in the Windows app
    private var audioPlayer: AudioPlayer? = null
    private val prefs by lazy { getSharedPreferences("rdtv", Context.MODE_PRIVATE) }

    // settings
    private var fps = 30
    private var quality = 80

    // cursor / input
    private val heldDirs = HashSet<Int>()
    private var holdStart = 0L
    private var lastTick = 0L
    private var lastMoveSent = 0L
    private var scrollMode = false
    private var menuVisible = false
    private var mouseDownButton = -1
    private var remoteCursor = false

    // phone vs TV
    private var isTv = false
    private var dragMode = false
    private var touchStartX = 0f
    private var touchStartY = 0f
    private var touchMoved = false
    private var maxPointers = 0
    private var longPressFired = false
    private var lastTapAt = 0L
    private var scrollAccum = 0f
    private var lastScrollY = 0f
    private var dragPressed = false
    private val longPressRunnable = Runnable {
        if (!touchMoved && maxPointers == 1 && !dragMode) {
            longPressFired = true
            click(Proto.BTN_RIGHT)
        }
    }

    private var wifiLock: WifiManager.WifiLock? = null
    private var wakeLock: PowerManager.WakeLock? = null

    private val ticker = object : Runnable {
        override fun run() {
            if (heldDirs.isEmpty()) return
            stepCursor()
            ui.postDelayed(this, 16)
        }
    }

    private val statsTick = object : Runnable {
        override fun run() {
            updateStats()
            ui.postDelayed(this, 1000)
        }
    }

    // ----------------------------------------------------------------- lifecycle

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        showStats = prefs.getBoolean("showStats", true)
        soundOn = prefs.getBoolean("soundOn", true)
        remoteCursor = prefs.getBoolean("remoteCursor", false)
        setContentView(R.layout.activity_session)
        screen = findViewById(R.id.screen)
        statusText = findViewById(R.id.status)
        statsText = findViewById(R.id.stats)
        hintText = findViewById(R.id.hint)
        menuPanel = findViewById(R.id.menu_panel)
        menuList = findViewById(R.id.menu_list)

        val uim = getSystemService(Context.UI_MODE_SERVICE) as UiModeManager
        isTv = uim.currentModeType == Configuration.UI_MODE_TYPE_TELEVISION ||
            packageManager.hasSystemFeature("android.software.leanback")
        if (!isTv) {
            findViewById<View>(R.id.touch_bar).visibility = View.VISIBLE
            findViewById<View>(R.id.btn_menu).setOnClickListener { showMenu() }
            findViewById<View>(R.id.btn_keyboard).setOnClickListener { promptText() }
        }
        val panelW = minOf(dp(340), resources.displayMetrics.widthPixels)
        menuPanel.layoutParams = menuPanel.layoutParams.apply { width = panelW }

        tiles = TileCanvas { screen.invalidateTiles() }
        screen.attachTiles(tiles)

        screen.surfaceView.holder.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) {
                decoder = H264Decoder(
                    holder.surface,
                    { w, h -> ui.post { screen.setVideoSize(w, h) } },
                    { w, h -> ui.post { onDecoderUnsupported(w, h) } }
                )
            }

            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}
            override fun surfaceDestroyed(holder: SurfaceHolder) {
                val d = decoder
                decoder = null
                decode.execute { d?.release() }
            }
        })

        acquireLocks()
        hideSystemUi()
        setStatus(getString(R.string.status_starting))

        val directHost = intent.getStringExtra(EXTRA_HOST)
        if (directHost != null) {
            val port = intent.getIntExtra(EXTRA_PORT, DirectLink.DEFAULT_PORT)
            val name = intent.getStringExtra(EXTRA_NAME) ?: directHost
            net.execute { runDirect(directHost, port, name) }
        } else {
            val server = intent.getStringExtra(EXTRA_SERVER).orEmpty()
            val code = intent.getStringExtra(EXTRA_CODE).orEmpty()
            net.execute { runFlow(server, code) }
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemUi()
    }

    override fun onStop() {
        super.onStop()
        if (!isChangingConfigurations) endSession(null)
    }

    override fun onDestroy() {
        super.onDestroy()
        endSession(null)
        decode.shutdown()
        net.shutdown()
    }

    @Suppress("DEPRECATION")
    private fun hideSystemUi() {
        window.decorView.systemUiVisibility = (View.SYSTEM_UI_FLAG_FULLSCREEN
            or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
            or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
            or View.SYSTEM_UI_FLAG_LAYOUT_STABLE
            or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
            or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION)
    }

    @Suppress("DEPRECATION")
    private fun acquireLocks() {
        try {
            val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "rdtv:wifi").apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (_: Exception) {
        }
        try {
            val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "rdtv:cpu").apply {
                setReferenceCounted(false)
                acquire(4 * 60 * 60 * 1000L)
            }
        } catch (_: Exception) {
        }
    }

    // ----------------------------------------------------------------- connection flow

    /** Server-less LAN mode: connect straight to the PC's TCP port. */
    private fun runDirect(host: String, port: Int, name: String) {
        try {
            post { setStatus(getString(R.string.status_waiting, name)) }
            log("direct connect $host:$port")
            val prefs = getSharedPreferences("rdtv", Context.MODE_PRIVATE)
            val id = prefs.getString("viewerId", null)
                ?: java.util.UUID.randomUUID().toString().replace("-", "").also { prefs.edit().putString("viewerId", it).apply() }

            val res = DirectLink.connect(host, port, id, android.os.Build.MODEL ?: "Android",
                object : DirectLink.Listener {
                    override fun onMessage(data: ByteArray) = this@SessionActivity.onMessage(data)
                    override fun onClosed(reason: String) = this@SessionActivity.onChannelClosed(reason)
                })
            val link = res.link
            if (link == null) {
                endSession(res.error ?: getString(R.string.err_rejected))
                return
            }
            if (ended) { link.close(); return }
            rtc = link
            link.startReading()
            log("accepted by ${link.hostName} viewOnly=${link.viewOnly}")
            fps = 30; quality = 80; maxWidth = 0
            onChannelOpen()
            if (link.viewOnly) post { flashHint(R.string.hint_view_only) }
        } catch (e: Exception) {
            endSession("${e.javaClass.simpleName}: ${e.message ?: ""}")
        }
    }

    private fun runFlow(server: String, code: String) {
        try {
            val a = Api(server)
            api = a
            post { setStatus(getString(R.string.status_login)) }
            log("server " + a.baseUrl)
            a.login(Config.SUPPORT_USERNAME, Config.SUPPORT_PASSWORD)
            log("login ok")

            post { setStatus(getString(R.string.status_connecting)) }
            val conn = a.connect(code)
            sessionId = conn.sessionId
            log("session " + conn.sessionId + " device " + conn.deviceName)

            post { setStatus(getString(R.string.status_waiting, conn.deviceName)) }
            val deadline = SystemClock.elapsedRealtime() + 3 * 60_000
            while (!ended) {
                val st = a.sessionStatus(conn.sessionId)
                val status = st?.first
                if (status == "Active") break
                if (status == "Ended" || status == "Failed") {
                    throw ApiException(st.second ?: getString(R.string.err_rejected))
                }
                if (SystemClock.elapsedRealtime() > deadline) throw ApiException(getString(R.string.err_timeout))
                Thread.sleep(600)
            }
            if (ended) return

            log("accepted by PC")
            post { setStatus(getString(R.string.status_rtc)) }
            val sig = Signaling(a.baseUrl, a.accessToken)
            signaling = sig
            sig.connect()
            log("signaling connected")
            // The PC prepares its WebRTC answerer right after accepting; an offer that arrives
            // earlier would be wiped by that reset, so let it settle first.
            Thread.sleep(1500)
            startRtc()
        } catch (e: ApiException) {
            endSession(e.message)
        } catch (e: Exception) {
            var t: Throwable = e
            while (t.cause != null && t.cause !== t) t = t.cause!!
            endSession("${e.javaClass.simpleName}: ${t.message ?: e.message ?: ""}")
        }
    }

    /** Creates the peer and retries a couple of times if the PC was not yet listening. */
    private fun startRtc() {
        val sid = sessionId ?: return
        val sig = signaling ?: return
        if (ended) return
        attempt++
        log("offer attempt $attempt")
        rtc?.close()
        val r = RtcSession(applicationContext, sid, sig, this)
        rtc = r
        r.start()
        val myAttempt = attempt
        ui.postDelayed({
            if (!ended && !channelOpen && myAttempt == attempt) {
                if (attempt >= 3) endSession(getString(R.string.err_rtc_timeout))
                else net.execute { startRtc() }
            }
        }, 14000)
    }

    private fun endSession(error: String?) {
        if (ended) return
        ended = true
        ui.removeCallbacks(ticker)
        ui.removeCallbacks(statsTick)
        if (error != null) log("END: $error")
        val shownError = if (error == null) null else error + "\n\n" + logTail(14)
        val sid = sessionId
        val a = api
        val r = rtc
        val s = signaling
        val d = decoder
        Thread {
            try { r?.send(Proto.disconnect()); Thread.sleep(150) } catch (_: Exception) {}
            try { r?.close() } catch (_: Exception) {}
            try { s?.close() } catch (_: Exception) {}
            try { if (sid != null) a?.terminate(sid) } catch (_: Exception) {}
            try { d?.release() } catch (_: Exception) {}
            try { audioPlayer?.release() } catch (_: Exception) {}
            try { wifiLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
            try { wakeLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        }.start()
        ui.post {
            val data = Intent().putExtra(EXTRA_ERROR, shownError ?: "")
            setResult(if (error == null) RESULT_OK else RESULT_CANCELED, data)
            if (!isFinishing) finish()
        }
    }

    private fun post(r: () -> Unit) = ui.post { if (!ended) r() }

    // ----------------------------------------------------------------- RtcSession.Listener

    override fun onChannelOpen() {
        channelOpen = true
        ui.post {
            if (ended) return@post
            statusText.visibility = View.GONE
            hintText.visibility = View.VISIBLE
            hintText.text = getString(if (isTv) R.string.hint_controls else R.string.hint_touch)
            ui.postDelayed({ hintText.visibility = View.GONE }, 9000)
            ui.postDelayed(statsTick, 1000)
        }
        rtc?.send(Proto.streamSettings(fps, quality, maxWidth, codecMode, soundOn, muteHost))
        if (remoteCursor) rtc?.send(Proto.showRemoteCursor(true)) // remembered from last time
        Thread {
            while (!ended && channelOpen) {
                rtc?.send(Proto.ping())
                try { Thread.sleep(2000) } catch (_: InterruptedException) { break }
            }
        }.start()
    }

    override fun onChannelClosed(reason: String) {
        val wasOpen = channelOpen
        channelOpen = false
        // Before the channel ever opened the retry watchdog in startRtc() handles failures.
        log("channel closed: $reason (wasOpen=$wasOpen)")
        if (!ended && wasOpen) endSession(reason)
    }

    override fun onLog(message: String) = log(message)

    private val logLines = java.util.ArrayDeque<String>()
    private val logFmt = java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.US)

    @Synchronized
    private fun log(message: String) {
        logLines.addLast(logFmt.format(java.util.Date()) + " " + message)
        while (logLines.size > 40) logLines.removeFirst()
    }

    @Synchronized
    private fun logTail(n: Int): String = logLines.toList().takeLast(n).joinToString("\n")

    override fun onMessage(data: ByteArray) {
        bytesIn.addAndGet(data.size.toLong())
        val env = Proto.parseEnvelope(data) ?: return
        when (env.type) {
            Proto.T_SCREEN -> {
                val f = Proto.parseScreenFrame(env) ?: return
                decode.execute { handleFrame(f) }
            }
            Proto.T_AUDIO -> if (soundOn) {
                val p = audioPlayer ?: AudioPlayer().also { audioPlayer = it }
                p.onPacket(env.data, env.offset, env.length)
            }
            Proto.T_PONG -> try {
                val j = JSONObject(String(env.data, env.offset, env.length))
                val rtt = System.currentTimeMillis() - j.optLong("TimestampMs")
                if (rtt in 0..60_000) rttMs = rtt
            } catch (_: Exception) {}
            Proto.T_PING -> try {
                val j = JSONObject(String(env.data, env.offset, env.length))
                rtc?.send(Proto.pong(j.optString("PingId"), j.optLong("TimestampMs")))
            } catch (_: Exception) {}
            Proto.T_CONTROL -> try {
                val j = JSONObject(String(env.data, env.offset, env.length))
                if (j.optInt("action", -1) == Proto.CTL_DISCONNECT) endSession(getString(R.string.err_pc_ended))
            } catch (_: Exception) {}
        }
    }

    // ----------------------------------------------------------------- video

    private fun handleFrame(f: Proto.ScreenFrame) {
        val d = f.data
        val off = f.offset
        val len = f.length
        if (len < 4) return
        if (d[off] == 'R'.code.toByte() && d[off + 1] == 'D'.code.toByte() && d[off + 2] == 'H'.code.toByte()) {
            val au = assembler.add(d, off, len) ?: return
            auCount.incrementAndGet()
            setStream(au.width, au.height)
            val dec = decoder ?: return
            if (tiles.bitmap != null) { tiles.clear() }
            dec.feed(au)
        } else if (tiles.isTilePacket(d, off, len)) {
            val size = tiles.applyTiles(d, off, len) ?: return
            framesTiles.incrementAndGet()
            setStream(size[0], size[1])
        } else if (d[off] == 0xFF.toByte() && d[off + 1] == 0xD8.toByte()) {
            val size = tiles.applyJpeg(d, off, len) ?: return
            framesTiles.incrementAndGet()
            setStream(size[0], size[1])
        }
    }

    private fun setStream(w: Int, h: Int) {
        if (w == streamW && h == streamH) return
        streamW = w
        streamH = h
        ui.post { screen.setVideoSize(w, h) }
    }

    private fun updateStats() {
        val now = SystemClock.elapsedRealtime()
        val dt = ((now - lastStatsAt) / 1000.0).coerceAtLeast(0.2)
        val rendered = (decoder?.rendered?.get() ?: 0L)
        val tl = framesTiles.get()
        val by = bytesIn.get()
        val fpsNow = ((rendered - lastRendered) + (tl - lastTiles)) / dt
        val mbps = (by - lastBytes) * 8 / dt / 1_000_000.0
        lastRendered = rendered; lastTiles = tl; lastBytes = by; lastStatsAt = now
        val d = decoder
        // Watchdog: access units keep arriving but nothing reaches the screen → try the next decoder
        // configuration, and finally ask the PC for JPEG tiles (software path that works everywhere).
        if (d != null && codecMode == "h264") {
            val au = auCount.get()
            val r = d.rendered.get()
            if (au - lastAu >= 3 && r == lastRenderedForWatchdog) stuckSeconds++ else stuckSeconds = 0
            lastAu = au; lastRenderedForWatchdog = r
            if (stuckSeconds >= 3) {
                stuckSeconds = 0
                log("H.264 output stalled (in=${d.fed.get()} out=$r err=${d.errors.get()} ${d.lastError})")
                if (d.escalate()) {
                    log("trying decoder ${d.describe()}")
                } else if (!tilesRequested) {
                    tilesRequested = true
                    setCompatibilityMode(true, auto = true)
                }
            }
        }
        if (showStats) {
            statsText.visibility = View.VISIBLE
            val dec = if (d != null && d.codecName.isNotEmpty()) "  ${d.describe()} in/out ${d.fed.get()}/${d.rendered.get()} drop ${d.dropped.get()} err ${d.errors.get()}" else ""
            statsText.text = String.format(
                "%dx%d  %.0f fps  %.1f Mbit/s  ping %s  %s%s",
                streamW, streamH, fpsNow, mbps, if (rttMs >= 0) "$rttMs ms" else "—",
                if (codecMode == "tiles") "[JPEG]" else "[H.264]", dec
            ) + (if (d != null && d.lastError.isNotEmpty()) "\n" + d.lastError else "")
        } else statsText.visibility = View.GONE
    }

    private fun setStatus(text: String) {
        statusText.visibility = View.VISIBLE
        statusText.text = text
    }

    // ----------------------------------------------------------------- remote input helpers

    private fun remoteX() = (screen.cursorX * (streamW - 1).coerceAtLeast(0)).toInt()
    private fun remoteY() = (screen.cursorY * (streamH - 1).coerceAtLeast(0)).toInt()
    private fun send(bytes: ByteArray) { rtc?.send(bytes) }

    private fun sendMove(force: Boolean = false) {
        val now = SystemClock.elapsedRealtime()
        if (!force && now - lastMoveSent < 30) return
        lastMoveSent = now
        send(Proto.mouseMove(remoteX(), remoteY()))
    }

    private fun click(button: Int) {
        sendMove(true)
        send(Proto.mouseButton(remoteX(), remoteY(), button, true))
        send(Proto.mouseButton(remoteX(), remoteY(), button, false))
    }

    private fun doubleClick() {
        sendMove(true)
        send(Proto.mouseDoubleClick(remoteX(), remoteY()))
    }

    private fun wheel(delta: Int) {
        send(Proto.mouseWheel(remoteX(), remoteY(), delta))
    }

    private fun pressKey(vararg vks: Int) {
        for (vk in vks) send(Proto.key(vk, true))
        for (vk in vks.reversed()) send(Proto.key(vk, false))
    }

    private fun typeText(text: String) {
        for (c in text) {
            if (c == '\n') pressKey(VK.ENTER) else send(Proto.char(c))
        }
    }

    // ----------------------------------------------------------------- cursor movement (D-pad)

    private fun stepCursor() {
        val now = SystemClock.elapsedRealtime()
        val dt = ((now - lastTick) / 1000f).coerceIn(0.001f, 0.1f)
        lastTick = now
        val held = (now - holdStart) / 1000f
        // 10% of the screen width per second, accelerating to 80% after ~1.2 s
        val speed = (0.10f + 0.70f * (held / 1.2f).coerceIn(0f, 1f))
        var dx = 0f
        var dy = 0f
        if (KeyEvent.KEYCODE_DPAD_LEFT in heldDirs) dx -= 1f
        if (KeyEvent.KEYCODE_DPAD_RIGHT in heldDirs) dx += 1f
        if (KeyEvent.KEYCODE_DPAD_UP in heldDirs) dy -= 1f
        if (KeyEvent.KEYCODE_DPAD_DOWN in heldDirs) dy += 1f
        if (scrollMode && dy != 0f) {
            val step = if (held > 0.8f) 240 else 120
            if (now - lastScroll > 60) { lastScroll = now; wheel(if (dy < 0) step else -step) }
            dy = 0f
        }
        if (dx == 0f && dy == 0f) return
        val pxStep = speed * screen.contentWidth * dt
        screen.setCursor(
            screen.cursorX + dx * pxStep / screen.contentWidth,
            screen.cursorY + dy * pxStep / screen.contentHeight
        )
        sendMove()
    }

    private var lastScroll = 0L

    // ----------------------------------------------------------------- key / pointer events

    override fun dispatchKeyEvent(e: KeyEvent): Boolean {
        val code = e.keyCode
        val down = e.action == KeyEvent.ACTION_DOWN

        if (menuVisible) {
            if (code == KeyEvent.KEYCODE_BACK || code == KeyEvent.KEYCODE_MENU) {
                if (!down) hideMenu()
                return true
            }
            return super.dispatchKeyEvent(e)
        }
        if (!channelOpen) {
            if (code == KeyEvent.KEYCODE_BACK) {
                if (!down) endSession(null)
                return true
            }
            return super.dispatchKeyEvent(e)
        }

        // Physical alphabetic keyboard → forward as typing.
        val dev = e.device
        if (dev != null && dev.keyboardType == InputDevice.KEYBOARD_TYPE_ALPHABETIC &&
            code != KeyEvent.KEYCODE_BACK && code != KeyEvent.KEYCODE_MENU && code != KeyEvent.KEYCODE_HOME
        ) {
            forwardKeyboard(e)
            return true
        }

        when (code) {
            KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT,
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN -> {
                if (down) {
                    if (heldDirs.isEmpty()) {
                        holdStart = SystemClock.elapsedRealtime()
                        lastTick = holdStart
                        ui.post(ticker)
                    }
                    heldDirs.add(code)
                } else {
                    heldDirs.remove(code)
                    if (heldDirs.isEmpty()) { ui.removeCallbacks(ticker); sendMove(true) }
                }
                return true
            }
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER, KeyEvent.KEYCODE_BUTTON_A -> {
                if (down && e.repeatCount == 0) {
                    sendMove(true)
                    send(Proto.mouseButton(remoteX(), remoteY(), Proto.BTN_LEFT, true))
                } else if (!down) {
                    send(Proto.mouseButton(remoteX(), remoteY(), Proto.BTN_LEFT, false))
                }
                return true
            }
            KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE, KeyEvent.KEYCODE_PROG_RED, KeyEvent.KEYCODE_BUTTON_X -> {
                if (down && e.repeatCount == 0) click(Proto.BTN_RIGHT)
                return true
            }
            KeyEvent.KEYCODE_CHANNEL_UP, KeyEvent.KEYCODE_PAGE_UP, KeyEvent.KEYCODE_MEDIA_REWIND -> {
                if (down) wheel(240)
                return true
            }
            KeyEvent.KEYCODE_CHANNEL_DOWN, KeyEvent.KEYCODE_PAGE_DOWN, KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> {
                if (down) wheel(-240)
                return true
            }
            KeyEvent.KEYCODE_PROG_GREEN -> {
                if (down && e.repeatCount == 0) { scrollMode = !scrollMode; flashHint(if (scrollMode) R.string.scroll_on else R.string.scroll_off) }
                return true
            }
            KeyEvent.KEYCODE_PROG_YELLOW -> {
                if (!down) promptText()
                return true
            }
            KeyEvent.KEYCODE_BACK, KeyEvent.KEYCODE_MENU, KeyEvent.KEYCODE_PROG_BLUE,
            KeyEvent.KEYCODE_INFO, KeyEvent.KEYCODE_GUIDE -> {
                if (!down) showMenu()
                return true
            }
        }
        return super.dispatchKeyEvent(e)
    }

    private fun forwardKeyboard(e: KeyEvent) {
        val down = e.action == KeyEvent.ACTION_DOWN
        val vk = KeyMap.toVk(e.keyCode)
        val combo = e.isCtrlPressed || e.isAltPressed || e.isMetaPressed
        val isModifier = vk == VK.SHIFT || vk == VK.CTRL || vk == VK.ALT || vk == VK.LWIN
        if (vk != null && (isModifier || KeyMap.isNonText(vk) || combo)) {
            send(Proto.key(vk, down))
        } else if (down) {
            val u = e.unicodeChar
            if (u in 1..0xFFFF) send(Proto.char(u.toChar()))
        }
    }

    override fun onTouchEvent(e: MotionEvent): Boolean {
        if (menuVisible || !channelOpen) return super.onTouchEvent(e)
        if (e.getToolType(0) == MotionEvent.TOOL_TYPE_MOUSE) return handleMouse(e)
        return handleFinger(e)
    }

    /** USB/BT mouse: buttons map 1:1. */
    private fun handleMouse(e: MotionEvent): Boolean {
        val (nx, ny) = screen.toNormalized(e.x, e.y)
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                screen.setCursor(nx, ny)
                mouseDownButton = when {
                    e.buttonState and MotionEvent.BUTTON_SECONDARY != 0 -> Proto.BTN_RIGHT
                    e.buttonState and MotionEvent.BUTTON_TERTIARY != 0 -> Proto.BTN_MIDDLE
                    else -> Proto.BTN_LEFT
                }
                sendMove(true)
                send(Proto.mouseButton(remoteX(), remoteY(), mouseDownButton, true))
            }
            MotionEvent.ACTION_MOVE -> { screen.setCursor(nx, ny); sendMove() }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                screen.setCursor(nx, ny)
                if (mouseDownButton >= 0) send(Proto.mouseButton(remoteX(), remoteY(), mouseDownButton, false))
                mouseDownButton = -1
            }
        }
        return true
    }

    /**
     * Touch scheme for phones: tap = click, double tap = double click, long press = right click,
     * one-finger drag = move the pointer (or drag with the left button in drag mode),
     * two-finger drag = scroll.
     */
    private fun handleFinger(e: MotionEvent): Boolean {
        val slop = ViewConfiguration.get(this).scaledTouchSlop
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                touchStartX = e.x; touchStartY = e.y
                touchMoved = false; longPressFired = false; maxPointers = 1
                scrollAccum = 0f
                val (nx, ny) = screen.toNormalized(e.x, e.y)
                screen.setCursor(nx, ny)
                if (dragMode) {
                    sendMove(true)
                    send(Proto.mouseButton(remoteX(), remoteY(), Proto.BTN_LEFT, true))
                    dragPressed = true
                } else ui.postDelayed(longPressRunnable, ViewConfiguration.getLongPressTimeout().toLong())
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                maxPointers = maxOf(maxPointers, e.pointerCount)
                ui.removeCallbacks(longPressRunnable)
                lastScrollY = avgY(e)
            }
            MotionEvent.ACTION_MOVE -> {
                if (e.pointerCount >= 2) {
                    touchMoved = true
                    val y = avgY(e)
                    scrollAccum += y - lastScrollY
                    lastScrollY = y
                    val step = 28f * resources.displayMetrics.density / 2f
                    while (scrollAccum >= step) { wheel(120); scrollAccum -= step }
                    while (scrollAccum <= -step) { wheel(-120); scrollAccum += step }
                } else {
                    if (!touchMoved && Math.hypot((e.x - touchStartX).toDouble(), (e.y - touchStartY).toDouble()) > slop) {
                        touchMoved = true
                        ui.removeCallbacks(longPressRunnable)
                    }
                    if (touchMoved && maxPointers == 1) {
                        val (nx, ny) = screen.toNormalized(e.x, e.y)
                        screen.setCursor(nx, ny)
                        sendMove()
                    }
                }
            }
            MotionEvent.ACTION_UP -> {
                ui.removeCallbacks(longPressRunnable)
                val (nx, ny) = screen.toNormalized(e.x, e.y)
                screen.setCursor(nx, ny)
                if (dragPressed) {
                    send(Proto.mouseButton(remoteX(), remoteY(), Proto.BTN_LEFT, false))
                    dragPressed = false
                } else if (!touchMoved && maxPointers == 1 && !longPressFired) {
                    val now = SystemClock.elapsedRealtime()
                    if (now - lastTapAt < 320) { doubleClick(); lastTapAt = 0 }
                    else { click(Proto.BTN_LEFT); lastTapAt = now }
                }
            }
            MotionEvent.ACTION_CANCEL -> {
                ui.removeCallbacks(longPressRunnable)
                if (dragPressed) {
                    send(Proto.mouseButton(remoteX(), remoteY(), Proto.BTN_LEFT, false))
                    dragPressed = false
                }
            }
        }
        return true
    }

    private fun avgY(e: MotionEvent): Float {
        var sum = 0f
        for (i in 0 until e.pointerCount) sum += e.getY(i)
        return sum / e.pointerCount
    }

    override fun onGenericMotionEvent(e: MotionEvent): Boolean {
        if (menuVisible || !channelOpen || !e.isFromSource(InputDevice.SOURCE_CLASS_POINTER)) {
            return super.onGenericMotionEvent(e)
        }
        when (e.actionMasked) {
            MotionEvent.ACTION_HOVER_MOVE -> {
                val (nx, ny) = screen.toNormalized(e.x, e.y)
                screen.setCursor(nx, ny)
                sendMove()
                return true
            }
            MotionEvent.ACTION_SCROLL -> {
                val v = e.getAxisValue(MotionEvent.AXIS_VSCROLL)
                if (v != 0f) wheel((v * 120).toInt())
                return true
            }
        }
        return super.onGenericMotionEvent(e)
    }

    // ----------------------------------------------------------------- menu

    private fun showMenu() {
        menuList.removeAllViews()
        fun item(label: String, keepOpen: Boolean = false, action: () -> Unit) {
            val b = Button(this)
            b.text = label
            b.isAllCaps = false
            b.textSize = 18f
            b.setTextColor(0xFFFFFFFF.toInt())
            b.setBackgroundResource(R.drawable.btn_bg)
            val lp = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(52))
            lp.setMargins(0, dp(3), 0, dp(3))
            b.layoutParams = lp
            b.setOnClickListener {
                action()
                if (!keepOpen) hideMenu()
            }
            menuList.addView(b)
        }
        // Most-needed entries first so a TV remote reaches them without scrolling.
        item(getString(R.string.menu_close)) { }
        item(getString(R.string.menu_disconnect)) { endSession(null) }
        item(getString(if (soundOn) R.string.menu_sound_off else R.string.menu_sound_on)) {
            soundOn = !soundOn
            prefs.edit().putBoolean("soundOn", soundOn).apply()
            if (!soundOn) { audioPlayer?.release(); audioPlayer = null }
            send(Proto.streamSettings(fps, quality, maxWidth, codecMode, soundOn, muteHost))
        }
        item(getString(R.string.menu_right_click)) { click(Proto.BTN_RIGHT) }
        item(getString(R.string.menu_double_click)) { doubleClick() }
        item(getString(if (scrollMode) R.string.menu_scroll_off else R.string.menu_scroll_on)) { scrollMode = !scrollMode }
        if (!isTv) item(getString(if (dragMode) R.string.menu_drag_off else R.string.menu_drag_on)) { dragMode = !dragMode }
        item(getString(R.string.menu_type)) { promptText() }
        item("Esc") { pressKey(VK.ESC) }
        item("Enter") { pressKey(VK.ENTER) }
        item("Backspace") { pressKey(VK.BACK) }
        item("Tab") { pressKey(VK.TAB) }
        item("Windows") { pressKey(VK.LWIN) }
        item("Alt + Tab") { pressKey(VK.ALT, VK.TAB) }
        item("Ctrl + C") { pressKey(VK.CTRL, 'C'.code) }
        item("Ctrl + V") { pressKey(VK.CTRL, 'V'.code) }
        item(getString(R.string.menu_quality_4k)) { unsupportedHandled = false; applyQuality(30, 80, 3840) }
        item(getString(R.string.menu_quality_hd60)) { applyQuality(60, 80, 1920) }
        item(getString(R.string.menu_quality_max)) { applyQuality(30, 80, 1920) }
        item(getString(R.string.menu_quality_balanced)) { applyQuality(30, 60, 0) }
        item(getString(R.string.menu_quality_low)) { applyQuality(20, 40, 0) }
        item(getString(if (codecMode == "tiles") R.string.menu_codec_h264 else R.string.menu_codec_tiles)) {
            setCompatibilityMode(codecMode != "tiles", auto = false)
        }
        item(getString(if (remoteCursor) R.string.menu_remote_cursor_off else R.string.menu_remote_cursor_on)) {
            remoteCursor = !remoteCursor
            prefs.edit().putBoolean("remoteCursor", remoteCursor).apply()
            send(Proto.showRemoteCursor(remoteCursor))
        }
        item(getString(if (showStats) R.string.menu_stats_off else R.string.menu_stats_on)) {
            showStats = !showStats
            prefs.edit().putBoolean("showStats", showStats).apply()
            updateStats()
        }

        heldDirs.clear()
        ui.removeCallbacks(ticker)
        menuVisible = true
        menuPanel.visibility = View.VISIBLE
        menuList.getChildAt(0)?.requestFocus()
    }

    private fun hideMenu() {
        menuVisible = false
        menuPanel.visibility = View.GONE
    }

    private fun applyQuality(f: Int, q: Int, w: Int) {
        fps = f; quality = q; maxWidth = w
        send(Proto.streamSettings(f, q, w, codecMode, soundOn, muteHost))
    }

    /** Compatibility mode: the PC sends JPEG tiles instead of H.264 (software path, works everywhere). */
    private fun setCompatibilityMode(on: Boolean, auto: Boolean) {
        codecMode = if (on) "tiles" else "h264"
        stuckSeconds = 0
        if (!on) tilesRequested = false
        send(Proto.streamSettings(fps, quality, maxWidth, codecMode, soundOn, muteHost))
        flashHint(if (auto) R.string.hint_codec_auto else if (on) R.string.hint_codec_tiles else R.string.hint_codec_h264)
    }

    /** The TV's hardware decoder cannot handle the requested size (e.g. 4K): fall back to 1080p. */
    private fun onDecoderUnsupported(w: Int, h: Int) {
        if (unsupportedHandled || ended) return
        unsupportedHandled = true
        log("decoder cannot handle ${w}x$h → 1080p")
        flashHint(R.string.err_decoder_size)
        applyQuality(30, 80, 1920)
    }

    private fun promptText() {
        hideMenu()
        val input = EditText(this)
        input.imeOptions = EditorInfo.IME_FLAG_NO_EXTRACT_UI
        input.setSingleLine(false)
        input.hint = getString(R.string.type_hint)
        menuVisible = true // keep D-pad focus navigation inside the dialog
        AlertDialog.Builder(this)
            .setTitle(R.string.menu_type)
            .setView(input)
            .setPositiveButton(R.string.send) { _, _ -> typeText(input.text.toString()) }
            .setNegativeButton(android.R.string.cancel, null)
            .setOnDismissListener { menuVisible = false; hideSystemUi() }
            .show()
    }

    private fun flashHint(res: Int) {
        hintText.text = getString(res)
        hintText.visibility = View.VISIBLE
        ui.postDelayed({ hintText.visibility = View.GONE }, 2000)
    }

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    companion object {
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_NAME = "name"
        const val EXTRA_SERVER = "server"
        const val EXTRA_CODE = "code"
        const val EXTRA_ERROR = "error"
    }
}
