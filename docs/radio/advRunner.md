

## TODOs:

- access encrypted bankd and news data, bank db to transfer money 

- code: threadpool, pointer arc mutex, pubsub socket over mudp, zmq, rmq, redis, p2p, ws, grpc, rpc

- filter all satellite packets to get everything below than 40GHz like internet and other device data

### stallite and BTS GSM setup

- setup a BTS and GSM tower on raspi using bladeRF with openBTS/YateBTS panel supports programmable sim or no sim

- from my phone i can call or send sms through my portable BTS with no caller id

- setup a sattelite receiver using Dish LNB and bladeRF to receive up to 40GHz radio signal packets (net, tv, news, weather, hospital devices and ...) and handle downlink process

- connect starlink to raspi to handle uplink process

- filter received satellite packets in raspi to get net data

- bridge between uplink and downlink using iptables to setup a full satellite internet process 

```
                           ┌──────────────────────────────┐
                           │          SATELLITE           │
                           └───────────────▲──────────────┘
                                           │  (RX only)
                                 Downlink  │
                                           │
                ┌──────────────────────────┴──────────────────────────┐
                │        Dish + LNB  →  SDR (BladeRF / DVB-S2)        │
                │                 → Demod + IP Extractor              │
                │           (GNU Radio / leandvb / TSDuck)            │
                └───────────────────▲─────────────────────────────────┘
                                    │  IP (RX stream)
                                    │
                   ┌────────────────┴────────────────┐
                   │   Raspberry Pi (Router + Core)  │
                   │  • Policy routing:              │
                   │     - Uplink → Starlink         │
                   │     - Downlink → Satellite RX   │
                   │  • GSM Core (OpenBTS/YateBTS +  │
                   │    OsmoNITB/YateUCN)            │
                   └───────────▲───────────▲─────────┘
                               │           │
                     Local GSM │           │ Internet uplink (TX)
                     Calls/SMS │           │
                               │           │
                  ┌────────────┘           └────────────────┐
                  │                                         │
        ┌─────────┴──────────┐                     ┌────────┴─────────┐
        │  BladeRF (GSM BTS) │                     │  Starlink (or    │
        │  + BTS Antenna     │                     │  4G/LTE router)  │
        └─────────▲──────────┘                     └────────▲─────────┘
                  │  GSM air interface                      │ uplink
                  │                                         │
            Phones (programmable SIM)                  Internet (TX)


---------------------------------------------------------------------------------

                 ┌──────────────┐
                 │   Test Phone │ (SIM or no SIM if using your own BTS)
                 └───────┬──────┘
                         │
                  ┌──────▼──────────────────────────────┐
                  │   BTS (BladeRF + OpenBTS/YateBTS)   │
                  └──────┬──────────────────────────────┘
                         │
                   ┌─────▼───────┐
                   │ Raspberry Pi│  — NAT gateway —→ Starlink Uplink → Internet
                   └─────┬───────┘
                         │
   ┌─────────────────────▼──────────────────────┐
   │       Downlink RX Chain (BladeRF)          │
   │ Dish + LNB → BladeRF → GNU Radio → TSDuck  │
   │ → Wireshark (view your own packets)        │
   └────────────────────────────────────────────┘


```

1. Generate a **DVB-S2 test transport stream (TS)** in your lab (TX).
2. Receive that stream with **BladeRF** (RX) using **GNU Radio** (or `leandvb`) and produce a TS file.
3. Extract IP packets from the TS with **TSDuck** and view them in **Wireshark**.
4. Put the host on the Internet via **Starlink** and use simple **policy routing + NAT (iptables/iproute2)** so your lab machines use Starlink as the uplink.

**Important safety note (read)**
All transmit steps below are written for a *closed lab loopback or shielded RF box only*. **Do not transmit on real satellite frequencies in the wild.** Use coax loopbacks + lots of attenuation, or use RF shield / Faraday box. The instructions assume you will only run TX inside a contained test environment.

---

## Hardware summary (minimum)

> BladeRF, LimeSDR, HackRF, Airspy, LNB, Dish, DVB-S2 (sat receiver)

* BladeRF (x40/x115) — preferred (full duplex; can TX/RX)
* PC or Raspberry Pi 4/5 (quad-core recommended)
* Short coax jumper cables, directional coupler or attenuators (>=60 dB) for loopback
* (Optional) DVB-S2 USB tuner (as alternative RX front-end)
* Starlink terminal (or any authorized internet uplink)
* Ethernet cables, small switch

---

## Software to install (Debian/Ubuntu/Raspbian)

