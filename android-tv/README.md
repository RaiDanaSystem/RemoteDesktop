# Remote Desktop TV (Android TV client)

Android TV / Android viewer for the Windows **Remote Support** system in this repository.
It plays the role of the *SupportAgent* (the WPF viewer): it signs in to the server, connects
to a PC by its support code, negotiates a WebRTC data channel and shows the PC's screen on the TV
with hardware H.264 decoding (JPEG-tile fallback), while sending mouse / keyboard input back.

## Usage

1. Start the server (`src/Server/Api`) and the **CustomerAgent** on the PC; note the support code.
2. In the PC's CustomerAgent, make sure remote input is allowed if you want to control the PC.
3. On the TV open **Remote Desktop TV** and enter:
   server address (e.g. `192.168.1.10:5096`), a SupportAgent/Admin account, and the support code.
4. Accept the connection on the PC. The desktop appears on the TV.

### Remote control

| Key | Action |
| --- | --- |
| D-pad | Move the cursor (accelerates while held) |
| OK / Enter | Left click (hold = drag) |
| Back / Menu / Blue / Info | Open the menu (right click, double click, keys, text, quality, disconnect) |
| Red / Play-Pause | Right click |
| Green | Toggle scroll mode (Up/Down scroll the page) |
| Yellow | Type text on the PC (works with Persian too) |
| CH+/CH− / Page Up/Down / FF/REW | Scroll |

USB/Bluetooth keyboards and mice work as well (a keyboard is forwarded to the PC as typing).

## Build

```
cd android-tv
./gradlew assembleRelease     # → app/build/outputs/apk/release/app-release.apk
```

GitHub Actions (`.github/workflows/android-tv.yml`) builds it on every push touching `android-tv/`
and stores the result in `android-tv/dist/RemoteDesktopTV.apk` and as a workflow artifact.

## Protocol notes

Compatible with the existing Windows agents without any server/agent change:
REST (`/api/v1/auth/login`, `/api/v1/supportagent/connect|session/{id}/status|terminate`),
SignalR `/hubs/webrtc` (`RequestOffer`, `SubmitOffer`, `SubmitIceCandidate`, …) and the
`TransportEnvelope` / `ScreenFrameTransport` / `RDH2` (H.264) / `RDRT` (JPEG tiles) formats from `src/Shared`.
