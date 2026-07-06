namespace EntKube.Web.Services;

/// <summary>
/// The first-party browser RUM snippet, served verbatim at <c>GET /rum/v1/rum.js</c>. Self-contained
/// (no build step, no deps): reads its <c>data-key</c>, derives the ingest endpoint from its own origin,
/// collects Core Web Vitals (LCP/CLS/INP/FCP) + navigation timing, JS errors, and fetch/XHR timings
/// (injecting a W3C traceparent on same-origin calls so AJAX rows link into the trace waterfall), then
/// batches and ships via sendBeacon (text/plain, no CORS preflight). Sampling is enforced server-side.
/// </summary>
public static class RumSnippet
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
    var endpoint = origin + "/ingest/rum/v1/" + encodeURIComponent(key);

    var SID_KEY = "_ekrum_sid";
    var sid = null;
    try { sid = sessionStorage.getItem(SID_KEY); } catch (e) {}
    if (!sid) { sid = rid(); try { sessionStorage.setItem(SID_KEY, sid); } catch (e) {} }
    var vid = rid();

    var ua = detectUa();
    var q = { views: [], errors: [], resources: [] };
    var vitals = { lcp: null, cls: 0, inp: null, fcp: null };
    var flushed = false;

    function rid() { return Date.now().toString(36) + Math.random().toString(36).slice(2, 12); }
    function hex(n) { var s = ""; for (var i = 0; i < n; i++) s += Math.floor(Math.random() * 16).toString(16); return s; }
    function now() { return Date.now(); }
    function path() { return location.pathname; }
    function trim(s, n) { return (s && s.length > n) ? s.slice(0, n) : s; }

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
      var device = /Mobi|Android|iPhone|iPad|iPod/.test(u) ? "mobile" : "desktop";
      return { browser: browser, os: os, device: device };
    }

    function observe(type, cb, extra) {
      try {
        var po = new PerformanceObserver(function (list) { cb(list.getEntries()); });
        var opts = { type: type, buffered: true };
        if (extra) for (var k in extra) opts[k] = extra[k];
        po.observe(opts);
      } catch (e) {}
    }

    observe("largest-contentful-paint", function (es) { var l = es[es.length - 1]; if (l) vitals.lcp = Math.round(l.startTime); });
    observe("layout-shift", function (es) { for (var i = 0; i < es.length; i++) { if (!es[i].hadRecentInput) vitals.cls += es[i].value; } });
    observe("paint", function (es) { for (var i = 0; i < es.length; i++) { if (es[i].name === "first-contentful-paint") vitals.fcp = Math.round(es[i].startTime); } });
    observe("event", function (es) { for (var i = 0; i < es.length; i++) { var e = es[i]; if (e.interactionId && (vitals.inp === null || e.duration > vitals.inp)) vitals.inp = Math.round(e.duration); } }, { durationThreshold: 40 });

    function navTiming() {
      try {
        var nav = performance.getEntriesByType("navigation")[0];
        if (!nav) return {};
        return { ttfb: Math.round(nav.responseStart), load: nav.loadEventEnd ? Math.round(nav.loadEventEnd) : null };
      } catch (e) { return {}; }
    }

    window.addEventListener("error", function (ev) {
      var msg = (ev && ev.message) ? ev.message : "error";
      var src = (ev && ev.filename) ? (ev.filename + ":" + (ev.lineno || 0) + ":" + (ev.colno || 0)) : null;
      var stack = (ev && ev.error && ev.error.stack) ? String(ev.error.stack) : null;
      push("errors", { t: now(), msg: trim(msg, 2000), src: src, stack: trim(stack, 8000), path: path() });
    });
    window.addEventListener("unhandledrejection", function (ev) {
      var r = ev ? ev.reason : null;
      var msg = (r && r.message) ? r.message : String(r);
      var stack = (r && r.stack) ? String(r.stack) : null;
      push("errors", { t: now(), msg: trim("Unhandled rejection: " + msg, 2000), stack: trim(stack, 8000), path: path() });
    });

    function isSameOrigin(url) { try { return new URL(url, location.href).origin === location.origin; } catch (e) { return false; } }
    function record(url, kind, dur, status, trace) {
      push("resources", { t: now(), name: trim(url, 500), kind: kind, dur: Math.round(dur), status: status || null, trace: trace, path: path() });
    }

    // Capture the native fetch up front so our own beacon fallback (and the wrapper) never re-enter the wrapper.
    var nativeFetch = window.fetch ? window.fetch.bind(window) : null;
    if (nativeFetch) {
      window.fetch = function (input, init) {
        // input may be a string, URL object (.href) or Request (.url) — resolve to a URL string for all.
        var url = (typeof input === "string") ? input : (input ? (input.url || input.href || "") : "");
        var trace = null;
        if (url && isSameOrigin(url)) {   // only same-origin — never inject a header on a cross-origin request
          trace = hex(32);
          try {
            init = init || {};
            var h = new Headers((init && init.headers) || (typeof input !== "string" && input && input.headers) || {});
            h.set("traceparent", "00-" + trace + "-" + hex(16) + "-01");
            init.headers = h;
          } catch (e) { trace = null; }
        }
        var start = now();
        return nativeFetch(input, init).then(function (res) {
          record(url, "fetch", now() - start, res && res.status, trace); return res;
        }, function (err) { record(url, "fetch", now() - start, 0, trace); throw err; });
      };
    }

    if (window.XMLHttpRequest) {
      var Open = XMLHttpRequest.prototype.open;
      var Send = XMLHttpRequest.prototype.send;
      XMLHttpRequest.prototype.open = function (method, url) {
        this._ek = { url: String(url || ""), start: 0 };
        // Attach loadend ONCE per object (reads the current _ek), else reusing an XHR stacks listeners
        // that emit duplicate, stale-duration rows.
        if (!this._ekBound) {
          this._ekBound = true;
          this.addEventListener("loadend", function () {
            var m = this._ek;
            if (m) record(m.url, "xhr", now() - m.start, this.status, m.trace || null);
          });
        }
        return Open.apply(this, arguments);
      };
      XMLHttpRequest.prototype.send = function () {
        var m = this._ek;
        if (m) {
          if (m.url && isSameOrigin(m.url)) { m.trace = hex(32); try { this.setRequestHeader("traceparent", "00-" + m.trace + "-" + hex(16) + "-01"); } catch (e) {} }
          m.start = now();
        }
        return Send.apply(this, arguments);
      };
    }

    function push(bucket, ev) { q[bucket].push(ev); if (q[bucket].length > 200) q[bucket].shift(); schedule(); }

    var timer = null;
    function schedule() { if (timer) return; timer = setTimeout(function () { timer = null; send(false); }, 5000); }

    function buildPageView() {
      var nt = navTiming();
      return { t: now(), path: path(), load: nt.load, ttfb: nt.ttfb, lcp: vitals.lcp, cls: Math.round(vitals.cls * 1000) / 1000, inp: vitals.inp, fcp: vitals.fcp };
    }

    function send(final) {
      if (final && !flushed) { q.views.push(buildPageView()); flushed = true; }
      if (!q.views.length && !q.errors.length && !q.resources.length) return;
      var payload = { session: sid, view: vid, path: path(), referrer: document.referrer || null, ua: ua, views: q.views, errors: q.errors, resources: q.resources };
      q = { views: [], errors: [], resources: [] };
      var body = JSON.stringify(payload);
      var ok = false;
      try { if (navigator.sendBeacon) ok = navigator.sendBeacon(endpoint, new Blob([body], { type: "text/plain" })); } catch (e) {}
      // Fallback uses the NATIVE fetch, not window.fetch — otherwise the ingest POST records itself as a
      // resource and re-arms the flush timer, looping on browsers without sendBeacon.
      if (!ok && nativeFetch) { try { nativeFetch(endpoint, { method: "POST", body: body, headers: { "Content-Type": "text/plain" }, keepalive: true, mode: "cors" }); } catch (e) {} }
    }

    addEventListener("visibilitychange", function () { if (document.visibilityState === "hidden") send(true); });
    addEventListener("pagehide", function () { send(true); });
    // A bfcache-restored page is a fresh visit: reset the one-shot page-view latch and start a new view id,
    // else its vitals/errors never produce a page-view row.
    addEventListener("pageshow", function (e) { if (e && e.persisted) { flushed = false; vid = rid(); } });
  } catch (e) { /* never break the host page */ }
})();
""";
}
