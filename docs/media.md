### 1. Parallel upload / download — split bytes into batches → send to channel via Fiber HTTP

**This is chunked/resumable large file transfer** (upload or download) using **HTTP** (usually with parallel goroutines + channels for coordination).

Typical scenarios:
- Uploading huge videos/files (> few GB) to backend / S3-like storage
- Downloading large backups/archives/ISOs with resume + speed-up via parallelism
- resumable file sync tools (like Dropbox-style chunking)

Key features:
- File split into fixed-size chunks (1–100 MB)
- Parallel HTTP requests (multipart or custom chunk endpoint)
- Use channels + worker pool to manage concurrency & ordering
- Often resumable (store uploaded chunk indices)

Rough Fiber server pattern (for **upload** of chunks):

```go
// client sends: ?chunkIndex=5&totalChunks=120&fileID=abc123
app.Post("/upload/chunk/:fileID", func(c *fiber.Ctx) error {
    fileID := c.Params("fileID")
    idx, _ := c.QueryInt("chunkIndex", -1)
    total, _ := c.QueryInt("totalChunks", 0)

    if idx < 0 || total <= 0 {
        return c.Status(400).SendString("bad chunk params")
    }

    // multipart/form-data or raw body
    file, err := c.FormFile("chunk")
    if err != nil {
        // or c.Body() for raw binary POST
        return err
    }

    // save chunk → disk / redis / S3 with key like fileID/chunk_0005
    // use channels + worker pool if you want to process in background

    // example: buffered channel to ordered writer
    // ch <- Chunk{Index: idx, Data: data}

    // when last chunk arrives → merge / notify

    return c.JSON(fiber.Map{"status": "chunk received", "index": idx})
})
```

**Client side** (parallel uploads):

- Split file → N goroutines → each does POST with chunk + index
- Use `sync.WaitGroup` + channel to track completion
- Many production libs do this: tusd (resumable), or custom with worker pool

**Pros**: resumable, parallel speed-up, works with CDNs  
**Cons**: needs custom merging logic, many small requests = overhead

### 2. Fiber HTTP online video playlist streaming

**This is classic on-demand video streaming** via **HTTP progressive download + range requests** (or HLS/DASH playlist-based).

Typical scenarios:
- YouTube/Vimeo-like VOD (video on demand)
- "Watch movie" feature in web/app

Two common styles in Fiber/Go:

**A. Simple byte-range MP4/WebM streaming** (single file)

```go
app.Get("/video/:filename", func(c *fiber.Ctx) error {
    path := "./videos/" + c.Params("filename")
    fi, err := os.Stat(path)
    if err != nil {
        return c.SendStatus(fiber.StatusNotFound)
    }

    size := fi.Size()
    rangeHeader := c.Get("Range")

    if rangeHeader == "" {
        // full file (rare for video)
        c.Set("Accept-Ranges", "bytes")
        c.Set("Content-Length", fmt.Sprintf("%d", size))
        return c.SendFile(path)
    }

    // parse Range: bytes=0- or 500000- or 0-999999
    start, end := parseRange(rangeHeader, size) // implement parser

    c.Set("Content-Range", fmt.Sprintf("bytes %d-%d/%d", start, end, size))
    c.Set("Accept-Ranges", "bytes")
    c.Set("Content-Length", fmt.Sprintf("%d", end-start+1))
    c.Status(206) // Partial Content

    file, _ := os.Open(path)
    defer file.Close()
    file.Seek(start, io.SeekStart)

    // stream the requested slice
    _, err = io.CopyN(c.Response().BodyWriter(), file, end-start+1)
    return err
})
```

**B. HLS (.m3u8 playlist + .ts segments)** — more modern / adaptive

- Generate .m3u8 manifest
- Serve segments via range requests or small files
- Fiber just serves static files + handles CORS

**Pros**: works with all browsers/HTML5 <video>, seeking, adaptive bitrate (HLS/DASH)  
**Cons**: needs ffmpeg/ffprobe for transcoding/segmentation

### 3. Video + audio realtime live streaming through sockets

**This is ultra-low-latency live streaming** (WebRTC, raw WebSocket binary, or WebSocket-FLV/TS).

Typical scenarios:
- Video calls (Zoom-like)
- Live gaming / screen sharing
- Security camera / drone feed
- Ultra-low-latency broadcasts (< 1–3 s delay)

Main approaches in Go:

**A. WebRTC** (most professional today)

- Use **Pion** (pure Go WebRTC library)
- Media → RTP → SFU (Selective Forwarding Unit) like ion-sfu, livekit, or your own
- Browser gets WebRTC peer connection

**B. Raw WebSocket binary streaming** (simpler, higher latency ~3–10 s)

```go
// using github.com/gofiber/websocket/v2 or gorilla/websocket

app.Use("/live", func(c *fiber.Ctx) error {
    if websocket.IsWebSocketUpgrade(c) {
        c.Locals("allowed", true)
        return c.Next()
    }
    return fiber.ErrUpgradeRequired
})

app.Get("/live/:room", websocket.New(func(c *websocket.Conn) {
    room := c.Params("room")

    // register to room broadcast channel
    broadcast.Register(room, c)

    for {
        mt, msg, err := c.ReadMessage()
        if err != nil {
            broadcast.Unregister(room, c)
            break
        }

        if mt == websocket.BinaryMessage {
            // msg = H.264 + AAC packet / Opus / VP8 / FLV chunk / ...
            broadcast.Send(room, msg) // fan-out to all viewers
        }
    }
}))
```

**C. Popular production stacks** (2025–2026):

- **Pion** + **WebRTC** → lowest latency + best quality
- **LAL** (github.com/q191201771/lal) → RTMP ingest → HLS + WebSocket-FLV/TS output
- **LiveKit** or **mediasoup** (Go client) + SFU
- WebSocket + Opus/VP8/VP9/AV1 → simple custom streaming

**Pros**: true real-time (<1 s possible with WebRTC), bidirectional  
**Cons**: complex (signaling server, NAT traversal, codec handling), bandwidth heavy

### Quick Comparison Table

| # | Type                            | Latency       | Use Case                  | Parallelism | Protocol        | Complexity |
|---|---------------------------------|---------------|---------------------------|-------------|-----------------|------------|
| 1 | Chunked parallel HTTP upload/download | seconds–minutes | Large file transfer       | Yes (goroutines) | HTTP            | Medium     |
| 2 | HTTP video playlist / range streaming | 5–30 s        | VOD (YouTube-style)       | No (per-request) | HTTP Range      | Low–Medium |
| 3 | Realtime live via sockets       | <1–5 s        | Live call / broadcast     | Yes (broadcast)  | WebSocket / WebRTC | High       |
