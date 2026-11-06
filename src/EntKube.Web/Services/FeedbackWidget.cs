namespace EntKube.Web.Services;

/// <summary>
/// The first-party feedback widget, served verbatim at <c>GET /feedback/v1/widget.js</c>. Like
/// <see cref="RumSnippet"/> it is self-contained: it reads its <c>data-key</c>, derives the endpoint
/// from its own origin, and needs no build step or dependency.
///
/// <para><b>The screenshot is the browser's own capture, on purpose.</b> Rendering the DOM to an
/// image would need a large third-party library and would silently include whatever is on the page.
/// <c>getDisplayMedia</c> makes the browser ask, and shows the person exactly what they are sharing.
/// Where it does not exist (mobile) the person picks an image instead. Both pass through the same
/// editor, whose black-out tool is flattened into the pixels: what reaches the server is the
/// re-encoded canvas, so a blacked-out area is gone rather than hidden, and a picked file's EXIF
/// location goes with it.</para>
///
/// <para>Rendered in a shadow root so the host page's CSS cannot reach in or be reached. Set
/// <c>data-lang="sv"</c> or <c>"en"</c> to override the browser's language.</para>
/// </summary>
public static class FeedbackWidget
{
    public const string Js = """
(function () {
  "use strict";
  try {
    var script = document.currentScript;
    if (!script) return;
    var key = script.getAttribute("data-key");
    if (!key) return;

    var origin;
    try { origin = new URL(script.src).origin; } catch (e) { origin = location.origin; }
    var endpoint = origin + "/ingest/feedback/v1/" + encodeURIComponent(key);

    var MAX_WIDTH = 1600, MAX_DATA_URL = 3800000, MAX_ERRORS = 5;

    var TEXT = {
      en: {
        open: "Feedback", title: "Send feedback", bug: "Bug", idea: "Idea", question: "Question", praise: "Praise",
        rate: "How is your experience?", star: " of 5", message: "What would you like to tell us?",
        email: "Email (optional)", addShot: "Add screenshot", editShot: "Edit screenshot", removeShot: "Remove",
        send: "Send", sending: "Sending…", close: "Close", thanks: "Thank you! We have received your feedback.",
        failed: "Sending failed. Please try again.", tooMany: "Too many reports right now. Please try again later.",
        shotFailed: "No screenshot was taken.", privacy: "Check the screenshot before sending. Use Black out to hide anything sensitive.",
        box: "Box", arrow: "Arrow", pen: "Pen", blackout: "Black out", undo: "Undo", done: "Done", cancel: "Cancel"
      },
      sv: {
        open: "Feedback", title: "Skicka feedback", bug: "Fel", idea: "Idé", question: "Fråga", praise: "Beröm",
        rate: "Hur är din upplevelse?", star: " av 5", message: "Vad vill du berätta för oss?",
        email: "E-post (valfritt)", addShot: "Lägg till skärmbild", editShot: "Redigera skärmbild", removeShot: "Ta bort",
        send: "Skicka", sending: "Skickar…", close: "Stäng", thanks: "Tack! Vi har tagit emot din feedback.",
        failed: "Det gick inte att skicka. Försök igen.", tooMany: "För många rapporter just nu. Försök igen senare.",
        shotFailed: "Ingen skärmbild togs.", privacy: "Kontrollera skärmbilden innan du skickar. Använd Maskera för att dölja känsligt innehåll.",
        box: "Ruta", arrow: "Pil", pen: "Penna", blackout: "Maskera", undo: "Ångra", done: "Klar", cancel: "Avbryt"
      }
    };
    var lang = script.getAttribute("data-lang") || (navigator.language || "").slice(0, 2);
    var T = TEXT[lang] || TEXT.en;

    // Uncaught errors from before the panel opens are the ones that made somebody open it.
    var errors = [];
    function remember(msg) {
      if (!msg) return;
      errors.push(String(msg).slice(0, 300));
      if (errors.length > MAX_ERRORS) errors.shift();
    }
    window.addEventListener("error", function (e) { remember(e.message); });
    window.addEventListener("unhandledrejection", function (e) {
      var r = e.reason;
      remember(r && r.message ? r.message : r);
    });

    var CSS = [
      ":host{all:initial}",
      "*{box-sizing:border-box;font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif}",
      "button,input,textarea{font-size:14px}",
      ".launch{position:fixed;right:20px;bottom:20px;z-index:2147483000;background:#1f2937;color:#fff;border:0;border-radius:999px;padding:10px 16px;cursor:pointer;box-shadow:0 2px 10px rgba(0,0,0,.3)}",
      ".panel{position:fixed;right:20px;bottom:20px;z-index:2147483000;width:340px;max-width:calc(100vw - 32px);max-height:calc(100vh - 32px);overflow:auto;background:#fff;color:#111827;border-radius:12px;box-shadow:0 8px 30px rgba(0,0,0,.3);padding:16px}",
      ".head{display:flex;justify-content:space-between;align-items:center;margin-bottom:12px}",
      ".head h2{font-size:16px;margin:0}",
      ".x{background:none;border:0;font-size:20px;line-height:1;cursor:pointer;color:#6b7280}",
      ".kinds{display:flex;gap:6px;margin-bottom:12px;flex-wrap:wrap}",
      ".kind{flex:1;background:#f3f4f6;color:#111827;border:1px solid #e5e7eb;border-radius:8px;padding:6px;cursor:pointer}",
      ".kind[aria-pressed=true]{background:#1f2937;color:#fff;border-color:#1f2937}",
      ".label{display:block;font-size:13px;color:#374151;margin-bottom:4px}",
      ".stars{display:flex;gap:2px;margin-bottom:12px}",
      ".star{background:none;border:0;font-size:22px;line-height:1;cursor:pointer;color:#d1d5db;padding:2px}",
      ".star[aria-pressed=true]{color:#f59e0b}",
      "textarea,input.mail{width:100%;border:1px solid #d1d5db;border-radius:8px;padding:8px;margin-bottom:12px;color:#111827;background:#fff}",
      "textarea{min-height:90px;resize:vertical}",
      ".shot{margin-bottom:12px}.shot img{display:block;width:100%;border:1px solid #e5e7eb;border-radius:8px;margin-bottom:6px}",
      ".link{background:none;border:0;color:#2563eb;cursor:pointer;padding:0;margin-right:12px}",
      ".hint{font-size:12px;color:#6b7280;margin:6px 0 0}",
      ".send{width:100%;background:#2563eb;color:#fff;border:0;border-radius:8px;padding:10px;cursor:pointer}",
      ".send:disabled{opacity:.5;cursor:default}",
      ".msg{font-size:13px;margin-top:8px;color:#b91c1c}.thanks{padding:12px 0;font-size:14px}",
      ".editor{position:fixed;inset:0;z-index:2147483001;background:rgba(17,24,39,.92);display:flex;flex-direction:column;align-items:center;padding:12px;gap:10px}",
      ".tools{display:flex;gap:6px;flex-wrap:wrap;justify-content:center}",
      ".tool{background:#374151;color:#fff;border:0;border-radius:8px;padding:6px 12px;cursor:pointer}",
      ".tool[aria-pressed=true]{background:#2563eb}.tool.primary{background:#16a34a}",
      "canvas{max-width:100%;max-height:calc(100vh - 80px);cursor:crosshair;touch-action:none;background:#fff}"
    ].join("");

    var host = document.createElement("div");
    host.setAttribute("data-entkube-feedback", "");
    var root = host.attachShadow({ mode: "open" });
    var style = document.createElement("style");
    style.textContent = CSS;
    root.appendChild(style);

    var ui = document.createElement("div");
    root.appendChild(ui);

    var state = { kind: "Bug", rating: null, message: "", email: "", shot: null, base: null, shapes: [] };

    function el(tag, attrs, children) {
      var e = document.createElement(tag);
      if (attrs) for (var k in attrs) {
        if (k === "text") e.textContent = attrs[k];
        else if (k === "on") { for (var ev in attrs.on) e.addEventListener(ev, attrs.on[ev]); }
        else e.setAttribute(k, attrs[k]);
      }
      (children || []).forEach(function (c) { if (c) e.appendChild(c); });
      return e;
    }

    function clear() { while (ui.firstChild) ui.removeChild(ui.firstChild); }

    function showLauncher() {
      clear();
      ui.appendChild(el("button", { "class": "launch", type: "button", text: T.open, on: { click: showForm } }));
    }

    function showForm() {
      clear();
      var note = el("div", { "class": "msg", role: "alert" });

      var kinds = el("div", { "class": "kinds" }, ["Bug", "Idea", "Question", "Praise"].map(function (k) {
        return el("button", {
          "class": "kind", type: "button", "aria-pressed": String(state.kind === k), text: T[k.toLowerCase()],
          on: { click: function () { state.kind = k; showForm(); } }
        });
      }));

      var stars = el("div", { "class": "stars" }, [1, 2, 3, 4, 5].map(function (n) {
        return el("button", {
          "class": "star", type: "button", "aria-label": n + T.star,
          "aria-pressed": String(state.rating !== null && n <= state.rating), text: "★",
          on: { click: function () { state.rating = state.rating === n ? null : n; showForm(); } }
        });
      }));

      var message = el("textarea", { maxlength: "4000", "aria-label": T.message, on: { input: function (e) { state.message = e.target.value; refresh(); } } });
      message.value = state.message;

      var email = el("input", { "class": "mail", type: "email", maxlength: "254", autocomplete: "email", "aria-label": T.email, on: { input: function (e) { state.email = e.target.value; } } });
      email.value = state.email;

      var send = el("button", { "class": "send", type: "button", text: T.send, on: { click: function () { submit(send, note); } } });
      function refresh() { send.disabled = !state.message.trim() && state.rating === null; }
      refresh();

      ui.appendChild(el("div", { "class": "panel", role: "dialog", "aria-label": T.title }, [
        el("div", { "class": "head" }, [
          el("h2", { text: T.title }),
          el("button", { "class": "x", type: "button", "aria-label": T.close, text: "×", on: { click: showLauncher } })
        ]),
        kinds,
        el("span", { "class": "label", text: T.rate }), stars,
        el("span", { "class": "label", text: T.message }), message,
        el("span", { "class": "label", text: T.email }), email,
        shotBlock(note),
        send,
        note
      ]));
    }

    function shotBlock(note) {
      if (state.shot) {
        return el("div", { "class": "shot" }, [
          el("img", { src: state.shot, alt: "" }),
          el("button", { "class": "link", type: "button", text: T.editShot, on: { click: showEditor } }),
          el("button", { "class": "link", type: "button", text: T.removeShot, on: { click: function () { state.shot = null; state.base = null; state.shapes = []; showForm(); } } }),
          el("p", { "class": "hint", text: T.privacy })
        ]);
      }
      return el("div", { "class": "shot" }, [
        el("button", { "class": "link", type: "button", text: T.addShot, on: { click: function () { addShot(note); } } })
      ]);
    }

    function addShot(note) {
      var capture = navigator.mediaDevices && navigator.mediaDevices.getDisplayMedia;
      (capture ? grabScreen() : pickFile()).then(function (canvas) {
        state.base = canvas;
        state.shapes = [];
        showEditor();
      }, function () {
        note.textContent = T.shotFailed;
      });
    }

    // The widget is hidden before the frame is taken, so it does not appear in its own report.
    function grabScreen() {
      return navigator.mediaDevices.getDisplayMedia({ video: true, audio: false, preferCurrentTab: true }).then(function (stream) {
        host.style.display = "none";
        var video = document.createElement("video");
        video.srcObject = stream;
        video.muted = true;
        function done() {
          stream.getTracks().forEach(function (t) { t.stop(); });
          host.style.display = "";
        }
        return video.play()
          .then(function () { return new Promise(function (r) { setTimeout(r, 300); }); })
          .then(function () {
            var c = document.createElement("canvas");
            c.width = video.videoWidth;
            c.height = video.videoHeight;
            c.getContext("2d").drawImage(video, 0, 0);
            done();
            return c;
          }, function (err) { done(); throw err; });
      });
    }

    function pickFile() {
      return new Promise(function (resolve, reject) {
        var input = document.createElement("input");
        input.type = "file";
        input.accept = "image/png,image/jpeg";
        input.addEventListener("cancel", reject);
        input.addEventListener("change", function () {
          var file = input.files && input.files[0];
          if (!file) { reject(); return; }
          var url = URL.createObjectURL(file);
          var img = new Image();
          img.onload = function () {
            var c = document.createElement("canvas");
            c.width = img.naturalWidth;
            c.height = img.naturalHeight;
            c.getContext("2d").drawImage(img, 0, 0);
            URL.revokeObjectURL(url);
            resolve(c);
          };
          img.onerror = function () { URL.revokeObjectURL(url); reject(); };
          img.src = url;
        });
        input.click();
      });
    }

    function drawShape(ctx, s) {
      var w = Math.max(3, ctx.canvas.width / 400);
      ctx.lineWidth = w;
      ctx.strokeStyle = "#ef4444";
      ctx.fillStyle = "#ef4444";
      ctx.lineCap = "round";
      ctx.lineJoin = "round";
      if (s.tool === "blackout") {
        ctx.fillStyle = "#000";
        ctx.fillRect(Math.min(s.x, s.x2), Math.min(s.y, s.y2), Math.abs(s.x2 - s.x), Math.abs(s.y2 - s.y));
      } else if (s.tool === "box") {
        ctx.strokeRect(s.x, s.y, s.x2 - s.x, s.y2 - s.y);
      } else if (s.tool === "arrow") {
        var a = Math.atan2(s.y2 - s.y, s.x2 - s.x), h = w * 5;
        ctx.beginPath(); ctx.moveTo(s.x, s.y); ctx.lineTo(s.x2, s.y2); ctx.stroke();
        ctx.beginPath(); ctx.moveTo(s.x2, s.y2);
        ctx.lineTo(s.x2 - h * Math.cos(a - .45), s.y2 - h * Math.sin(a - .45));
        ctx.lineTo(s.x2 - h * Math.cos(a + .45), s.y2 - h * Math.sin(a + .45));
        ctx.closePath(); ctx.fill();
      } else if (s.tool === "pen") {
        ctx.beginPath();
        s.points.forEach(function (p, i) { if (i) ctx.lineTo(p[0], p[1]); else ctx.moveTo(p[0], p[1]); });
        ctx.stroke();
      }
    }

    function render(ctx) {
      ctx.drawImage(state.base, 0, 0);
      state.shapes.forEach(function (s) { drawShape(ctx, s); });
    }

    function showEditor() {
      clear();
      var tool = "box";
      var canvas = document.createElement("canvas");
      canvas.width = state.base.width;
      canvas.height = state.base.height;
      var ctx = canvas.getContext("2d");
      render(ctx);

      function point(e) {
        var r = canvas.getBoundingClientRect();
        return [(e.clientX - r.left) * canvas.width / r.width, (e.clientY - r.top) * canvas.height / r.height];
      }
      var drawing = null;
      canvas.addEventListener("pointerdown", function (e) {
        var p = point(e);
        drawing = { tool: tool, x: p[0], y: p[1], x2: p[0], y2: p[1], points: [p] };
        state.shapes.push(drawing);
        canvas.setPointerCapture(e.pointerId);
      });
      canvas.addEventListener("pointermove", function (e) {
        if (!drawing) return;
        var p = point(e);
        drawing.x2 = p[0]; drawing.y2 = p[1]; drawing.points.push(p);
        render(ctx);
      });
      function end() { drawing = null; }
      canvas.addEventListener("pointerup", end);
      canvas.addEventListener("pointercancel", end);

      var buttons = {};
      function pick(t) {
        tool = t;
        for (var k in buttons) buttons[k].setAttribute("aria-pressed", String(k === t));
      }
      var row = ["box", "arrow", "pen", "blackout"].map(function (t) {
        buttons[t] = el("button", { "class": "tool", type: "button", "aria-pressed": String(t === tool), text: T[t], on: { click: function () { pick(t); } } });
        return buttons[t];
      });
      row.push(el("button", { "class": "tool", type: "button", text: T.undo, on: { click: function () { state.shapes.pop(); render(ctx); } } }));
      row.push(el("button", { "class": "tool", type: "button", text: T.cancel, on: { click: showForm } }));
      row.push(el("button", { "class": "tool primary", type: "button", text: T.done, on: { click: function () { state.shot = flatten(canvas); showForm(); } } }));

      ui.appendChild(el("div", { "class": "editor", role: "dialog", "aria-label": T.editShot }, [el("div", { "class": "tools" }, row), canvas]));
    }

    // Everything drawn is burned into one image here, so nothing under a black-out survives.
    function flatten(canvas) {
      var scale = Math.min(1, MAX_WIDTH / canvas.width);
      var out = document.createElement("canvas");
      out.width = Math.round(canvas.width * scale);
      out.height = Math.round(canvas.height * scale);
      out.getContext("2d").drawImage(canvas, 0, 0, out.width, out.height);
      var quality = 0.8, url = out.toDataURL("image/jpeg", quality);
      while (url.length > MAX_DATA_URL && quality > 0.3) {
        quality -= 0.15;
        url = out.toDataURL("image/jpeg", quality);
      }
      return url.length > MAX_DATA_URL ? null : url;
    }

    function detectUa() {
      var u = navigator.userAgent || "";
      var browser = "other";
      if (/Edg\//.test(u)) browser = "Edge";
      else if (/OPR\//.test(u) || /Opera/.test(u)) browser = "Opera";
      else if (/Chrome\//.test(u)) browser = "Chrome";
      else if (/Firefox\//.test(u)) browser = "Firefox";
      else if (/Safari\//.test(u)) browser = "Safari";
      var os = "other";
      if (/Windows/.test(u)) os = "Windows";
      else if (/Mac OS X/.test(u)) os = "macOS";
      else if (/Android/.test(u)) os = "Android";
      else if (/(iPhone|iPad|iPod)/.test(u)) os = "iOS";
      else if (/Linux/.test(u)) os = "Linux";
      return { browser: browser, os: os, device: /Mobi|Android|iPhone|iPad|iPod/.test(u) ? "mobile" : "desktop" };
    }

    function submit(button, note) {
      button.disabled = true;
      button.textContent = T.sending;
      note.textContent = "";

      var sid = null;
      try { sid = sessionStorage.getItem("_ekrum_sid"); } catch (e) {}
      var ua = detectUa();

      // Path only: the query string and fragment are where tokens live.
      var body = {
        kind: state.kind, rating: state.rating, message: state.message, email: state.email,
        path: location.pathname, browser: ua.browser, os: ua.os, device: ua.device,
        viewportWidth: window.innerWidth, viewportHeight: window.innerHeight,
        sessionId: sid, errors: errors, screenshot: state.shot
      };

      // text/plain keeps this a simple request, as the RUM beacon is: no preflight.
      fetch(endpoint, { method: "POST", headers: { "content-type": "text/plain" }, body: JSON.stringify(body) })
        .then(function (r) {
          if (r.ok) {
            state = { kind: "Bug", rating: null, message: "", email: "", shot: null, base: null, shapes: [] };
            clear();
            ui.appendChild(el("div", { "class": "panel", role: "status" }, [
              el("div", { "class": "head" }, [
                el("h2", { text: T.title }),
                el("button", { "class": "x", type: "button", "aria-label": T.close, text: "×", on: { click: showLauncher } })
              ]),
              el("div", { "class": "thanks", text: T.thanks })
            ]));
            return;
          }
          throw r.status === 429 ? T.tooMany : T.failed;
        })
        .catch(function (e) {
          note.textContent = typeof e === "string" ? e : T.failed;
          button.disabled = false;
          button.textContent = T.send;
        });
    }

    function mount() {
      document.body.appendChild(host);
      showLauncher();
    }
    if (document.body) mount();
    else document.addEventListener("DOMContentLoaded", mount);
  } catch (e) {}
})();
""";
}
