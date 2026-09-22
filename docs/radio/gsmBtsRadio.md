
* 📡 **SIM-less mode**
* 🛠 **Custom programmable SIMs**
* 📱 Calls, SMS, and Internet (GPRS/EDGE)
* 🍓 Running on a Raspberry Pi
* 🔒 Running in a safe/private environment

---

# 📡 **Private GSM Network (SIM-less / Custom SIM) with Raspberry Pi + SDR**

> BTS is cellular communication between phones and the operator’s core network.

`gsm bts radio signals for sms and calls ⇄ baldeRf ⇄ raspi openBTS ⇄ satellite net core`

* For BTS: BladeRF + GSM/LTE antenna

* For Satellite: BladeRF + dish + LNB

* For Wi-Fi: BladeRF + 2.4/5 GHz Wi-Fi antenna

## **1. Overview**

This setup creates your own GSM BTS network that can:

* Let phones **connect without SIM** (open registration).
* Use **custom programmable SIMs** with your own MCC/MNC.
* Provide **voice calls**, **SMS**, and **mobile internet** (GPRS/EDGE).
* Run on a Raspberry Pi for portability.

**Architecture:**

```
[Phone] ⇄ [BTS: BladeRF/USRP + YateBTS/OpenBTS] ⇄ [Core Network: YateUCN or Osmocom] ⇄ [Internet Uplink]
```

---

## **2. Hardware**

| Component                                    | Purpose                       | Notes                         |
| -------------------------------------------- | ----------------------------- | ----------------------------- |
| **Raspberry Pi 4/5**                         | Main controller               | 4GB+ RAM recommended          |
| **SDR (BladeRF x40/x115 or USRP B200/B210)** | Radio transceiver for GSM     | BladeRF is cheaper, smaller   |
| **GSM Antenna (900/1800 MHz)**               | Broadcast/receive GSM signals | Omnidirectional works fine    |
| **Internet uplink**                          | For GPRS data                 | Ethernet, 4G dongle, or Wi-Fi |
| (Optional) **Programmable SIMs**             | For locked devices            | Sysmocom sysmoUSIM or similar |
| (Optional) **SIM card reader/writer**        | To program SIMs               | USB SIM reader                |

---

## **3. Software Options**

### **Option A – YateBTS + YateUCN (Simple, all-in-one core)**

* Easy web interface for configuration.
* Supports SIM-less open mode.
* Includes GPRS internet routing.

### **Option B – Osmocom Stack (Modular, advanced)**

* Components:

  `openBTS, openGSM, osmocom and yatebts`

  * **OsmoBTS** or **OpenBTS** – BTS software.
  * **OpenBSC** – Handles voice/SMS.
  * **OsmoSGSN** – Packet data.
  * **OsmoGGSN** – Internet gateway.

---

## **4. SIM-less vs. Programmable SIM Modes**

| Mode                             | Pros                                       | Cons                                                |
| -------------------------------- | ------------------------------------------ | --------------------------------------------------- |
| **SIM-less** (Open Registration) | No SIM needed, instant connect             | Some phones refuse to register; limited APN control |
| **Custom Programmable SIM**      | Works on all phones, custom APN, full auth | Need SIM hardware & programming tool                |

---

## **5. Raspberry Pi Setup (YateBTS Example)**

### **Step 1 – Install OS & Dependencies**

```bash
sudo apt update
sudo apt install build-essential cmake libusb-1.0-0-dev git
sudo apt install bladerf libbladerf-dev
```

### **Step 2 – Build Yate & YateBTS**

```bash
git clone https://github.com/yatevoip/yate.git
git clone https://github.com/yatevoip/yatebts.git
cd yate && ./autogen.sh && ./configure && make -j4 && sudo make install
cd ../yatebts && ./autogen.sh && ./configure && make -j4 && sudo make install
```

### **Step 3 – Configure YateBTS**

Edit `/usr/local/etc/yate/ybts.conf`:

```ini
; GSM Band & Channel
Radio.Band=900
Radio.C0=123

; Network Identity
GSM.Identity.MCC=901
GSM.Identity.MNC=70

; Open registration for SIM-less
GSM.Authentication=open

; Enable GPRS Internet
GPRS.Enable=yes
GPRS.NAT=yes
GPRS.DNS=8.8.8.8
```

---

## **6. Internet Access Setup**

### With YateUCN:

* Set **APN** to `internet.private` in `ybts.conf`.
* NAT traffic via Raspberry Pi:

