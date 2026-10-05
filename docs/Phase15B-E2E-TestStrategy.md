# Phase 15B — E2E Test Strategy

## Objective
Verify the complete remote desktop flow:
```
SupportAgent → SignalR → Server → SignalR → CustomerAgent → WebRTC DataChannel → Screen/Input/Clipboard
```

## Test Environment

### Machines
- **Machine A**: Server + SupportAgent
- **Machine B**: CustomerAgent
- **Network**: LAN or Docker network for local testing

### Processes
1. `Server.Api` — ASP.NET Core Web API + SignalR
2. `CustomerAgent` — WPF app (customer side)
3. `SupportAgent` — WPF app (agent side)
4. `coturn` — TURN server (optional, Docker)

### Test Topology
```
┌─────────────────┐      SignalR       ┌─────────────────┐
│   SupportAgent  │ ←────────────────→ │     Server      │
│   (Machine A)   │      SignalR       │   (Machine A)   │
└────────┬────────┘                    └────────┬────────┘
         │                                      │
         │         WebRTC DataChannel           │
         └──────────────────────────────────────┘
                          │
                   ┌──────┴──────┐
                   │ CustomerAgent│
                   │ (Machine B)  │
                   └─────────────┘
```

## Prerequisites

### Setup
```bash
# 1. Start Server
cd src/Server/Api
dotnet run

# 2. Start Coturn (optional, for TURN test)
cd infra/turn
docker-compose up -d

# 3. Start CustomerAgent
cd src/CustomerAgent
dotnet run

# 4. Start SupportAgent
cd src/SupportAgent
dotnet run
```

### Credentials
- Admin: `admin` / `Admin@12345`
- Customer: Auto-generated device token

## Manual E2E Test Procedure

### Test 1: SignalR Signaling
**Steps**:
1. Start Server → verify it listens on http://localhost:5096
2. Start CustomerAgent → Generate support code
3. Start SupportAgent → Login with admin credentials
4. Enter customer support code → Request connection
5. CustomerAgent shows connection request → Accept

**Expected**:
- SignalR connection established on both sides
- Session created and activated in server

### Test 2: WebRTC Connection
**Steps**:
1. After acceptance, SupportAgent initiates WebRTC
2. Observe log messages on both sides

**Expected**:
- SDP offer created by SupportAgent
- SDP answer created by CustomerAgent
- ICE candidates exchanged
- WebRTC state: Connected
- DataChannel state: Open

### Test 3: Screen Streaming
**Steps**:
1. After connection, CustomerAgent screen is captured
2. Frames travel over DataChannel
3. SupportAgent displays remote screen

**Expected**:
- SupportAgent shows CustomerAgent's screen
- FPS > 5 (minimum viable)
- Resolution matches remote display
- Aspect ratio preserved

### Test 4: Mouse Input
**Steps**:
1. Move mouse over SupportAgent display
2. Click left button
3. Click right button

**Expected**:
- Mouse moves on CustomerAgent
- Left click triggers action on CustomerAgent
- Right click triggers action on CustomerAgent

### Test 5: Input Consent
**Steps**:
1. CustomerAgent revokes input consent
2. SupportAgent tries to send mouse input
3. CustomerAgent re-grants consent
4. SupportAgent tries again

**Expected**:
- Input rejected when consent revoked
- Input accepted when consent granted

### Test 6: Clipboard Sync
**Steps**:
1. CustomerAgent copies text → SupportAgent receives
2. SupportAgent copies text → CustomerAgent receives

**Expected**:
- Text syncs both directions
- No echo loop

### Test 7: Disconnect
**Steps**:
1. SupportAgent clicks Disconnect
2. Verify cleanup on both sides

**Expected**:
- WebRTC connection closed
- DataChannel closed
- Screen capture stopped
- Resources disposed

### Test 8: TURN Fallback (if available)
**Steps**:
1. Configure Coturn TURN server
2. Block direct UDP P2P (firewall rule)
3. Run connection test

**Expected**:
- Connection succeeds via TURN relay
- TURN server shows active allocation

## Automated Tests

### Unit Tests
- `CoordinateMapperTests` — coordinate transformation
- `ScreenFrameTransportTests` — screen frame serialization
- `InputTransportTests` — input event serialization
- `ClipboardTransportTests` — clipboard message serialization
- `ConsentEnforcementTests` — consent validation/rejection
- `ControlMessageTests` — control message serialization
- `WebRtcConfigurationWithTurnTests` — TURN configuration

### Integration Tests
Run existing tests:
```bash
cd tests/Server.Tests
dotnet test
```

## Test Results Template

| Test | Status | Evidence |
|------|--------|----------|
| SignalR Signaling | PASS/FAIL | Logs, connection state |
| WebRTC Connection | PASS/FAIL | PeerConnection state |
| Screen Streaming | PASS/FAIL | FPS, resolution |
| Mouse Input | PASS/FAIL | Action on remote |
| Input Consent | PASS/FAIL | Rejection/acceptance |
| Clipboard Sync | PASS/FAIL | Text sync |
| Disconnect | PASS/FAIL | Cleanup verification |
| TURN Fallback | PASS/FAIL | Relay allocation |

## Known Limitations
- WPF E2E automation requires UI automation framework (e.g., FlaUI, Appium)
- Mouse input verification requires observing remote screen
- TURN test requires separate network or firewall manipulation
- No automated reconnection testing in this phase
