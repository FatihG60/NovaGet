using System.Diagnostics;
using System.Globalization;
using System.Text;
using MyIdm.Engine.Models;

namespace MyIdm.Engine.Engine;

public sealed record YtDlpProgress(
    long DownloadedBytes,
    long TotalBytes,
    double SpeedBytesPerSec,
    int? EtaSeconds,
    double Percent,
    int StreamIndex,
    string? ResolvedTitle,
    string Phase);

/// <summary>
/// Handles YouTube (bypassing SABR / sabr.malformed_config), X/Twitter, TikTok, Instagram,
/// Twitch, Vimeo, Reddit, and other complex streaming platforms with parallel fragment downloading
/// and automatic FFmpeg video+audio lossless muxing into MP4.
/// </summary>
public sealed class YtDlpStreamDownloader
{
    private readonly int _maxConcurrency;
    private readonly DownloadRequestHeaders? _headers;
    private readonly string _quality;

    private static readonly string[] SupportedHosts =
    [
        "youtube.com/",
        "youtu.be/",
        "googlevideo.com/videoplayback",
        "twitter.com/",
        "x.com/",
        "tiktok.com/",
        "instagram.com/",
        "twitch.tv/",
        "clips.twitch.tv/",
        "vimeo.com/",
        "dailymotion.com/",
        "reddit.com/",
        "v.redd.it/"
    ];

    public YtDlpStreamDownloader(
        int maxConcurrency = 8,
        DownloadRequestHeaders? headers = null,
        string? quality = null)
    {
        _maxConcurrency = Math.Clamp(maxConcurrency, 1, 32);
        _headers = headers;
        _quality = string.IsNullOrWhiteSpace(quality) ? "best" : quality.Trim().ToLowerInvariant();
    }

    public static bool ShouldUseYtDlp(string url, string? referrer = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string lower = url.ToLowerInvariant();

        foreach (var host in SupportedHosts)
        {
            if (lower.Contains(host, StringComparison.Ordinal))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(referrer))
        {
            string refLower = referrer.ToLowerInvariant();
            if (refLower.Contains("youtube.com/watch") ||
                refLower.Contains("youtube.com/shorts") ||
                refLower.Contains("youtu.be/"))
            {
                if (lower.Contains("googlevideo.com"))
                    return true;
            }
        }

        return false;
    }

