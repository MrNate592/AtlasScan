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
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using NTwain;
using NTwain.Data;

static class AtlasScanService
{
    const int Port = 18990;
    const string WiaFormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";
    const int TwainTimeoutMs = 150000;
    const string DuplexUnsupportedMessage =
        "This scanner's Windows driver doesn't support double-sided scanning (this is a common limitation — many scanners, " +
        "including most Epson document scanners, only expose duplex through their own TWAIN software, and even that TWAIN " +
        "source wasn't found or doesn't support duplex here). Use \"Feeder — Front Only\" and flip the stack for the reverse side instead.";

    static readonly object ScanLock = new object();
    static readonly object LogLock = new object();
    static string _logPath;

    // A timed-out TWAIN operation abandons its worker thread rather than killing it (a truly
    // stuck native driver call often can't be aborted). That orphaned thread can keep holding
    // the scanner/USB stack, and has been observed corrupting a *later, unrelated* WIA scan on
    // the same physical device (an OutOfMemoryException from a corrupted COM marshaling
    // boundary, not real memory pressure). Once that happens there's no reliable in-process
    // recovery, so every subsequent scan is refused with a clear message instead of risking
    // another confusing crash — the tray app needs a restart to actually clear the native state.
    static volatile bool _twainWedged = false;

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
            LogTwainEnvironment();

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

        if (_twainWedged)
            return Err("A previous double-sided scan didn't finish cleanly and may still be holding the scanner. " +
                        "Restart AtlasScan (right-click the tray icon → Exit, then relaunch it) before scanning again.");

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

        if (source == "feeder-duplex")
        {
            string wiaName;
            try { wiaName = chosen.Properties["Name"].Value.ToString(); } catch { wiaName = null; }
            return ScanDuplexViaTwain(wiaName, color, paper, dpi, brightness, contrast);
        }

        dynamic device = chosen.Connect();
        Log("  driver-reported Document Handling Capabilities (3086): " + ReadHandlingCapabilities(device.Properties));

        bool feeder = source == "feeder"; // "feeder-duplex" is intercepted above, before reaching WIA
        // Document Handling Select: 1=FEEDER, 2=FLATBED
        int wantHandling = feeder ? 1 : 2;
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

