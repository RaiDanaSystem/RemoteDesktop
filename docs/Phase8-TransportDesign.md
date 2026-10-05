# Phase 8: Secure P2P Transport & Relay Architecture

## Overview

This phase implements the transport layer for real-time data exchange between Support Agent and Customer Agent. The transport must be secure, reliable, and efficient.

## Architecture Decision

### Why Not Full WebRTC?

Full WebRTC (via libwebrtc or WebView2) requires:
- Native binary dependencies (libwebrtc ~100MB)
- Complex interop in WPF
- Browser context for WebView2 WebRTC

### Chosen Approach: Custom Secure Transport

A lightweight, purpose-built transport using:
1. **UDP hole-punching** for direct P2P (NAT traversal via STUN)
2. **TCP relay server** as fallback (TURN-like)
3. **AES-256-GCM** for transport encryption
4. **Session-bound tokens** for authentication
5. **SignalR** for signaling (already implemented)

### Why This Works

- Lightweight (~50KB transport code vs ~100MB WebRTC)
- Full control over protocol
- Session-bound security (no external dependencies)
- Deterministic behavior (no browser quirks)
- Works in any .NET environment

## Components

### 1. TransportManager (Server-side)
- Issues session-bound transport tokens
- Validates token authenticity
- Tracks connection state
- Manages relay assignment

### 2. PeerConnection (Client-side)
- UDP socket management
- STUN binding for NAT detection
- Hole-punching for P2P
- AES-GCM encryption
- Connection state machine

### 3. RelayConnection (Server-side/Client-side)
- TCP relay server (in Api process)
- Bidirectional data forwarding
- Session-scoped isolation
- Bandwidth monitoring

### 4. ConnectionDiagnostics
- Connection type (P2P/Relay)
- State tracking
- Latency/RTT measurement
- Bytes sent/received
- Reconnect count

## Protocol

### Signaling (via SignalR)
1. Agent requests transport token from server
2. Server validates session, issues encrypted token
3. Agent sends token to customer via SignalR
4. Both agents attempt P2P (UDP hole-punch)
5. If P2P fails, both connect to relay server
6. Relay validates tokens, bridges connections

### Transport Packet Format
```
[4 bytes: packet_type] [4 bytes: sequence] [16 bytes: nonce] [N bytes: encrypted_payload] [16 bytes: auth_tag]
```

### Encryption
- Key derivation: HKDF from session-bound secret
- Algorithm: AES-256-GCM
- Nonce: Counter-based (per-packet)
- Auth tag: GCM authentication tag

## Security Model

### Session Binding
- Transport tokens are cryptographically bound to session ID
- Token = HMAC(session_id + agent_id + expiry + nonce, server_secret)
- Server validates token before allowing relay connection

### No Long-Lived Secrets
- Customer Agent receives short-lived transport tokens
- Tokens expire with session
- No permanent keys in Customer Agent

### Replay Protection
- Sequence numbers monotonically increase
- Nonce includes timestamp
- Server rejects out-of-order or expired packets
