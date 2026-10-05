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
    private lateinit var user: EditText
    private lateinit var pass: EditText
    private lateinit var code: EditText
    private lateinit var error: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        server = findViewById(R.id.server)
        user = findViewById(R.id.user)
        pass = findViewById(R.id.pass)
        code = findViewById(R.id.code)
        error = findViewById(R.id.error)

        val prefs = getSharedPreferences("rdtv", Context.MODE_PRIVATE)
        server.setText(prefs.getString("server", ""))
        user.setText(prefs.getString("user", ""))
        pass.setText(prefs.getString("pass", ""))

        val connect = findViewById<Button>(R.id.connect)
        connect.setOnClickListener { start() }
        code.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_DONE || actionId == EditorInfo.IME_ACTION_GO) {
                start(); true
            } else false
        }
        // Jump to the field that still needs input.
        when {
            server.text.isBlank() -> server.requestFocus()
            user.text.isBlank() -> user.requestFocus()
            pass.text.isBlank() -> pass.requestFocus()
            else -> code.requestFocus()
        }
    }

    private fun start() {
        val s = server.text.toString().trim()
        val u = user.text.toString().trim()
        val p = pass.text.toString()
        val c = code.text.toString().trim()
        if (s.isEmpty() || u.isEmpty() || p.isEmpty() || c.isEmpty()) {
            error.text = getString(R.string.err_fill_all)
            return
        }
        error.text = ""
        getSharedPreferences("rdtv", Context.MODE_PRIVATE).edit()
            .putString("server", s).putString("user", u).putString("pass", p).apply()
        val i = Intent(this, SessionActivity::class.java)
            .putExtra(SessionActivity.EXTRA_SERVER, s)
            .putExtra(SessionActivity.EXTRA_USER, u)
            .putExtra(SessionActivity.EXTRA_PASS, p)
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
