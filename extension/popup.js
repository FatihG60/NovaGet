// MyIDM Popup Controller
(async function () {
  const statusEl = document.getElementById("status");
  const statusText = document.getElementById("statusText");
  const mediaListEl = document.getElementById("mediaList");
  const mediaCountEl = document.getElementById("mediaCount");
  const enabledBox = document.getElementById("enabled");
  const minSizeBox = document.getElementById("minSizeMB");
  const extsBox = document.getElementById("extensions");
  const saveBtn = document.getElementById("save");

  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const tabId = tab?.id;

  async function refresh() {
    try {
      const resp = await chrome.runtime.sendMessage({ type: "status", tabId });
      if (!resp) return;

      // Status indicator
      if (resp.alive) {
        statusEl.className = "pill ok";
        statusText.textContent = "Bağlı (10010)";
      } else {
        statusEl.className = "pill bad";
        statusText.textContent = "Masaüstü Kapalı";
      }

      // Settings
      if (resp.settings) {
        enabledBox.checked = !!resp.settings.enabled;
        minSizeBox.value = resp.settings.minSizeMB ?? 1;
        extsBox.value = resp.settings.extensions || "";
      }

      // Media list
      renderMedia(resp.mediaList || []);
    } catch (e) {
      statusEl.className = "pill bad";
      statusText.textContent = "Hata";
    }
  }

  function renderMedia(list) {
    mediaCountEl.textContent = String(list.length);

    if (list.length === 0) {
      mediaListEl.innerHTML = `
        <div class="empty-state">
          Bu sayfada henüz video/ses akışı yakalanmadı.<br>
          Bir video oynattığınızda burada listelenecektir.
        </div>`;
      return;
    }

    // Prioritize Video and Master playlists above Audio-only tracks
    const sorted = [...list].sort((a, b) => {
      const getRank = (item) => {
        const fmt = item.format || "";
        if (fmt === "HLS-MASTER") return 1;
        if (fmt === "HLS-VIDEO" || fmt === "MP4" || fmt === "VIDEO") return 2;
        if (fmt === "DASH") return 3;
        if (fmt.includes("AUDIO")) return 4;
        return 5;
      };
      return getRank(a) - getRank(b);
    });

    mediaListEl.innerHTML = "";
    sorted.forEach((item) => {
      const card = document.createElement("div");
      card.className = "media-card";

      const titleRow = document.createElement("div");
      titleRow.className = "media-title-row";

      const titleSpan = document.createElement("div");
      titleSpan.className = "media-title";
      titleSpan.textContent = item.filename || "Video Akışı";
      titleSpan.title = item.url;

      const formatTag = document.createElement("span");
      formatTag.className = `format-tag format-${item.format || 'VIDEO'}`;

      let label = item.format || "VIDEO";
      if (item.format === "HLS-MASTER") label = "🎬 Video (Master)";
      else if (item.format === "HLS-VIDEO") label = "🎬 Video Akışı";
      else if (item.format === "HLS-AUDIO") label = "🎵 Ses (Audio Only)";
      else if (item.format === "AUDIO") label = "🎵 Ses Dosyası";
      formatTag.textContent = label;

      titleRow.appendChild(titleSpan);
      titleRow.appendChild(formatTag);

      const metaRow = document.createElement("div");
      metaRow.className = "media-meta-row";

      const sizeSpan = document.createElement("span");
      sizeSpan.className = "media-size";
      sizeSpan.textContent = item.sizeText || "Bilinmiyor";

      const actionGroup = document.createElement("div");
      actionGroup.className = "action-group";

      const qualitySelect = document.createElement("select");
      qualitySelect.className = "quality-select";
      qualitySelect.title = "Kalite Seçimi";
      qualitySelect.innerHTML = `
        <option value="best">En İyi</option>
        <option value="1080">1080p</option>
        <option value="720">720p</option>
        <option value="480">480p</option>
        <option value="audio">🎵 MP3</option>
      `;

      const dlBtn = document.createElement("button");
      dlBtn.className = "btn-download";
      dlBtn.innerHTML = `
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5">
          <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path>
          <polyline points="7 10 12 15 17 10"></polyline>
          <line x1="12" y1="15" x2="12" y2="3"></line>
        </svg>
        <span>İndir</span>
      `;

      dlBtn.addEventListener("click", async () => {
        dlBtn.disabled = true;
        dlBtn.textContent = "⏳ Gönderiliyor...";
        const selectedQuality = qualitySelect.value || "best";
        let fname = item.filename || "video.mp4";
        if (selectedQuality === "audio") {
          fname = fname.replace(/\.(mp4|ts|mkv|webm)$/i, "") + ".mp3";
        }
        try {
          const res = await chrome.runtime.sendMessage({
            type: "sendMedia",
            url: item.url,
            filename: fname,
            referrer: tab?.url,
            isHls: item.isHls,
            quality: selectedQuality
          });

          if (res && res.ok) {
            dlBtn.textContent = "✓ Aktarıldı!";
            dlBtn.style.background = "#059669";
          } else {
            dlBtn.textContent = "Hata!";
            dlBtn.style.background = "#DC2626";
            setTimeout(() => { dlBtn.disabled = false; dlBtn.textContent = "İndir"; dlBtn.style.background = ""; }, 2000);
          }
        } catch (err) {
          dlBtn.textContent = "Hata!";
          setTimeout(() => { dlBtn.disabled = false; dlBtn.textContent = "İndir"; }, 2000);
        }
      });

      actionGroup.appendChild(qualitySelect);
      actionGroup.appendChild(dlBtn);

      metaRow.appendChild(sizeSpan);
      metaRow.appendChild(actionGroup);

      card.appendChild(titleRow);
      card.appendChild(metaRow);
      mediaListEl.appendChild(card);
    });
  }

  saveBtn.addEventListener("click", async () => {
    saveBtn.disabled = true;
    saveBtn.textContent = "Kaydediliyor…";
    await chrome.runtime.sendMessage({
      type: "saveSettings",
      settings: {
        enabled: enabledBox.checked,
        minSizeMB: parseFloat(minSizeBox.value) || 0,
        extensions: extsBox.value
      }
    });
    saveBtn.textContent = "✓ Kaydedildi";
    setTimeout(() => {
      saveBtn.disabled = false;
      saveBtn.textContent = "Ayarları Kaydet";
    }, 1500);
  });

  await refresh();
  setInterval(refresh, 2500);
})();