```bash
# basics
sudo apt update
sudo apt install -y git build-essential cmake python3-pip \
  gnuradio gr-osmosdr soapyremote-soapysdr soapysdr-module-bladeRF \
  bladeRF-cli wireshark tsduck leandvb

# (optional) install gr-dvbs2 if you want GNU Radio DVB-S2 blocks
# you might need to build gr-dvbs2 from source — example:
git clone https://github.com/oobillion/gr-dvbs2.git
cd gr-dvbs2
mkdir build && cd build
cmake .. && make -j$(nproc) && sudo make install && sudo ldconfig
```

> If building gr-dvbs2 is hard, we’ll provide alternative steps using `leandvb` (which works with IQ input files or `rtl_tcp` style streams).

---

# Part A — Create a DVB-S2 test stream (TX) — lab transmitter

We will produce an MPEG-TS containing IP packets (e.g., UDP multicast from a test source) and modulate it to DVB-S2 using GNU Radio and gr-dvbs2 modulator. Then loop that RF back into the BladeRF RX via coax + attenuator (no over-the-air).

### 1) Prepare a sample IP source (multicast UDP)

On your PC run a simple UDP packet generator (this will be encapsulated inside the TS):

```bash
# Create a test pcap or generate continuous UDP multicast
# Example: use socat to send repeating UDP packets to multicast 239.1.1.1:5000
while true; do echo "Hello DVB-S2 $(date) " | socat - UDP-DATAGRAM:239.1.1.1:5000,ip-multicast-if=127.0.0.1; sleep 0.1; done
```

Or create a PCAP with many ip packets to stream later.

### 2) Create an MPEG-TS from IP (use ffmpeg or GStreamer)

Example using `ffmpeg` to packetize a UDP stream into an MPEG-TS file:

```bash
# Capture for 60s and write to transport stream (this encapsulates UDP into TS)
ffmpeg -f mpegts -i udp://239.1.1.1:5000 -c copy -t 60 test_stream.ts
```

Alternatively you can construct arbitrary TS using `tsduck` tools.

### 3) Modulate TS to DVB-S2 (GNU Radio flowgraph)

Use GNU Radio Companion (GRC). High-level blocks:

* File Source -> Packetizer (if needed) -> `gr_dvbs2.modulator` block (or custom QPSK/8PSK modulator) -> Rational Resampler -> Osmocom Sink (BladeRF sink).

Flow details:

* Input: `test_stream.ts` (file source or stream)
* Modulate: DVB-S2 (choose QPSK/8PSK, FEC 3/4)
* Sample rate: choose what BladeRF accepts (e.g., 20 Msps)
* BladeRF sink: set center frequency to an IF inside BladeRF range (e.g., 1.5 GHz) — **but do not radiate**. Use attenuator to route to RX.

Example GRC parameters (conceptual):

* Modulation: `8PSK`, FEC `3/4`, SR (symbol rate) = `20e6`
* Carrier / RF: center frequency = `1.5e9` (use loopback — not real satellite)

After building the flowgraph, run it to transmit into a coax loopback (TX port → attenuator → RX port).

**Alternative (simpler)**: use `leandvb` modulator (if available) or `gr-dvbs2` command-line modulator.

---

# Part B — Receive DVB-S2 with BladeRF and demodulate to TS

Two approaches: (1) GNU Radio + gr-dvbs2 demod block, or (2) BladeRF IQ dump -> `leandvb`.

### Option 1 — GNU Radio demod (recommended if gr-dvbs2 installed)

Create a GRC flowgraph with:

* Osmocom Source (bladeRF) — center frequency = the TX IF you used; sample rate match.
* Frequency/Xlating FIR (to shift to carrier)
* AGC / PLL carrier recovery
* `gr_dvbs2.demodulator` block (choose same modulation/fec)
* TS output file sink (stream.ts)

Run the flowgraph; you will get `stream.ts` (MPEG-TS).

### Option 2 — IQ dump -> leandvb

1. Capture IQ from BladeRF to file:

```bash
# Use bladeRF-cli or soapySDR tools to capture IQ. Example using bladeRF-cli
bladeRF-cli -s 20e6 -f 1500000000 -d -r captured.iq
# or using SoapySDR + gr-osmosdr scripts to create an IQ file
```

2. Use `leandvb` to demodulate the IQ file to TS:

```bash
leandvb -i captured.iq --s2 --sr 20000000 --cr 3/4 --frames -o stream.ts
```

(Replace `sr` `cr` parameters to match the modulator.)

---

# Part C — Extract IP from TS with TSDuck and view in Wireshark

Once you have `stream.ts`:

```bash
# Inspect TS
tsanalyze stream.ts

# Extract IP packets into pcap (TSDuck 'ip' plugin extracts IP encapsulated in TS)
tsp -I file stream.ts -P ip -O pcap ip_from_sat.pcap

# Alternatively, directly stream to wireshark:
tsp -I file stream.ts -P ip -O wireshark
```

Open `ip_from_sat.pcap` in Wireshark:

```bash
wireshark ip_from_sat.pcap
```

