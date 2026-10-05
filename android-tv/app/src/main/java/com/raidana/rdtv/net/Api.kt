package com.raidana.rdtv.net

import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.util.concurrent.TimeUnit

class ApiException(message: String) : Exception(message)

/** Minimal REST client for the Remote Support server (support-agent flow). All calls block. */
class Api(rawServerUrl: String) {
    val baseUrl: String = normalize(rawServerUrl)

    private val http = OkHttpClient.Builder()
        .connectTimeout(6, TimeUnit.SECONDS)
        .readTimeout(10, TimeUnit.SECONDS)
        .writeTimeout(10, TimeUnit.SECONDS)
        .build()

    var accessToken: String = ""
        private set
    private var refreshToken: String = ""

    class Connected(val sessionId: String, val deviceName: String)

    fun login(username: String, password: String) {
        val body = JSONObject().put("Username", username).put("Password", password)
        val json = post("/api/v1/auth/login", body, auth = false)
        val user = json.optJSONObject("user")
        val role = user?.optString("role").orEmpty()
        if (role != "SupportAgent" && role != "Admin") {
            throw ApiException("This account ($role) cannot start remote sessions.")
        }
        accessToken = json.getJSONObject("accessToken").getString("token")
        refreshToken = json.optJSONObject("refreshToken")?.optString("token").orEmpty()
    }

    fun connect(supportCode: String): Connected {
        val trimmed = supportCode.trim()
        val digits = trimmed.filter { it.isDigit() }
        val code = if (digits.isNotEmpty()) digits else trimmed.uppercase()
        val json = post("/api/v1/supportagent/connect", JSONObject().put("SupportCode", code))
        return Connected(json.getString("sessionId"), json.optString("customerDeviceName", "PC"))
    }

    /** Returns the session status string: Pending, Active, Ended, Failed (or null when unknown). */
    fun sessionStatus(sessionId: String): Pair<String, String?>? {
        return try {
            val json = get("/api/v1/supportagent/session/$sessionId/status")
            json.optString("status") to json.optString("endReason").ifEmpty { null }
        } catch (e: ApiException) {
            null
        }
    }

    fun terminate(sessionId: String) {
        try {
            post("/api/v1/supportagent/session/$sessionId/terminate", JSONObject().put("Reason", "Android TV viewer disconnected"))
        } catch (_: Exception) {
        }
    }

    private fun refresh(): Boolean {
        if (refreshToken.isEmpty()) return false
        return try {
            val json = post("/api/v1/auth/refresh", JSONObject().put("RefreshToken", refreshToken), auth = false, retry = false)
            accessToken = json.getJSONObject("accessToken").getString("token")
            refreshToken = json.optJSONObject("refreshToken")?.optString("token").orEmpty()
            true
        } catch (e: Exception) {
            false
        }
    }

    private fun get(path: String, retry: Boolean = true): JSONObject {
        val req = Request.Builder().url(baseUrl + path).header("Authorization", "Bearer $accessToken").build()
        return execute(req, retry) { get(path, false) }
    }

    private fun post(path: String, body: JSONObject, auth: Boolean = true, retry: Boolean = true): JSONObject {
        val rb = body.toString().toRequestBody("application/json".toMediaType())
        val b = Request.Builder().url(baseUrl + path).post(rb)
        if (auth) b.header("Authorization", "Bearer $accessToken")
        return execute(b.build(), auth && retry) { post(path, body, auth, false) }
    }

    private fun execute(req: Request, canRetry: Boolean, again: () -> JSONObject): JSONObject {
        try {
            http.newCall(req).execute().use { resp ->
                val text = resp.body?.string().orEmpty()
                if (resp.code == 401 && canRetry && refresh()) return again()
                if (!resp.isSuccessful) {
                    val msg = try {
                        JSONObject(text).optString("error").ifEmpty { null }
                    } catch (_: Exception) {
                        null
                    }
                    throw ApiException(msg ?: if (resp.code == 401) "Wrong username or password." else "Server error (${resp.code}).")
                }
                return if (text.isBlank()) JSONObject() else JSONObject(text)
            }
        } catch (e: ApiException) {
            throw e
        } catch (e: java.io.IOException) {
            throw ApiException("Cannot reach the server at $baseUrl")
        }
    }

    companion object {
        fun normalize(raw: String): String {
            var s = raw.trim().trimEnd('/')
            if (s.isEmpty()) return s
            // Use exactly what the user typed; only add a scheme when it is missing.
            if (!s.startsWith("http://") && !s.startsWith("https://")) s = "http://$s"
            return s
        }
    }
}