```bash
sudo sysctl -w net.ipv4.ip_forward=1
sudo iptables -t nat -A POSTROUTING -o eth0 -j MASQUERADE
```

---

## **7. Using Programmable SIMs**

* Buy blank SIMs (e.g., from Sysmocom).
* Program MCC/MNC to match your BTS.
* Set custom APN (matches BTS config).
* Works with any locked phone.

---

## **8. SIM-less Mode**

* Works by setting `GSM.Authentication=open`.
* Phone will register without IMSI.
* Not all models support data in SIM-less mode (some require dummy IMSI).

---

## **9. Safety & Legal**

* Licensed GSM bands require telecom authority permission.
* For testing, use:

  * Shielded RF box
  * Faraday tent
  * Lab-grade attenuators

---

## **10. Example Diagram**

```
[Phone] ⇄ GSM Air ⇄ [BladeRF + YateBTS/OpenBTS on Raspberry Pi]
        ⇄ [Core Network (YateUCN/Osmocom)]
        ⇄ [Internet Uplink]
```

---

## **Where does the internet for my GSM BTS come from?**

if you build your own BTS (e.g., BladeRF + Raspberry Pi + YateBTS/OpenBTS) and you want to offer mobile devices internet through it:

**Flow looks like this:**

```
Phone ⇄ Your BTS ⇄ Raspberry Pi/Core Network ⇄ Internet Uplink
```

* The **Raspberry Pi** (or whatever runs your core network) is the “bridge” between the BTS and the internet.
* The Pi can connect to **any uplink**:

  * Starlink
  * Our own Satellite net
  * Fiber
  * 4G modem
  * Wi-Fi from somewhere else
  * VPN tunnel to a remote server
* If you hook the Pi to **Starlink**, yes — your GSM users will be effectively browsing via Starlink.

If you want GPRS over satellite, you wouldn’t send GPRS directly to space — you’d run your GSM BTS as normal, and your core network (RasPi) would connect to the internet through a satellite uplink (e.g., via an LNB for RX + uplink amplifier for TX).

# How **4G LTE can act as an uplink** for your GSM BTS project?

## **1. The Core Idea**

Your GSM BTS (OpenBTS / YateBTS) will let nearby phones connect for calls/SMS/data.
But **the BTS still needs internet** if you want those phones to browse the web, use messaging apps, etc.
A **4G LTE connection** is just one way to feed that internet into your BTS.

---

## **2. The Flow**

Here’s the chain:

```
Phone ⇄ Your BTS (BladeRF/USRP + Raspberry Pi) ⇄ Core Network (OpenBSC/Osmo/ YateUCN) ⇄ Internet Uplink (4G LTE modem) ⇄ ISP ⇄ Internet
```

---

## **3. How 4G LTE Fits In**

* **Your Raspberry Pi** or server runs the BTS software.
* You plug in a **4G LTE modem** (USB stick or router) to the Raspberry Pi.
* The LTE modem connects to a commercial mobile carrier’s LTE tower.
* That LTE connection becomes your **uplink** — essentially replacing Starlink, fiber, or Wi-Fi.
* Your BTS routes all connected phone traffic through that LTE modem.

---

## **4. Example Hardware Setup**

* **BladeRF / USRP** → for GSM radio transmission
* **Raspberry Pi 4** → runs OpenBTS/YateBTS and core network
* **Huawei E3372h** LTE USB stick → acts as the uplink to the internet
* **SIM card** → in the LTE stick, from a carrier that gives you internet access

---

## **5. Why Use LTE as Uplink**

* **Portability** → works anywhere with LTE coverage
* **Speed** → LTE can give you tens or even hundreds of Mbps
* **No fixed line needed** → perfect for field setups

---

## **6. Networking Details**

When the Raspberry Pi gets internet from the LTE modem:

* The Pi will have a `ppp0`, `wwan0`, or similar interface for LTE.
* The BTS software will NAT/route traffic from the phones onto that LTE interface.
* From the phones’ perspective, it’s just “the internet” — they don’t know it’s going through LTE first.

# GPS, GPRS, Wi-Fi, BTS, and GSM generally operate at **higher frequencies** than car key fobs, NFC, and most Sub-GHz IoT radios.

### **Higher-frequency signals**

(Above \~800 MHz, often in the MHz–GHz range)