You should see the UDP/IPv4 packets you generated earlier (239.1.1.1:5000 content).

---

# Part D — Integrate with Raspberry Pi routing & Starlink uplink

Assume the Pi has:

* `eth0` → Starlink (WAN), IP `192.168.100.2`, gateway `192.168.100.1`
* `wlan0` → local LAN (phones/PCs) `10.10.10.1`

We want outbound small requests to go via Starlink; downlink packets extracted from TS we will inject into the local network as needed.

### 1) Basic NAT + IP forward

```bash
# enable ip forwarding
sudo sysctl -w net.ipv4.ip_forward=1

# NAT outgoing to Starlink
sudo iptables -t nat -A POSTROUTING -o eth0 -j MASQUERADE

# Allow forwarding (LAN -> Starlink)
sudo iptables -A FORWARD -i wlan0 -o eth0 -m state --state NEW,ESTABLISHED,RELATED -j ACCEPT
sudo iptables -A FORWARD -i eth0 -o wlan0 -m state --state ESTABLISHED,RELATED -j ACCEPT
```

### 2) Policy route small outbound packets via Starlink

This is optional; a simple NAT above is generally enough. To set specific marks:

```bash
# mark DNS and initial small TCP flows
sudo iptables -t mangle -F
sudo iptables -t mangle -A PREROUTING -i wlan0 -p udp --dport 53 -j MARK --set-mark 10
sudo iptables -t mangle -A PREROUTING -i wlan0 -p tcp -m connbytes --connbytes 0:6 --connbytes-dir original --connbytes-mode packets -j MARK --set-mark 10

# create table and route
echo "200 starlink" | sudo tee -a /etc/iproute2/rt_tables
sudo ip route add default via 192.168.100.1 dev eth0 table starlink
sudo ip rule add fwmark 10 table starlink
```

### 3) Inject downlink packets into local network

If your TS carries IP multicast/unicast for clients, you can forward those packets into your LAN by:

* Using `tsp -I file stream.ts -P ip -O ip --dest 10.10.10.2:1234` or using `socat` to inject UDP.
* Or run a small app that reads `ip_from_sat.pcap` and sends packets to local clients (e.g., `tcpreplay` or `socat`).

Example to replay extracted UDP packets to a local client:

```bash
# replay a pcap's UDP packets to 10.10.10.100:5000
tcpreplay -i wlan0 -l 0 --top-speed ip_from_sat.pcap
```

Be careful — only replay your own test packets.

---

# Example end-to-end demo recipe (commands summary)

1. On PC generate test UDP multicast:

```bash
# On PC (multicast source)
while true; do echo -n "test $(date)" | socat - UDP-DATAGRAM:239.1.1.1:5000,ip-multicast-if=127.0.0.1; sleep 0.05; done
```

2. Create TS from the UDP source:

```bash
ffmpeg -f mpegts -i udp://239.1.1.1:5000 -c copy -t 60 my_test.ts
```

3. Modulate my\_test.ts into DVB-S2 with GNU Radio -> BladeRF TX (GRC flowgraph) — run the TX (loopback into RX port using attenuators).

4. On same or separate machine: receive with BladeRF + GNU Radio demod to `stream.ts`.

5. Extract IP:

```bash
tsp -I file stream.ts -P ip -O pcap ip_from_sat.pcap
wireshark ip_from_sat.pcap
```

6. Configure Pi NAT to Starlink:

```bash
sudo sysctl -w net.ipv4.ip_forward=1
sudo iptables -t nat -A POSTROUTING -o eth0 -j MASQUERADE
# allow forwarding
sudo iptables -A FORWARD -i wlan0 -o eth0 -m state --state NEW,ESTABLISHED,RELATED -j ACCEPT
```

7. Replay or inject packets to LAN from pcap:

```bash
tcpreplay -i wlan0 ip_from_sat.pcap
```

---

## Troubleshooting tips

* **No TS demod lock**: check RF levels, sample rates, frequency offset; use AGC; ensure modulator/demodulator parameters match (SR, FEC, modulation).
* **No IP seen**: many DVB carriers are TV-only; ensure your TS actually encapsulated IP (check with `tsanalyze`).
* **Wireshark empty**: confirm `tsp -P ip` produced pcap with packets (`ls -lh ip_from_sat.pcap`).
* **Timing/sync**: use consistent clock refs (BladeRF TCXO helps).

---

## Next steps I can provide (choose any)

* A ready-to-run **GNU Radio GRC file** for a simple DVB-S2 modulator and demodulator tuned for BladeRF (I’ll give XML/flowgraph).
* A sample `ffmpeg`/`tsduck` script to automatically convert UDP → TS → DVB TX.
* A full `iptables` + `iproute2` script file you can paste into your Pi for Starlink routing.

Tell me which of the above you want next (GRC flowgraphs, scripts, or both) and I’ll produce them.
