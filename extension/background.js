// MyIDM Manifest V3 Service Worker — Gelişmiş Video & Ağ Yakalayıcı (Sniffer)
const BRIDGE = "http://127.0.0.1:10010";
const DEFAULTS = {
  enabled: true,
  minSizeMB: 1,
  extensions: "zip,rar,7z,exe,msi,iso,bin,mp4,mkv,avi,mp3,pdf,dmg,apk,tar,gz,webm,flv,mov,m4v,ts,m3u8,mpd"
};

// tabId -> Map(url -> MediaInfo)
const mediaByTab = new Map();

async function getSettings() {
  const s = await chrome.storage.local.get(DEFAULTS);
  return { ...DEFAULTS, ...s };
}

async function isBridgeAlive() {
  try {
    const r = await fetch(`${BRIDGE}/api/status`, { signal: AbortSignal.timeout(800) });
    return r.ok;
  } catch {
    return false;
  }
}

async function cookieHeaderFor(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map(c => `${c.name}=${c.value}`).join("; ");
  } catch {
    return "";
  }
}

async function sendToMyIdm({ url, filename, referrer, isHls = false, quality = null }) {
  const body = {
    url,
    filename: filename || null,
    referrer: referrer || null,
    cookies: await cookieHeaderFor(url),
    userAgent: navigator.userAgent,
    isHls,
    quality: quality || null
  };
  const r = await fetch(`${BRIDGE}/api/download`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body)
  });
  if (!r.ok) throw new Error(`Köprü hata döndü: ${r.status}`);
  return r.json();
}

function formatBytes(bytes) {
  if (!bytes || bytes <= 0) return "Bilinmiyor";
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + " KB";
  if (bytes < 1024 * 1024 * 1024) return (bytes / (1024 * 1024)).toFixed(1) + " MB";
  return (bytes / (1024 * 1024 * 1024)).toFixed(2) + " GB";
}

function cleanFilename(str) {
  if (!str) return "video";
  return str
    .replace(/^\(\d+\)\s*/, "")
    .replace(/[\\/:*?"<>|\r\n]+/g, "_")
    .trim()
    .substring(0, 80);
}

// 1. Standart Tarayıcı İndirmelerini Devralma
chrome.downloads.onCreated.addListener(async (item) => {
  const settings = await getSettings();
  if (!settings.enabled) return;

  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url)) return;

  const allowed = settings.extensions.split(",").map(s => s.trim().toLowerCase()).filter(Boolean);
  const extMatch = /\.([a-z0-9]{1,6})(?:$|\?)/i.exec(item.filename || url || "");
  const ext = extMatch ? extMatch[1].toLowerCase() : "";
  const sizeOk = item.totalBytes <= 0 || item.totalBytes >= settings.minSizeMB * 1024 * 1024;

  if (!allowed.includes(ext) || !sizeOk) return;
  if (!(await isBridgeAlive())) return;

  try {
    await chrome.downloads.cancel(item.id);
    await chrome.downloads.erase({ id: item.id });
    const fname = item.filename ? item.filename.split(/[\\/]/).pop() : null;
    await sendToMyIdm({ url, filename: fname, referrer: item.referrer });
  } catch (e) {
    console.warn("MyIDM devralma başarısız, tarayıcıya geri bırakılıyor:", e);
    chrome.downloads.download({ url }).catch(() => {});
  }
});

// 2. Gelişmiş Video & Medya İstekleri Dinleyicisi
const MEDIA_EXTS_REGEX = /\.(mp4|m4v|webm|mkv|flv|avi|mov|mpd|m3u8|mp3|m4a|ogg|opus|flac|wav)(\?|$)/i;
const HLS_REGEX = /(\.m3u8|\/hls\/.*?\.(txt|m3u8)|\/master\.txt)(\?|$)/i;
const DASH_REGEX = /\.mpd(\?|$)/i;
const SEGMENT_CHUNK_REGEX = /\.(jpg|jpeg|png|gif|webp|svg|ico|css|js|woff2?|ttf|vtt|srt|ass|ts|m4s|aac)(\?|$)/i;

chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (details.tabId < 0) return;

    // Ignore YouTube SABR internal chunks (handled via clean watch URL)
    if (details.url.includes("googlevideo.com/videoplayback")) return;

    const lowerUrl = details.url.toLowerCase();
    const pathPart = lowerUrl.split("?")[0].split("#")[0];

    // Never treat disguised image/segment chunks (.jpg, .ts, .m4s, .vtt) as standalone videos
    if (SEGMENT_CHUNK_REGEX.test(pathPart) ||
        /(?:\/|_|-)(?:seg|segment|chunk|frag|fragment|image\d+|sublist_aud)\d*/i.test(pathPart)) {
      return;
    }

    const headers = details.responseHeaders || [];
    const getHeader = (name) => (headers.find(h => h.name.toLowerCase() === name)?.value || "");
    const contentType = getHeader("content-type").toLowerCase();
    const contentLength = parseInt(getHeader("content-length") || "0", 10);
    const contentDisposition = getHeader("content-disposition");

    // Ignore raw MPEG-TS segment chunks (video/mp2t)
    if (contentType.includes("video/mp2t")) {
      return;
    }

    const isHls = HLS_REGEX.test(details.url) || contentType.includes("mpegurl");
    const isDash = DASH_REGEX.test(details.url) || contentType.includes("dash");
    const isVideoMime = contentType.startsWith("video/") || isHls || isDash;
    const isAudioMime = contentType.startsWith("audio/");
    const isMediaUrl = MEDIA_EXTS_REGEX.test(pathPart) || isHls || isDash;

    if (!isVideoMime && !isAudioMime && !isMediaUrl && details.type !== "media") {
      return;
    }

    // Ignore tiny fragments / audio pings under 300 KB unless it is an explicit m3u8 / master.txt / mpd manifest
    if (!isHls && !isDash && contentLength > 0 && contentLength < 300 * 1024) {
      return;
    }

    // Extract filename from disposition or URL
    let filename = "";
    if (contentDisposition) {
      const match = /filename\*?=['"]?(?:UTF-8'')?([^;"'\r\n]+)/i.exec(contentDisposition);
      if (match && match[1]) filename = decodeURIComponent(match[1]);
    }
    if (!filename) {
      filename = pathPart.split("/").pop() || "";
    }

    const isAudioUrl = pathPart.includes("audio") || pathPart.includes("sound") || pathPart.includes("voice") ||
                       pathPart.includes("a-eng") || pathPart.includes("/audio/") || pathPart.includes("sublist_aud") ||
                       pathPart.endsWith(".mp3") || pathPart.endsWith(".m4a");

    let format = "VIDEO";
    if (isHls) {
      if (pathPart.includes("master") || pathPart.includes("playlist") || pathPart.includes("index")) {
        format = "HLS-MASTER";
      } else if (isAudioUrl || isAudioMime) {
        format = "HLS-AUDIO";
      } else {
        format = "HLS-VIDEO";
      }
    }
    else if (isDash) format = "DASH";
    else if (contentType.includes("mp4") || pathPart.endsWith(".mp4")) format = isAudioUrl ? "AUDIO" : "MP4";
    else if (contentType.includes("webm") || pathPart.endsWith(".webm")) format = isAudioUrl ? "AUDIO" : "WebM";
    else if (isAudioMime || isAudioUrl) format = "AUDIO";

    let tabMap = mediaByTab.get(details.tabId);
    if (!tabMap) {
      tabMap = new Map();
      mediaByTab.set(details.tabId, tabMap);
    }

    if (!tabMap.has(details.url)) {
      tabMap.set(details.url, {
        id: details.url,
        url: details.url,
        filename: cleanFilename(filename) || "video",
        format,
        sizeBytes: contentLength,
        sizeText: format === "HLS-AUDIO" ? "Ses Akışı (Audio Only)" : (isHls ? "HLS Video + Ses (MP4)" : formatBytes(contentLength)),
        isHls,
        isDash,
        detectedAt: Date.now()
      });

      chrome.action.setBadgeBackgroundColor({ color: "#0284C7", tabId: details.tabId });
      chrome.action.setBadgeText({ text: String(tabMap.size), tabId: details.tabId });
    }
  },
  { urls: ["<all_urls>"], types: ["xmlhttprequest", "media", "other"] },
  ["responseHeaders"]
);

// Tab Temizliği
chrome.tabs.onRemoved.addListener((tabId) => mediaByTab.delete(tabId));
chrome.tabs.onUpdated.addListener((tabId, info) => {
  if (info.status === "loading" && info.url) {
    mediaByTab.delete(tabId);
    chrome.action.setBadgeText({ text: "", tabId });
  }
});