* **GSM / BTS**: 850 MHz, 900 MHz, 1800 MHz, 1900 MHz bands
* **GPRS**: Same bands as GSM (since it’s a packet service over GSM)
* **3G/4G LTE**: 700 MHz to \~2.6 GHz (and even higher for 5G)
* **Wi-Fi**: 2.4 GHz, 5 GHz, 6 GHz bands
* **GPS**: \~1.575 GHz (L1), \~1.227 GHz (L2), newer bands higher
* **Satellite (Ku/Ka bands)**: 4–40 GHz
  These are **shorter wavelengths** → need smaller antennas, more line-of-sight, more bandwidth potential.

---

### **Lower-frequency / Sub-GHz signals**

(Below \~800 MHz)

* **Car key fobs**: 315 MHz, 433 MHz, 868 MHz, 915 MHz (region-dependent)
* **NFC / RFID HF**: 13.56 MHz (much lower)
* **Garage door openers, simple IoT devices**: often 315, 433, 868, 915 MHz
* **LoRa**: 433, 868, 915 MHz bands
  These are **longer wavelengths** → better at penetrating walls, longer range at low power, but less bandwidth.

**radio signal capability setup list** written clearly:

---

## **1. Flipper Zero Setup** *(small portable device)*

* **Wi-Fi**: Needs external Wi-Fi dev board (ESP32 Wi-Fi board for Flipper)
* **GPS**: Needs GPS module (UART/USB module) connected to Flipper
* **Sub-GHz**: Built-in — supports 315/433/868/915 MHz bands (car keys, remote controls, IoT devices)
* **NFC / RFID**: Built-in for 13.56 MHz HF tags and some LF 125 kHz tags
* **Infrared**: Built-in for controlling TVs, AC units, etc.

---

## **2. HackRF One Setup** *(half-duplex SDR)*

* **Frequency range**: \~1 MHz to 6 GHz
* **Extensions**:

  * **Wi-Fi**: Needs specific Wi-Fi antenna + GNU Radio / Wi-Fi toolchains
  * **GPS**: Needs GPS antenna + GNSS SDR software
  * **Cellular/BTS**: Can RX/TX in GSM/3G/LTE bands but only in half-duplex mode (limited for real BTS)
* **Best for**: Scanning, replay attacks, research — not ideal for live full-duplex comms.

---

## **3. BladeRF Setup** *(full-duplex SDR — better for BTS/Satellite work)*

* **Core unit**: BladeRF x40/x115
* **Antenna modules**:

  * **GSM/LTE BTS antenna** for 850/900/1800/1900 MHz bands
  * **Satellite dish + LNB** for Ku/Ka/C-band satellite downlink
  * **Feedhorn + TX amplifier** for satellite uplink
* **Software stack**:

  * **For BTS**: OpenBTS, YateBTS, Osmocom stack
  * **For Satellite**: Custom modems, Satcom SDR software

---

## **4. Raspberry Pi Integration**

* Acts as **controller** for BladeRF/HackRF
* Can run:

  * SDR software (OpenBTS, GNU Radio, YateBTS)
  * Network routing (share uplink from Starlink, 4G LTE, fiber, etc.)
  * Data logging & automation

---

## **Quick Summary Table**

| Device       | Duplex | Range             | Main Uses                              | Needs for Wi-Fi | Needs for GPS | Needs for BTS | Needs for Satellite |
| ------------ | ------ | ----------------- | -------------------------------------- | --------------- | ------------- | ------------- | ------------------- |
| Flipper Zero | N/A    | Sub-GHz + NFC     | RFID/NFC, Sub-GHz, IR remote           | Ext. board      | Ext. module   | ❌             | ❌                   |
| HackRF One   | Half   | 1 MHz – 6 GHz     | Scanning, replay, research             | Wi-Fi antenna   | GPS antenna   | Partial       | ❌                   |
| BladeRF      | Full   | \~300 MHz – 3.8G+ | Full-duplex SDR, BTS, Satcom           | Antenna         | Antenna       | ✔             | ✔                   |
| Raspberry Pi | N/A    | N/A               | SDR control, routing, internet sharing | N/A             | N/A           | Controller    | Controller          |

# Frequency Ranges between Satellite and BTS**

### **Satellite**

* **Uplink / Downlink Bands** (common civilian):

  * **C-band**: 3.4–4.2 GHz (downlink) / 5.8–6.4 GHz (uplink) – big dishes, weather-resistant.
  * **Ku-band**: 10.7–12.75 GHz (downlink) / 13.75–14.5 GHz (uplink) – most consumer TV/data VSAT.
  * **Ka-band**: 17.7–21.2 GHz (downlink) / 27.5–31 GHz (uplink) – high throughput, weather sensitive.
