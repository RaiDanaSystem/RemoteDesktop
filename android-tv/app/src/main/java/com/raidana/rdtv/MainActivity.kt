package com.raidana.rdtv

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.inputmethod.EditorInfo
import android.widget.Button
import android.widget.EditText
import android.widget.TextView

/** Connect screen: server address, account and the support code shown on the Windows PC. */
class MainActivity : Activity() {

    private lateinit var server: EditText
    private lateinit var code: EditText
    private lateinit var error: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        server = findViewById(R.id.server)
        code = findViewById(R.id.code)
        error = findViewById(R.id.error)

        val prefs = getSharedPreferences("rdtv", Context.MODE_PRIVATE)
        server.setText(prefs.getString("server", ""))

        val connect = findViewById<Button>(R.id.connect)
        connect.setOnClickListener { start() }
        code.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_DONE || actionId == EditorInfo.IME_ACTION_GO) {
                start(); true
            } else false
        }
        // Jump to the field that still needs input.
        if (server.text.isBlank()) server.requestFocus() else code.requestFocus()

        // Phones: form fills the width; TV: fixed comfortable column.
        val form = findViewById<android.view.View>(R.id.form)
        val screenDp = resources.displayMetrics.widthPixels / resources.displayMetrics.density
        val widthDp = minOf(480f, screenDp - 32f)
        form.layoutParams = form.layoutParams.apply { width = (widthDp * resources.displayMetrics.density).toInt() }
    }

    private fun start() {
        val s = server.text.toString().trim()
        val c = code.text.toString().trim()
        if (s.isEmpty() || c.isEmpty()) {
            error.text = getString(R.string.err_fill_all)
            return
        }
        error.text = ""
        getSharedPreferences("rdtv", Context.MODE_PRIVATE).edit()
            .putString("server", s).apply()
        val i = Intent(this, SessionActivity::class.java)
            .putExtra(SessionActivity.EXTRA_SERVER, s)
            .putExtra(SessionActivity.EXTRA_CODE, c)
        @Suppress("DEPRECATION")
        startActivityForResult(i, 1)
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        val msg = data?.getStringExtra(SessionActivity.EXTRA_ERROR).orEmpty()
        error.text = msg
        code.setText("")
        code.requestFocus()
    }
}
