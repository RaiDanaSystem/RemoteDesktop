package com.raidana.rdtv

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.wifi.WifiManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.view.inputmethod.EditorInfo
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import com.raidana.rdtv.net.DirectLink
import com.raidana.rdtv.net.LanDiscovery

/**
 * Start screen. Primary path: computers on the local network running the Windows app are listed
 * automatically and connected to directly (no server). A server/support-code path is kept below.
 */
class MainActivity : Activity() {

    private lateinit var server: EditText
    private lateinit var code: EditText
    private lateinit var manual: EditText
    private lateinit var error: TextView
    private lateinit var lanList: LinearLayout
    private lateinit var lanNone: TextView

    private val ui = Handler(Looper.getMainLooper())
    private var discovery: LanDiscovery? = null
    private var multicastLock: WifiManager.MulticastLock? = null
    private var shownKey = ""

    private val refreshTick = object : Runnable {
        override fun run() {
            refreshPeers()
            ui.postDelayed(this, 1500)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        server = findViewById(R.id.server)
        code = findViewById(R.id.code)
        manual = findViewById(R.id.manual)
        error = findViewById(R.id.error)
        lanList = findViewById(R.id.lan_list)
        lanNone = findViewById(R.id.lan_none)

        val prefs = getSharedPreferences("rdtv", Context.MODE_PRIVATE)
        server.setText(prefs.getString("server", ""))
        manual.setText(prefs.getString("manual", ""))

        findViewById<Button>(R.id.connect).setOnClickListener { startServer() }
        findViewById<Button>(R.id.manual_connect).setOnClickListener { startManual() }
        findViewById<Button>(R.id.lan_refresh).setOnClickListener { discovery?.probe(); refreshPeers() }
        code.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_DONE || actionId == EditorInfo.IME_ACTION_GO) { startServer(); true } else false
        }
        manual.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_DONE || actionId == EditorInfo.IME_ACTION_GO) { startManual(); true } else false
        }

        // Phones: full-width column; TV: fixed comfortable width.
        val form = findViewById<View>(R.id.form)
        val screenDp = resources.displayMetrics.widthPixels / resources.displayMetrics.density
        val widthDp = minOf(480f, screenDp - 32f)
        form.layoutParams = form.layoutParams.apply { width = (widthDp * resources.displayMetrics.density).toInt() }
    }

    override fun onResume() {
        super.onResume()
        try {
            val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            multicastLock = wm.createMulticastLock("rdtv:lan").apply { setReferenceCounted(false); acquire() }
        } catch (_: Exception) {
        }
        val prefs = getSharedPreferences("rdtv", Context.MODE_PRIVATE)
        val id = prefs.getString("viewerId", null)
            ?: java.util.UUID.randomUUID().toString().replace("-", "").also { prefs.edit().putString("viewerId", it).apply() }
        val d = LanDiscovery(id)
        d.onChanged = { ui.post { refreshPeers() } }
        d.start()
        discovery = d
        shownKey = ""
        ui.post(refreshTick)
    }

    override fun onPause() {
        super.onPause()
        ui.removeCallbacks(refreshTick)
        discovery?.stop()
        discovery = null
        try { multicastLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
    }

    private fun refreshPeers() {
        val peers = discovery?.peers() ?: emptyList()
        val key = peers.joinToString("|") { it.id + it.address + it.port + it.name }
        if (key == shownKey) return
        shownKey = key
        lanList.removeAllViews()
        lanNone.visibility = if (peers.isEmpty()) View.VISIBLE else View.GONE
        var first = true
        for (p in peers) {
            val b = Button(this)
            b.isAllCaps = false
            b.text = "${p.name}\n${p.platform} · ${p.address}"
            b.textSize = 18f
            b.setTextColor(0xFFFFFFFF.toInt())
            b.setBackgroundResource(R.drawable.btn_bg)
            b.minimumHeight = (64 * resources.displayMetrics.density).toInt()
            val lp = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
            lp.setMargins(0, 0, 0, (8 * resources.displayMetrics.density).toInt())
            b.layoutParams = lp
            b.setOnClickListener { startDirect(p.address, p.port, p.name) }
            lanList.addView(b)
            if (first && manual.text.isBlank() && server.text.isBlank()) { b.requestFocus(); first = false }
        }
    }

    private fun startDirect(host: String, port: Int, name: String) {
        error.text = ""
        val i = Intent(this, SessionActivity::class.java)
            .putExtra(SessionActivity.EXTRA_HOST, host)
            .putExtra(SessionActivity.EXTRA_PORT, port)
            .putExtra(SessionActivity.EXTRA_NAME, name)
        @Suppress("DEPRECATION")
        startActivityForResult(i, 1)
    }

    private fun startManual() {
        val text = manual.text.toString().trim()
        if (text.isEmpty()) { error.text = getString(R.string.err_fill_all); return }
        getSharedPreferences("rdtv", Context.MODE_PRIVATE).edit().putString("manual", text).apply()
        var host = text.removePrefix("http://").removePrefix("tcp://").trimEnd('/')
        var port = DirectLink.DEFAULT_PORT
        val idx = host.lastIndexOf(':')
        if (idx > 0 && host.indexOf(':') == idx) {
            host.substring(idx + 1).toIntOrNull()?.let { port = it; host = host.substring(0, idx) }
        }
        startDirect(host, port, host)
    }

    private fun startServer() {
        val s = server.text.toString().trim()
        val c = code.text.toString().trim()
        if (s.isEmpty() || c.isEmpty()) {
            error.text = getString(R.string.err_fill_all)
            return
        }
        error.text = ""
        getSharedPreferences("rdtv", Context.MODE_PRIVATE).edit().putString("server", s).apply()
        val i = Intent(this, SessionActivity::class.java)
            .putExtra(SessionActivity.EXTRA_SERVER, s)
            .putExtra(SessionActivity.EXTRA_CODE, c)
        @Suppress("DEPRECATION")
        startActivityForResult(i, 1)
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        error.text = data?.getStringExtra(SessionActivity.EXTRA_ERROR).orEmpty()
        code.setText("")
    }
}
