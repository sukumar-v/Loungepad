/* The button glyphs: the same drawings the launcher uses (Loungepad/ui/glyphs.js). The A on an
   Xbox pad is a green disc, on a DualSense a cross, on a Switch Pro the B that sits where an Xbox
   A does; on a keyboard it is the key that does the same job. Keep this in step with the app.

   Every <span data-glyph data-btn="A" data-size="28"> on the page is drawn by LPGlyph.paint(fam);
   data-fam pins one to a family, data-label draws a pill (or key) with that text instead. */
(function () {
  'use strict';

  var RING = 'inset 0 0 0 1.5px rgba(255,255,255,0.28)';
  var DARK = '#26262C';
  var SW = '#1B1B1F';

  var PATHS = {
    cross: ['M13.5 13.5l13 13M26.5 13.5l-13 13', '#7C9BE6', 3.2],
    circle: ['M27.5 20a7.5 7.5 0 1 1-15 0a7.5 7.5 0 1 1 15 0z', '#E0554F', 3.2],
    square: ['M14 12.5h12a1.5 1.5 0 0 1 1.5 1.5v12a1.5 1.5 0 0 1-1.5 1.5H14a1.5 1.5 0 0 1-1.5-1.5V14a1.5 1.5 0 0 1 1.5-1.5z', '#E68AC0', 3.2],
    triangle: ['M20 11.5 28.8 26.5H11.2z', '#63C58F', 3.2],
    lines: ['M12.5 14h15M12.5 20h15M12.5 26h15', '#FFFFFF', 2.4],
    plus: ['M12 20h16M20 12v16', '#FFFFFF', 3],
    minus: ['M12 20h16', '#FFFFFF', 3],
    home: ['M11 19.5 20 11.5l9 8V28a1 1 0 0 1-1 1h-5.5v-6h-5v6H12a1 1 0 0 1-1-1z', '#FFFFFF', 2]
  };

  function disc(fill, text, ink, ring) { return { kind: 'disc', fill: fill, text: text, ink: ink, shadow: ring ? RING : 'none' }; }
  function shape(name, fill) { return { kind: 'shape', fill: fill || DARK, shape: name }; }
  function key(t) { return { kind: 'key', text: t }; }
  function pill(t) { return { kind: 'pill', text: t }; }

  var ART = {
    xbox: {
      A: disc('#3AA03C', 'A', '#FFFFFF'), B: disc('#D3433C', 'B', '#FFFFFF'),
      X: disc('#3C7CD3', 'X', '#FFFFFF'), Y: disc('#E2B128', 'Y', '#101012'),
      Menu: shape('lines'), View: pill('VIEW'), Guide: pill('XBOX'),
      LB: pill('LB'), RB: pill('RB'), LT: pill('LT'), RT: pill('RT'), LS: pill('LS'), RS: pill('RS')
    },
    playstation: {
      A: shape('cross'), B: shape('circle'), X: shape('square'), Y: shape('triangle'),
      Menu: pill('OPTIONS'), View: pill('CREATE'), Guide: disc(DARK, 'PS', '#FFFFFF', true),
      LB: pill('L1'), RB: pill('R1'), LT: pill('L2'), RT: pill('R2'), LS: pill('L3'), RS: pill('R3')
    },
    switch: {
      A: disc(SW, 'B', '#FFFFFF', true), B: disc(SW, 'A', '#FFFFFF', true),
      X: disc(SW, 'Y', '#FFFFFF', true), Y: disc(SW, 'X', '#FFFFFF', true),
      Menu: shape('plus', SW), View: shape('minus', SW), Guide: shape('home', SW),
      LB: pill('L'), RB: pill('R'), LT: pill('ZL'), RT: pill('ZR'), LS: pill('LS'), RS: pill('RS')
    },
    keyboard: {
      A: key('Enter'), B: key('Esc'), X: key('X'), Y: key('Y'),
      Menu: key('M'), View: key('/'), Guide: key('M'),
      LB: key('['), RB: key(']'), LT: key('LT'), RT: key('RT'), LS: key('LS'), RS: key('RS')
    }
  };

  function esc(s) {
    return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }

  // The inner markup for one glyph. The outer element (inline-flex, `size` tall) is the span on the page.
  function html(btn, fam, size, label) {
    fam = fam || 'xbox';
    btn = btn || 'A';
    size = Number(size) || 28;
    label = label || '';
    var g;
    if (label) g = fam === 'keyboard' ? key(label) : pill(label);
    else if (btn === 'Dpad' || btn === 'DpadH' || btn === 'DpadV') {
      if (fam === 'keyboard') g = key(btn === 'DpadH' ? '← →' : btn === 'DpadV' ? '↑ ↓' : '↑↓←→');
      else g = { kind: 'dpad', arms: btn === 'DpadH' ? 'h' : btn === 'DpadV' ? 'v' : 'all' };
    } else {
      var set = ART[fam] || ART.xbox;
      g = set[btn] || ART.xbox[btn] || pill(btn);
    }
    var text = String(g.text || '');
    var r = Math.round;
    if (g.kind === 'disc') {
      return '<span class="gd" style="width:' + size + 'px;height:' + size + 'px;background:' + g.fill +
        ';box-shadow:' + g.shadow + ';color:' + g.ink + ';font-size:' + r(size * (text.length > 1 ? 0.4 : 0.54)) + 'px">' + esc(text) + '</span>';
    }
    if (g.kind === 'shape') {
      var p = PATHS[g.shape] || PATHS.lines;
      return '<span class="gs" style="width:' + size + 'px;height:' + size + 'px;background:' + g.fill + '">' +
        '<svg viewBox="0 0 40 40" width="' + size + '" height="' + size + '" style="color:' + p[1] + '" aria-hidden="true">' +
        '<path d="' + p[0] + '" fill="none" stroke="currentColor" stroke-width="' + p[2] + '" stroke-linecap="round" stroke-linejoin="round"></path></svg></span>';
    }
    if (g.kind === 'key') {
      return '<span class="gk" style="min-width:' + size + 'px;height:' + r(size * 0.86) + 'px;padding:0 ' + r(size * 0.24) +
        'px;font-size:' + r(size * (text.length > 2 ? 0.36 : 0.46)) + 'px">' + esc(text) + '</span>';
    }
    if (g.kind === 'pill') {
      return '<span class="gp" style="min-width:' + r(size * 1.1) + 'px;height:' + r(size * 0.72) + 'px;padding:0 ' + r(size * 0.28) +
        'px;font-size:' + r(size * (text.length > 3 ? 0.3 : 0.38)) + 'px">' + esc(text) + '</span>';
    }
    // D-pad: the arms that do something are lit.
    var lit = function (a) {
      if (g.arms === 'all') return 0.95;
      if (g.arms === 'v') return (a === 'up' || a === 'down') ? 0.95 : 0.26;
      if (g.arms === 'h') return (a === 'left' || a === 'right') ? 0.95 : 0.26;
      return 0.26;
    };
    return '<svg viewBox="0 0 40 40" width="' + size + '" height="' + size + '" class="gdp" aria-hidden="true">' +
      '<rect x="15" y="2" width="10" height="12" rx="2" fill="currentColor" fill-opacity="' + lit('up') + '"></rect>' +
      '<rect x="15" y="26" width="10" height="12" rx="2" fill="currentColor" fill-opacity="' + lit('down') + '"></rect>' +
      '<rect x="2" y="15" width="12" height="10" rx="2" fill="currentColor" fill-opacity="' + lit('left') + '"></rect>' +
      '<rect x="26" y="15" width="12" height="10" rx="2" fill="currentColor" fill-opacity="' + lit('right') + '"></rect>' +
      '<rect x="14" y="14" width="12" height="12" fill="currentColor" fill-opacity="0.26"></rect></svg>';
  }

  // Redraws every glyph on the page for a family. A span with data-fam keeps its own.
  function paint(fam, root) {
    var els = (root || document).querySelectorAll('[data-glyph]');
    for (var i = 0; i < els.length; i++) {
      var el = els[i];
      var f = el.getAttribute('data-fam') || fam;
      var k = f + '|' + (el.getAttribute('data-btn') || '') + '|' + (el.getAttribute('data-size') || '') + '|' + (el.getAttribute('data-label') || '');
      if (el.getAttribute('data-drawn') === k) continue;
      el.innerHTML = html(el.getAttribute('data-btn'), f, el.getAttribute('data-size'), el.getAttribute('data-label'));
      el.setAttribute('data-drawn', k);
      el.style.height = (Number(el.getAttribute('data-size')) || 28) + 'px';
    }
  }

  var api = { html: html, paint: paint, ART: ART };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  if (typeof window !== 'undefined') window.LPGlyph = api;
})();
