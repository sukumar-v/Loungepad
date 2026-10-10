/* ============================================================================
   Theme templates
   ============================================================================

   Lets a theme supply markup, not just styling. A theme.html sitting next to
   theme.css holds <template> blocks; anything it does not define falls back to
   the built-in markup, so a theme can override one tile and nothing else.

   TWO KINDS OF TEMPLATE, and the split is what keeps this safe:

   1. ITEM templates decide what one thing looks like -- a game tile, a carousel
      tile, a collection card. The theme owns the markup completely.

        <template data-template="game-tile">
          <div class="tile" data-focusable data-game-id="{{id}}">
            <img src="{{cover}}" alt="">
            <span data-if="favorite">*</span>
            <b>{{title}}</b><i>{{platform}} · {{playtime}}</i>
          </div>
        </template>

   2. SCREEN templates decide where the app's own regions go. The theme lays out
      slots; the app moves its regions into them, IDs and handlers intact.

        <template data-template="screen-library">
          <aside data-slot="topbar"></aside>
          <main><div data-slot="continue"></div><div data-slot="grid"></div></main>
        </template>

      That is the whole trick. A theme cannot rebuild #gridScroll and hope the
      renderer still finds it, so it never has to: it says where the grid goes,
      and the app puts the real one there. A slot the theme leaves out hides its
      region rather than deleting it, so every $("id") lookup still resolves and
      the navigation engine skips it (focusables() ignores [hidden]).

   BINDING is deliberately small -- {{field}} in text and attributes, data-if /
   data-unless on an element, data-each to repeat one for every entry of a list,
   and data-bg to put a picture behind one. No expressions and no script: a theme
   is markup, and the interesting layout freedom is in CSS anyway now that
   navigation follows geometry.

     <div data-each="media" data-limit="8" data-bg="{{thumb}}">{{name}}</div>

   Inside a data-each, a name is looked up on the entry first and then outwards,
   so {{title}} still finds the game's title from inside its media; {{@index}},
   {{@number}}, {{@count}}, {{@first}} and {{@last}} say where in the list it is,
   and {{@value}} is the entry itself when it is a plain string or number.

   What the fields ARE, and what a [data-act] in a template does, is the app's
   (themeview.js); docs/THEMES.md lists both.
   ============================================================================ */