    // Best-effort read of what the driver claims it can do (FEEDER=1, FLATBED=2, DUPLEX=4,
    // combined as bit flags) — purely diagnostic, logged so a duplex rejection can be
    // confirmed as a driver limitation without trial-and-error across paper sizes.
    static string ReadHandlingCapabilities(dynamic props)
    {
        try { return Convert.ToInt32(props["3086"].Value).ToString(); }
        catch (Exception ex) { return "unavailable (" + Root(ex).Message + ")"; }
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

    // ── TWAIN (duplex only — WIA handles flatbed/simplex-feeder above) ──
    //
    // TWAIN needs a live Windows message loop pumping messages to the driver, unlike WIA's
    // pure-COM model. NTwain's TwainSession.Open() spins up its own dedicated background
    // thread with an internal message pump and marshals every stateful call onto it,
    // blocking the caller until done — so the per-request thread below doesn't need a
    // pump of its own. The one real risk is a driver that ignores "no UI" and blocks
    // inside Enable() showing its own dialog; running the whole operation on a throwaway
    // Thread and Join()-ing it with a timeout (rather than just waiting on a completion
    // event) covers that case too, and still frees the caller/ScanLock either way.

    static object ScanDuplexViaTwain(string wiaDeviceName, string color, string paper,
                                      int dpi, int brightness, int contrast)
    {
        // Shared with the worker thread so that a timeout can still return whatever pages
        // DID transfer before the driver stalled, instead of throwing them away. Without
        // this, a scan that physically ran to completion but never fired the TWAIN "done"
        // signal would report zero pages to the browser despite the paper having gone
        // through the feeder.
        var pages = new List<string>();
        var pagesLock = new object();
        object result = null;
        Exception workerCrash = null;
        var workerFinished = new ManualResetEvent(false);

        var worker = new Thread(() =>
        {
            try { result = RunTwainScan(wiaDeviceName, color, paper, dpi, brightness, contrast, pages, pagesLock); }
            catch (Exception ex) { workerCrash = ex; }
            finally { workerFinished.Set(); }
        });
        worker.IsBackground = true; // never blocks process exit if truly wedged
        worker.Start();

        if (!workerFinished.WaitOne(TwainTimeoutMs))
        {
            _twainWedged = true;
            List<string> snapshot;
            lock (pagesLock) { snapshot = new List<string>(pages); }
            Log("  TWAIN scan did not finish within " + TwainTimeoutMs + "ms (" + snapshot.Count + " page(s) had transferred " +
                "by then) — refusing further scans until AtlasScan is restarted. If a window from the scanner's own " +
                "software appeared on the PC it may still be open; the worker thread is abandoned in the background.");
            if (snapshot.Count > 0)
            {
                return new Dictionary<string, object> {
                    { "pages", snapshot }, { "dpi", dpi },
                    { "warning", snapshot.Count + " page(s) came through before the scan stalled and never signalled " +
                                 "completion. Restart AtlasScan (right-click the tray icon → Exit, then relaunch it) before scanning again." }
                };
            }
            return Err("The scan timed out before any pages came through. Restart AtlasScan (right-click the tray icon → Exit, then relaunch it) before scanning again.");
        }
        if (workerCrash != null)
        {
            Log("  TWAIN worker crashed: " + Root(workerCrash).Message);
            return Err(Friendly(workerCrash));
        }
        return result;
    }

    static object RunTwainScan(string wiaDeviceName, string color, string paper,
                                int dpi, int brightness, int contrast,
                                List<string> pages, object pagesLock)
    {
        var appId = TWIdentity.CreateFromAssembly(DataGroups.Image, Assembly.GetExecutingAssembly());
        var session = new TwainSession(appId);
        DataSource src = null;
        try
        {
            var rc = session.Open();
            if (rc != ReturnCode.Success)
            {
                Log("  TWAIN session.Open() failed: " + rc);
                return Err("Could not start the TWAIN driver manager (" + rc + "). Check that twaindsm.dll is installed " +
                            "(Epson's own scanner software normally installs it).");
            }

            src = FindTwainSourceForWiaDevice(session, wiaDeviceName);
            if (src == null) return Err(DuplexUnsupportedMessage);

            if (src.Open() != ReturnCode.Success)
            {
                Log("  TWAIN source.Open() failed for " + src.Name);
                return Err("Could not open the TWAIN scanner driver.");
            }

            bool duplexOk = src.Capabilities.CapDuplexEnabled.CanSet
                && (!src.Capabilities.CapDuplex.IsSupported || src.Capabilities.CapDuplex.GetCurrent() != Duplex.None)
                && src.Capabilities.CapUIControllable.IsSupported;
            Log("  TWAIN source: " + src.Name + " CapDuplexEnabled.CanSet=" + src.Capabilities.CapDuplexEnabled.CanSet +
                " CapUIControllable=" + src.Capabilities.CapUIControllable.IsSupported);
            if (!duplexOk) return Err(DuplexUnsupportedMessage);

            if (src.Capabilities.CapFeederEnabled.CanSet) src.Capabilities.CapFeederEnabled.SetValue(BoolType.True);
            src.Capabilities.CapDuplexEnabled.SetValue(BoolType.True);
            if (src.Capabilities.ICapPixelType.CanSet) src.Capabilities.ICapPixelType.SetValue(TwainPixelTypeFor(color));
            if (src.Capabilities.ICapXResolution.CanSet) src.Capabilities.ICapXResolution.SetValue((float)dpi);
            if (src.Capabilities.ICapYResolution.CanSet) src.Capabilities.ICapYResolution.SetValue((float)dpi);
            if (brightness != 0 && src.Capabilities.ICapBrightness.CanSet) src.Capabilities.ICapBrightness.SetValue(brightness * 10f);
            if (contrast != 0 && src.Capabilities.ICapContrast.CanSet) src.Capabilities.ICapContrast.SetValue(contrast * 10f);
            session.StopOnTransferError = true;

            string firstError = null;
            var done = new ManualResetEvent(false);
            int readyCount = 0;

            // Logged verbosely and unconditionally (not just on failure) because a stall with
            // physical scanning but no output has been observed on this driver, and the only
            // way to tell "never started transferring" from "transferred fine but never got
            // the done signal" apart is to see exactly which of these fired before the stall.
            EventHandler<TransferReadyEventArgs> onReady = (s, e) =>
            {
                readyCount++;
                Log("  TWAIN TransferReady #" + readyCount);
            };
            EventHandler<DataTransferredEventArgs> onTransferred = (s, e) =>
            {
                try
                {
                    if (e.NativeData != IntPtr.Zero)
                        using (var stream = e.GetNativeImageStream())
                            if (stream != null)
                                using (var img = System.Drawing.Image.FromStream(stream))
                                {
                                    string b64 = JpegBase64(img);
                                    int countNow;
                                    lock (pagesLock) { pages.Add(b64); countNow = pages.Count; }
                                    Log("  TWAIN DataTransferred — page " + countNow + " decoded ok");
                                }
                }
                catch (Exception ex) { Log("  TWAIN page decode failed: " + Root(ex).Message); }
            };
            EventHandler<TransferErrorEventArgs> onError = (s, e) =>
            {
                firstError = e.Exception != null ? Root(e.Exception).Message : ("TWAIN error " + e.ReturnCode);
                Log("  TWAIN transfer error: " + firstError);
            };
            EventHandler onDisabled = (s, e) => { Log("  TWAIN SourceDisabled fired"); done.Set(); };

            session.TransferReady += onReady;
            session.DataTransferred += onTransferred;
            session.TransferError += onError;
            session.SourceDisabled += onDisabled;
            try
            {
                var enableRc = src.Enable(SourceEnableMode.NoUI, false, IntPtr.Zero);
                if (enableRc != ReturnCode.Success)
                    return Err("The TWAIN scanner would not start (code " + enableRc + ").");

                done.WaitOne(); // bounded by the ScanDuplexViaTwain worker-thread timeout

                List<string> snapshot;
                lock (pagesLock) { snapshot = new List<string>(pages); }
                if (snapshot.Count == 0) return Err(firstError ?? "The document feeder is empty. Load pages and try again.");

                Log("TWAIN duplex scan done: " + snapshot.Count + " page(s)");
                return new Dictionary<string, object> { { "pages", snapshot }, { "dpi", dpi } };
            }
            finally
            {
                session.TransferReady -= onReady;
                session.DataTransferred -= onTransferred;
                session.TransferError -= onError;
                session.SourceDisabled -= onDisabled;
            }
        }
        finally
        {
            // Teardown order matches NTwain's own sample app.
            try { if (src != null && session.State == 4) src.Close(); } catch { }
            try { if (session.State == 3) session.Close(); else if (session.State > 2) session.ForceStepDown(2); } catch { }
        }
    }

    static PixelType TwainPixelTypeFor(string color)
    {
        return color == "bw" ? PixelType.BlackWhite : color == "gray" ? PixelType.Gray : PixelType.RGB;
    }

    // WIA and TWAIN enumerate devices independently with no shared ID, so the only way to
    // find "the TWAIN source for this WIA device" is by comparing vendor product-name strings.
    static DataSource FindTwainSourceForWiaDevice(TwainSession session, string wiaDeviceName)
    {
        if (string.IsNullOrEmpty(wiaDeviceName)) return null;
        Log("  Matching TWAIN sources against WIA device name: \"" + wiaDeviceName + "\"");
        foreach (DataSource src in session.GetSources())
        {
            Log("  TWAIN source seen: " + src.Name + " (mfr=" + src.Manufacturer + ")");
            if (NamesLikelySameScanner(wiaDeviceName, src.Name)) return src;
        }
        return null;
    }

    // Deliberately conservative: a false match here means Enable() gets called against the
    // wrong physical source, which can hang for the full TWAIN timeout and — as seen against
    // a phantom "HP TWAIN USB" stub with no real hardware behind it — leave the driver stack
    // in a state that corrupts later WIA scans on the real device. A raw substring check
    // (dropped from an earlier version) let a single generic shared word like "USB" count as
    // a match; requiring at least two shared, non-trivial tokens (brand + model, typically)
    // means "no confident match" correctly falls through to the existing duplex-unsupported
    // error instead of guessing.
    static bool NamesLikelySameScanner(string wiaName, string twainName)
    {
        string a = NormalizeDeviceName(wiaName), b = NormalizeDeviceName(twainName);
        if (a.Length == 0 || b.Length == 0) return false;
        var bTokens = new HashSet<string>(b.Split(' '));
        return a.Split(' ').Count(t => t.Length >= 2 && bTokens.Contains(t)) >= 2;
    }

    static string NormalizeDeviceName(string s)
    {
        if (s == null) return "";
        var sb = new StringBuilder();
        foreach (char c in s.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    // One-time startup diagnostic: is a TWAIN Data Source Manager even findable for this
    // process's bitness? Surfaces the answer up front instead of only after a confusing
    // scan failure — same log file the WIA diagnostics already use.
    static void LogTwainEnvironment()
    {
        try
        {
            var p = PlatformInfo.Current;
            Log("TWAIN environment: 64bit=" + p.IsApp64Bit + " dsmSupported=" + p.IsSupported +
                " dsmExists=" + p.DsmExists + " expectedDsmPath=" + p.ExpectedDsmPath);
        }
        catch (Exception ex) { Log("TWAIN environment check failed: " + Root(ex).Message); }
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