* **Specialty bands**: L-band (1–2 GHz, e.g., Inmarsat), X-band (military), S-band (some broadcast/mobile satcom).
* **Bandwidth per transponder**: 36–72 MHz typical.
* **Coverage**: *Huge* — one GEO satellite beam can cover a third of the planet’s surface.

---

### **BTS (Cell Tower)**

* **Frequency bands** vary by country/operator, typically:

  * **2G (GSM)**: 850, 900, 1800, 1900 MHz.
  * **3G (UMTS)**: 850, 900, 1900, 2100 MHz.
  * **4G LTE**: 700, 800, 900, 1800, 2100, 2300, 2600 MHz.
  * **5G NR**: 600–900 MHz (low band), 2–4 GHz (mid band), 24–40 GHz (mmWave).
* **Cell radius**: From \~200 m in dense cities to 30+ km in rural macro sites.
* **Bandwidth per carrier**: 1.4–20 MHz (LTE), up to 100 MHz (5G NR).

---

# Satellite Net RX-Only Setup Overview**

### **Hardware**

1. **Satellite Dish**

   * Size depends on satellite footprint and frequency band.
   * For Ku-band DVB-S2 internet downlinks in Iran, **90–120 cm** is usually enough.
   * If you go C-band (rare for consumer internet), you’ll need **1.8–2.4 m**.

2. **LNB (Low Noise Block)**

   * Matches the band (Ku-band: 10.7–12.75 GHz downlink).
   * Low noise figure (< 0.3 dB preferred).
   * Example: Inverto Black Ultra (Ku), Norsat (C-band).

3. **SDR (Software Defined Radio) or Satellite Receiver**

   * **Cheap option**: PCI/USB DVB-S2 tuner card (TBS 6903, TBS 5927 USB).
   * **Flexible option**: SDR like Airspy R2, BladeRF, or LimeSDR for raw IQ capture & custom demod.
   * SDR gives you more control for packet sniffing but is harder to set up.

4. **Coaxial Cable**

   * Low-loss 75Ω coax (e.g., RG-6 for DVB-S2 cards) or 50Ω if SDR uses different IF interface.

5. **Computer / Raspberry Pi**

   * For running the demodulation software and packet capture.

---

### **Software**

1. **Drivers**

   * For DVB-S2 cards: TBS Linux/Windows driver.
   * For SDR: SoapySDR or vendor driver.

2. **Demodulation**

   * **DVB-S2 cards**: `dvbsnoop`, CrazyScan, or TBS IP Data filter.
   * **SDR**: GNU Radio, SDRangel, gr-dvbs2.

3. **IP Packet Capture**

   * `tcpdump` or Wireshark to sniff IP streams.
   * You’ll see multicast or unicast IP packets coming from the satellite stream.

4. **Routing / Filtering**

   * Configure routing so that HTTP requests go out your uplink (4G modem) but incoming large data comes from the sat link.
   * Can be done with `iptables` + `ip rule` in Linux.

---

## **2. Data You Can Receive**

With RX-only, you’re limited to **downlink content already being broadcast** to all users of that satellite internet beam.

* If you subscribe to a satellite ISP, you can get normal internet downloads.
* If you don’t subscribe but just sniff open multicast IP streams, you may see:

  * Free-to-air satellite internet streams (rare).
  * DVB-S2 TV, radio, news feeds.
  * Some open data services (weather maps, AIS ship tracking, etc.).
  * Occasional misconfigured corporate traffic (banks, ISPs, oil companies, etc.), but **most serious data is encrypted now**.

---

## **3. Example Practical Setup**

* **Dish**: 100 cm offset dish with Ku LNB.
* **LNB**: Inverto Black Ultra Ku-band.
* **Receiver**: TBS 5927 USB DVB-S2 card (supports wide symbol rates).
* **PC**: Linux laptop.
* **Software**:

  * CrazyScan to lock transponder.
  * `dvbsnoop` or `tsp` (from TSduck) to dump Transport Stream.
  * `tcpdump` to capture IP packets from the DVB interface.
  * Wireshark to analyze HTTP, FTP, or other protocols in downlink.

---

## **4. How It Looks in Action**

1. Dish points at a known DVB-S2 internet transponder (e.g., Yahsat, TurkmenÄlem, Eutelsat).
2. LNB sends IF signal to TBS card.
3. Driver demodulates DVB-S2 into an IP interface on your PC (`dvb0_0`).
4. You run `tcpdump -i dvb0_0 -w sat_dump.pcap` to capture raw packets.
5. Wireshark opens the `.pcap` file and you see live traffic from the satellite downlink.
