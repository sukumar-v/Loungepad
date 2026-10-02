/* loungepad.app — the interactive parts. The copy lives in index.html; this file only moves it.
   Built from website/design/Main.dc.html: the TV's attract cycle, the viewer, the Power Wheel,
   the controller stage, and the Gamepad API / keyboard navigation that drives all of it. */
(function () {
  'use strict';

  var DL = 'https://github.com/sukumar-v/Loungepad/releases/latest';
  var GH = 'https://github.com/sukumar-v/Loungepad';
  var WIDTHS = [480, 720, 1080, 1440, 1920];
  var VIEWER_SIZES = 'min(calc(100vw - 170px), calc((100vh - 220px) * 1.7778))';
  var ICON = {
    pointer: 'M5.5 3.5 18.5 11l-5.6 1.7 3.2 6-2.7 1.4-3.2-6-4.7 3.9z',
    moon: 'M20.5 14.2A8.5 8.5 0 0 1 9.8 3.5a8.5 8.5 0 1 0 10.7 10.7z',
    heart: 'M12 20s-7.5-4.6-7.5-10.2A4.3 4.3 0 0 1 12 7.2a4.3 4.3 0 0 1 7.5 2.6C19.5 15.4 12 20 12 20z'
  };
  // The app's own accents (ACCENTS in Loungepad/ui/app.js).
  var ACCENTS = [
    { name: 'Ember', hex: '#F0A253' }, { name: 'Coral', hex: '#E97A6C' }, { name: 'Rose', hex: '#F07AA8' }, { name: 'Orchid', hex: '#C78BE8' },
    { name: 'Indigo', hex: '#8098F0' }, { name: 'Aqua', hex: '#5FC9D6' }, { name: 'Mint', hex: '#6FCF97' }, { name: 'Lime', hex: '#B8D96B' }
  ];
  var FAM_NAME = { xbox: 'Xbox', playstation: 'PlayStation', switch: 'Switch Pro', keyboard: 'Keyboard' };
  var NAMES = {
    xbox: { guide: 'Xbox button', view: 'View', y: 'Y' },
    playstation: { guide: 'PS button', view: 'Create', y: 'Triangle' },
    switch: { guide: 'Home', view: 'Minus', y: 'X' },
    keyboard: { guide: 'Guide', view: 'View', y: 'Y' }
  };

  var $ = function (id) { return document.getElementById(id); };
  var all = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };

  var root = $('lp');
  var page = $('page');
  var tv = $('tv');
  var shots = all('.lp-shot', tv);
  var glowImgs = all('.lp-tv-glow img').concat(all('.lp-console-glow img'));
  var shotTexts = all('#shotText [data-shot]');
  var dots = all('.lp-dot');
  var tiles = all('.lp-tile');
  var wheel = $('wheel');
  var spokes = all('.lp-wheel-spoke', wheel);
  var viewer = $('viewer');

  var S = {
    input: 'mouse', fam: 'xbox', famLocked: false, padFam: null, padName: null,
    accent: '#F0A253', shot: 0, hold: false, reduce: false, viewer: null, wheel: false, spoke: 0
  };
  var attract = true;
  var cycleTimer = null;
  var opener = null;

  // ---- the TV ----
  function cycling() { return attract && !S.hold && !S.reduce; }
  function renderDots() {
    var on = cycling();
    dots.forEach(function (d, i) {
      var active = i === S.shot;
      d.setAttribute('aria-pressed', active ? 'true' : 'false');
      var bar = d.firstElementChild;
      // A fresh span restarts the progress animation from 0, so the pill is honest.
      bar.innerHTML = active ? (on ? '<span class="lp-dot-prog"></span>' : '<span class="lp-dot-still"></span>') : '';
    });
  }
  function setShot(i) {
    S.shot = i;
    shots.forEach(function (p, k) { p.classList.toggle('on', k === i); });
    glowImgs.forEach(function (img, k) { img.classList.toggle('on', k % shots.length === i); });
    shotTexts.forEach(function (t, k) { t.hidden = k !== i; });
    var title = shotTexts[i].querySelector('.lp-shot-title').textContent;
    tv.setAttribute('aria-label', title + ' — see it larger');
    renderDots();
  }
  // Attract mode: the TV walks through the screens on its own until someone takes over.
  // Restarted on every manual change so the progress pill is honest.
  function startCycle() {
    clearInterval(cycleTimer);
    cycleTimer = setInterval(function () {
      if (!attract || S.reduce || S.hold || S.viewer != null || S.wheel) return;
      if (document.hidden) return;
      setShot((S.shot + 1) % shots.length);
    }, 7000);
  }
  function stepShot(d) {
    S.hold = true;
    setShot((S.shot + d + shots.length) % shots.length);
    startCycle();
  }
  function hold() { if (!S.hold) { S.hold = true; renderDots(); } }
  function release() { S.hold = false; renderDots(); startCycle(); }

  // ---- the accent: on the document as custom properties, the way the launcher's applyTheme does it ----
  function applyAccent(hex) {
    var h = String(hex).replace('#', '');
    var full = h.length === 3 ? h.split('').map(function (c) { return c + c; }).join('') : h.slice(0, 6);
    var n = parseInt(full, 16);
    if (isNaN(n)) return;
    S.accent = '#' + full.toUpperCase();
    var rgb = [(n >> 16) & 255, (n >> 8) & 255, n & 255].join(',');
    var st = document.documentElement.style;
    st.setProperty('--accent', '#' + full);
    [10, 15, 20, 30, 40, 50].forEach(function (p) { st.setProperty('--accent-' + p, 'rgba(' + rgb + ',' + (p / 100) + ')'); });
    var acc = null;
    all('.lp-swatch').forEach(function (b) {
      var on = b.getAttribute('data-hex').toUpperCase() === S.accent;
      b.setAttribute('aria-pressed', on ? 'true' : 'false');
      if (on) acc = ACCENTS.filter(function (a) { return a.hex.toUpperCase() === S.accent; })[0] || null;
    });
    $('accentName').textContent = acc ? acc.name : 'Custom';
  }

  // ---- the controller stage ----
  function setFam(fam) {
    if (!FAM_NAME[fam]) fam = 'xbox';
    S.fam = fam;
    window.LPGlyph.paint(fam);
    $('famName').textContent = FAM_NAME[fam];
    all('.lp-chip').forEach(function (c) { c.setAttribute('aria-pressed', c.getAttribute('data-fam') === fam ? 'true' : 'false'); });
    $('mapPad').hidden = fam === 'keyboard';
    $('mapKeys').hidden = fam !== 'keyboard';
    $('rowTouch').hidden = fam !== 'playstation';
    var names = NAMES[fam];
    all('[data-name]').forEach(function (el) { el.textContent = names[el.getAttribute('data-name')]; });
    renderPad();
  }
  function renderPad() {
    var fam = S.fam, name = S.padName;
    $('famCap').textContent = name && !S.famLocked ? 'Drawn for the ' + name + ' in your hands'
      : fam === 'keyboard' ? 'Shown as keys on your keyboard' : 'The buttons, as they look on this controller';
    var selectName = fam === 'playstation' ? 'Cross' : fam === 'switch' ? 'B' : 'A';
    $('detectLine').textContent = name
      ? 'Your ' + name + ' is connected. Try it on this page: the D-pad moves, ' + selectName + ' selects, and Menu opens the wheel.'
      : 'Plug in a controller and press any button: this page will show its buttons, and you can browse it from the couch.';
    $('legendLeft').textContent = name ? name + ' connected' : 'Loungepad for Windows';
    root.classList.toggle('lp-pad-on', !!name);
  }
  function setInput(v) {
    if (S.input === v) return;
    S.input = v;
    root.setAttribute('data-input', v);
  }

  // ---- pictures for the viewer (the same set index.html carries) ----
  function esc(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'); }
  function srcset(key, ext) { return WIDTHS.map(function (w) { return 'img/' + key + '-' + w + '.' + ext + ' ' + w + 'w'; }).join(', '); }
  function pictureHtml(key, sizes, alt) {
    return '<picture><source type="image/avif" srcset="' + srcset(key, 'avif') + '" sizes="' + sizes + '">' +
      '<source type="image/webp" srcset="' + srcset(key, 'webp') + '" sizes="' + sizes + '">' +
      '<img src="img/' + key + '-1080.jpg" srcset="' + srcset(key, 'jpg') + '" sizes="' + sizes + '" alt="' + esc(alt) + '" width="1920" height="1080" decoding="async"></picture>';
  }

  // ---- overlays ----
  function remember() {
    var a = document.activeElement;
    opener = a && a.closest && a.closest('[data-scope="page"]') ? a : null;
  }
  function restore() {
    var o = opener;
    opener = null;
    if (o && document.contains(o)) setTimeout(function () { o.focus({ preventScroll: true }); }, 40);
  }
  function focusFirst(scope) {
    setTimeout(function () {
      var el = scope.querySelector('[data-nav]');
      if (el) el.focus({ preventScroll: true });
    }, 40);
  }
  function showOverlay(el) { el.hidden = false; page.inert = true; }
  function hideOverlay(el) { el.hidden = true; if (!S.wheel && S.viewer == null) page.inert = false; }

  function renderViewer() {
    var i = S.viewer;
    var tile = tiles[i];
    var title = tile.querySelector('.lp-tile-title').textContent;
    var body = tile.querySelector('.lp-tile-body').textContent;
    var kicker = tile.closest('.lp-group').getAttribute('data-kicker');
    var pad = function (n) { return (n < 10 ? '0' : '') + n; };
    $('viewerKicker').textContent = kicker;
    $('viewerPos').textContent = pad(i + 1) + ' / ' + pad(tiles.length);
    $('viewerTitle').textContent = title;
    $('viewerBody').textContent = body;
    viewer.setAttribute('aria-label', title);
    var img = tile.getAttribute('data-img');
    $('viewerStage').innerHTML = img
      ? pictureHtml(img, VIEWER_SIZES, 'Loungepad: ' + title)
      : '<div class="lp-viewer-icon"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="' + ICON[tile.getAttribute('data-icon')] + '"></path></svg></div>';
  }
  function openViewer(i) {
    if (!S.wheel) remember();
    if (S.wheel) { S.wheel = false; wheel.hidden = true; }
    S.viewer = i;
    S.hold = true;
    renderViewer();
    showOverlay(viewer);
    focusFirst(viewer);
    renderDots();
  }
  function closeViewer() {
    S.viewer = null;
    S.hold = false;
    hideOverlay(viewer);
    restore();
    renderDots();
    startCycle();
  }
  function step(d) {
    var n = tiles.length;
    var v = S.viewer != null ? S.viewer : 0;
    S.viewer = (v + d + n) % n;
    renderViewer();
  }

  function setSpoke(i) {
    S.spoke = i;
    spokes.forEach(function (sp, k) { sp.classList.toggle('on', k === i); });
    $('spokeTitle').textContent = spokes[i].getAttribute('data-title');
    $('spokeDesc').textContent = spokes[i].getAttribute('data-desc');
  }
  function openWheel() {
    remember();
    if (S.viewer != null) { S.viewer = null; viewer.hidden = true; }
    S.wheel = true;
    setSpoke(0);
    showOverlay(wheel);
    focusFirst(wheel);
  }
  function closeWheel() { S.wheel = false; hideOverlay(wheel); restore(); }
  function toggleWheel() { if (S.wheel) closeWheel(); else openWheel(); }
  // A spoke that leads somewhere closes the wheel without handing the focus back: the page is
  // about to scroll to the section, and the next D-pad press lands there.
  function leaveWheel() { opener = null; S.wheel = false; hideOverlay(wheel); }

  // ---- input ----
  function open(url) {
    // Browsers only open new tabs for a click or a keypress; a controller press may be refused.
    // When it is, go there in this tab instead, so the button always does something.
    var w = null;
    try { w = window.open(url, '_blank'); } catch (e) { w = null; }
    if (w) { try { w.opener = null; } catch (e2) {} return; }
    try { window.location.assign(url); } catch (e3) {}
  }
  function activate() {
    var el = document.activeElement;
    if (!el || !el.closest || !el.closest('.lp-root')) return;
    // A link that opens a new tab goes through open(), which falls back to this tab when the
    // browser won't open one for a controller press.
    if (el.tagName === 'A' && el.getAttribute('target') === '_blank' && el.href) {
      open(el.href);
      if (el.classList.contains('lp-wheel-spoke')) leaveWheel();
      return;
    }
    if (el.click) el.click();
  }
  function back() {
    if (S.wheel) closeWheel();
    else if (S.viewer != null) closeViewer();
  }
  function focusEl(el) {
    el.focus({ preventScroll: true });
    if (el.scrollIntoView) el.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: S.reduce ? 'auto' : 'smooth' });
  }
  // Spatial navigation, the way the launcher moves its highlight: nearest neighbour in the direction pressed.
  function nav(dir) {
    if (S.viewer != null && (dir === 'left' || dir === 'right')) { step(dir === 'left' ? -1 : 1); return; }
    // On the TV, left and right change the screen, like the D-pad drawn under it.
    var ae = document.activeElement;
    if (!S.wheel && S.viewer == null && ae && ae.hasAttribute && ae.hasAttribute('data-tv') && (dir === 'left' || dir === 'right')) {
      stepShot(dir === 'left' ? -1 : 1);
      return;
    }
    var scope = S.wheel ? wheel : S.viewer != null ? viewer : page;
    var els = all('[data-nav]', scope).filter(function (el) {
      var r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0;
    });
    if (!els.length) return;
    var cur = document.activeElement;
    if (els.indexOf(cur) < 0) {
      var vh = window.innerHeight;
      var first = els.filter(function (el) { var r = el.getBoundingClientRect(); return r.top >= 60 && r.bottom <= vh - 70; })[0] || els[0];
      focusEl(first);
      return;
    }
    var r = cur.getBoundingClientRect();
    var cx = r.left + r.width / 2, cy = r.top + r.height / 2;
    var best = null, bestScore = Infinity;
    els.forEach(function (el) {
      if (el === cur) return;
      var q = el.getBoundingClientRect();
      var x = q.left + q.width / 2, y = q.top + q.height / 2;
      var dx = x - cx, dy = y - cy;
      var main, cross;
      if (dir === 'left') { if (dx > -8) return; main = -dx; cross = Math.abs(dy); }
      else if (dir === 'right') { if (dx < 8) return; main = dx; cross = Math.abs(dy); }
      else if (dir === 'up') { if (dy > -8) return; main = -dy; cross = Math.abs(dx); }
      else { if (dy < 8) return; main = dy; cross = Math.abs(dx); }
      var score = main + cross * 2.2;
      if (score < bestScore) { bestScore = score; best = el; }
    });
    if (best) focusEl(best);
    else if (dir === 'up' || dir === 'down') window.scrollBy({ top: (dir === 'down' ? 1 : -1) * window.innerHeight * 0.6, behavior: S.reduce ? 'auto' : 'smooth' });
  }

  function onKey(e) {
    var t = e.target;
    if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    var k = e.key;
    var handled = true;
    if (k === 'ArrowLeft') nav('left');
    else if (k === 'ArrowRight') nav('right');
    else if (k === 'ArrowUp') nav('up');
    else if (k === 'ArrowDown') nav('down');
    else if (k === 'Escape') back();
    else if (k === 'x' || k === 'X') open(DL);
    else if (k === 'y' || k === 'Y') open(GH);
    else if (k === 'm' || k === 'M') toggleWheel();
    else handled = false;
    if (!handled) return;
    e.preventDefault();
    setInput('key');
    if (!S.famLocked && S.fam !== 'keyboard') setFam('keyboard');
  }
  function onMove(e) {
    if (e && e.movementX === 0 && e.movementY === 0) return;
    setInput('mouse');
  }

  function famFromId(id) {
    var s = String(id || '').toLowerCase();
    if (/054c|dualsense|dualshock|playstation/.test(s)) return 'playstation';
    if (/057e|pro controller|nintendo|joy-con/.test(s)) return 'switch';
    return 'xbox';
  }
  function padName(id) {
    var s = String(id || '').toLowerCase();
    if (/0ce6|0df2|dualsense/.test(s)) return 'DualSense';
    if (/05c4|09cc|dualshock/.test(s)) return 'DualShock 4';
    if (/054c/.test(s)) return 'PlayStation controller';
    if (/2009|pro controller/.test(s)) return 'Switch Pro Controller';
    if (/057e/.test(s)) return 'Switch controller';
    if (/045e|xbox|xinput/.test(s)) return 'Xbox controller';
    return 'Controller';
  }
  var prev = {}, rep = {};
  var BTN = [0, 1, 2, 3, 8, 9, 16];
  function pollPads() {
    var list = navigator.getGamepads ? navigator.getGamepads() : [];
    var pad = null;
    for (var i = 0; i < list.length; i++) { if (list[i] && list[i].connected) { pad = list[i]; break; } }
    var name = pad ? padName(pad.id) : null;
    if (name !== S.padName) { S.padName = name; renderPad(); }
    if (!pad) return;
    var now = performance.now();
    var down = function (b) { return !!(pad.buttons[b] && pad.buttons[b].pressed); };
    var edges = {};
    BTN.forEach(function (b) { var v = down(b); edges[b] = v && !prev[b]; prev[b] = v; });
    var ax = pad.axes[0] || 0, ay = pad.axes[1] || 0;
    var dirs = { left: down(14) || ax < -0.6, right: down(15) || ax > 0.6, up: down(12) || ay < -0.6, down: down(13) || ay > 0.6 };
    var moved = false;
    Object.keys(dirs).forEach(function (d) {
      if (!dirs[d]) { rep[d] = null; return; }
      var t = rep[d];
      if (t == null) { rep[d] = now + 380; nav(d); moved = true; }
      else if (now >= t) { rep[d] = now + 120; nav(d); moved = true; }
    });
    var any = moved || BTN.some(function (b) { return edges[b]; });
    if (any) {
      var fam = famFromId(pad.id);
      setInput('pad');
      S.padFam = fam;
      if (!S.famLocked && S.fam !== fam) setFam(fam);
    }
    if (edges[0]) activate();
    if (edges[1]) back();
    if (edges[2]) open(DL);
    if (edges[3]) open(GH);
    if (edges[9] || edges[16]) toggleWheel();
  }

  // ---- wiring ----
  tv.addEventListener('click', function () { openViewer(Number(shots[S.shot].getAttribute('data-feature'))); });
  tv.addEventListener('mouseenter', hold);
  tv.addEventListener('mouseleave', release);
  dots.forEach(function (d, i) { d.addEventListener('click', function () { stepShot(i - S.shot); }); });
  tiles.forEach(function (t, i) { t.addEventListener('click', function () { openViewer(i); }); });
  all('.lp-chip').forEach(function (c) {
    c.addEventListener('click', function () { S.famLocked = true; setFam(c.getAttribute('data-fam')); });
  });
  all('.lp-swatch').forEach(function (b) { b.addEventListener('click', function () { applyAccent(b.getAttribute('data-hex')); }); });
  $('menuBtn').addEventListener('click', toggleWheel);
  $('wheelBg').addEventListener('click', closeWheel);
  spokes.forEach(function (sp, i) {
    sp.addEventListener('mouseenter', function () { if (S.spoke !== i) setSpoke(i); });
    sp.addEventListener('focus', function () { if (S.spoke !== i) setSpoke(i); });
    sp.addEventListener('click', function (e) {
      if (sp.hasAttribute('data-close')) { e.preventDefault(); closeWheel(); return; }
      leaveWheel();
    });
  });
  $('viewerBack').addEventListener('click', closeViewer);
  $('viewerPrev').addEventListener('click', function () { step(-1); });
  $('viewerNext').addEventListener('click', function () { step(1); });
  document.addEventListener('keydown', onKey);
  document.addEventListener('mousemove', onMove);

  // ---- start ----
  var mq = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)');
  S.reduce = !!(mq && mq.matches);
  if (mq && mq.addEventListener) mq.addEventListener('change', function () { S.reduce = mq.matches; renderDots(); });
  window.LPGlyph.paint(S.fam);
  setShot(0);
  startCycle();
  if (navigator.getGamepads) {
    var loop = function () { try { pollPads(); } catch (e) {} requestAnimationFrame(loop); };
    requestAnimationFrame(loop);
  }
})();
