# AtlasScan Scanner Service

Homegrown replacement for Asprise Scanner.js. A small Windows tray app that
exposes the machine's scanners (via Windows Image Acquisition / WIA) to web
pages over `http://127.0.0.1:18990`.

## How it fits together

```
web page (browser)                     AtlasScan.exe (this folder)
──────────────────                     ────────────────────────────
Click "Scan"
  → scan dialog opens    ── GET  /devices ──►  lists WIA scanners
  → click "Scan"         ── POST /scan ─────►  drives the scanner,
                                               returns pages as base64 JPEG
  → preview pages, click OK
  → jsPDF builds the PDF in the browser
  → the page uploads it to an API or saves it to disk
```

## Setup (per scanning workstation)

1. Copy this `scan-service` folder to the workstation.
2. Double-click `build.cmd` — compiles `AtlasScan.exe` using the C# compiler
   that ships with Windows (no SDK/Visual Studio install needed).
3. Double-click `AtlasScan.exe` — a tray icon appears; the service is running.
4. Optional: run `install-autostart.cmd` so it starts automatically on login.

To stop it: right-click the tray icon → Exit.

## Endpoints

| Method | Path       | Description                                    |
|--------|------------|------------------------------------------------|
| GET    | `/ping`    | Health check                                   |
| GET    | `/devices` | List available scanners `{devices:[{id,name}]}`|
| POST   | `/scan`    | Perform a scan, returns `{pages:[b64…], dpi}`  |

`/scan` body: `{ deviceId, source: "flatbed"|"feeder"|"feeder-duplex", paper: "default"|"a4"|"letter"|"legal"|"a5", color: "color"|"gray"|"bw", dpi, brightness: -100..100, contrast: -100..100 }`

The service only listens on 127.0.0.1 (never reachable from the network).
One scan runs at a time; a second request gets HTTP 409.

## Troubleshooting

- **"Scanner service not detected" in the web page** — `AtlasScan.exe` isn't
  running. Start it (check the tray).
- **Scanner missing from the list** — the device must have a WIA driver
  (virtually all HP/Canon/Epson/Brother devices do; network MFPs must be
  installed in Windows first). Click the refresh button after connecting.
- **Detailed errors** — right-click the tray icon → Open log
  (`%LOCALAPPDATA%\AtlasScan\service.log`).
- **Page reloads when scanning during development** — the log is deliberately
  written outside this folder so dev servers that watch the project tree
  (VS Code Live Server) don't reload the page mid-scan. Keep it that way.

## Differences from Asprise

- Uses WIA, not TWAIN. Devices that only ship a TWAIN driver (rare today)
  won't appear.
- No OCR: the produced PDF contains the page images but is not
  text-searchable (Asprise ran OCR with `text_searchable: true`). The upload
  API receives a normal PDF either way.
- Blank-page removal and PDF assembly happen in the browser.
