// Keycloak visual theme editor — preview iframe management

const _SHADOW_MAP = [
    'none',
    '0 1px 3px rgba(0,0,0,.12),0 1px 2px rgba(0,0,0,.08)',
    '0 4px 12px rgba(0,0,0,.16),0 2px 4px rgba(0,0,0,.12)',
    '0 12px 28px rgba(0,0,0,.22),0 4px 8px rgba(0,0,0,.14)',
];

function _cardShadowCss(level) {
    return _SHADOW_MAP[Math.min(3, Math.max(0, level ?? 1))] ?? 'none';
}

// Map of Google-font family name → URL-encoded value for the Fonts v2 API
const GOOGLE_FONTS = {
    'inter':         'Inter',
    'roboto':        'Roboto',
    'open sans':     'Open+Sans',
    'lato':          'Lato',
    'source sans 3': 'Source+Sans+3',
};

// Per-iframe set of already-injected font names (lower-case)
const _loadedFonts = {};

function _loadPreviewFont(iframeId, doc, fontFamily) {
    const firstFont = fontFamily.split(',')[0].trim().replace(/['"]/g, '').toLowerCase();
    if (!GOOGLE_FONTS[firstFont]) return;
    if (!_loadedFonts[iframeId]) _loadedFonts[iframeId] = new Set();
    if (_loadedFonts[iframeId].has(firstFont)) return;
    _loadedFonts[iframeId].add(firstFont);
    const link = doc.createElement('link');
    link.rel = 'stylesheet';
    link.href = `https://fonts.googleapis.com/css2?family=${GOOGLE_FONTS[firstFont]}:wght@400;500;600&display=swap`;
    doc.head.appendChild(link);
}

// ── Shared initial form HTML (identical for v1 and v2 previews) ────────────

const _FORM_INNER = `<div id="lp-error" style="display:none;background:color-mix(in srgb,var(--ek-error) 10%,transparent);border:1px solid var(--ek-error);border-radius:4px;padding:8px 12px;margin-bottom:14px;font-size:.82rem;color:var(--ek-error);">
          Invalid username or password.
        </div>
        <h1 class="lp-title">Sign in to your account</h1>
        <form onsubmit="return false">
          <div class="lp-group">
            <label for="lp-user">Username or email</label>
            <input class="lp-input" type="text" id="lp-user" placeholder="Enter username" autocomplete="off">
          </div>
          <div class="lp-group">
            <label for="lp-pass">Password</label>
            <input class="lp-input" type="password" id="lp-pass" placeholder="Enter password">
            <a href="#" class="lp-forgot">Forgot password?</a>
          </div>
          <button class="lp-btn" type="submit">Sign In</button>
        </form>
        <div class="lp-divider" id="lp-divider" style="display:none">or</div>
        <div id="lp-social"></div>`;

// ── Shared CSS block (everything except header layout) ─────────────────────

const _SHARED_CSS = `*,*::before,*::after{box-sizing:border-box}
:root{
  --ek-primary:#0066CC;--ek-primary-dark:#004E99;--ek-bg:#f4f4f4;
  --ek-card-bg:#ffffff;--ek-text:#151515;--ek-muted:#6a6e73;
  --ek-link:#0066CC;--ek-input-border:#8a8d90;--ek-input-bg:#ffffff;
  --ek-input-text:#151515;--ek-btn-text:#ffffff;--ek-error:#c9190b;
  --ek-header-bg:#151515;--ek-header-text:#ffffff;
  --ek-font:system-ui,-apple-system,sans-serif;--ek-font-size:14px;
  --ek-btn-radius:3px;--ek-input-radius:3px;--ek-card-radius:4px;
  --ek-card-shadow:0 1px 3px rgba(0,0,0,.12),0 1px 2px rgba(0,0,0,.08);
  --ek-card-max-width:500px;--ek-logo-height:48px;
}
body{margin:0;padding:0;font-family:var(--ek-font);font-size:var(--ek-font-size);background-color:var(--ek-bg);color:var(--ek-text);min-height:100vh;}
.lp-page{display:flex;flex-direction:column;min-height:100vh}
.lp-container{width:100%;max-width:var(--ek-card-max-width,500px)}
.lp-logo{height:var(--ek-logo-height);max-width:240px;object-fit:contain}
.lp-card{background:var(--ek-card-bg);border-radius:var(--ek-card-radius);box-shadow:var(--ek-card-shadow);padding:32px 36px 24px;}
.lp-title{font-size:1.2rem;font-weight:600;color:var(--ek-text);margin:0 0 22px}
.lp-group{margin-bottom:14px}
label{display:block;font-size:.85rem;font-weight:500;color:var(--ek-text);margin-bottom:5px}
.lp-input{display:block;width:100%;padding:8px 10px;font-size:var(--ek-font-size);font-family:var(--ek-font);color:var(--ek-input-text);background:var(--ek-input-bg);border:1px solid var(--ek-input-border);border-radius:var(--ek-input-radius);outline:none;}
.lp-input:focus{border-color:var(--ek-primary);box-shadow:0 0 0 3px color-mix(in srgb,var(--ek-primary) 18%,transparent)}
.lp-forgot{display:block;font-size:.78rem;color:var(--ek-link);text-decoration:none;margin-top:3px;text-align:right}
.lp-btn{display:block;width:100%;padding:9px 16px;margin-top:6px;font-size:var(--ek-font-size);font-family:var(--ek-font);font-weight:500;color:var(--ek-btn-text);background:var(--ek-primary);border:none;border-radius:var(--ek-btn-radius);cursor:pointer;transition:background .12s;}
.lp-btn:hover{background:var(--ek-primary-dark)}
.lp-divider{display:flex;align-items:center;margin:18px 0;color:var(--ek-muted);font-size:.75rem}
.lp-divider::before,.lp-divider::after{content:'';flex:1;height:1px;background:var(--ek-input-border);margin:0 8px}
.lp-social-btn{width:100%;padding:7px 12px;margin-bottom:8px;background:#fff;border:1px solid var(--ek-input-border);border-radius:var(--ek-input-radius);font-size:.82rem;font-family:var(--ek-font);color:var(--ek-text);cursor:pointer;display:flex;align-items:center;justify-content:center;gap:8px;}
.lp-footer{padding:12px;text-align:center;font-size:.72rem;color:var(--ek-muted);}
.lp-grid{display:grid;grid-template-columns:1fr 1fr;gap:10px}
.lp-sub{font-size:.85rem;color:var(--ek-muted);margin:-10px 0 16px}
.lp-link-row{text-align:center;font-size:.78rem;color:var(--ek-muted);margin-top:14px}
.lp-link-row a{color:var(--ek-link);text-decoration:none}`;

// ── Build a preview HTML document for the given base theme ─────────────────

function _buildPreviewHtml(isV1) {
    // Header CSS differs: v1 = full-width coloured bar; v2 = minimal logo-above-card area
    const headerCss = isV1
        ? `.lp-header{background:var(--ek-header-bg);padding:0 24px;display:flex;align-items:center;min-height:60px;}
.lp-logo-text{color:var(--ek-header-text);font-size:1.1rem;font-weight:600;letter-spacing:.02em}
.lp-main{flex:1;display:flex;align-items:center;justify-content:center;padding:40px 16px;}`
        : `.lp-header{display:flex;align-items:center;padding:0 2px 20px;min-height:56px;}
.lp-logo-text{color:var(--ek-text);font-size:1.1rem;font-weight:600;letter-spacing:.02em}
.lp-main{flex:1;display:flex;align-items:center;justify-content:center;padding:48px 16px 40px;}`;

    const header = `<div class="lp-header" id="lp-header"><span id="lp-logo" class="lp-logo-text">Brand</span></div>`;
    const card   = `<div class="lp-card">${_FORM_INNER}</div>`;
    const footer = `<div class="lp-footer" id="lp-footer" style="display:none"></div>`;

    // v1: header bar sits OUTSIDE .lp-main (full width at top of page)
    // v2: header/logo lives INSIDE .lp-container (above the card, on page background)
    const body = isV1
        ? `${header}<div class="lp-main"><div class="lp-container">${card}${footer}</div></div>`
        : `<div class="lp-main"><div class="lp-container">${header}${card}${footer}</div></div>`;

    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
${_SHARED_CSS}
${headerCss}
</style>
<style id="ek-vars"></style>
<style id="ek-extra"></style>
</head>
<body>
<div class="lp-page">
  ${body}
</div>
</body>
</html>`;
}

const _BASE_HTML_V1 = _buildPreviewHtml(true);
const _BASE_HTML_V2 = _buildPreviewHtml(false);

// Map of iframeId → {ready: bool, pendingUpdate: object|null}
const _state = {};

export function initPreview(iframeId, baseTheme) {
    const iframe = document.getElementById(iframeId);
    if (!iframe) return;
    _state[iframeId] = { ready: false, pendingUpdate: null };
    iframe.srcdoc = baseTheme === 'keycloak' ? _BASE_HTML_V1 : _BASE_HTML_V2;
    iframe.onload = () => {
        _state[iframeId].ready = true;
        if (_state[iframeId].pendingUpdate) {
            _applyUpdate(iframeId, _state[iframeId].pendingUpdate);
            _state[iframeId].pendingUpdate = null;
        }
    };
}

export function updatePreview(iframeId, vars, logoDataUri, bgDataUri) {
    const update = { vars, logoDataUri, bgDataUri };
    if (!_state[iframeId]?.ready) {
        if (_state[iframeId]) _state[iframeId].pendingUpdate = update;
        return;
    }
    _applyUpdate(iframeId, update);
}

function _applyUpdate(iframeId, { vars, logoDataUri, bgDataUri }) {
    const iframe = document.getElementById(iframeId);
    const doc = iframe?.contentDocument;
    if (!doc) return;

    // Build CSS variable block
    let css = `:root{`;
    css += `--ek-primary:${vars.primaryColor};`;
    css += `--ek-primary-dark:${vars.primaryColorDark};`;
    css += `--ek-bg:${vars.pageBackground};`;
    css += `--ek-card-bg:${vars.cardBackground};`;
    css += `--ek-text:${vars.textColor};`;
    css += `--ek-muted:${vars.mutedTextColor};`;
    css += `--ek-link:${vars.linkColor};`;
    css += `--ek-input-border:${vars.inputBorderColor};`;
    css += `--ek-input-bg:${vars.inputBackground};`;
    css += `--ek-input-text:${vars.inputTextColor};`;
    css += `--ek-btn-text:${vars.buttonTextColor};`;
    css += `--ek-error:${vars.errorColor};`;
    css += `--ek-header-bg:${vars.headerBackground};`;
    css += `--ek-header-text:${vars.headerTextColor};`;
    css += `--ek-font:${vars.fontFamily};`;
    css += `--ek-font-size:${vars.fontSizePx}px;`;
    css += `--ek-btn-radius:${vars.buttonRadiusPx}px;`;
    css += `--ek-input-radius:${vars.inputRadiusPx}px;`;
    css += `--ek-card-radius:${vars.cardRadiusPx}px;`;
    css += `--ek-card-shadow:${_cardShadowCss(vars.cardShadowLevel)};`;
    css += `--ek-card-max-width:${vars.cardMaxWidthPx ?? 500}px;`;
    css += `--ek-logo-height:${vars.logoHeightPx}px;`;
    css += `}`;

    // Background image
    if (vars.useBackgroundImage && bgDataUri) {
        css += `body{background-image:url('${bgDataUri}');background-size:cover;background-position:center;}`;
    } else {
        css += `body{background-image:none;}`;
    }

    const styleEl = doc.getElementById('ek-vars');
    if (styleEl) styleEl.textContent = css;

    const extraEl = doc.getElementById('ek-extra');
    if (extraEl) extraEl.textContent = vars.extraCss || '';

    _loadPreviewFont(iframeId, doc, vars.fontFamily);

    // Header visibility
    const header = doc.getElementById('lp-header');
    if (header) header.style.display = vars.showLogo ? '' : 'none';

    // Logo element
    const logoEl = doc.getElementById('lp-logo');
    if (logoEl) {
        const src = logoDataUri || vars.logoExternalUrl || null;
        if (src) {
            if (logoEl.tagName !== 'IMG') {
                const img = doc.createElement('img');
                img.id = 'lp-logo';
                img.className = 'lp-logo';
                img.alt = 'Logo';
                header.replaceChild(img, logoEl);
                img.src = src;
            } else {
                logoEl.src = src;
            }
        } else {
            if (logoEl.tagName === 'IMG') {
                const span = doc.createElement('span');
                span.id = 'lp-logo';
                span.className = 'lp-logo-text';
                span.textContent = 'Brand';
                header.replaceChild(span, logoEl);
            }
        }
    }

    // Footer
    const footer = doc.getElementById('lp-footer');
    if (footer) {
        if (vars.showFooter && vars.footerText) {
            footer.textContent = vars.footerText;
            footer.style.display = 'block';
        } else {
            footer.style.display = 'none';
        }
    }
}

// Direct property setter — called from oninput handlers for zero-latency preview
export function setPreviewVar(iframeId, prop, value) {
    const doc = document.getElementById(iframeId)?.contentDocument;
    if (!doc) return;
    doc.documentElement.style.setProperty(prop, value);
    if (prop === '--ek-font') _loadPreviewFont(iframeId, doc, value);
}

export function setPreviewBodyBg(iframeId, dataUri) {
    const doc = document.getElementById(iframeId)?.contentDocument;
    if (!doc) return;
    doc.body.style.backgroundImage = dataUri ? `url('${dataUri}')` : 'none';
    if (dataUri) {
        doc.body.style.backgroundSize = 'cover';
        doc.body.style.backgroundPosition = 'center';
    }
}

export function setPreviewHeaderVisible(iframeId, visible) {
    const el = document.getElementById(iframeId)?.contentDocument?.getElementById('lp-header');
    if (el) el.style.display = visible ? '' : 'none';
}

export function setPreviewFooter(iframeId, visible, text) {
    const el = document.getElementById(iframeId)?.contentDocument?.getElementById('lp-footer');
    if (!el) return;
    if (visible && text) { el.textContent = text; el.style.display = 'block'; }
    else el.style.display = 'none';
}

export function setPreviewLogo(iframeId, dataUri, externalUrl, heightPx) {
    const doc = document.getElementById(iframeId)?.contentDocument;
    if (!doc) return;
    const header = doc.getElementById('lp-header');
    if (!header) return;
    const src = dataUri || externalUrl || null;
    const existing = doc.getElementById('lp-logo');
    doc.documentElement.style.setProperty('--ek-logo-height', `${heightPx}px`);
    if (src) {
        if (existing?.tagName !== 'IMG') {
            const img = doc.createElement('img');
            img.id = 'lp-logo'; img.className = 'lp-logo'; img.alt = 'Logo'; img.src = src;
            if (existing) header.replaceChild(img, existing); else header.appendChild(img);
        } else {
            existing.src = src;
        }
    } else {
        if (existing?.tagName === 'IMG') {
            const span = doc.createElement('span');
            span.id = 'lp-logo'; span.className = 'lp-logo-text'; span.textContent = 'Brand';
            header.replaceChild(span, existing);
        }
    }
}

// ── Page type templates ────────────────────────────────────────────────────

const _ERR = `<div id="lp-error" style="display:none;background:color-mix(in srgb,var(--ek-error) 10%,transparent);border:1px solid var(--ek-error);border-radius:4px;padding:8px 12px;margin-bottom:14px;font-size:.82rem;color:var(--ek-error);">Invalid username or password.</div>`;

// Inline SVG icons for social buttons (no external resources)
const _GOOGLE_SVG = `<svg width="16" height="16" viewBox="0 0 24 24" aria-hidden="true"><path d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z" fill="#4285F4"/><path d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z" fill="#34A853"/><path d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l3.66-2.84z" fill="#FBBC05"/><path d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z" fill="#EA4335"/></svg>`;
const _GITHUB_SVG = `<svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12 2C6.477 2 2 6.484 2 12.017c0 4.425 2.865 8.18 6.839 9.504.5.092.682-.217.682-.483 0-.237-.008-.868-.013-1.703-2.782.605-3.369-1.343-3.369-1.343-.454-1.158-1.11-1.466-1.11-1.466-.908-.62.069-.608.069-.608 1.003.07 1.531 1.032 1.531 1.032.892 1.53 2.341 1.088 2.91.832.092-.647.35-1.088.636-1.338-2.22-.253-4.555-1.113-4.555-4.951 0-1.093.39-1.988 1.029-2.688-.103-.253-.446-1.272.098-2.65 0 0 .84-.27 2.75 1.026A9.564 9.564 0 0112 6.844a9.59 9.59 0 012.504.337c1.909-1.296 2.747-1.027 2.747-1.027.546 1.379.202 2.398.1 2.651.64.7 1.028 1.595 1.028 2.688 0 3.848-2.339 4.695-4.566 4.943.359.309.678.92.678 1.855 0 1.338-.012 2.419-.012 2.747 0 .268.18.58.688.482A10.019 10.019 0 0022 12.017C22 6.484 17.522 2 12 2z"/></svg>`;
const _MS_SVG = `<svg width="16" height="16" viewBox="0 0 24 24" aria-hidden="true"><rect x="1" y="1" width="10" height="10" fill="#F25022"/><rect x="13" y="1" width="10" height="10" fill="#7FBA00"/><rect x="1" y="13" width="10" height="10" fill="#00A4EF"/><rect x="13" y="13" width="10" height="10" fill="#FFB900"/></svg>`;
const _SOCIAL_HTML = `
<button class="lp-social-btn">${_GOOGLE_SVG} Continue with Google</button>
<button class="lp-social-btn">${_GITHUB_SVG} Continue with GitHub</button>
<button class="lp-social-btn">${_MS_SVG} Continue with Microsoft</button>`;

const PAGE_INNER = {
    login: `${_ERR}
<h1 class="lp-title">Sign in to your account</h1>
<form onsubmit="return false">
  <div class="lp-group">
    <label>Username or email</label>
    <input class="lp-input" type="text" placeholder="Enter username" autocomplete="off">
  </div>
  <div class="lp-group">
    <label>Password</label>
    <input class="lp-input" type="password" placeholder="Enter password">
    <a href="#" class="lp-forgot">Forgot password?</a>
  </div>
  <button class="lp-btn" type="submit">Sign In</button>
</form>
<div class="lp-divider" id="lp-divider" style="display:none">or</div>
<div id="lp-social"></div>`,
    register: `${_ERR}
<h1 class="lp-title">Create your account</h1>
<form onsubmit="return false">
  <div class="lp-grid">
    <div class="lp-group"><label>First name</label><input class="lp-input" placeholder="First name"></div>
    <div class="lp-group"><label>Last name</label><input class="lp-input" placeholder="Last name"></div>
  </div>
  <div class="lp-group"><label>Email</label><input class="lp-input" type="email" placeholder="name@company.com"></div>
  <div class="lp-group"><label>Username</label><input class="lp-input" placeholder="Choose a username"></div>
  <div class="lp-grid">
    <div class="lp-group"><label>Password</label><input class="lp-input" type="password" placeholder="Password"></div>
    <div class="lp-group"><label>Confirm</label><input class="lp-input" type="password" placeholder="Repeat password"></div>
  </div>
  <button class="lp-btn" type="submit">Register</button>
</form>
<div class="lp-link-row">Already have an account? <a href="#">Sign in</a></div>`,
    forgot: `${_ERR}
<h1 class="lp-title">Forgot your password?</h1>
<p class="lp-sub">Enter your username or email and we&rsquo;ll send a reset link.</p>
<form onsubmit="return false">
  <div class="lp-group">
    <label>Username or email</label>
    <input class="lp-input" type="text" placeholder="Enter username or email" autocomplete="off">
  </div>
  <button class="lp-btn" type="submit">Send reset link</button>
</form>
<div class="lp-link-row"><a href="#">Back to sign in</a></div>`,
    otp: `${_ERR}
<h1 class="lp-title">Two-step verification</h1>
<p class="lp-sub">Enter the 6-digit code from your authenticator app.</p>
<form onsubmit="return false">
  <div class="lp-group">
    <label>One-time code</label>
    <input class="lp-input" type="text" placeholder="000 000"
      style="letter-spacing:.35em;text-align:center;font-size:1.2rem" autocomplete="one-time-code">
  </div>
  <button class="lp-btn" type="submit">Verify</button>
</form>
<div class="lp-link-row"><a href="#">Try another way to sign in</a></div>`,
};

export function setPreviewPage(iframeId, type, showError, showSocial) {
    const doc = document.getElementById(iframeId)?.contentDocument;
    if (!doc) return;
    const card = doc.querySelector('.lp-card');
    if (!card) return;
    card.innerHTML = PAGE_INNER[type] ?? PAGE_INNER.login;
    const errEl = card.querySelector('#lp-error');
    if (errEl) errEl.style.display = showError ? 'block' : 'none';
    // Social buttons only on login page
    if (type === 'login') _applySocial(card, showSocial);
}

export function setPreviewSocial(iframeId, enabled) {
    const doc = document.getElementById(iframeId)?.contentDocument;
    if (!doc) return;
    _applySocial(doc.querySelector('.lp-card'), enabled);
}

function _applySocial(card, enabled) {
    if (!card) return;
    const social = card.querySelector('#lp-social');
    const divider = card.querySelector('#lp-divider');
    if (!social || !divider) return;
    if (enabled) {
        social.innerHTML = _SOCIAL_HTML;
        divider.style.display = 'flex';
    } else {
        social.innerHTML = '';
        divider.style.display = 'none';
    }
}

export function openPreviewTab(iframeId) {
    const doc = document.getElementById(iframeId)?.contentDocument;
    if (!doc) return;
    const html = '<!DOCTYPE html>' + doc.documentElement.outerHTML;
    const url = URL.createObjectURL(new Blob([html], { type: 'text/html' }));
    window.open(url, '_blank');
    setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

export function destroyPreview(iframeId) {
    delete _state[iframeId];
    delete _loadedFonts[iframeId];
}

export async function copyToClipboard(text) {
    try { await navigator.clipboard.writeText(text); return true; }
    catch { return false; }
}

export function setPreviewErrorState(iframeId, visible) {
    const el = document.getElementById(iframeId)?.contentDocument?.getElementById('lp-error');
    if (el) el.style.display = visible ? 'block' : 'none';
}

export function setPreviewExtraCss(iframeId, css) {
    const el = document.getElementById(iframeId)?.contentDocument?.getElementById('ek-extra');
    if (el) el.textContent = css || '';
}

export function downloadJson(filename, content) {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([content], { type: 'application/json' }));
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(a.href);
}

// ── Section-dialog Escape-to-close ──────────────────────────────────────────
let _escHandler = null;

export function registerKeyClose(dotNetRef) {
    unregisterKeyClose();
    _escHandler = (e) => {
        if (e.key === 'Escape') dotNetRef.invokeMethodAsync('OnDialogEscape');
    };
    document.addEventListener('keydown', _escHandler);
}

export function unregisterKeyClose() {
    if (_escHandler) {
        document.removeEventListener('keydown', _escHandler);
        _escHandler = null;
    }
}