    public static string ResolveEffectiveUrl(string url, string? referrer)
    {
        if (url.Contains("googlevideo.com/videoplayback", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(referrer) &&
            (referrer.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase) ||
             referrer.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase)))
        {
            return referrer;
        }
        return url;
    }

    public async Task<(long TotalBytes, string FinalOutputPath, string? Title)> DownloadAsync(
        string url,
        string outputPath,
        Action<YtDlpProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        string effectiveUrl = ResolveEffectiveUrl(url, _headers?.Referrer);
        bool isYouTube = effectiveUrl.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase) ||
                         effectiveUrl.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase);
        bool isAudioOnly = _quality == "audio";
        string targetExt = isAudioOnly ? ".mp3" : ".mp4";

        // Ensure .mp4 or .mp3 extension for output
        if (!outputPath.EndsWith(targetExt, StringComparison.OrdinalIgnoreCase))
        {
            outputPath = Path.ChangeExtension(outputPath, targetExt);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        // Delete existing tiny/corrupted file (e.g., 31-byte sabr.malformed_config) if present
        if (File.Exists(outputPath))
        {
            try { File.Delete(outputPath); } catch { }
        }

        var args = new StringBuilder();
        // -u forces unbuffered stdout in Python; --no-simulate --progress forces live progress even when --print is used
        args.Append("-u -m yt_dlp --js-runtimes node --no-playlist --no-part --no-simulate --progress --newline ");
        args.Append($"--concurrent-fragments {_maxConcurrency} ");

        if (isAudioOnly)
        {
            args.Append("-f \"ba/b\" -x --audio-format mp3 --audio-quality 0 ");
        }
        else if (int.TryParse(_quality, out int maxH) && maxH > 0)
        {
            args.Append($"-f \"bv*[height<={maxH}][ext=mp4]+ba[ext=m4a]/b[height<={maxH}][ext=mp4]/bv*[height<={maxH}]+ba/b[height<={maxH}]/b\" ");
            args.Append("--merge-output-format mp4 ");
        }
        else
        {
            args.Append("-f \"bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b\" ");
            args.Append("--merge-output-format mp4 ");
        }

        args.Append("--print \"before_dl:MYIDM_TITLE:%(title)s\" ");
        args.Append("--progress-template \"download:MYIDM_PROG:%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s\" ");

        if (!isYouTube && !string.IsNullOrWhiteSpace(_headers?.Referrer))
        {
            args.Append($"--referer \"{EscapeArg(_headers.Referrer!)}\" ");
        }

        args.Append($"-o \"{EscapeArg(outputPath)}\" ");
        args.Append($"\"{EscapeArg(effectiveUrl)}\"");

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = args.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        string? resolvedTitle = null;
        int streamIndex = 1; // 1 = Video Track, 2 = Audio Track
        long completedStreamsBytes = 0;
        long currentStreamDownloaded = 0;
        long currentStreamTotal = 0;
        var errorLines = new List<string>();

        proc.Start();

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        var stdoutTask = Task.Run(async () =>
        {
            while (await proc.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("MYIDM_TITLE:", StringComparison.Ordinal))
                {
                    string rawTitle = line["MYIDM_TITLE:".Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(rawTitle) && rawTitle != "NA")
                    {
                        resolvedTitle = SanitizeFileName(rawTitle);
                        onProgress?.Invoke(new YtDlpProgress(
                            0, 0, 0, null, 0, 1, resolvedTitle, "Video Bilgisi Alındı, Akış Başlatılıyor..."));
                    }
                }
                else if (line.StartsWith("MYIDM_PROG:", StringComparison.Ordinal))
                {
                    var parts = line["MYIDM_PROG:".Length..].Split('|');
                    if (parts.Length >= 5)
                    {
                        long down = ParseLong(parts[0]);
                        long total = ParseLong(parts[1]);
                        if (total <= 0) total = ParseLong(parts[2]);
                        double speed = ParseDouble(parts[3]);
                        int? eta = ParseInt(parts[4]);

                        // Detect transition from Stream 1 (Video) to Stream 2 (Audio)
                        if (currentStreamDownloaded > 0 && down < currentStreamDownloaded / 2 && streamIndex == 1)
                        {
                            streamIndex = 2;
                            completedStreamsBytes += Math.Max(currentStreamDownloaded, currentStreamTotal);
                            currentStreamDownloaded = 0;
                            currentStreamTotal = 0;
                        }

                        if (down > 0) currentStreamDownloaded = down;
                        if (total > 0) currentStreamTotal = total;

                        double streamPct = currentStreamTotal > 0
                            ? Math.Clamp(currentStreamDownloaded * 100.0 / currentStreamTotal, 0.0, 100.0)
                            : 0.0;

                        // Weight overall progress: Video track = 0..85%, Audio track = 85..99%
                        double overallPct = streamIndex == 1
                            ? streamPct * 0.85
                            : 85.0 + (streamPct * 0.14);

                        string phase = streamIndex == 1
                            ? $"Video Akışı İndiriliyor (%{streamPct:F0})"
                            : $"Ses Akışı İndiriliyor (%{streamPct:F0})";

                        onProgress?.Invoke(new YtDlpProgress(
                            completedStreamsBytes + currentStreamDownloaded,
                            completedStreamsBytes + currentStreamTotal,
                            speed,
                            eta,
                            overallPct,
                            streamIndex,
                            resolvedTitle,
                            phase));
                    }
                }
                else if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("Merging formats", StringComparison.OrdinalIgnoreCase))
                {
                    onProgress?.Invoke(new YtDlpProgress(
                        completedStreamsBytes + currentStreamDownloaded,
                        completedStreamsBytes + currentStreamTotal,
                        0,
                        0,
                        99.0,
                        streamIndex,
                        resolvedTitle,
                        "FFmpeg ile Video + Ses Birleştiriliyor (MP4)..."));
                }
            }
        }, CancellationToken.None);

        var stderrTask = Task.Run(async () =>
        {
            while (await proc.StandardError.ReadLineAsync() is { } errLine)
            {
                if (!string.IsNullOrWhiteSpace(errLine) && !errLine.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
                {
                    errorLines.Add(errLine);
                }
            }
        }, CancellationToken.None);

        await Task.WhenAll(stdoutTask, stderrTask, proc.WaitForExitAsync(ct));

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        if (proc.ExitCode != 0 || !File.Exists(outputPath))
        {
            string errDetail = errorLines.Count > 0 ? string.Join(" | ", errorLines.TakeLast(2)) : $"Exit code {proc.ExitCode}";
            throw new InvalidOperationException($"Video akışı indirilemedi: {errDetail}");
        }

        // If the initial file name was generic like "(89) YouTube.mp4" or "YouTube.mp4" or "YouTube_Video_...",
        // rename it to the real resolved video title!
        string finalPath = outputPath;
        if (!string.IsNullOrWhiteSpace(resolvedTitle))
        {
            string currentNameNoExt = Path.GetFileNameWithoutExtension(outputPath);
            if (IsGenericTitle(currentNameNoExt))
            {
                string dir = Path.GetDirectoryName(outputPath)!;
                string betterPath = Path.Combine(dir, resolvedTitle + targetExt);
                if (!string.Equals(betterPath, outputPath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (File.Exists(betterPath)) File.Delete(betterPath);
                        File.Move(outputPath, betterPath);
                        finalPath = betterPath;
                    }
                    catch { }
                }
            }
        }

        long finalSize = new FileInfo(finalPath).Length;
        return (finalSize, finalPath, resolvedTitle);
    }

    private static bool IsGenericTitle(string name)
    {
        string clean = System.Text.RegularExpressions.Regex.Replace(name, @"^\(\d+\)\s*", "").Trim();
        return string.IsNullOrWhiteSpace(clean) ||
               clean.Equals("YouTube", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("video", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("videoplayback", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("YouTube_Video_", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("download_", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name.Trim();
    }

    private static string EscapeArg(string s) => s.Replace("\"", "\\\"");

    private static long ParseLong(string s)
        => long.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out long v) ? v : 0;

    private static double ParseDouble(string s)
        => double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : 0;

    private static int? ParseInt(string s)
        => int.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out int v) ? v : null;
}
