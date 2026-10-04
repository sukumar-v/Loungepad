/*
 * The YouTube player, in a page of its own.
 *
 * YouTube's IFrame API is a script off youtube.com, and a script runs with the authority of the
 * page that loaded it. Loaded into the launcher page it could post bridge commands -- launch an
 * exe, save settings -- and read anything the page could. So it is loaded HERE instead, in a
 * document on a separate origin that has no bridge and nothing worth reading, and the launcher
 * talks to it through postMessage. Both sides check the other's origin and window on every
 * message: the parent's origin arrives in the query string (?o=) and is the only one answered.
 *
 * Commands in: { cmd: "cue", id } | "play" | "pause" | "stop" | "mute" | "unmute" |
 *              { cmd: "volume", v: 0-100 } | { cmd: "seek", t: seconds }
 * Events out:  { ev: "ready" } | { ev: "state", s } | { ev: "time", t, d, s } every 250 ms |
 *              { ev: "error", code }
 *
 * The embed is asked for on youtube-nocookie.com, which keeps YouTube's tracking cookies out of
 * the launcher's browser profile; the API script itself still has to come from youtube.com.
 */
(() => {
  const params = new URLSearchParams(location.search);
  const parentOrigin = params.get("o") || "";
  if (!parentOrigin || window.parent === window) return;

  const post = (msg) => window.parent.postMessage(msg, parentOrigin);
  let player = null;
  let ready = false;
  const queue = [];

  // The one way to keep captions off; unloaded on ready and on every start, as the page did.
  function hideCaptions() {
    try { player.unloadModule("captions"); } catch (e) { /* older player */ }
    try { player.unloadModule("cc"); } catch (e) { /* older player */ }
  }

  function tick() {
    if (!player) return;
    let t = 0, d = 0, s = -1;
    try { t = player.getCurrentTime() || 0; d = player.getDuration() || 0; s = player.getPlayerState(); }
    catch (e) { return; }
    post({ ev: "time", t, d, s });
  }

  function run(m) {
    try {
      switch (m.cmd) {
        case "cue": player.cueVideoById(String(m.id || "")); break;
        case "play": player.playVideo(); break;
        case "pause": player.pauseVideo(); break;
        case "stop": player.stopVideo(); break;
        case "mute": player.mute(); break;
        case "unmute": player.unMute(); break;
        case "volume": player.setVolume(Math.max(0, Math.min(100, Math.round(Number(m.v) || 0)))); break;
        case "seek": player.seekTo(Math.max(0, Number(m.t) || 0), true); break;
      }
    } catch (e) { /* the player is between states; the next command lands */ }
  }

  window.addEventListener("message", (e) => {
    if (e.source !== window.parent || e.origin !== parentOrigin) return;
    const m = e.data;
    if (!m || typeof m.cmd !== "string") return;
    if (ready) run(m); else queue.push(m);
  });

  window.onYouTubeIframeAPIReady = () => {
    player = new YT.Player("p", {
      host: "https://www.youtube-nocookie.com",
      width: "100%", height: "100%",
      playerVars: {
        controls: 0, disablekb: 1, fs: 0, iv_load_policy: 3, modestbranding: 1, rel: 0,
        playsinline: 1, autoplay: 0, cc_load_policy: 0, hl: "en", origin: location.origin,
      },
      events: {
        onReady: () => {
          hideCaptions();
          ready = true;
          post({ ev: "ready" });
          queue.splice(0).forEach(run);
          setInterval(tick, 250);
        },
        onStateChange: (e) => {
          if (e.data === YT.PlayerState.PLAYING) hideCaptions();
          post({ ev: "state", s: e.data });
        },
        onError: (e) => post({ ev: "error", code: e && e.data }),
      },
    });
  };

  const s = document.createElement("script");
  s.src = "https://www.youtube.com/iframe_api";
  s.onerror = () => post({ ev: "error", code: "api" });
  document.head.appendChild(s);
})();
