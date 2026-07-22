/*!
 * AtlasScan — drop-in web scanning client (homegrown Asprise Scanner.js replacement)
 *
 * Requires the companion local service (AtlasScan.exe from the scan-service folder)
 * running on the user's machine at http://127.0.0.1:18990.
 *
 * Usage:
 *   <script src="atlasscan.js"></script>
 *   <script>
 *     const result = await AtlasScan.scan();          // opens the scan dialog
 *     if (result) {
 *       result.base64;     // base64 of the PDF (no data: prefix)
 *       result.dataUrl;    // data:application/pdf;base64,...
 *       result.blob;       // Blob, ready for FormData upload
 *       result.pageCount;  // number of pages
 *     }                    // result is null if the user cancelled
 *   </script>
 *
 * Options: AtlasScan.scan({ serviceUrl, theme: 'dark'|'light', title })
 * jsPDF is loaded automatically from a CDN if the page hasn't already included it.
 */
(function (global) {
  'use strict';

  var DEFAULT_SERVICE_URL = 'http://127.0.0.1:18990';
  var JSPDF_CDN = 'https://cdn.jsdelivr.net/npm/jspdf@2.5.1/dist/jspdf.umd.min.js';

  var ui = null;          // cached DOM references after first injection
  var pages = [];         // { dataUrl, width, height, dpi, blank }
  var busy = false;
  var resolveScan = null; // pending promise resolver for the active dialog
  var zoomIndex = -1;
  var opts = {};
  var jspdfPromise = null;

  // ────────────────────────────────────────────
  // Styles (self-contained; s2s- prefix avoids collisions)
  // ────────────────────────────────────────────
  var CSS = [
    '.s2s-overlay{--s2s-bg:#0d1524;--s2s-panel:#111d33;--s2s-border:#22345c;--s2s-field:#0a1120;',
    '--s2s-text:#dbe7f6;--s2s-label:#7c93b5;--s2s-muted:#43587a;--s2s-blue:#1565C0;--s2s-blue-hi:#42A5F5;',
    '--s2s-gold:#C9A015;--s2s-red:#EF5350;',
    'position:fixed;inset:0;z-index:2147483000;display:none;align-items:center;justify-content:center;',
    'padding:1.25rem;background:rgba(0,0,0,.72);backdrop-filter:blur(5px);',
    "font-family:'Segoe UI',system-ui,-apple-system,sans-serif;color:var(--s2s-text);box-sizing:border-box}",
    '.s2s-overlay.s2s-light{--s2s-bg:#f4f8ff;--s2s-panel:#ffffff;--s2s-border:#c3d5ee;--s2s-field:#eef4fc;',
    '--s2s-text:#122642;--s2s-label:#5f7a9d;--s2s-muted:#93a9c6;--s2s-blue-hi:#1565C0}',
    '.s2s-overlay *{box-sizing:border-box;margin:0;padding:0}',
    '.s2s-overlay.s2s-open{display:flex}',
    '.s2s-modal{background:var(--s2s-panel);border:1px solid var(--s2s-border);border-radius:1rem;overflow:hidden;',
    'box-shadow:0 30px 70px rgba(0,0,0,.5);width:100%;max-width:940px;max-height:92vh;display:flex;flex-direction:column}',
    '.s2s-accent{height:3px;flex-shrink:0;background:linear-gradient(90deg,var(--s2s-blue),var(--s2s-gold),var(--s2s-blue))}',
    '.s2s-header{display:flex;align-items:center;justify-content:space-between;padding:.9rem 1.4rem;border-bottom:1px solid var(--s2s-border)}',
    '.s2s-title{font-size:.85rem;font-weight:700;text-transform:uppercase;letter-spacing:.08em}',
    '.s2s-x{background:none;border:none;cursor:pointer;color:var(--s2s-label);width:30px;height:30px;border-radius:.5rem;',
    'display:flex;align-items:center;justify-content:center;font-size:1rem}',
    '.s2s-x:hover{background:rgba(211,47,47,.15);color:var(--s2s-red)}',
    '.s2s-body{display:grid;grid-template-columns:280px 1fr;flex:1;min-height:0;overflow:hidden}',
    '.s2s-settings{padding:1.1rem 1.4rem;border-right:1px solid var(--s2s-border);overflow-y:auto}',
    '.s2s-group{margin-bottom:.9rem}',
    '.s2s-label{display:block;font-size:.7rem;font-weight:600;text-transform:uppercase;letter-spacing:.06em;',
    'color:var(--s2s-label);margin-bottom:.4rem}',
    '.s2s-select{width:100%;background:var(--s2s-field);border:1.5px solid var(--s2s-border);border-radius:.55rem;',
    'padding:.55rem .8rem;color:var(--s2s-text);font-size:.85rem;font-family:inherit;outline:none;cursor:pointer}',
    '.s2s-select:focus{border-color:var(--s2s-blue)}',
    '.s2s-row{display:flex;gap:.5rem}',
    '.s2s-refresh{width:40px;flex-shrink:0;background:var(--s2s-field);border:1.5px solid var(--s2s-border);',
    'border-radius:.55rem;color:var(--s2s-blue-hi);cursor:pointer;font-size:.9rem}',
    '.s2s-refresh:hover{border-color:var(--s2s-blue)}',
    '.s2s-slider{width:100%;accent-color:var(--s2s-blue);cursor:pointer}',
    '.s2s-val{float:right;color:var(--s2s-blue-hi);font-weight:700}',
    '.s2s-check{display:flex;align-items:center;gap:.5rem;font-size:.82rem;cursor:pointer}',
    '.s2s-check input{accent-color:var(--s2s-blue);width:15px;height:15px;cursor:pointer}',
    '.s2s-btn{display:inline-flex;align-items:center;justify-content:center;gap:.4rem;padding:.6rem 1.3rem;',
    'border-radius:.55rem;border:none;cursor:pointer;font-family:inherit;font-size:.85rem;font-weight:600;transition:filter .15s}',
    '.s2s-btn:disabled{opacity:.5;cursor:not-allowed}',
    '.s2s-btn:hover:not(:disabled){filter:brightness(1.1)}',
    '.s2s-btn-primary{background:linear-gradient(135deg,#1565C0,#1976D2);color:#fff;width:100%}',
    '.s2s-btn-ok{background:linear-gradient(135deg,#C9A015,#E8BA1E);color:#12233f;font-weight:700}',
    '.s2s-btn-cancel{background:var(--s2s-field);border:1.5px solid var(--s2s-border);color:var(--s2s-text)}',
    '.s2s-btn-clear{background:rgba(211,47,47,.1);border:1px solid rgba(211,47,47,.3);color:var(--s2s-red);',
    'padding:.4rem .9rem;font-size:.78rem}',
    '.s2s-status{font-size:.78rem;color:var(--s2s-label);margin-top:.7rem;min-height:1.1em;line-height:1.5}',
    '.s2s-preview{display:flex;flex-direction:column;min-height:0;min-width:0}',
    '.s2s-svcerr{display:none;margin:1rem 1.4rem 0;background:rgba(211,47,47,.1);border:1px solid rgba(211,47,47,.35);',
    'border-radius:.55rem;padding:.7rem 1rem;color:var(--s2s-red);font-size:.82rem;line-height:1.5}',
    '.s2s-svcerr a{color:#EF9A9A}',
    '.s2s-grid{flex:1;overflow-y:auto;padding:1.1rem 1.4rem;display:grid;',
    'grid-template-columns:repeat(auto-fill,minmax(130px,1fr));gap:.8rem;align-content:start;',
    'background:var(--s2s-bg);margin:1rem 1.4rem;border:1px solid var(--s2s-border);border-radius:.7rem;min-height:280px}',
    '.s2s-empty{grid-column:1/-1;align-self:center;text-align:center;color:var(--s2s-muted);font-size:.85rem;padding:3rem 1rem}',
    '.s2s-thumb{position:relative;border:1.5px solid var(--s2s-border);border-radius:.55rem;overflow:hidden;background:#fff}',
    '.s2s-thumb img{width:100%;display:block;cursor:zoom-in}',
    '.s2s-num{position:absolute;bottom:5px;left:5px;background:rgba(13,30,58,.75);color:#fff;font-size:.65rem;',
    'font-weight:700;padding:.1rem .45rem;border-radius:.75rem}',
    '.s2s-del{position:absolute;top:5px;right:5px;width:22px;height:22px;border:none;border-radius:50%;',
    'background:rgba(211,47,47,.85);color:#fff;font-size:.7rem;font-weight:700;cursor:pointer;line-height:1}',
    '.s2s-del:hover{background:#D32F2F}',
    '.s2s-footer{display:flex;align-items:center;gap:.7rem;padding:.8rem 1.4rem;border-top:1px solid var(--s2s-border)}',
    '.s2s-count{font-size:.8rem;color:var(--s2s-label)}',
    '.s2s-count strong{color:var(--s2s-text)}',
    '.s2s-spacer{flex:1}',
    /* zoom lightbox */
    '.s2s-zoom{position:fixed;inset:0;z-index:2147483100;display:none;background:rgba(0,0,0,.88);',
    'align-items:center;justify-content:center;padding:3.5rem 4.5rem}',
    '.s2s-zoom.s2s-open{display:flex}',
    '.s2s-zoom img{max-width:100%;max-height:100%;border-radius:.5rem;background:#fff;cursor:zoom-out;',
    'box-shadow:0 20px 60px rgba(0,0,0,.6)}',
    '.s2s-zbtn{position:absolute;width:44px;height:44px;display:flex;align-items:center;justify-content:center;',
    'background:rgba(255,255,255,.1);border:1px solid rgba(255,255,255,.25);border-radius:50%;color:#fff;',
    'cursor:pointer;font-size:1.05rem}',
    '.s2s-zbtn:hover:not(:disabled){background:rgba(255,255,255,.22)}',
    '.s2s-zbtn:disabled{opacity:.3;cursor:default}',
    '.s2s-zclose{top:1rem;right:1rem}',
    '.s2s-zprev{left:1rem;top:50%;transform:translateY(-50%)}',
    '.s2s-znext{right:1rem;top:50%;transform:translateY(-50%)}',
    '.s2s-zcount{position:absolute;bottom:1rem;left:50%;transform:translateX(-50%);color:rgba(255,255,255,.85);',
    'font-size:.85rem;font-weight:600;background:rgba(0,0,0,.45);padding:.3rem .9rem;border-radius:2rem}',
    '@media (max-width:760px){.s2s-body{grid-template-columns:1fr;overflow-y:auto}',
    '.s2s-settings{border-right:none;border-bottom:1px solid var(--s2s-border)}.s2s-grid{min-height:200px}}'
  ].join('');

  // ────────────────────────────────────────────
  // Dialog markup
  // ────────────────────────────────────────────
  var HTML =
    '<div class="s2s-modal">' +
      '<div class="s2s-accent"></div>' +
      '<div class="s2s-header">' +
        '<span class="s2s-title"></span>' +
        '<button type="button" class="s2s-x" title="Close">✕</button>' +
      '</div>' +
      '<div class="s2s-body">' +
        '<div class="s2s-settings">' +
          '<div class="s2s-group"><label class="s2s-label">Scanner</label>' +
            '<div class="s2s-row">' +
              '<select class="s2s-select" data-s2s="device" style="flex:1;min-width:0"></select>' +
              '<button type="button" class="s2s-refresh" title="Refresh scanner list">⟳</button>' +
            '</div></div>' +
          '<div class="s2s-group"><label class="s2s-label">Paper Source</label>' +
            '<select class="s2s-select" data-s2s="source">' +
              '<option value="flatbed">Flatbed</option>' +
              '<option value="feeder">Feeder — Front Only</option>' +
              '<option value="feeder-duplex">Feeder — Double-Sided</option>' +
            '</select></div>' +
          '<div class="s2s-group"><label class="s2s-label">Paper Size</label>' +
            '<select class="s2s-select" data-s2s="paper">' +
              '<option value="default">Default</option><option value="a4">A4</option>' +
              '<option value="letter">Letter</option><option value="legal">Legal</option>' +
              '<option value="a5">A5</option>' +
            '</select></div>' +
          '<div class="s2s-group"><label class="s2s-label">Color Mode</label>' +
            '<select class="s2s-select" data-s2s="color">' +
              '<option value="color">Color</option><option value="gray">Grayscale</option>' +
              '<option value="bw">Black &amp; White</option>' +
            '</select></div>' +
          '<div class="s2s-group"><label class="s2s-label">Resolution</label>' +
            '<select class="s2s-select" data-s2s="dpi">' +
              '<option value="150">150 DPI</option><option value="200">200 DPI</option>' +
              '<option value="300" selected>300 DPI</option><option value="400">400 DPI</option>' +
              '<option value="600">600 DPI</option>' +
            '</select></div>' +
          '<div class="s2s-group"><label class="s2s-label">Brightness <span class="s2s-val" data-s2s="brightnessVal">0</span></label>' +
            '<input type="range" class="s2s-slider" data-s2s="brightness" min="-100" max="100" value="0"></div>' +
          '<div class="s2s-group"><label class="s2s-label">Contrast <span class="s2s-val" data-s2s="contrastVal">0</span></label>' +
            '<input type="range" class="s2s-slider" data-s2s="contrast" min="-100" max="100" value="0"></div>' +
          '<div class="s2s-group"><label class="s2s-check">' +
            '<input type="checkbox" data-s2s="removeBlank"> Remove blank pages</label></div>' +
          '<button type="button" class="s2s-btn s2s-btn-primary" data-s2s="scanBtn">Scan</button>' +
          '<p class="s2s-status" data-s2s="status"></p>' +
        '</div>' +
        '<div class="s2s-preview">' +
          '<div class="s2s-svcerr" data-s2s="svcerr">' +
            '<strong>Scanner service not detected.</strong><br>' +
            'Start <em>AtlasScan.exe</em> on this computer, then <a href="#" data-s2s="retry">retry</a>.' +
          '</div>' +
          '<div class="s2s-grid" data-s2s="grid"></div>' +
          '<div class="s2s-footer">' +
            '<span class="s2s-count">Pages:&nbsp;<strong data-s2s="count">0</strong></span>' +
            '<button type="button" class="s2s-btn s2s-btn-clear" data-s2s="clearBtn" style="display:none">Clear</button>' +
            '<span class="s2s-spacer"></span>' +
            '<button type="button" class="s2s-btn s2s-btn-ok" data-s2s="okBtn" disabled>OK</button>' +
            '<button type="button" class="s2s-btn s2s-btn-cancel" data-s2s="cancelBtn">Cancel</button>' +
          '</div>' +
        '</div>' +
      '</div>' +
    '</div>';

  var ZOOM_HTML =
    '<img alt="Scanned page">' +
    '<button type="button" class="s2s-zbtn s2s-zclose" title="Close (Esc)">✕</button>' +
    '<button type="button" class="s2s-zbtn s2s-zprev" title="Previous page (←)">‹</button>' +
    '<button type="button" class="s2s-zbtn s2s-znext" title="Next page (→)">›</button>' +
    '<span class="s2s-zcount"></span>';

  // ────────────────────────────────────────────
  // UI bootstrap
  // ────────────────────────────────────────────
  function ensureUI() {
    if (ui) return;

    var style = document.createElement('style');
    style.textContent = CSS;
    document.head.appendChild(style);

    var overlay = document.createElement('div');
    overlay.className = 's2s-overlay';
    overlay.innerHTML = HTML;
    document.body.appendChild(overlay);

    var zoom = document.createElement('div');
    zoom.className = 's2s-zoom';
    zoom.innerHTML = ZOOM_HTML;
    document.body.appendChild(zoom);

    ui = { overlay: overlay, zoom: zoom, title: overlay.querySelector('.s2s-title') };
    overlay.querySelectorAll('[data-s2s]').forEach(function (el) { ui[el.getAttribute('data-s2s')] = el; });
    ui.zoomImg   = zoom.querySelector('img');
    ui.zoomCount = zoom.querySelector('.s2s-zcount');
    ui.zoomPrev  = zoom.querySelector('.s2s-zprev');
    ui.zoomNext  = zoom.querySelector('.s2s-znext');

    // events
    overlay.querySelector('.s2s-x').onclick = function () { finish(null); };
    ui.cancelBtn.onclick = function () { finish(null); };
    ui.okBtn.onclick = buildPdfAndFinish;
    ui.scanBtn.onclick = doScan;
    ui.clearBtn.onclick = function () { pages = []; renderPages(); setStatus(''); };
    overlay.querySelector('.s2s-refresh').onclick = loadScanners;
    ui.retry.onclick = function (e) { e.preventDefault(); loadScanners(); };
    ui.brightness.oninput = function () { ui.brightnessVal.textContent = this.value; };
    ui.contrast.oninput = function () { ui.contrastVal.textContent = this.value; };

    zoom.querySelector('.s2s-zclose').onclick = closeZoom;
    ui.zoomImg.onclick = closeZoom;
    ui.zoomPrev.onclick = function () { zoomNav(-1); };
    ui.zoomNext.onclick = function () { zoomNav(1); };
    zoom.onclick = function (e) { if (e.target === zoom) closeZoom(); };

    document.addEventListener('keydown', function (e) {
      if (zoom.classList.contains('s2s-open')) {
        if (e.key === 'Escape') closeZoom();
        if (e.key === 'ArrowLeft') zoomNav(-1);
        if (e.key === 'ArrowRight') zoomNav(1);
      } else if (overlay.classList.contains('s2s-open') && e.key === 'Escape') {
        finish(null);
      }
    });
  }

  function ensureJsPdf() {
    if (global.jspdf && global.jspdf.jsPDF) return Promise.resolve();
    if (!jspdfPromise) {
      jspdfPromise = new Promise(function (resolve, reject) {
        var s = document.createElement('script');
        s.src = JSPDF_CDN;
        s.onload = resolve;
        s.onerror = function () { jspdfPromise = null; reject(new Error('Could not load jsPDF')); };
        document.head.appendChild(s);
      });
    }
    return jspdfPromise;
  }

  // ────────────────────────────────────────────
  // Dialog behaviour
  // ────────────────────────────────────────────
  function setStatus(msg) { ui.status.textContent = msg; }

  function loadScanners() {
    ui.svcerr.style.display = 'none';
    ui.device.innerHTML = '<option value="">Detecting scanners…</option>';
    var ctrl = new AbortController();
    var timer = setTimeout(function () { ctrl.abort(); }, 5000);
    fetch(opts.serviceUrl + '/devices', { signal: ctrl.signal })
      .then(function (r) { return r.json(); })
      .then(function (data) {
        clearTimeout(timer);
        if (data.error) throw new Error(data.error);
        ui.device.innerHTML = '';
        if (!data.devices || !data.devices.length) {
          ui.device.innerHTML = '<option value="">No scanners found</option>';
          setStatus('No scanners detected — check the scanner is on, then refresh.');
          return;
        }
        data.devices.forEach(function (d) {
          var o = document.createElement('option');
          o.value = d.id;
          o.textContent = d.name;
          ui.device.appendChild(o);
        });
        setStatus('');
      })
      .catch(function (err) {
        clearTimeout(timer);
        ui.device.innerHTML = '<option value="">Service unavailable</option>';
        ui.svcerr.style.display = 'block';
        console.error('Scanner service not reachable:', err);
      });
  }

  function doScan() {
    if (busy) return;
    var deviceId = ui.device.value;
    if (!deviceId) { setStatus('Select a scanner first.'); return; }
    busy = true;
    ui.scanBtn.disabled = true;
    setStatus('Scanning — check the scanner…');

    fetch(opts.serviceUrl + '/scan', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        deviceId: deviceId,
        source: ui.source.value,
        paper: ui.paper.value,
        color: ui.color.value,
        dpi: parseInt(ui.dpi.value, 10),
        brightness: parseInt(ui.brightness.value, 10),
        contrast: parseInt(ui.contrast.value, 10)
      })
    })
      .then(function (r) { return r.json(); })
      .then(function (data) {
        if (data.error) { setStatus(data.error); return; }
        var dpi = data.dpi || 300;
        var removeBlank = ui.removeBlank.checked;
        var list = data.pages || [];
        var added = 0, skipped = 0;
        var chain = Promise.resolve();
        list.forEach(function (b64) {
          chain = chain.then(function () {
            return loadPageInfo('data:image/jpeg;base64,' + b64, dpi).then(function (page) {
              if (removeBlank && page.blank) { skipped++; return; }
              pages.push(page);
              added++;
            });
          });
        });
        return chain.then(function () {
          renderPages();
          if (data.warning) {
            setStatus(data.warning + ' (' + added + ' page(s) added below.)');
          } else {
            setStatus(added + ' page(s) scanned' + (skipped ? ', ' + skipped + ' blank page(s) removed' : '') + '.');
          }
        });
      })
      .catch(function (err) {
        console.error(err);
        setStatus('Could not reach the scanner service.');
        ui.svcerr.style.display = 'block';
      })
      .then(function () {
        busy = false;
        ui.scanBtn.disabled = false;
      });
  }

  // Loads a scanned page to record its pixel size and flag near-blank pages
  // (low luminance variance on a mostly-white downscaled sample).
  function loadPageInfo(dataUrl, dpi) {
    return new Promise(function (resolve) {
      var img = new Image();
      img.onload = function () {
        var blank = false;
        try {
          var w = 100, h = Math.max(1, Math.round(img.naturalHeight * (w / img.naturalWidth)));
          var c = document.createElement('canvas');
          c.width = w; c.height = h;
          var ctx = c.getContext('2d');
          ctx.drawImage(img, 0, 0, w, h);
          var d = ctx.getImageData(0, 0, w, h).data;
          var sum = 0, sumSq = 0, n = w * h;
          for (var i = 0; i < d.length; i += 4) {
            var lum = 0.299 * d[i] + 0.587 * d[i + 1] + 0.114 * d[i + 2];
            sum += lum; sumSq += lum * lum;
          }
          var mean = sum / n;
          var std = Math.sqrt(Math.max(0, sumSq / n - mean * mean));
          blank = std < 8 && mean > 180;
        } catch (e) { /* blank detection is best-effort */ }
        resolve({ dataUrl: dataUrl, width: img.naturalWidth, height: img.naturalHeight, dpi: dpi, blank: blank });
      };
      img.onerror = function () { resolve({ dataUrl: dataUrl, width: 0, height: 0, dpi: dpi, blank: false }); };
      img.src = dataUrl;
    });
  }

  function renderPages() {
    ui.grid.innerHTML = '';
    if (!pages.length) {
      ui.grid.innerHTML = '<div class="s2s-empty">Scanned pages will appear here</div>';
    } else {
      pages.forEach(function (p, i) {
        var div = document.createElement('div');
        div.className = 's2s-thumb';
        var img = document.createElement('img');
        img.src = p.dataUrl;
        img.title = 'Click to enlarge';
        img.onclick = function () { openZoom(i); };
        var num = document.createElement('span');
        num.className = 's2s-num';
        num.textContent = i + 1;
        var del = document.createElement('button');
        del.type = 'button';
        del.className = 's2s-del';
        del.title = 'Remove page';
        del.textContent = '✕';
        del.onclick = function () { pages.splice(i, 1); renderPages(); };
        div.appendChild(img); div.appendChild(num); div.appendChild(del);
        ui.grid.appendChild(div);
      });
    }
    ui.count.textContent = pages.length;
    ui.okBtn.disabled = !pages.length;
    ui.clearBtn.style.display = pages.length ? 'inline-flex' : 'none';
  }

  // ── zoom lightbox ──
  function openZoom(i) { zoomIndex = i; updateZoom(); ui.zoom.classList.add('s2s-open'); }
  function closeZoom() { ui.zoom.classList.remove('s2s-open'); zoomIndex = -1; }
  function zoomNav(d) {
    var n = zoomIndex + d;
    if (n < 0 || n >= pages.length) return;
    zoomIndex = n; updateZoom();
  }
  function updateZoom() {
    if (zoomIndex < 0 || zoomIndex >= pages.length) { closeZoom(); return; }
    ui.zoomImg.src = pages[zoomIndex].dataUrl;
    ui.zoomCount.textContent = 'Page ' + (zoomIndex + 1) + ' of ' + pages.length;
    ui.zoomPrev.disabled = zoomIndex === 0;
    ui.zoomNext.disabled = zoomIndex === pages.length - 1;
  }

  // ── PDF assembly ──
  function buildPdf() {
    return ensureJsPdf().then(function () {
      var jsPDF = global.jspdf.jsPDF;
      var doc = null;
      pages.forEach(function (p) {
        var wpt = (p.width / p.dpi) * 72 || 612;
        var hpt = (p.height / p.dpi) * 72 || 792;
        var orientation = wpt > hpt ? 'landscape' : 'portrait';
        if (!doc) doc = new jsPDF({ unit: 'pt', format: [wpt, hpt], orientation: orientation });
        else doc.addPage([wpt, hpt], orientation);
        doc.addImage(p.dataUrl, 'JPEG', 0, 0, wpt, hpt);
      });
      return doc;
    });
  }

  function buildPdfAndFinish() {
    if (!pages.length) return;
    ui.okBtn.disabled = true;
    buildPdf()
      .then(function (doc) {
        var dataUrl = doc.output('datauristring');
        var base64 = dataUrl.substring(dataUrl.indexOf('base64,') + 7);
        finish({
          base64: base64,
          dataUrl: 'data:application/pdf;base64,' + base64,
          blob: base64ToBlob(base64, 'application/pdf'),
          pageCount: pages.length
        });
      })
      .catch(function (err) {
        ui.okBtn.disabled = false;
        setStatus('Could not build the PDF: ' + err.message);
      });
  }

  function base64ToBlob(b64, mime) {
    var raw = atob(b64);
    var arr = new Uint8Array(raw.length);
    for (var i = 0; i < raw.length; i++) arr[i] = raw.charCodeAt(i);
    return new Blob([arr], { type: mime });
  }

  function finish(result) {
    ui.overlay.classList.remove('s2s-open');
    closeZoom();
    pages = [];
    var r = resolveScan;
    resolveScan = null;
    if (r) r(result);
  }

  // ────────────────────────────────────────────
  // Public API
  // ────────────────────────────────────────────
  function scan(options) {
    opts = Object.assign({
      serviceUrl: DEFAULT_SERVICE_URL,
      theme: 'dark',
      title: 'Scan Document'
    }, options || {});

    ensureUI();
    if (resolveScan) return Promise.reject(new Error('A scan dialog is already open.'));

    ui.title.textContent = opts.title;
    ui.overlay.classList.toggle('s2s-light', opts.theme === 'light');
    pages = [];
    renderPages();
    setStatus('');
    ui.overlay.classList.add('s2s-open');
    loadScanners();
    ensureJsPdf().catch(function () { /* retried on OK */ });

    return new Promise(function (resolve) { resolveScan = resolve; });
  }

  function isServiceAvailable(serviceUrl) {
    var url = (serviceUrl || DEFAULT_SERVICE_URL) + '/ping';
    return fetch(url).then(function (r) { return r.json(); })
      .then(function (d) { return !!d.ok; })
      .catch(function () { return false; });
  }

  global.AtlasScan = { scan: scan, isServiceAvailable: isServiceAvailable };

})(window);