window.Theme = (() => {
  let templates = {};      // name -> HTMLTemplateElement
  let loadedFrom = null;   // url the current set came from, so a reload is detectable

  /** Parse a theme.html. Returns the number of templates found. */
  function load(html, url) {
    templates = {};
    loadedFrom = url || null;
    if (!html) return 0;
    try {
      const doc = new DOMParser().parseFromString(html, "text/html");
      doc.querySelectorAll("template[data-template]").forEach(t => {
        disarm(t.content);
        templates[t.dataset.template] = t;
      });
    } catch (e) {
      console.warn("theme.html could not be parsed", e);
      templates = {};
    }
    return Object.keys(templates).length;
  }

  /**
   * Make "a theme cannot run script" true rather than merely intended.
   *
   * DOMParser builds an inert document, and a <script> inside a <template> does not run while it
   * is parsed -- which is what made this look safe. But render() CLONES the template's content
   * and appends it to the live document, and a cloned script that has never started runs the
   * moment it lands there. Tested: both a <script> and an `onerror` on an <img> fired.
   *
   * That matters because script in the page can post to the host bridge, and the bridge launches
   * games and writes settings. Installing a theme is already an act of trust, but the trust asked
   * for should be "this folder restyles my launcher", not "this folder runs code" -- and the
   * documentation promises the first one.
   */
  function disarm(root) {
    root.querySelectorAll("script").forEach(s => s.remove());
    root.querySelectorAll("*").forEach(el => {
      for (const attr of [...el.attributes]) {
        // Every event handler is an on* attribute, and there is no legitimate use for one here:
        // the app attaches its own listeners to whatever render() hands back.
        if (/^on/i.test(attr.name)) el.removeAttribute(attr.name);
      }
    });
  }

  function clear() { templates = {}; loadedFrom = null; }
  function has(name) { return !!templates[name]; }
  function source() { return loadedFrom; }
  function names() { return Object.keys(templates); }

  /* {{a.b}} against a chain of scopes, innermost first: inside a data-each the entry, then
     whatever was outside it. Missing values render empty rather than "undefined" -- a theme
     referencing a field the app does not have should leave a gap, not print a word. */
  const FIELD = /\{\{\s*(@?[\w.]+)\s*\}\}/g;

  function walk(v, parts) {
    for (const part of parts) {
      if (v === null || v === undefined) return undefined;
      v = v[part];
    }
    return v;
  }

  function lookup(scopes, path) {
    if (!Array.isArray(scopes)) scopes = [scopes];
    const parts = path.split(".");
    for (const sc of scopes) {
      if (sc !== null && typeof sc === "object" && sc[parts[0]] !== undefined) return walk(sc, parts);
    }
    return undefined;
  }

  function substitute(text, scopes) {
    return text.replace(FIELD, (_, path) => {
      const v = lookup(scopes, path);
      return v === undefined || v === null || typeof v === "object" ? "" : String(v);
    });
  }

  function truthy(v) {
    return !(v === undefined || v === null || v === false || v === "" || v === 0
             || (Array.isArray(v) && v.length === 0));
  }

  /* A URL for background-image, quoted so that nothing in it can end the url() and start a
     declaration of its own. Titles and store answers end up in these. */
  function cssUrl(u) {
    const safe = String(u).replace(/[\\"\n\r\f]/g, c => "%" + c.charCodeAt(0).toString(16).padStart(2, "0"));
    return 'url("' + safe + '")';
  }

  /** Bind one node and everything under it, against `scopes`. */
  function bindNode(node, scopes) {
    if (node.nodeType === 3) {
      if (node.nodeValue.includes("{{")) node.nodeValue = substitute(node.nodeValue, scopes);
      return;
    }
    if (node.nodeType !== 1) return;
    const el = node;

    // A list: one copy of the element per entry, each bound with the entry as the innermost
    // scope. The copies go where the element was; the element itself was only the pattern.
    if (el.hasAttribute("data-each")) {
      const list = lookup(scopes, el.getAttribute("data-each").trim());
      const limit = parseInt(el.getAttribute("data-limit"), 10);
      el.removeAttribute("data-each");
      el.removeAttribute("data-limit");
      const items = Array.isArray(list) ? (limit > 0 ? list.slice(0, limit) : list) : [];
      items.forEach((item, i) => {
        const copy = el.cloneNode(true);
        const where = { "@index": i, "@number": i + 1, "@count": items.length, "@first": i === 0, "@last": i === items.length - 1 };
        if (item === null || typeof item !== "object") where["@value"] = item;
        el.parentNode.insertBefore(copy, el);
        bindNode(copy, [where, item, ...scopes]);
      });
      el.remove();
      return;
    }

    // Conditionals before anything else: no point binding something about to be dropped.
    if (el.hasAttribute("data-if") || el.hasAttribute("data-unless")) {
      const showIf = el.hasAttribute("data-if") ? truthy(lookup(scopes, el.getAttribute("data-if").trim())) : true;
      const hideIf = el.hasAttribute("data-unless") ? truthy(lookup(scopes, el.getAttribute("data-unless").trim())) : false;
      if (!showIf || hideIf) { el.remove(); return; }
      el.removeAttribute("data-if");
      el.removeAttribute("data-unless");
    }

    // Attribute values go through setAttribute, never innerHTML, so a game title full of angle
    // brackets cannot become markup.
    for (const attr of [...el.attributes]) {
      if (attr.value.includes("{{")) el.setAttribute(attr.name, substitute(attr.value, scopes));
    }
    if (el.hasAttribute("data-bg")) {
      const url = el.getAttribute("data-bg").trim();
      el.removeAttribute("data-bg");
      if (url) el.style.backgroundImage = cssUrl(url);
    }
    [...el.childNodes].forEach(child => bindNode(child, scopes));
  }

  /**
   * Render an item template to an element.
   *
   * Returns null when the theme has no such template, which is the caller's cue
   * to build its own markup -- every call site keeps its original path.
   */
  function render(name, data) {
    const tpl = templates[name];
    if (!tpl) return null;

    const frag = tpl.content.cloneNode(true);
    [...frag.childNodes].forEach(child => bindNode(child, [data]));

    // One element per item, so the caller has something to attach handlers and
    // focus attributes to. A template with several roots gets wrapped.
    const els = [...frag.children];
    if (els.length === 1) return els[0];
    const wrap = document.createElement("div");
    wrap.append(frag);
    return wrap;
  }

  /**
   * Rearrange a screen into the theme's layout.
   *
   * The screen's own regions are moved -- not copied -- so every element keeps
   * its identity, its listeners and its scroll position. Called again with no
   * template, it puts them back in their original order.
   */
  function applyScreen(screen, name) {
    if (!screen) return false;
    const regions = [...screen.querySelectorAll("[data-region]")];
    if (!regions.length) return false;

    // Remember the original home once, so restoring is exact -- including whether the
    // region was hidden to begin with. Some regions are opt-in: they exist for themes
    // that want them and stay out of the built-in layout until one asks.
    if (!screen.__regionHome) {
      screen.__regionHome = regions.map(el =>
        ({ el, parent: el.parentNode, next: el.nextSibling, hidden: el.hidden }));
      // The screen's own top-level boxes, so the scaffolding they form can be put out of the
      // way. Regions nest inside these (.lib-body wraps four of them), and lifting a region
      // into a slot leaves its wrapper behind -- still a flex child of the screen, still
      // claiming its share of the height, now with nothing in it. That cost the theme
      // exactly half the screen, silently, and it is not something a theme should have to
      // know the class name of.
      screen.__shellHome = [...screen.children].map(el => ({ el, hidden: el.hidden }));
    }

    const tpl = templates[name];
    if (!tpl) {
      if (!screen.__themed) return false;
      screen.__regionHome.forEach(({ el, parent, next, hidden }) => {
        el.hidden = hidden;
        parent.insertBefore(el, next);
      });
      screen.__shellHome.forEach(({ el, hidden }) => { el.hidden = hidden; });
      // Anything the theme added is not ours to keep.
      [...screen.children].forEach(c => { if (c.dataset.themeLayout !== undefined) c.remove(); });
      screen.__themed = false;
      return true;
    }

    const layout = document.createElement("div");
    layout.dataset.themeLayout = "";
    layout.className = "theme-layout";
    layout.append(tpl.content.cloneNode(true));

    const used = new Set();
    layout.querySelectorAll("[data-slot]").forEach(slot => {
      const region = screen.querySelector(`[data-region="${CSS.escape(slot.dataset.slot)}"]`)
                  || screen.__regionHome.find(r => r.el.dataset.region === slot.dataset.slot)?.el;
      if (!region) return;
      region.hidden = false;
      slot.append(region);
      used.add(region);
    });

    // A region the theme did not ask for stays in the document but out of sight,
    // so $("gridScroll") still resolves and nothing has to null-check.
    screen.__regionHome.forEach(({ el }) => { if (!used.has(el)) el.hidden = true; });

    [...screen.children].forEach(c => { if (c.dataset.themeLayout !== undefined) c.remove(); });
    screen.append(layout);
    // Whatever is still a direct child is scaffolding the theme has replaced. A wrapper whose
    // regions all moved into slots is now empty; one the theme did not ask for is hidden
    // anyway. Either way it must stop taking up room.
    // Except what the screen marks data-keep: the game page's art, film and shades, which the app
    // goes on driving whatever the layout -- a film hidden by a layout would still be heard.
    screen.__shellHome.forEach(({ el }) => { if (el.parentNode === screen && el.dataset.keep === undefined) el.hidden = true; });
    screen.__themed = true;
    return true;
  }

  return { load, clear, has, render, applyScreen, source, names, lookup, cssUrl };
})();
