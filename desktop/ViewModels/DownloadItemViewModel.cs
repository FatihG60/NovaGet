using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using MyIdm.Engine.Engine;
using MyIdm.Engine.Models;

namespace MyIdm.Desktop.ViewModels;

public sealed class SegmentBarViewModel : INotifyPropertyChanged
{
    private string _rangeText = "";
    private string _downloadedText = "";
    private double _progressPercent;
    private string _statusBadge = "Bekliyor";
    private Brush _barBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246));

    public int Id { get; init; }

    public string RangeText
    {
        get => _rangeText;
        set { if (_rangeText != value) { _rangeText = value; OnPropertyChanged(); } }
    }

    public string DownloadedText
    {
        get => _downloadedText;
        set { if (_downloadedText != value) { _downloadedText = value; OnPropertyChanged(); } }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set { if (Math.Abs(_progressPercent - value) > 0.05) { _progressPercent = value; OnPropertyChanged(); } }
    }

    public string StatusBadge
    {
        get => _statusBadge;
        set { if (_statusBadge != value) { _statusBadge = value; OnPropertyChanged(); } }
    }

    public Brush BarBrush
    {
        get => _barBrush;
        set { if (_barBrush != value) { _barBrush = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DownloadItemViewModel : INotifyPropertyChanged
{
    private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246));    // Cobalt 500 (#3B82F6)
    private static readonly Brush CompletedBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Emerald 500 (#10B981)
    private static readonly Brush PausedBrush = new SolidColorBrush(Color.FromRgb(138, 143, 158));   // Neutral Slate (#8A8F9E)
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));     // Red 500 (#EF4444)

    private const string PausePath = "M6,4 L10,4 L10,20 L6,20 Z M14,4 L18,4 L18,20 L14,20 Z";
    private const string PlayPath = "M5,3 L19,12 L5,21 L5,3 Z";
    private const string FolderPath = "M22,19 C22,20.1 21.1,21 20,21 L4,21 C2.9,21 2,20.1 2,19 L2,5 C2,3.9 2.9,3 4,3 L9,3 L11,6 L20,6 C21.1,6 22,6.9 22,8 L22,19 Z";
    private const string RetryPath = "M23,4 L23,10 L17,10 M1,20 L1,14 L7,14 M3.51,9 A9,9 0 0,1 18.36,5.64 L23,10 M1,14 L5.64,18.36 A9,9 0 0,0 20.49,15";

    private string _statusText = "Hazırlanıyor...";
    private Brush _statusBrush = ActiveBrush;
    private double _progressPercent;
    private string _sizeText = "0,00 / 0,00 MB";
    private string _speedText = "0,00 MB/sn";
    private string _etaText = "--:--";
    private string _segmentsSummary = "1 Parça";
    private string _actionButtonText = "Duraklat";
    private string _actionIconData = PausePath;
    private bool _isRunning;
    private bool _isCompleted;
    private long _lastDownloadedBytes;
    private DateTime _lastSpeedSampleTime = DateTime.UtcNow;
    private double _smoothedSpeedMBps;

    private CancellationTokenSource? _cts;
    private DynamicSegmentEngine? _activeEngine;

    private string _fileName = "";
    private string _outputPath = "";

    public Guid JobId { get; } = Guid.NewGuid();

    public required string FileName
    {
        get => _fileName;
        set { if (_fileName != value) { _fileName = value; OnPropertyChanged(); OnPropertyChanged(nameof(CategoryText)); OnPropertyChanged(nameof(CategoryBackground)); } }
    }

    public required string Url { get; set; }

    public required string OutputPath
    {
        get => _outputPath;
        set { if (_outputPath != value) { _outputPath = value; OnPropertyChanged(); } }
    }

    public int MaxConnections { get; set; } = 8;
    public bool IsHlsStream { get; set; }
    public string Quality { get; set; } = "best";
    public DownloadRequestHeaders? Headers { get; set; }

    public event Action<DownloadItemViewModel>? StateChanged;
    public event Action<DownloadItemViewModel>? DownloadCompleted;

    public void RestoreSavedState(
        bool isCompleted,
        double progressPercent,
        string sizeText,
        string statusText,
        string segmentsSummary)
    {
        IsRunning = false;
        IsCompleted = isCompleted;
        ProgressPercent = isCompleted ? 100.0 : Math.Clamp(progressPercent, 0.0, 99.9);
        SizeText = !string.IsNullOrWhiteSpace(sizeText) ? sizeText : "0,00 MB";
        SegmentsSummary = !string.IsNullOrWhiteSpace(segmentsSummary) ? segmentsSummary : $"{MaxConnections} Parça";
        SpeedText = "0,00 MB/sn";
        EtaText = isCompleted ? "00:00" : "Duraklatıldı";

        if (isCompleted)
        {
            StatusText = !string.IsNullOrWhiteSpace(statusText) ? statusText : "Tamamlandı";
            StatusBrush = CompletedBrush;
            ActionButtonText = "Klasörde Göster";
            ActionIconData = FolderPath;
        }
        else
        {
            StatusText = "Duraklatıldı · Devam etmeye hazır";
            StatusBrush = PausedBrush;
            ActionButtonText = "Devam Et";
            ActionIconData = PlayPath;
        }
    }

    public string CategoryText
    {
        get
        {
            string ext = Path.GetExtension(FileName).ToLowerInvariant();
            if (Quality == "audio" || ext is ".mp3" or ".m4a" or ".wav" or ".flac" or ".aac") return "AUDIO";
            if (IsHlsStream || YtDlpStreamDownloader.ShouldUseYtDlp(Url, Headers?.Referrer) ||
                ext is ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".ts" or ".flv") return "VIDEO";
            if (ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".iso") return "ARCHIVE";
            if (ext is ".exe" or ".msi" or ".bin" or ".apk" or ".dmg") return "BINARY";
            if (ext is ".pdf" or ".doc" or ".docx" or ".txt") return "DOC";
            return "FILE";
        }
    }

    public Brush CategoryBackground => new SolidColorBrush(Color.FromRgb(23, 26, 36)); // Restrained Carbon-Slate #171A24

    public ObservableCollection<SegmentBarViewModel> SegmentBars { get; } = [];

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        set { _statusBrush = value; OnPropertyChanged(); }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set { _progressPercent = value; OnPropertyChanged(); }
    }

    public string SizeText
    {
        get => _sizeText;
        set { _sizeText = value; OnPropertyChanged(); }
    }

    public string SpeedText
    {
        get => _speedText;
        set { _speedText = value; OnPropertyChanged(); }
    }

    public string EtaText
    {
        get => _etaText;
        set { _etaText = value; OnPropertyChanged(); }
    }

    public string SegmentsSummary
    {
        get => _segmentsSummary;
        set { _segmentsSummary = value; OnPropertyChanged(); }
    }

    public string ActionButtonText
    {
        get => _actionButtonText;
        set { _actionButtonText = value; OnPropertyChanged(); }
    }

    public string ActionIconData
    {
        get => _actionIconData;
        set { _actionIconData = value; OnPropertyChanged(); }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set { _isRunning = value; OnPropertyChanged(); }
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        private set { _isCompleted = value; OnPropertyChanged(); }
    }

    public double CurrentSpeedMBps => _isRunning ? _smoothedSpeedMBps : 0.0;

    public async Task StartOrResumeAsync()
    {
        if (IsRunning || IsCompleted) return;

        IsRunning = true;
        StatusText = YtDlpStreamDownloader.ShouldUseYtDlp(Url, Headers?.Referrer)
            ? "Medya akışı çözümleniyor..."
            : (IsHlsStream ? "HLS akışı indiriliyor" : "İndiriliyor");
        StatusBrush = ActiveBrush;
        ActionButtonText = "Duraklat";
        ActionIconData = PausePath;
        _lastSpeedSampleTime = DateTime.UtcNow;
        _cts = new CancellationTokenSource();

        try
        {
            if (YtDlpStreamDownloader.ShouldUseYtDlp(Url, Headers?.Referrer))
            {
                await RunYtDlpDownloadAsync(_cts.Token);
            }
            else if (IsHlsStream)
            {
                await RunHlsDownloadAsync(_cts.Token);
            }
            else
            {
                await RunSegmentedDownloadAsync(_cts.Token);
            }

            if (IsCompleted)
            {
                StateChanged?.Invoke(this);
                DownloadCompleted?.Invoke(this);
            }
        }
        catch (OperationCanceledException)
        {
            IsRunning = false;
            StatusText = "Duraklatıldı (.part kaydedildi)";
            StatusBrush = PausedBrush;
            SpeedText = "0,00 MB/sn";
            EtaText = "Duraklatıldı";
            ActionButtonText = "Devam Et";
            ActionIconData = PlayPath;
            foreach (var bar in SegmentBars)
            {
                if (bar.ProgressPercent < 100.0)
                {
                    bar.StatusBadge = "Duraklatıldı";
                    bar.BarBrush = PausedBrush;
                }
            }
            StateChanged?.Invoke(this);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            StatusText = $"Hata: {ex.Message}";
            StatusBrush = ErrorBrush;
            SpeedText = "0,00 MB/sn";
            ActionButtonText = "Tekrar Dene";
            ActionIconData = RetryPath;
            StateChanged?.Invoke(this);
        }
    }

    public void Pause()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
    }

    public async Task TogglePauseResumeAsync()
    {
        if (IsCompleted)
        {
            OpenContainingFolder();
            return;
        }
        if (IsRunning)
        {
            Pause();
        }
        else
        {
            await StartOrResumeAsync();
        }
    }

    public void OpenContainingFolder()
    {
        if (File.Exists(OutputPath))
        {
            Process.Start("explorer.exe", $"/select,\"{OutputPath}\"");
        }
        else if (Directory.Exists(Path.GetDirectoryName(OutputPath)))
        {
            Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(OutputPath)}\"");
        }
    }

    private async Task RunSegmentedDownloadAsync(CancellationToken ct)
    {
        using var engine = new DynamicSegmentEngine(new EngineOptions
        {
            MaxConnections = MaxConnections,
            CustomHeaders = Headers
        });
        _activeEngine = engine;

        DownloadMetrics metrics;
        try
        {
            metrics = await engine.DownloadAsync(
                Url,
                OutputPath,
                (segments, totalBytes) =>
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        UpdateFromSegments(segments, totalBytes, engine.DynamicSplitsPerformed, engine.OverlapVerificationsPassed);
                    });
                },
                ct);
        }
        catch (HlsDetectedException)
        {
            IsHlsStream = true;
            StatusText = "HLS Akışı Tespit Edildi, Birleştiriliyor...";
            await RunHlsDownloadAsync(ct);
            return;
        }

        IsRunning = false;
        IsCompleted = true;
        ProgressPercent = 100.0;
        StatusText = $"Tamamlandı ({metrics.ThroughputMBps:F1} MB/sn)";
        StatusBrush = CompletedBrush;
        SpeedText = $"{metrics.ThroughputMBps:F2} MB/sn (Ort.)";
        EtaText = "00:00";
        ActionButtonText = "Klasörde Göster";
        ActionIconData = FolderPath;
    }

    private async Task RunHlsDownloadAsync(CancellationToken ct)
    {
        using var hls = new HlsStreamDownloader(MaxConnections, Headers, Quality);
        var sw = Stopwatch.StartNew();

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            SegmentsSummary = $"{MaxConnections} Paralel HLS Akışı";
            if (SegmentBars.Count == 0)
            {
                SegmentBars.Add(new SegmentBarViewModel
                {
                    Id = 1,
                    RangeText = "HLS Medya Akışı (Video + Ses TS Parçaları)",
                    StatusBadge = "Bağlanıyor...",
                    BarBrush = ActiveBrush,
                    ProgressPercent = 0
                });
            }
        });

        long initialCachedBytes = -1;
        int initialCachedSegs = -1;

        long totalBytes = await hls.DownloadM3u8Async(
            Url,
            OutputPath,
            (doneSegs, totalSegs, bytesDown) =>
            {
                if (initialCachedBytes < 0)
                {
                    initialCachedBytes = doneSegs > 1 ? bytesDown : 0;
                    initialCachedSegs = doneSegs > 1 ? doneSegs : 0;
                }

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    ProgressPercent = totalSegs > 0 ? Math.Clamp(doneSegs * 100.0 / totalSegs, 0.0, 100.0) : 0;
                    double mb = bytesDown / (1024.0 * 1024.0);
                    double newlyDownloadedMb = Math.Max(0.0, (bytesDown - initialCachedBytes) / (1024.0 * 1024.0));
                    double sec = Math.Max(0.2, sw.Elapsed.TotalSeconds);
                    _smoothedSpeedMBps = newlyDownloadedMb / sec;

                    SizeText = $"{mb:F2} MB ({doneSegs}/{totalSegs} Parça)";
                    SpeedText = $"{_smoothedSpeedMBps:F2} MB/sn";
                    StatusText = $"HLS İndiriliyor (%{ProgressPercent:F0} — {doneSegs}/{totalSegs})";
                    SegmentsSummary = $"{doneSegs}/{totalSegs} HLS Parça";

                    int newSegsDone = Math.Max(0, doneSegs - initialCachedSegs);
                    if (newSegsDone > 0 && totalSegs > doneSegs)
                    {
                        double secsPerSeg = sec / newSegsDone;
                        double remSecs = (totalSegs - doneSegs) * secsPerSeg;
                        var eta = TimeSpan.FromSeconds(Math.Clamp(remSecs, 0, 359999));
                        EtaText = eta.TotalHours >= 1 ? eta.ToString(@"hh\:mm\:ss") : eta.ToString(@"mm\:ss");
                    }
                    else if (doneSegs >= totalSegs && totalSegs > 0)
                    {
                        StatusText = "FFmpeg ile MP4 Olarak Birleştiriliyor...";
                        EtaText = "00:00";
                    }

                    if (SegmentBars.Count > 0)
                    {
                        SegmentBars[0].ProgressPercent = ProgressPercent;
                        SegmentBars[0].StatusBadge = $"%{ProgressPercent:F0} ({SpeedText})";
                        SegmentBars[0].DownloadedText = SizeText;
                    }
                });
            },
            ct);

        double finalMb = totalBytes / (1024.0 * 1024.0);
        IsRunning = false;
        IsCompleted = true;
        ProgressPercent = 100.0;
        SizeText = $"{finalMb:F2} MB (Tamamlandı)";
        StatusText = "HLS Birleştirildi (.ts/.mp4)";
        StatusBrush = CompletedBrush;
        EtaText = "00:00";
        ActionButtonText = "Klasörde Göster";
        ActionIconData = FolderPath;

        if (SegmentBars.Count > 0)
        {
            SegmentBars[0].ProgressPercent = 100.0;
            SegmentBars[0].StatusBadge = "Tamamlandı";
            SegmentBars[0].BarBrush = CompletedBrush;
        }
    }

    private async Task RunYtDlpDownloadAsync(CancellationToken ct)
    {
        var ytdlp = new YtDlpStreamDownloader(MaxConnections, Headers, Quality);
        var sw = Stopwatch.StartNew();
        bool isAudioOnly = string.Equals(Quality, "audio", StringComparison.OrdinalIgnoreCase);
        string ext = isAudioOnly ? ".mp3" : ".mp4";

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            string qLabel = Quality switch
            {
                "2160" => "4K UHD",
                "1080" => "1080p FHD",
                "720" => "720p HD",
                "480" => "480p",
                "audio" => "Sadece Ses (MP3)",
                _ => "En İyi Kalite"
            };
            SegmentsSummary = $"{MaxConnections} Akış ({qLabel})";
            SegmentBars.Clear();
            SegmentBars.Add(new SegmentBarViewModel
            {
                Id = 1,
                RangeText = isAudioOnly ? "Akış #1 — Yüksek Kalite Ses (MP3)" : $"Akış #1 — Video ({qLabel})",
                StatusBadge = "Çözümleniyor...",
                BarBrush = ActiveBrush,
                ProgressPercent = 0
            });
            if (!isAudioOnly)
            {
                SegmentBars.Add(new SegmentBarViewModel
                {
                    Id = 2,
                    RangeText = "Akış #2 — Yüksek Kalite Ses (M4A/AAC)",
                    StatusBadge = "Bekliyor",
                    BarBrush = ActiveBrush,
                    ProgressPercent = 0
                });
            }
        });

        (long totalBytes, string finalPath, string? resolvedTitle) = await ytdlp.DownloadAsync(
            Url,
            OutputPath,
            prog =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!string.IsNullOrWhiteSpace(prog.ResolvedTitle))
                    {
                        string currentNoExt = Path.GetFileNameWithoutExtension(FileName);
                        string cleanCurr = System.Text.RegularExpressions.Regex.Replace(currentNoExt, @"^\(\d+\)\s*", "").Trim();
                        if (string.IsNullOrWhiteSpace(cleanCurr) ||
                            cleanCurr.Equals("YouTube", StringComparison.OrdinalIgnoreCase) ||
                            cleanCurr.Equals("video", StringComparison.OrdinalIgnoreCase) ||
                            cleanCurr.Equals("videoplayback", StringComparison.OrdinalIgnoreCase) ||
                            cleanCurr.StartsWith("YouTube_Video_", StringComparison.OrdinalIgnoreCase) ||
                            cleanCurr.StartsWith("download_", StringComparison.OrdinalIgnoreCase))
                        {
                            FileName = prog.ResolvedTitle + ext;
                        }
                    }

                    StatusText = prog.Phase;
                    if (prog.Percent > 0)
                    {
                        ProgressPercent = Math.Clamp(prog.Percent, 0.0, 99.5);
                    }

                    double downMb = prog.DownloadedBytes / (1024.0 * 1024.0);
                    double totalMb = prog.TotalBytes / (1024.0 * 1024.0);
                    if (prog.TotalBytes > 0)
                    {
                        SizeText = $"{downMb:F2} / {totalMb:F2} MB (%{ProgressPercent:F0})";
                    }
                    else if (prog.DownloadedBytes > 0)
                    {
                        SizeText = $"{downMb:F2} MB indirildi";
                    }

                    if (prog.SpeedBytesPerSec > 0)
                    {
                        _smoothedSpeedMBps = prog.SpeedBytesPerSec / (1024.0 * 1024.0);
                        SpeedText = $"{_smoothedSpeedMBps:F2} MB/sn";
                    }

                    if (prog.EtaSeconds.HasValue && prog.EtaSeconds.Value >= 0)
                    {
                        var ts = TimeSpan.FromSeconds(prog.EtaSeconds.Value);
                        EtaText = ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
                    }

                    if (SegmentBars.Count == 1)
                    {
                        double pct = Math.Clamp(prog.Percent / 0.85, 0.0, 100.0);
                        ProgressPercent = pct;
                        SegmentBars[0].ProgressPercent = pct;
                        SegmentBars[0].StatusBadge = $"%{pct:F0} ({SpeedText})";
                        SegmentBars[0].DownloadedText = SizeText;
                    }
                    else if (SegmentBars.Count >= 2)
                    {
                        if (prog.StreamIndex == 1)
                        {
                            double videoPct = Math.Clamp(prog.Percent / 0.85, 0.0, 100.0);
                            SegmentBars[0].ProgressPercent = videoPct;
                            SegmentBars[0].StatusBadge = $"%{videoPct:F0} ({SpeedText})";
                            SegmentBars[0].DownloadedText = SizeText;
                        }
                        else
                        {
                            SegmentBars[0].ProgressPercent = 100.0;
                            SegmentBars[0].StatusBadge = "Tamamlandı";
                            SegmentBars[0].BarBrush = CompletedBrush;

                            double audioPct = Math.Clamp((prog.Percent - 85.0) / 0.14, 0.0, 100.0);
                            SegmentBars[1].ProgressPercent = audioPct;
                            SegmentBars[1].StatusBadge = $"%{audioPct:F0} ({SpeedText})";
                            SegmentBars[1].DownloadedText = SizeText;
                        }
                    }
                });
            },
            ct);

        OutputPath = finalPath;
        FileName = Path.GetFileName(finalPath);

        double finalSizeMb = totalBytes / (1024.0 * 1024.0);
        double avgSpeed = finalSizeMb / Math.Max(0.1, sw.Elapsed.TotalSeconds);

        IsRunning = false;
        IsCompleted = true;
        ProgressPercent = 100.0;
        SizeText = $"{finalSizeMb:F2} / {finalSizeMb:F2} MB";
        StatusText = isAudioOnly ? "Tamamlandı (Yüksek Kalite MP3)" : "Tamamlandı (Video + Ses MP4)";
        StatusBrush = CompletedBrush;
        SpeedText = $"{avgSpeed:F2} MB/sn (Ort.)";
        EtaText = "00:00";
        ActionButtonText = "Klasörde Göster";
        ActionIconData = FolderPath;

        foreach (var bar in SegmentBars)
        {
            bar.ProgressPercent = 100.0;
            bar.StatusBadge = "Tamamlandı";
            bar.BarBrush = CompletedBrush;
        }
    }

    private void UpdateFromSegments(
        IReadOnlyList<SegmentState> segments,
        long totalBytes,
        int dynamicSplits,
        int overlapPassed)
    {
        long downloaded = 0;
        int activeCount = 0;

        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            downloaded += s.DownloadedBytes;
            if (s.IsActive) activeCount++;

            SegmentBarViewModel bar;
            if (i < SegmentBars.Count)
            {
                bar = SegmentBars[i];
            }
            else
            {
                bar = new SegmentBarViewModel { Id = s.Id };
                SegmentBars.Add(bar);
            }

            double startMb = s.StartOffset / (1024.0 * 1024.0);
            double downMb = s.DownloadedBytes / (1024.0 * 1024.0);

            if (totalBytes > 0 && s.EndOffset < long.MaxValue - 1000)
            {
                double endMb = s.EndOffset / (1024.0 * 1024.0);
                double segTotalMb = s.TotalSegmentBytes / (1024.0 * 1024.0);

                bar.RangeText = $"{startMb:F2} MB – {endMb:F2} MB";
                bar.DownloadedText = $"{downMb:F2} / {segTotalMb:F2} MB (%{s.ProgressPercent:F0})";
                bar.ProgressPercent = s.ProgressPercent;
            }
            else
            {
                bar.RangeText = $"{startMb:F2} MB – Akış";
                bar.DownloadedText = $"{downMb:F2} MB (Canlı)";
                bar.ProgressPercent = s.IsCompleted ? 100.0 : 50.0;
            }

            if (s.IsCompleted)
            {
                bar.StatusBadge = s.OverlapVerified ? "Tamamlandı · 64B Doğrulandı" : "Tamamlandı";
                bar.BarBrush = CompletedBrush;
            }
            else if (s.IsActive)
            {
                bar.StatusBadge = s.OverlapVerified ? "Aktif · 64B Doğrulandı" : "İndiriliyor";
                bar.BarBrush = ActiveBrush;
            }
            else
            {
                bar.StatusBadge = "Sırada";
                bar.BarBrush = PausedBrush;
            }
        }

        double downloadedMb = downloaded / (1024.0 * 1024.0);
        if (totalBytes > 0)
        {
            double totalMb = totalBytes / (1024.0 * 1024.0);
            ProgressPercent = Math.Clamp(downloaded * 100.0 / totalBytes, 0.0, 100.0);
            SizeText = $"{downloadedMb:F2} / {totalMb:F2} MB";
        }
        else
        {
            ProgressPercent = 50.0;
            SizeText = $"{downloadedMb:F2} MB (Canlı Akış)";
        }

        SegmentsSummary = $"{segments.Count} Parça · {activeCount} Aktif · {dynamicSplits} Bölünme" +
                          (overlapPassed > 0 ? $" · {overlapPassed} Doğrulama" : "");

        var now = DateTime.UtcNow;
        double dt = (now - _lastSpeedSampleTime).TotalSeconds;
        if (dt >= 0.12)
        {
            long deltaBytes = Math.Max(0, downloaded - _lastDownloadedBytes);
            double instantMBps = (deltaBytes / (1024.0 * 1024.0)) / dt;
            _smoothedSpeedMBps = _smoothedSpeedMBps <= 0.01
                ? instantMBps
                : (_smoothedSpeedMBps * 0.65 + instantMBps * 0.35);

            _lastDownloadedBytes = downloaded;
            _lastSpeedSampleTime = now;

            if (IsRunning)
            {
                SpeedText = $"{_smoothedSpeedMBps:F2} MB/sn";
                if (totalBytes > 0)
                {
                    long remBytes = Math.Max(0, totalBytes - downloaded);
                    if (_smoothedSpeedMBps > 0.05)
                    {
                        double remSec = (remBytes / (1024.0 * 1024.0)) / _smoothedSpeedMBps;
                        var ts = TimeSpan.FromSeconds(Math.Min(359999, remSec));
                        EtaText = ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
                    }
                }
                else
                {
                    EtaText = "Akış";
                }
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
