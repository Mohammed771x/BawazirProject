// QA prompt 06: the admin-web accessibility pass, pasted into the browser
// console on `npm run dev:mock` (http://localhost:5181/admin/). Walks the
// pages through the router and returns one row per page. A heuristic, not a
// WCAG conformance claim: contrast is computed against the nearest opaque
// background, and charts are counted, not judged.
(async () => {
  const lum = c => { const m = c.match(/[\d.]+/g); if (!m) return null;
    const [r, g, b] = m.slice(0, 3).map(Number).map(v => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); });
    return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
  const alpha = c => { const m = c.match(/[\d.]+/g); return m && m.length > 3 ? Number(m[3]) : 1; };
  const bgOf = el => { for (let e = el; e; e = e.parentElement) { const b = getComputedStyle(e).backgroundColor; if (alpha(b) > 0.9) return b; } return getComputedStyle(document.body).backgroundColor; };
  const name = el => (el.getAttribute('aria-label') || el.getAttribute('title') || el.innerText || '').trim();
  const audit = () => {
    const visible = e => e.offsetParent !== null;
    const unnamed = [...document.querySelectorAll('button,a[href],[role=button],[role=tab]')].filter(e => visible(e) && !name(e)).length;
    const unlabeled = [...document.querySelectorAll('input,select,textarea')].filter(e => e.type !== 'hidden' && visible(e) && !(e.labels && e.labels.length) && !e.getAttribute('aria-label') && !e.getAttribute('title')).length;
    let low = 0;
    for (const el of document.querySelectorAll('body *')) {
      if (![...el.childNodes].some(n => n.nodeType === 3 && n.textContent.trim()) || !visible(el)) continue;
      const cs = getComputedStyle(el); const f = lum(cs.color), b = lum(bgOf(el));
      if (f == null || b == null || alpha(cs.color) < 0.5) continue;
      const r = (Math.max(f, b) + 0.05) / (Math.min(f, b) + 0.05);
      const size = parseFloat(cs.fontSize), bold = Number(cs.fontWeight) >= 700;
      if (r < ((size >= 24 || (bold && size >= 18.66)) ? 3 : 4.5)) low++;
    }
    const charts = [...document.querySelectorAll('svg')].filter(s => s.getBoundingClientRect().width > 80);
    const chartsNoName = charts.filter(s => !s.getAttribute('aria-label') && !s.querySelector('title') && s.getAttribute('aria-hidden') !== 'true' && !s.closest('[aria-label]')).length;
    const small = [...document.querySelectorAll('button,a[href],input,select')].filter(visible).map(e => e.getBoundingClientRect()).filter(r => r.width > 0 && (r.height < 24 || r.width < 24)).length;
    return { path: location.pathname, width: innerWidth, lang: document.documentElement.lang, dir: document.documentElement.dir,
      hScroll: document.documentElement.scrollWidth > innerWidth + 1, unnamed, unlabeled, lowContrast: low, charts: charts.length, chartsNoName, targetsUnder24: small };
  };
  const pages = ['/admin/', '/admin/users', '/admin/learning', '/admin/behavior', '/admin/retention', '/admin/notifications',
    '/admin/problems', '/admin/feedback', '/admin/ai', '/admin/decisions', '/admin/changes', '/admin/settings', '/admin/learning/READING'];
  const out = [];
  for (const p of pages) { history.pushState({}, '', p); window.dispatchEvent(new PopStateEvent('popstate')); await new Promise(r => setTimeout(r, 1200)); out.push(audit()); }
  console.table(out);
  return out;
})();
