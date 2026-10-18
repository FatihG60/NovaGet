// NovaGet Injected Script — Runs in MAIN world (Page Context)
// Intercepts MediaSource, Fetch, XHR, JWPlayer, VideoJS, and page-level video streams
(function () {
  "use strict";

  if (window.__MYIDM_INJECTED__) return;
  window.__MYIDM_INJECTED__ = true;

  // Map: fullUrl -> reportedFormat (allows upgrading e.g. "STREAM" -> "HLS-MASTER")
  const REPORTED_FORMATS = new Map();
  const BLOB_MAP = new Map();

  const FORMAT_PRIORITY = {
    "HLS-MASTER": 5,
    "DASH": 4,
    "HLS-VIDEO": 3,
    "HLS": 2,
    "MP4": 2,
    "WebM": 2,
    "VIDEO": 1,
    "HLS-AUDIO": 1,
    "AUDIO": 1,
    "STREAM": 0
  };

  function cleanUrl(u) {
    if (!u) return "";
    try {
      return new URL(u, window.location.href).href;
    } catch {
      return String(u);
    }
  }

  function isSegmentOrAssetPath(pathLower) {
    // Disguised HLS segments (.jpg, .png, .ts, .m4s) or subtitles/assets
    if (/\.(jpg|jpeg|png|gif|webp|svg|ico|css|js|woff2?|ttf|vtt|srt|ass|ts|m4s|aac)(\?|$)/i.test(pathLower)) {
      return true;
    }
    if (/(?:\/|_|-)(?:seg|segment|chunk|frag|fragment|part|image\d+|sublist_aud)\d*/i.test(pathLower)) {
      return true;
    }
    return false;
  }

  function reportStream(url, format, context = "net", title = null) {
    if (!url) return;
    const fullUrl = cleanUrl(url);
    if (fullUrl.startsWith("blob:") || fullUrl.startsWith("data:")) return;
    if (fullUrl.includes("googlevideo.com/videoplayback")) return;

    const effectiveFormat = format || "HLS";
    const prevFormat = REPORTED_FORMATS.get(fullUrl);
    if (prevFormat) {
      const prevPrio = FORMAT_PRIORITY[prevFormat] ?? 0;
      const newPrio = FORMAT_PRIORITY[effectiveFormat] ?? 0;
      if (newPrio <= prevPrio) return;
    }
    REPORTED_FORMATS.set(fullUrl, effectiveFormat);

    const rawTitle = (title || document.title || "").replace(/^\(\d+\)\s*/, "").trim();

    window.postMessage({
      type: "__MYIDM_DETECTED__",
      url: fullUrl,
      format: effectiveFormat,
      context,
      title: rawTitle,
      referrer: window.location.href
    }, "*");
  }

  function checkUrl(url, source = "net") {
    if (!url || typeof url !== "string") return;
    const lower = url.toLowerCase();
    const pathPart = lower.split("?")[0].split("#")[0];

    // Ignore YouTube internal SABR / googlevideo chunk requests (handled via clean watch URL)
    if (lower.includes("googlevideo.com/videoplayback")) return;

    // Never classify disguised image/segment chunks (.jpg, .ts, .m4s, .vtt) as standalone streams
    if (isSegmentOrAssetPath(pathPart)) return;

    const isAudio = pathPart.includes("audio") || pathPart.includes("sound") || pathPart.includes("voice") ||
                    pathPart.includes("a-eng") || pathPart.includes("/audio/") ||
                    pathPart.endsWith(".mp3") || pathPart.endsWith(".m4a");

    // 1. Explicit HLS Playlists (.m3u8 or CloseLoad/HDFilmCehennemi master.txt / sublist_*.txt)
    if (pathPart.endsWith(".m3u8") ||
        ((pathPart.includes("/hls/") || pathPart.includes("/m3u8/")) && pathPart.endsWith(".txt"))) {
      if (pathPart.includes("master") || pathPart.includes("playlist") || pathPart.includes("index")) {
        reportStream(url, "HLS-MASTER", source);
      } else if (isAudio || pathPart.includes("sublist_aud")) {
        reportStream(url, "HLS-AUDIO", source);
      } else {
        reportStream(url, "HLS-VIDEO", source);
      }
      return;
    }

    // 2. DASH Manifests
    if (pathPart.endsWith(".mpd")) {
      reportStream(url, "DASH", source);
      return;
    }

    // 3. Direct Video Files (only when the URL pathname itself ends with a video extension, NOT a parent folder!)
    if (/\.(mp4|webm|mkv|mov|flv|m4v)$/i.test(pathPart)) {
      reportStream(url, isAudio ? "AUDIO" : "MP4", source);
      return;
    }
  }

  // 1. Hook URL.createObjectURL for MediaSource tracking (JWPlayer & HTML5 players)
  const origCreateObjectURL = URL.createObjectURL;
  URL.createObjectURL = function (obj) {
    const blobUrl = origCreateObjectURL.call(URL, obj);
    try {
      if (obj instanceof MediaSource) {
        BLOB_MAP.set(blobUrl, { mediaSource: obj, createdAt: Date.now() });
        window.postMessage({
          type: "__MYIDM_BLOB_CREATED__",
          blobUrl,
          pageUrl: window.location.href
        }, "*");
      }
    } catch (e) {}
    return blobUrl;
  };

  // 2. Hook window.fetch
  const origFetch = window.fetch;
  window.fetch = async function (input, init) {
    try {
      const url = typeof input === "string" ? input : (input && input.url ? input.url : "");
      checkUrl(url, "fetch");
    } catch (e) {}

    const response = await origFetch.apply(this, arguments);

    try {
      const inputUrl = typeof input === "string" ? input : (input && input.url ? input.url : "");
      const pathPart = inputUrl.toLowerCase().split("?")[0];
      if (inputUrl && !/\.(jpg|jpeg|png|gif|webp|ts|m4s|vtt|srt)$/i.test(pathPart)) {
        const ct = ((response.headers && response.headers.get("content-type")) || "").toLowerCase();
        if (pathPart.endsWith(".m3u8") || pathPart.endsWith(".txt") ||
            ct.includes("mpegurl") || ct.includes("octet-stream") || ct.includes("text/plain")) {
          const clone = response.clone();
          clone.text().then(text => {
            if (text && text.trim().startsWith("#EXTM3U")) {
              if (text.includes("#EXT-X-STREAM-INF")) {
                reportStream(inputUrl, "HLS-MASTER", "fetch-body");
              } else if (text.includes("TYPE=AUDIO") || pathPart.includes("audio") || pathPart.includes("sublist_aud")) {
                reportStream(inputUrl, "HLS-AUDIO", "fetch-body");
              } else {
                reportStream(inputUrl, "HLS-VIDEO", "fetch-body");
              }
            }
          }).catch(() => {});
        }
      }
    } catch (e) {}

    return response;
  };

  // 3. Hook XMLHttpRequest (used heavily by JWPlayer, Hls.js, Dash.js)
  const origOpen = XMLHttpRequest.prototype.open;
  const origSend = XMLHttpRequest.prototype.send;

  XMLHttpRequest.prototype.open = function (method, url) {
    this._myidm_url = url;
    try {
      checkUrl(url, "xhr");
    } catch (e) {}
    return origOpen.apply(this, arguments);
  };

  XMLHttpRequest.prototype.send = function () {
    this.addEventListener("load", function () {
      try {
        const url = this._myidm_url;
        if (!url || typeof url !== "string") return;
        const pathPart = url.toLowerCase().split("?")[0];
        if (/\.(jpg|jpeg|png|gif|webp|ts|m4s|vtt|srt)$/i.test(pathPart)) return;

        if (this.responseType === "" || this.responseType === "text") {
          const text = this.responseText;
          if (text && typeof text === "string" && text.trim().startsWith("#EXTM3U")) {
            if (text.includes("#EXT-X-STREAM-INF")) {
              reportStream(url, "HLS-MASTER", "xhr-body");
            } else if (text.includes("TYPE=AUDIO") || pathPart.includes("audio") || pathPart.includes("sublist_aud")) {
              reportStream(url, "HLS-AUDIO", "xhr-body");
            } else {
              reportStream(url, "HLS-VIDEO", "xhr-body");
            }
          }
        }
      } catch (e) {}
    });
    return origSend.apply(this, arguments);
  };

  // 4. Hook JWPlayer API (Rapidvid, CloseLoad, HDFilmCehennemi, Vidmoly, etc.)
  function inspectJwPlayer() {
    try {
      if (typeof window.jwplayer === "function") {
        const instances = [window.jwplayer()];
        for (let i = 0; i < 5; i++) {
          try {
            const inst = window.jwplayer(i);
            if (inst && !instances.includes(inst)) instances.push(inst);
          } catch (e) {}
        }

        const handleJwSource = (fileUrl, typeHint) => {
          if (!fileUrl || typeof fileUrl !== "string") return;
          const lower = fileUrl.toLowerCase();
          if (typeHint === "hls" || lower.includes(".m3u8") || lower.includes("master.txt") || lower.includes("/hls/")) {
            reportStream(fileUrl, "HLS-MASTER", "jwplayer");
          } else {
            checkUrl(fileUrl, "jwplayer");
          }
        };

        instances.forEach(jw => {
          if (!jw || typeof jw.getPlaylist !== "function") return;
          const pl = jw.getPlaylist();
          if (Array.isArray(pl)) {
            pl.forEach(item => {
              if (item.file) handleJwSource(item.file, item.type);
              if (Array.isArray(item.sources)) {
                item.sources.forEach(s => {
                  if (s && s.file) handleJwSource(s.file, s.type);
                });
              }
            });
          }

          if (typeof jw.getPlaylistItem === "function") {
            const item = jw.getPlaylistItem();
            if (item) {
              if (item.file) handleJwSource(item.file, item.type);
              if (Array.isArray(item.sources)) {
                item.sources.forEach(s => {
                  if (s && s.file) handleJwSource(s.file, s.type);
                });
              }
            }
          }
        });
      }
    } catch (e) {}
  }

  // 5. Hook HTML5 MediaSource / Video.js / Plyr
  function inspectVideoJs() {
    try {
      if (window.videojs && window.videojs.players) {
        Object.values(window.videojs.players).forEach(p => {
          if (p && typeof p.currentSrc === "function") {
            const src = p.currentSrc();
            if (src) checkUrl(src, "videojs");
          }
        });
      }
    } catch (e) {}
  }

  // 6. Deep Scan Inline Scripts for Player configs (ignoring application/ld+json dummy SEO tags)
  function scanInlineScripts() {
    try {
      const scripts = document.querySelectorAll("script:not([type='application/ld+json'])");
      const regexes = [
        /["'](https?:\/\/[^"']+\.m3u8(?:\?[^"']*)?)["']/gi,
        /["'](https?:\/\/[^"']+\/master\.txt(?:\?[^"']*)?)["']/gi,
        /["'](https?:\/\/[^"']+\.mpd(?:\?[^"']*)?)["']/gi,
        /["'](https?:\/\/[^"']+\.(?:mp4|webm|mkv)(?:\?[^"']*)?)["']/gi,
        /file\s*:\s*["'](https?:\/\/[^"']+)["']/gi,
        /source\s*:\s*["'](https?:\/\/[^"']+)["']/gi
      ];

      scripts.forEach(s => {
        const code = s.textContent;
        if (!code || code.length < 15) return;
        regexes.forEach(re => {
          let m;
          while ((m = re.exec(code)) !== null) {
            const candidate = m[1];
            if (candidate && !candidate.includes("google") && !candidate.includes("analytics") && !candidate.includes(".vtt") && !candidate.includes(".srt")) {
              checkUrl(candidate, "inline-script");
            }
          }
        });
      });
    } catch (e) {}
  }

  setInterval(() => {
    inspectJwPlayer();
    inspectVideoJs();
  }, 1000);

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", scanInlineScripts);
  } else {
    scanInlineScripts();
  }
})();
