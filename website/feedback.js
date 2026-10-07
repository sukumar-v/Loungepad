/* The feedback page's form. Tally's embed script sizes the frame to the form; without it (blocked,
   offline) the frame still loads at its fixed height and scrolls inside. Tally's own snippet is
   inline script, which the site's CSP refuses, so this is that snippet as a file. */
(function () {
  'use strict';
  function load() {
    if (typeof Tally !== 'undefined') { Tally.loadEmbeds(); return; }
    document.querySelectorAll('iframe[data-tally-src]:not([src])').forEach(function (f) {
      f.src = f.dataset.tallySrc;
    });
  }
  // Both scripts are deferred and run in order, so Tally's has run (or failed) by now.
  load();
})();
