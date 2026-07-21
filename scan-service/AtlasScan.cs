// AtlasScan local scanner service
// Bridges web pages to scanners via Windows Image Acquisition (WIA).
// Build with build.cmd (uses the C# compiler that ships with the .NET Framework — no SDK needed).
// Runs as a tray icon and listens on http://127.0.0.1:18990
//
// Endpoints:
//   GET  /ping     -> { ok: true }
//   GET  /devices  -> { devices: [ { id, name } ] }
//   POST /scan     -> { pages: [ "<base64 jpeg>", ... ], dpi }  or  { error: "..." }
//     body: { deviceId, source: "flatbed"|"feeder", paper: "default"|"a4"|"letter"|"legal"|"a5",
//             color: "color"|"gray"|"bw", dpi, brightness: -100..100, contrast: -100..100 }

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class AtlasScanService
{
    const int Port = 18990;
    const string WiaFormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";

    static readonly object ScanLock = new object();
    static readonly object LogLock = new object();
    static string _logPath;

    [STAThread]
    static void Main()
    {
        bool createdNew;
        using (new Mutex(true, "AtlasScan_Service", out createdNew))
        {
            if (!createdNew) return; // already running

            // Log outside the app folder: dev servers (e.g. VS Code Live Server) watch the
            // project tree and reload the page whenever a file in it changes.
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AtlasScan");
            try { Directory.CreateDirectory(logDir); } catch { }
            _logPath = Path.Combine(logDir, "service.log");
            Log("Service starting on http://127.0.0.1:" + Port);

            var listener = new TcpListener(IPAddress.Loopback, Port);
            try { listener.Start(); }
            catch (Exception ex)
            {
                Log("FATAL: could not listen on port " + Port + ": " + ex.Message);
                MessageBox.Show(
                    "AtlasScan service could not start:\n" + ex.Message +
                    "\n\nIs another copy already running?",
                    "AtlasScan", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var acceptThread = new Thread(() => AcceptLoop(listener)) { IsBackground = true };
            acceptThread.Start();

            var menu = new ContextMenu();
            var title = menu.MenuItems.Add("AtlasScan service — port " + Port);
            title.Enabled = false;
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("Open log", (s, e) => { try { System.Diagnostics.Process.Start(_logPath); } catch { } });
            menu.MenuItems.Add("Exit", (s, e) => Application.Exit());

            var tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "AtlasScan scanner service",
                Visible = true,
                ContextMenu = menu,
            };

            Application.Run();

            tray.Visible = false;
            tray.Dispose();
            try { listener.Stop(); } catch { }
            Log("Service stopped.");
        }
    }

    // ── HTTP server (hand-rolled over TcpListener so no admin/urlacl is needed) ──

    static void AcceptLoop(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { break; }

            var t = new Thread(() =>
            {
                try { HandleClient(client); }
                catch (Exception ex) { Log("Client error: " + Root(ex).Message); }
            });
            t.SetApartmentState(ApartmentState.STA); // WIA is COM — keep it on an STA thread
            t.IsBackground = true;
            t.Start();
        }
    }

    static void HandleClient(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            stream.ReadTimeout = 15000;

            string method, path;
            byte[] body;
            if (!ReadRequest(stream, out method, out path, out body)) return;

            try
            {
                if (method == "OPTIONS") { WriteResponse(stream, 204, null); return; }
                if (method == "GET" && path == "/ping")
                {
                    WriteJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "service", "atlasscan" }, { "version", "1.0" } });
                    return;
                }
                if (method == "GET" && path == "/devices") { WriteJson(stream, 200, ListDevices()); return; }
                if (method == "POST" && path == "/scan")
                {
                    if (!Monitor.TryEnter(ScanLock)) { WriteJson(stream, 409, Err("A scan is already in progress.")); return; }
                    try { WriteJson(stream, 200, Scan(body)); }
                    finally { Monitor.Exit(ScanLock); }
                    return;
                }
                WriteJson(stream, 404, Err("Not found"));
            }
            catch (Exception ex)
            {
                Log("Request " + method + " " + path + " failed: " + Root(ex).Message);
                try { WriteJson(stream, 500, Err(Friendly(ex))); } catch { }
            }
        }
    }

    static bool ReadRequest(NetworkStream stream, out string method, out string path, out byte[] body)
    {
        method = null; path = null; body = new byte[0];

        var buf = new MemoryStream();
        var tmp = new byte[8192];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n;
            try { n = stream.Read(tmp, 0, tmp.Length); } catch { return false; }
            if (n <= 0) return false;
            buf.Write(tmp, 0, n);
            headerEnd = FindDoubleCrlf(buf.GetBuffer(), (int)buf.Length);
            if (buf.Length > (1 << 20)) return false; // header too large
        }

        var raw = buf.GetBuffer();
        string head = Encoding.ASCII.GetString(raw, 0, headerEnd);
        var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return false;
        method = parts[0].ToUpperInvariant();
        path = parts[1].Split('?')[0];

        int contentLength = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c > 0 && lines[i].Substring(0, c).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(lines[i].Substring(c + 1).Trim(), out contentLength);
        }

        var bodyMs = new MemoryStream();
        int already = (int)buf.Length - (headerEnd + 4);
        if (already > 0) bodyMs.Write(raw, headerEnd + 4, already);
        while (bodyMs.Length < contentLength)
        {
            int n;
            try { n = stream.Read(tmp, 0, tmp.Length); } catch { break; }
            if (n <= 0) break;
            bodyMs.Write(tmp, 0, n);
        }
        body = bodyMs.ToArray();
        return true;
    }

    static int FindDoubleCrlf(byte[] data, int len)
    {
        for (int i = 3; i < len; i++)
            if (data[i - 3] == '\r' && data[i - 2] == '\n' && data[i - 1] == '\r' && data[i] == '\n')
                return i - 3;
        return -1;
    }

    static void WriteJson(NetworkStream stream, int status, object obj)
    {
        var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        WriteResponse(stream, status, ser.Serialize(obj));
    }

    static void WriteResponse(NetworkStream stream, int status, string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json ?? "");
        string statusText =
            status == 200 ? "OK" :
            status == 204 ? "No Content" :
            status == 404 ? "Not Found" :
            status == 409 ? "Conflict" : "Internal Server Error";

        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(statusText).Append("\r\n");
        if (payload.Length > 0) sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
        sb.Append("Access-Control-Allow-Private-Network: true\r\n");
        sb.Append("Connection: close\r\n\r\n");

        byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(head, 0, head.Length);
        if (payload.Length > 0) stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    // ── WIA scanning ──

    static object ListDevices()
    {
        dynamic dm = Activator.CreateInstance(Type.GetTypeFromProgID("WIA.DeviceManager"));
        var list = new List<object>();
        int count = dm.DeviceInfos.Count;
        for (int i = 1; i <= count; i++)
        {
            dynamic info = dm.DeviceInfos[i];
            if ((int)info.Type != 1) continue; // 1 = scanner
            string name;
            try { name = info.Properties["Name"].Value.ToString(); }
            catch { name = "Scanner " + i; }
            list.Add(new Dictionary<string, object> { { "id", (string)info.DeviceID }, { "name", name } });
        }
        // Always offer a hardware-free test device so the full web flow can be
        // exercised without a working scanner.
        list.Add(new Dictionary<string, object> { { "id", "test" }, { "name", "Test Scanner (sample pages, no hardware)" } });
        Log("Devices listed: " + list.Count);
        return new Dictionary<string, object> { { "devices", list } };
    }

    static object Scan(byte[] body)
    {
        var ser = new JavaScriptSerializer();
        var req = (body != null && body.Length > 0)
            ? ser.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body))
            : new Dictionary<string, object>();

        string deviceId = Str(req, "deviceId");
        string source   = Str(req, "source", "flatbed");
        string color    = Str(req, "color", "color");
        string paper    = Str(req, "paper", "default");
        int dpi         = Clamp(Int(req, "dpi", 300), 50, 1200);
        int brightness  = Clamp(Int(req, "brightness", 0), -100, 100);
        int contrast    = Clamp(Int(req, "contrast", 0), -100, 100);

        if (deviceId == "test") return TestScan(source, paper, color, dpi);

        dynamic dm = Activator.CreateInstance(Type.GetTypeFromProgID("WIA.DeviceManager"));
        dynamic chosen = null;
        int count = dm.DeviceInfos.Count;
        for (int i = 1; i <= count; i++)
        {
            dynamic d = dm.DeviceInfos[i];
            if ((int)d.Type != 1) continue;
            if (string.IsNullOrEmpty(deviceId) || (string)d.DeviceID == deviceId) { chosen = d; break; }
        }
        if (chosen == null) return Err("Scanner not found. Check it is connected and powered on, then refresh.");

        Log("Scan start: device=" + (string)chosen.DeviceID + " source=" + source + " dpi=" + dpi + " color=" + color + " paper=" + paper);
        dynamic device = chosen.Connect();

        bool feeder = source == "feeder" || source == "feeder-duplex";
        bool duplex = source == "feeder-duplex";
        // Document Handling Select: 1=FEEDER, 2=FLATBED, 4=DUPLEX (combined with FEEDER)
        int wantHandling = duplex ? 5 : (feeder ? 1 : 2);
        TrySetProp(device.Properties, "3088", wantHandling);
        if (feeder && !HandlingSelectApplied(device.Properties, wantHandling))
        {
            Log("  feeder requested but driver kept a different Document Handling Select value — aborting instead of silently scanning the flatbed.");
            return Err("This scanner didn't accept the feeder as the paper source (no feeder attached, or the driver doesn't support switching). Scan cancelled rather than silently using the flatbed — choose Flatbed if that's what you want, or check the feeder tray.");
        }

        dynamic item = device.Items[1];
        int intent = color == "bw" ? 4 : color == "gray" ? 2 : 1;
        TrySetProp(item.Properties, "6146", intent); // Current Intent
        TrySetProp(item.Properties, "6147", dpi);    // Horizontal resolution
        TrySetProp(item.Properties, "6148", dpi);    // Vertical resolution
        TrySetProp(item.Properties, "6149", 0);      // Horizontal start
        TrySetProp(item.Properties, "6150", 0);      // Vertical start

        double[] size = PaperInches(paper);
        if (size != null)
        {
            TrySetExtent(item.Properties, "6151", (int)Math.Round(size[0] * dpi)); // width  px
            TrySetExtent(item.Properties, "6152", (int)Math.Round(size[1] * dpi)); // height px
        }

        TrySetScaled(item.Properties, "6154", brightness);
        TrySetScaled(item.Properties, "6155", contrast);

        var pages = new List<string>();
        while (true)
        {
            dynamic img;
            try { img = item.Transfer(WiaFormatBmp); }
            catch (Exception ex)
            {
                if (IsPaperEmpty(ex))
                {
                    if (pages.Count > 0) break;
                    return Err("The document feeder is empty. Load pages and try again.");
                }
                throw;
            }
            pages.Add(ToJpegBase64(img));
            if (!feeder) break;
            if (pages.Count >= 200) break;
            if (!FeederReady(device)) break;
        }

        Log("Scan done: " + pages.Count + " page(s)");
        return new Dictionary<string, object> { { "pages", pages }, { "dpi", dpi } };
    }

    // Confirms the driver actually kept the Document Handling Select value we asked for.
    // Some drivers accept the write but silently clamp/normalize it back to flatbed when,
    // e.g., no feeder is attached — TrySetProp only sees a successful call, not that.
    static bool HandlingSelectApplied(dynamic props, int expected)
    {
        try { return Convert.ToInt32(props["3088"].Value) == expected; }
        catch { return true; } // can't read it back — don't block the scan over that
    }

    static bool FeederReady(dynamic device)
    {
        try
        {
            int status = Convert.ToInt32(device.Properties["3087"].Value); // Document Handling Status
            return (status & 1) != 0; // FEED_READY
        }
        catch { return true; } // unknown — keep going; the paper-empty error ends the loop
    }

    static string ToJpegBase64(dynamic img)
    {
        byte[] raw = (byte[])img.FileData.BinaryData;
        using (var inMs = new MemoryStream(raw))
        using (var bmp = System.Drawing.Image.FromStream(inMs))
        {
            return JpegBase64(bmp);
        }
    }

    static string JpegBase64(System.Drawing.Image img)
    {
        using (var outMs = new MemoryStream())
        {
            var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
            img.Save(outMs, JpegCodec(), ep);
            return Convert.ToBase64String(outMs.ToArray());
        }
    }

    // ── test scanner (no hardware) ──

    static object TestScan(string source, string paper, string color, int dpi)
    {
        double[] size = PaperInches(paper) ?? new[] { 8.5, 11.0 };
        int pageCount = source == "feeder-duplex" ? 4 : source == "feeder" ? 3 : 1;
        var pages = new List<string>();
        for (int p = 1; p <= pageCount; p++)
            pages.Add(RenderTestPage(p, pageCount, size, color, dpi));
        Log("Test scan: " + pageCount + " sample page(s) at " + dpi + " dpi");
        return new Dictionary<string, object> { { "pages", pages }, { "dpi", dpi } };
    }

    static string RenderTestPage(int num, int total, double[] size, string color, int dpi)
    {
        int w = (int)Math.Round(size[0] * dpi);
        int h = (int)Math.Round(size[1] * dpi);
        bool colored = color == "color";
        var ink   = colored ? Color.FromArgb(21, 101, 192) : Color.FromArgb(30, 30, 30);
        var faint = colored ? Color.FromArgb(120, 150, 180) : Color.FromArgb(140, 140, 140);
        float u = dpi / 100f; // scale everything relative to resolution

        using (var bmp = new Bitmap(w, h))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            int margin = (int)(60 * u);
            using (var pen = new Pen(ink, 3 * u))
                g.DrawRectangle(pen, margin, margin, w - 2 * margin, h - 2 * margin);

            using (var titleFont = new Font("Arial", 36 * u, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var bodyFont  = new Font("Arial", 20 * u, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var inkBrush  = new SolidBrush(ink))
            using (var faintBrush = new SolidBrush(faint))
            {
                float y = margin + 40 * u;
                g.DrawString("AtlasScan — TEST PAGE", titleFont, inkBrush, margin + 40 * u, y);
                y += 70 * u;
                g.DrawString("Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                             "   |   " + dpi + " DPI   |   " + color, bodyFont, faintBrush, margin + 40 * u, y);
                y += 40 * u;
                g.DrawString("This page was produced by the Test Scanner device — no hardware was used.",
                             bodyFont, faintBrush, margin + 40 * u, y);
                y += 80 * u;

                // simulated text lines so the page never registers as blank
                var rnd = new Random(num * 7919);
                float lineH = 22 * u, gap = 16 * u;
                float bottom = h - margin - 120 * u;
                while (y + lineH < bottom)
                {
                    float len = (float)(w - 2 * margin - 80 * u) * (0.55f + (float)rnd.NextDouble() * 0.4f);
                    g.FillRectangle(faintBrush, margin + 40 * u, y, len, lineH * 0.45f);
                    y += lineH + gap;
                }

                g.DrawString("Page " + num + " of " + total, bodyFont, inkBrush,
                             w - margin - 200 * u, h - margin - 60 * u);
            }
            return JpegBase64(bmp);
        }
    }

    static ImageCodecInfo JpegCodec()
    {
        foreach (var c in ImageCodecInfo.GetImageEncoders())
            if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
        throw new Exception("JPEG encoder not available");
    }

    static double[] PaperInches(string paper)
    {
        switch ((paper ?? "").ToLowerInvariant())
        {
            case "a4":     return new[] { 8.27, 11.69 };
            case "letter": return new[] { 8.5, 11.0 };
            case "legal":  return new[] { 8.5, 14.0 };
            case "a5":     return new[] { 5.83, 8.27 };
            default:       return null; // scanner default
        }
    }

    // ── WIA property helpers (all best-effort; drivers vary in what they support) ──

    static void TrySetProp(dynamic props, string id, object value)
    {
        try { props[id].Value = value; }
        catch (Exception ex) { Log("  prop " + id + "=" + value + " not set: " + Root(ex).Message); }
    }

    static void TrySetExtent(dynamic props, string id, int desired)
    {
        try
        {
            dynamic p = props[id];
            int v = desired;
            try { int max = Convert.ToInt32(p.SubTypeMax); if (max > 0 && v > max) v = max; } catch { }
            p.Value = v;
        }
        catch (Exception ex) { Log("  extent " + id + " not set: " + Root(ex).Message); }
    }

    // Maps a -100..100 slider onto the driver's own min..max range.
    static void TrySetScaled(dynamic props, string id, int slider)
    {
        if (slider == 0) return;
        try
        {
            dynamic p = props[id];
            int min = -1000, max = 1000;
            try { min = Convert.ToInt32(p.SubTypeMin); max = Convert.ToInt32(p.SubTypeMax); } catch { }
            int v = slider > 0
                ? (int)Math.Round(slider / 100.0 * max)
                : (int)Math.Round(-slider / 100.0 * min);
            p.Value = v;
        }
        catch (Exception ex) { Log("  prop " + id + " not set: " + Root(ex).Message); }
    }

    // ── misc helpers ──

    static object Err(string msg) { return new Dictionary<string, object> { { "error", msg } }; }

    static string Str(Dictionary<string, object> d, string k, string def = null)
    {
        object v;
        return d.TryGetValue(k, out v) && v != null ? v.ToString() : def;
    }

    static int Int(Dictionary<string, object> d, string k, int def)
    {
        object v;
        if (!d.TryGetValue(k, out v) || v == null) return def;
        try { return Convert.ToInt32(v); } catch { return def; }
    }

    static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

    static Exception Root(Exception ex)
    {
        while (ex.InnerException != null) ex = ex.InnerException;
        return ex;
    }

    static uint Hresult(Exception ex)
    {
        for (Exception e = ex; e != null; e = e.InnerException)
        {
            var ce = e as COMException;
            if (ce != null) return (uint)ce.ErrorCode;
        }
        return 0;
    }

    static bool IsPaperEmpty(Exception ex) { return Hresult(ex) == 0x80210003; }

    static string Friendly(Exception ex)
    {
        switch (Hresult(ex))
        {
            case 0x80210002: return "Paper jam in the scanner.";
            case 0x80210003: return "The document feeder is empty.";
            case 0x80210004: return "The scanner reported a paper problem.";
            case 0x80210005: return "The scanner is offline. Check power and cable.";
            case 0x80210006: return "The scanner is busy. Try again in a moment.";
            case 0x80210007: return "The scanner is warming up — try again shortly.";
            case 0x8021000D: return "The scanner is locked by another application.";
            case 0x80210015: return "No scanner was found. Check it is connected and powered on.";
            default: return Root(ex).Message;
        }
    }

    static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 512 * 1024)
                    File.Delete(_logPath);
                File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
        }
        catch { }
    }
}