// 3. Sağ Tık Menüsü
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: "myidm-download",
    title: "⚡ NovaGet ile İndir",
    contexts: ["link", "video", "audio", "image"]
  });
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const url = info.linkUrl || info.srcUrl;
  if (!url) return;
  try {
    await sendToMyIdm({
      url,
      referrer: info.pageUrl || tab?.url,
      isHls: HLS_REGEX.test(url)
    });
  } catch (e) {
    console.warn("NovaGet'e gönderilemedi:", e);
  }
});

// 4. İletişim (Popup ve Content Script)
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  (async () => {
    const tabId = sender.tab?.id ?? msg.tabId;

    if (msg.type === "status") {
      const settings = await getSettings();
      const mediaList = tabId != null ? [...(mediaByTab.get(tabId)?.values() || [])] : [];
      sendResponse({ alive: await isBridgeAlive(), settings, mediaList });
    } else if (msg.type === "getTabMedia") {
      const mediaList = tabId != null ? [...(mediaByTab.get(tabId)?.values() || [])] : [];
      sendResponse({ mediaList });
    } else if (msg.type === "mediaDetected") {
      if (tabId != null && msg.media && msg.media.url) {
        const pathLower = msg.media.url.toLowerCase().split("?")[0];
        if (SEGMENT_CHUNK_REGEX.test(pathLower)) {
          sendResponse({ ok: true });
          return;
        }

        let tabMap = mediaByTab.get(tabId);
        if (!tabMap) {
          tabMap = new Map();
          mediaByTab.set(tabId, tabMap);
        }
        let rawTitle = msg.media.title || "";
        const tabTitle = sender.tab?.title || "";
        if ((!rawTitle || /^(video|player|embed|stream|index|watch|rapidvid|jwplayer|master\.txt)$/i.test(rawTitle.trim())) && tabTitle) {
          rawTitle = tabTitle;
        }
        const cleanTitle = cleanFilename(rawTitle || "video").replace(/\.(mp4|ts|mkv|webm|txt|m3u8)$/i, "");
        const newFilename = `${cleanTitle}.mp4`;
        const isHlsStream = (msg.media.format && msg.media.format.startsWith("HLS")) || HLS_REGEX.test(msg.media.url);

        const existing = tabMap.get(msg.media.url);
        if (!existing) {
          tabMap.set(msg.media.url, {
            id: msg.media.url,
            url: msg.media.url,
            filename: newFilename,
            format: msg.media.format || (isHlsStream ? "HLS-MASTER" : "MP4"),
            sizeBytes: 0,
            sizeText: isHlsStream ? "HLS Video + Ses (MP4)" : "Video + Ses (MP4)",
            isHls: isHlsStream,
            detectedAt: Date.now()
          });
          chrome.action.setBadgeBackgroundColor({ color: "#0284C7", tabId });
          chrome.action.setBadgeText({ text: String(tabMap.size), tabId });
        } else {
          if (msg.media.format === "HLS-MASTER" || (!existing.isHls && isHlsStream)) {
            existing.format = msg.media.format || "HLS-MASTER";
            existing.isHls = isHlsStream;
            existing.sizeText = "HLS Video + Ses (MP4)";
          }
          if (cleanTitle && !/^(youtube|video|player|embed|master)$/i.test(cleanTitle)) {
            existing.filename = newFilename;
          }
        }
      }
      sendResponse({ ok: true });
    } else if (msg.type === "sendMedia") {
      try {
        let finalFilename = msg.filename || "video.mp4";
        const basePart = finalFilename.replace(/\.[a-z0-9]+$/i, "").trim();
        const extPart = (/\.[a-z0-9]+$/i.exec(finalFilename)?.[0] || ".mp4").replace(/^\.ts$/i, ".mp4");
        const tabTitle = sender.tab?.title || "";
        if (tabTitle && (!basePart || /^(video|player|embed|stream|index|watch|rapidvid|jwplayer)$/i.test(basePart) || (basePart.length > 25 && !basePart.includes(" ") && !basePart.includes("_")))) {
          finalFilename = `${cleanFilename(tabTitle)}${extPart}`;
        } else {
          finalFilename = `${basePart}${extPart}`;
        }

        await sendToMyIdm({
          url: msg.url,
          filename: finalFilename,
          referrer: msg.referrer,
          isHls: msg.isHls,
          quality: msg.quality
        });
        sendResponse({ ok: true });
      } catch (e) {
        sendResponse({ ok: false, error: String(e) });
      }
    } else if (msg.type === "saveSettings") {
      await chrome.storage.local.set(msg.settings);
      sendResponse({ ok: true });
    }
  })();
  return true;
});
