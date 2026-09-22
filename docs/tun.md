

> Tunnels: a VPN (TOR V2Ray WireGaurd DeeperNet) like tunnel which is a local port forwarder in which client packets will be sent to VPS then to destination, a ligolo and chisel like tunnel which is a reverse port forwarder in which client connects to VPS and tell I have this port; forward any packets from outside through yourself to my port. With SOCKS5 (for either local or reverse port forward) proxy we can add dynamic routing instead of exposing a fixed port. 

## 1. SSH Tunnel Commands (SSH (port 22) + SSH Ed25519 Encryption)

```bash
# ── LOCAL PORT FORWARD (laptop → VPS → internal host) ──
# Forward laptop:8080 to 10.0.0.5:80 through VPS
ssh -L 8080:10.0.0.5:80 user@45.94.215.65

# Forward a Windows RDP
ssh -L 3389:10.0.0.5:3389 user@45.94.215.65

# ── REVERSE PORT FORWARD (VPS → tunnel → laptop/target) ──
# VPS listens on 2222 → forwards to target's localhost:4444
ssh -R 2222:localhost:4444 user@45.94.215.65

# Expose a web service from your laptop on the VPS
ssh -R 8080:localhost:3000 user@45.94.215.65

# ── SOCKS5 PROXY (dynamic, local side) ──
# Laptop gets SOCKS5 at localhost:1080, traffic exits from VPS
ssh -D 1080 user@45.94.215.65

# ── REVERSE SOCKS5 (into target network) — workaround ──
# Step 1: on VPS run a SOCKS5 server listening on localhost:1080
#         (e.g. microsocks/dante/socks5 server)
# Step 2: on target, forward that back through tunnel
ssh -R 1080:localhost:1080 user@45.94.215.65

# ── COMBINED / PRODUCTION ──
# Silent, no shell, auto-keepalive
ssh -N -D 1080 -o ServerAliveInterval=60 -o ServerAliveCountMax=3 user@45.94.215.65

# On custom port (443) to dodge DPI
ssh -N -D 1080 -p 443 user@45.94.215.65

# With key auth, background (autossh for auto-reconnect)
autossh -M 0 -N -D 1080 user@45.94.215.65
```

---

## 2. Chisel Tunnel Commands (HTTP/WebSocket + TLS optional (default off))

```bash
# ── SERVER (VPS) ──
chisel server --port 8081 --reverse

# With auth
chisel server --port 8081 --reverse --auth user:pass

# With TLS (looks like HTTPS, dodges DPI)
chisel server --port 443 --reverse --tls-cert cert.pem --tls-key key.pem

# ── CLIENT (TARGET) — LOCAL PORT FORWARD ──
# Forward VPS:8080 → target reaches 10.0.0.5:80 (from target's network)
chisel client 45.94.215.65:8081 L:8080:10.0.0.5:80

# ── CLIENT — REVERSE PORT FORWARD ──
# VPS listens on 2222 → forwards to target's localhost:4444
chisel client 45.94.215.65:8081 R:2222:localhost:4444

# ── CLIENT — SOCKS5 (server-side proxy) ──
# Operator connects to VPS:1080, traffic routes through target
chisel client 45.94.215.65:8081 1080:socks5

# ── CLIENT — REVERSE SOCKS5 ──
# Same as above, VPS-side SOCKS into target's network
chisel client 45.94.215.65:8081 R:socks

# ── SERVER — REVERSE SOCKS5 (full example) ──
# VPS exposes a SOCKS5 door on :1080; the operator routes through the target's
# network via the reverse tunnel (the target dials out, so no inbound ports)
#
# 1) Server (VPS):
chisel server --reverse --port 8081 --socks5
#    --reverse  allow reverse (client-initiated) tunnels
#    --socks5   expose the SOCKS5 proxy door on the server side
#    (--port 443 --tls-cert cert.pem --tls-key key.pem → HTTPS masquerade, DPI-resistant)
#
# 2) Client (target / foothold):
chisel client 45.94.215.65:8081 R:socks
#    R:socks      reverse SOCKS5 door; defaults to :1080 on the server
#    R:1080:socks pin the door port explicitly
#
# 3) Operator (laptop) — through the VPS door into the LAN:
ncat.exe --proxy 45.94.215.65:1080 --proxy-type socks5 10.0.0.5 3389
ncat.exe --proxy 45.94.215.65:1080 --proxy-type socks5 127.0.0.1 52341
# Linux (proxychains → socks5 45.94.215.65 1080):
proxychains4 nc 10.0.0.5 445

# ── CLIENT — MULTIPLE TUNNELS IN ONE ──
chisel client 45.94.215.65:8081 R:2222:localhost:4444 1080:socks5 R:socks

# ── CLIENT — RESTART / PERSISTENT ──
# Auto-reconnect forever
while true; do chisel client 45.94.215.65:8081 R:2222:localhost:4444; sleep 5; done
```