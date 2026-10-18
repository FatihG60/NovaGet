// MyIDM Content Script — Video Sniffer & Floating Overlay Bar
(function () {
  "use strict";

  const PROCESSED = new WeakSet();
  const PAGE_STREAMS = [];
  const KNOWN_BLOBS = new Set();

  function sanitizeFileName(str) {
    if (!str) return "video";
    return str
      .replace(/^\(\d+\)\s*/, "")
      .replace(/[\\/:*?"<>|\r\n]+/g, "_")
      .trim()
      .substring(0, 80);
  }

  function isYouTubeWatchOrShorts() {
    const host = window.location.hostname.toLowerCase();
    const path = window.location.pathname;
    return (host.includes("youtube.com") && (path.startsWith("/watch") || path.startsWith("/shorts/"))) ||
           host === "youtu.be";
  }

  function getMediaTitle() {
    // 1. YouTube specific selectors (modern Polymer / Watch Metadata + Shorts)
    const ytSelectors = [
      "ytd-watch-metadata h1 yt-formatted-string",
      "#title h1 yt-formatted-string",
      "h1.ytd-video-primary-info-renderer yt-formatted-string",
      "ytd-reel-player-overlay-renderer h2 yt-formatted-string",
      "h2.ytd-reel-player-header-renderer"
    ];
    for (const sel of ytSelectors) {
      const el = document.querySelector(sel);
      if (el && el.textContent && el.textContent.trim()) {
        return sanitizeFileName(el.textContent.trim());
      }
    }

    // 2. OpenGraph / Twitter meta tags (only when not stale on SPA navigation)
    if (!window.location.hostname.includes("youtube.com")) {
      const ogTitle = document.querySelector('meta[property="og:title"], meta[name="twitter:title"]');
      if (ogTitle && ogTitle.content && ogTitle.content.trim()) {
        return sanitizeFileName(ogTitle.content.trim());
      }
    }

    // 3. Fallback to document title (strip leading notification count like "(89) " and site suffix)
    let title = (document.title || "video").replace(/^\(\d+\)\s*/, "").trim();
    title = title.replace(/\s*[-–|]\s*(JWPlayer|YouTube|Twitter|X|Vimeo|Dailymotion|Facebook|Instagram).*$/i, "");
    return sanitizeFileName(title || "video");
  }

  function detectFormat(url, mime) {
    const clean = (url || "").split("?")[0].toLowerCase();
    if (clean.endsWith(".m3u8") || (mime && mime.includes("mpegurl"))) return "HLS";
    if (clean.endsWith(".mpd") || (mime && mime.includes("dash"))) return "DASH";
    if (clean.endsWith(".mp4") || (mime && mime.includes("mp4"))) return "MP4";
    if (clean.endsWith(".webm") || (mime && mime.includes("webm"))) return "WebM";
    if (clean.endsWith(".mkv")) return "MKV";
    if (clean.endsWith(".ts")) return "TS";
    if (clean.endsWith(".mp3") || (mime && mime.includes("audio/mpeg"))) return "MP3";
    if (clean.endsWith(".m4a") || (mime && mime.includes("audio/mp4"))) return "M4A";
    return "VIDEO";
  }

  function reportMedia(url, format, element) {
    if (!url || url.startsWith("blob:") || url.startsWith("data:")) return;
    if (url.includes("googlevideo.com/videoplayback")) return;
    const title = getMediaTitle();
    chrome.runtime.sendMessage({
      type: "mediaDetected",
      media: {
        url,
        title,
        format: format || detectFormat(url),
        referrer: window.location.href,
        detectedAt: Date.now()
      }
    }).catch(() => {});
  }

  // Listen to messages from injected.js (MAIN world)
  window.addEventListener("message", (event) => {
    if (event.source !== window || !event.data) return;

    if (event.data.type === "__MYIDM_DETECTED__") {
      const { url, format, title } = event.data;
      const existing = PAGE_STREAMS.find(s => s.url === url);
      if (!existing) {
        PAGE_STREAMS.unshift({ url, format, title: title || getMediaTitle() });
      } else if (format) {
        existing.format = format;
      }
      reportMedia(url, format);
      scanMediaElements();
    } else if (event.data.type === "__MYIDM_BLOB_CREATED__") {
      KNOWN_BLOBS.add(event.data.blobUrl);
      scanMediaElements();
    }
  });

  function isLikelyHlsUrl(url, format) {
    if (format && format.startsWith("HLS")) return true;
    const lower = (url || "").toLowerCase().split("?")[0];
    return lower.endsWith(".m3u8") ||
           lower.endsWith("master.txt") ||
           lower.endsWith("playlist.txt") ||
           lower.includes("/hls/");
  }

  function attachDownloadOverlay(video) {
    if (PROCESSED.has(video)) return;
    PROCESSED.add(video);

    const parent = video.parentElement;
    if (!parent) return;

    const overlay = document.createElement("div");
    overlay.className = "myidm-download-btn";
    overlay.setAttribute("title", "NovaGet ile bu videoyu indir");

    const qualitySelect = document.createElement("select");
    qualitySelect.className = "myidm-quality-select";
    qualitySelect.title = "Video Kalitesi Seçin";
    qualitySelect.innerHTML = `
      <option value="best">En İyi</option>
      <option value="1080">1080p</option>
      <option value="720">720p</option>
      <option value="480">480p</option>
      <option value="audio">🎵 MP3</option>
    `;
    qualitySelect.addEventListener("click", (e) => e.stopPropagation());
    qualitySelect.addEventListener("mousedown", (e) => e.stopPropagation());

    const formatBadge = document.createElement("span");
    formatBadge.className = "myidm-btn-badge";
    formatBadge.textContent = "İndir";

    overlay.innerHTML = `
      <svg class="myidm-btn-icon" viewBox="0 0 24 24">
        <path d="M13 2L3 14h9l-1 8 10-12h-9l1-8z"/>
      </svg>
      <span>NovaGet</span>
    `;
    overlay.appendChild(qualitySelect);
    overlay.appendChild(formatBadge);

    const closeBtn = document.createElement("span");
    closeBtn.className = "myidm-btn-close";
    closeBtn.innerHTML = "&times;";
    closeBtn.title = "Kapat";
    closeBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      overlay.style.display = "none";
    });
    overlay.appendChild(closeBtn);

    function selectBestStream(list) {
      if (!list || list.length === 0) return null;

      // Filter out any disguised image/segment chunks (.jpg, .png, .ts, .m4s)
      const validList = list.filter(s => {
        if (!s || !s.url) return false;
        const p = s.url.toLowerCase().split("?")[0];
        return !/\.(jpg|jpeg|png|gif|webp|vtt|srt|ts|m4s|aac)$/i.test(p);
      });
      const pool = validList.length > 0 ? validList : list;

      // 1. Master Playlist (Contains both video and audio)
      const master = pool.find(s =>
        s.format === "HLS-MASTER" ||
        (s.url && s.url.toLowerCase().includes("master.m3u8")) ||
        (s.url && s.url.toLowerCase().includes("playlist.m3u8")) ||
        (s.url && s.url.toLowerCase().includes("master.txt"))
      );
      if (master) return master;

      // 2. High Quality Video Stream (Non-audio HLS or MP4)
      const videoStream = pool.find(s =>
        s.format === "HLS-VIDEO" ||
        s.format === "VIDEO" ||
        s.format === "MP4" ||
        (s.format === "HLS" && !s.url.toLowerCase().includes("audio") && !s.url.toLowerCase().includes("sublist_aud"))
      );
      if (videoStream) return videoStream;

      // 3. Any non-audio stream
      const nonAudio = pool.find(s =>
        s.format !== "HLS-AUDIO" &&
        s.format !== "AUDIO" &&
        !s.url.toLowerCase().includes("audio") &&
        !s.url.toLowerCase().includes("sublist_aud")
      );
      if (nonAudio) return nonAudio;

      // 4. Fallback only if no video exists
      return pool[0];
    }

    async function findBestUrl() {
      // 0. YouTube watch / shorts page: use the clean page URL so yt-dlp + FFmpeg downloads full video+audio MP4
      if (isYouTubeWatchOrShorts()) {
        return { url: window.location.href, isHls: false, title: getMediaTitle() };
      }

      // 1. Check direct video tag source (if not blob)
      const current = video.currentSrc || video.src;
      if (current && !current.startsWith("blob:") && !current.startsWith("data:")) {
        return { url: current, isHls: isLikelyHlsUrl(current, null) };
      }

      // 2. Check child <source> tags
      const sources = video.querySelectorAll("source");
      for (const s of sources) {
        if (s.src && !s.src.startsWith("blob:") && !s.src.startsWith("data:")) {
          return { url: s.src, isHls: isLikelyHlsUrl(s.src, null) };
        }
      }

      // 3. For blob: (JWPlayer, Rapidvid, CloseLoad, MediaSource)
      // First check streams reported directly by injected.js on this page!
      if (PAGE_STREAMS.length > 0) {
        const best = selectBestStream(PAGE_STREAMS);
        if (best) {
          const isHls = isLikelyHlsUrl(best.url, best.format);
          return { url: best.url, isHls, title: best.title };
        }
      }

      // 4. Query background worker for all media captured on this tab
      try {
        const resp = await chrome.runtime.sendMessage({ type: "getTabMedia" });
        if (resp && resp.mediaList && resp.mediaList.length > 0) {
          const best = selectBestStream(resp.mediaList);
          if (best) {
            const isHls = best.isHls || isLikelyHlsUrl(best.url, best.format);
            return { url: best.url, isHls, title: best.title || best.filename };
          }
        }
      } catch (err) {}

      return null;
    }

    overlay.addEventListener("click", async (e) => {
      if (e.target === qualitySelect || qualitySelect.contains(e.target)) return;
      e.stopPropagation();
      e.preventDefault();

      formatBadge.textContent = "⏳...";
      const result = await findBestUrl();

      if (!result || !result.url) {
        formatBadge.textContent = "Yakalanıyor";
        setTimeout(() => { formatBadge.textContent = "İndir"; }, 1500);
        return;
      }

      const selectedQuality = qualitySelect.value || "best";
      let title = (result.title || getMediaTitle()).trim();
      title = title.replace(/^\(\d+\)\s*/, "").replace(/\.(mp4|ts|mkv|webm|m4v|mp3|bin)$/i, "");
      const ext = selectedQuality === "audio" ? ".mp3" : ".mp4";
      const filename = `${title}${ext}`;

      try {
        const res = await chrome.runtime.sendMessage({
          type: "sendMedia",
          url: result.url,
          filename: filename,
          referrer: window.location.href,
          isHls: result.isHls,
          quality: selectedQuality
        });

        if (res && res.ok) {
          formatBadge.textContent = "✓ Aktarıldı";
          overlay.style.borderColor = "#10B981";
          setTimeout(() => {
            formatBadge.textContent = "İndir";
            overlay.style.borderColor = "rgba(56, 189, 248, 0.4)";
          }, 3000);
        } else {
          formatBadge.textContent = "Hata!";
          setTimeout(() => { formatBadge.textContent = "İndir"; }, 2000);
        }
      } catch (err) {
        formatBadge.textContent = "Hata!";
        setTimeout(() => { formatBadge.textContent = "İndir"; }, 2000);
      }
    });

    function positionOverlay() {
      if (video.offsetWidth < 180 || video.offsetHeight < 120) {
        overlay.style.display = "none";
        return;
      }
      overlay.style.display = "flex";
      
      const computedPos = window.getComputedStyle(parent).position;
      if (computedPos === "static") {
        parent.style.position = "relative";
      }
    }

    parent.appendChild(overlay);
    positionOverlay();

    const checkSource = () => {
      if (isYouTubeWatchOrShorts()) {
        reportMedia(window.location.href, "MP4", video);
      } else {
        const src = video.currentSrc || video.src;
        if (src && !src.startsWith("blob:")) {
          reportMedia(src, detectFormat(src), video);
        }
      }
      positionOverlay();
    };

    video.addEventListener("play", checkSource);
    video.addEventListener("loadedmetadata", checkSource);
    video.addEventListener("canplay", checkSource);
  }

  function scanMediaElements() {
    if (isYouTubeWatchOrShorts()) {
      reportMedia(window.location.href, "MP4");
    }

    const videos = document.querySelectorAll("video");
    videos.forEach((v) => {
      attachDownloadOverlay(v);
    });

    const audios = document.querySelectorAll("audio");
    audios.forEach((a) => {
      const src = a.currentSrc || a.src;
      if (src && !src.startsWith("blob:")) {
        reportMedia(src, "AUDIO", a);
      }
    });
  }

  scanMediaElements();

  // Handle YouTube SPA navigation
  window.addEventListener("yt-navigate-finish", () => {
    setTimeout(scanMediaElements, 600);
  });

  const observer = new MutationObserver(() => {
    scanMediaElements();
  });

  observer.observe(document.documentElement || document.body, {
    childList: true,
    subtree: true
  });
})();
