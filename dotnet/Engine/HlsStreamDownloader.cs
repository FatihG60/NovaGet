using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using MyIdm.Engine.Models;

namespace MyIdm.Engine.Engine;

public sealed record HlsSegmentItem(int Index, string Url, long? ByteRangeOffset, long? ByteRangeLength);

/// <summary>
/// Clean-room HLS (.m3u8 / master.txt) playlist parser and resumable segment downloader.
/// Features:
/// - Persistent per-segment disk cache (.hls_cache) so paused or interrupted downloads resume instantly.
/// - Automatic HTTP 429 (Too Many Requests) & 5xx retry with global cooldown & Retry-After support.
/// - Supports Master Playlists (#EXT-X-STREAM-INF), Fragmented MP4 (#EXT-X-MAP:URI=...), #EXT-X-BYTERANGE,
///   and separate Audio Tracks (#EXT-X-MEDIA:TYPE=AUDIO) with automatic lossless FFmpeg MP4 remuxing.
/// </summary>
public sealed class HlsStreamDownloader : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly int _maxConcurrency;
    private readonly DownloadRequestHeaders? _headers;
    private readonly string? _quality;
    private long _cooldownUntilUtcTicks;
    private int _adaptiveDelayMs;

    public HlsStreamDownloader(
        int maxConcurrency = 8,
        DownloadRequestHeaders? headers = null,
        string? quality = null)
    {
        // Cap HLS segment concurrency to 4 to avoid triggering CDN/Cloudflare 429 burst blocks
        // while still saturating bandwidth across thousands of small segment files.
        _maxConcurrency = Math.Clamp(maxConcurrency, 1, 4);
        _headers = headers;
        _quality = quality;
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = _maxConcurrency * 2,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        string ua = !string.IsNullOrWhiteSpace(headers?.UserAgent)
            ? headers!.UserAgent!
            : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36";
        _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd(ua);
    }

    private void ApplyHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");

        if (_headers is null)
        {
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_headers.Referrer) &&
            Uri.TryCreate(_headers.Referrer, UriKind.Absolute, out var refUri))
        {
            req.Headers.Referrer = refUri;
            req.Headers.TryAddWithoutValidation("Origin", $"{refUri.Scheme}://{refUri.Authority}");
            string fetchSite = req.RequestUri is not null &&
                               string.Equals(refUri.Host, req.RequestUri.Host, StringComparison.OrdinalIgnoreCase)
                ? "same-origin"
                : "cross-site";
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
        }
        else
        {
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        }

        if (!string.IsNullOrWhiteSpace(_headers.Cookies))
        {
            req.Headers.TryAddWithoutValidation("Cookie", _headers.Cookies);
        }
    }

    private async Task WaitForRateLimitCooldownAsync(CancellationToken ct)
    {
        while (true)
        {
            long untilTicks = Interlocked.Read(ref _cooldownUntilUtcTicks);
            long nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks >= untilTicks) break;

            TimeSpan wait = TimeSpan.FromTicks(untilTicks - nowTicks);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct);
            }
        }

        int paceMs = Volatile.Read(ref _adaptiveDelayMs);
        if (paceMs > 0)
        {
            await Task.Delay(paceMs + Random.Shared.Next(0, 60), ct);
        }
    }

    private void TriggerRateLimitCooldown(HttpResponseMessage? resp, int attempt)
    {
        // Enable adaptive pacing (120ms..350ms) once a 429 is encountered on this CDN
        int newPace = Math.Clamp(120 * attempt, 120, 400);
        Interlocked.Exchange(ref _adaptiveDelayMs, newPace);

        double waitSeconds = Math.Clamp(2.5 * attempt, 2.5, 20.0);
        if (resp?.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta.HasValue && retryAfter.Delta.Value.TotalSeconds > 0)
            {
                waitSeconds = Math.Clamp(retryAfter.Delta.Value.TotalSeconds, 1.0, 30.0);
            }
            else if (retryAfter.Date.HasValue)
            {
                double diff = (retryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
                if (diff > 0) waitSeconds = Math.Clamp(diff, 1.0, 30.0);
            }
        }

        long targetTicks = DateTime.UtcNow.AddSeconds(waitSeconds).Ticks;
        long currentTicks = Interlocked.Read(ref _cooldownUntilUtcTicks);
        if (targetTicks > currentTicks)
        {
            Interlocked.CompareExchange(ref _cooldownUntilUtcTicks, targetTicks, currentTicks);
        }
    }

    public async Task<long> DownloadM3u8Async(
        string m3u8Url,
        string outputPath,
        Action<int, int, long>? onSegmentProgress = null,
        CancellationToken ct = default)
    {
        var baseUri = new Uri(m3u8Url);
        string playlistText = await FetchTextAsync(m3u8Url, ct);

        string? audioTrackUrl = null;
        string? videoVariantUrl = null;
        bool isAudioOnlyMode = string.Equals(_quality, "audio", StringComparison.OrdinalIgnoreCase);

        // 1. Master Playlist (#EXT-X-STREAM-INF)
        if (playlistText.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
        {
            int? maxH = int.TryParse(_quality, out int parsedH) && parsedH > 0 ? parsedH : null;
            videoVariantUrl = SelectBestVariantUrl(baseUri, playlistText, maxH);
            audioTrackUrl = SelectAudioTrackUrl(baseUri, playlistText);
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);

        // Persistent segment cache directory for instant pause/error resume
        string cacheDir = fullOutputPath + ".hls_cache";
        Directory.CreateDirectory(cacheDir);

        // Audio-only mode when separate audio playlist exists
        if (isAudioOnlyMode && !string.IsNullOrEmpty(audioTrackUrl))
        {
            string tempAudioOnly = Path.Combine(cacheDir, "audio_only.bin");
            var audioUri = new Uri(baseUri, audioTrackUrl);
            string audioPlaylistText = await FetchTextAsync(audioUri.ToString(), ct);
            List<HlsSegmentItem> audioItems = ParseMediaPlaylist(audioUri, audioPlaylistText);

            long audioBytes = await DownloadSegmentsToFileAsync(
                audioItems, cacheDir, "a", tempAudioOnly, onSegmentProgress, ct);

            bool converted = await TryRemuxSingleWithFfmpegAsync(tempAudioOnly, fullOutputPath, isAudioOnly: true, ct);
            if (!converted)
            {
                if (File.Exists(fullOutputPath)) File.Delete(fullOutputPath);
                File.Move(tempAudioOnly, fullOutputPath);
            }

            CleanupCacheDirectory(cacheDir);
            return audioBytes;
        }

        // CASE A: Demuxed stream with separate Video and Audio playlists
        if (!string.IsNullOrEmpty(videoVariantUrl) && !string.IsNullOrEmpty(audioTrackUrl))
        {
            string tempVideoPath = Path.Combine(cacheDir, "video_track.bin");
            string tempAudioPath = Path.Combine(cacheDir, "audio_track.bin");

            var videoUri = new Uri(baseUri, videoVariantUrl);
            string videoPlaylistText = await FetchTextAsync(videoUri.ToString(), ct);
            List<HlsSegmentItem> videoItems = ParseMediaPlaylist(videoUri, videoPlaylistText);

            var audioUri = new Uri(baseUri, audioTrackUrl);
            string audioPlaylistText = await FetchTextAsync(audioUri.ToString(), ct);
            List<HlsSegmentItem> audioItems = ParseMediaPlaylist(audioUri, audioPlaylistText);

            int totalSegments = videoItems.Count + audioItems.Count;

            // Download video track segments (resumes any already-cached segments on disk)
            long videoBytes = await DownloadSegmentsToFileAsync(
                videoItems,
                cacheDir,
                "v",
                tempVideoPath,
                (doneVideo, _, trackBytesSoFar) =>
                {
                    onSegmentProgress?.Invoke(doneVideo, totalSegments, trackBytesSoFar);
                },
                ct);

            // Download audio track segments (resumes any already-cached segments on disk)
            int videoCount = videoItems.Count;
            long audioBytes = await DownloadSegmentsToFileAsync(
                audioItems,
                cacheDir,
                "a",
                tempAudioPath,
                (doneAudio, _, trackBytesSoFar) =>
                {
                    onSegmentProgress?.Invoke(videoCount + doneAudio, totalSegments, videoBytes + trackBytesSoFar);
                },
                ct);

            long totalBytes = videoBytes + audioBytes;

            // Losslessly remux both tracks into MP4 container with FFmpeg
            bool muxed = await TryRemuxWithFfmpegAsync(tempVideoPath, tempAudioPath, fullOutputPath, ct);
            if (!muxed)
            {
                if (File.Exists(fullOutputPath)) File.Delete(fullOutputPath);
                File.Move(tempVideoPath, fullOutputPath);
            }

            CleanupCacheDirectory(cacheDir);
            return totalBytes;
        }

        // CASE B: Single variant or pre-multiplexed stream
        if (!string.IsNullOrEmpty(videoVariantUrl))
        {
            baseUri = new Uri(baseUri, videoVariantUrl);
            playlistText = await FetchTextAsync(baseUri.ToString(), ct);
        }

        List<HlsSegmentItem> items = ParseMediaPlaylist(baseUri, playlistText);
        if (items.Count == 0)
            throw new InvalidOperationException("M3U8 listesinde medya segmenti bulunamadı.");

        string tempSinglePath = Path.Combine(cacheDir, "single_track.bin");
        long bytes = await DownloadSegmentsToFileAsync(items, cacheDir, "s", tempSinglePath, onSegmentProgress, ct);
        bool remuxed = await TryRemuxSingleWithFfmpegAsync(tempSinglePath, fullOutputPath, isAudioOnlyMode, ct);
        if (!remuxed)
        {
            if (File.Exists(fullOutputPath)) File.Delete(fullOutputPath);
            File.Move(tempSinglePath, fullOutputPath);
        }

        CleanupCacheDirectory(cacheDir);
        return bytes;
    }

    private static void CleanupCacheDirectory(string cacheDir)
    {
        try
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
            }
        }
        catch { }
    }

    private async Task<long> DownloadSegmentsToFileAsync(
        List<HlsSegmentItem> items,
        string cacheDir,
        string trackPrefix,
        string targetFilePath,
        Action<int, int, long>? onSegmentProgress,
        CancellationToken ct)
    {
        int completedCount = 0;
        long totalBytesDownloaded = 0;

        // Pre-scan cached segments so resumed downloads immediately reflect existing progress
        var pendingItems = new List<HlsSegmentItem>(items.Count);
        foreach (var item in items)
        {
            string segPath = GetSegmentCachePath(cacheDir, trackPrefix, item.Index);
            if (File.Exists(segPath))
            {
                long existingLen = new FileInfo(segPath).Length;
                if (existingLen > 0)
                {
                    completedCount++;
                    totalBytesDownloaded += existingLen;
                    continue;
                }
            }
            pendingItems.Add(item);
        }

        if (completedCount > 0)
        {
            onSegmentProgress?.Invoke(completedCount, items.Count, totalBytesDownloaded);
        }

        if (pendingItems.Count > 0)
        {
            await Parallel.ForEachAsync(
                pendingItems,
                new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrency, CancellationToken = ct },
                async (item, token) =>
                {
                    string segPath = GetSegmentCachePath(cacheDir, trackPrefix, item.Index);
                    byte[] data = await FetchSegmentBytesWithRetryAsync(item, token);

                    string tempSegPath = segPath + ".tmp";
                    await File.WriteAllBytesAsync(tempSegPath, data, token);
                    File.Move(tempSegPath, segPath, overwrite: true);

                    int done = Interlocked.Increment(ref completedCount);
                    long bytes = Interlocked.Add(ref totalBytesDownloaded, data.LongLength);
                    onSegmentProgress?.Invoke(done, items.Count, bytes);
                });
        }

        // Concatenate all cached segment files in order into targetFilePath
        await using var outStream = new FileStream(
            targetFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, useAsync: true);
        byte[] copyBuffer = new byte[65536];

        for (int i = 0; i < items.Count; i++)
        {
            string segPath = GetSegmentCachePath(cacheDir, trackPrefix, i);
            await using var segStream = new FileStream(
                segPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            int read;
            while ((read = await segStream.ReadAsync(copyBuffer, ct)) > 0)
            {
                await outStream.WriteAsync(copyBuffer.AsMemory(0, read), ct);
            }
        }
        await outStream.FlushAsync(ct);

        return totalBytesDownloaded;
    }

    private static string GetSegmentCachePath(string cacheDir, string trackPrefix, int index)
        => Path.Combine(cacheDir, $"{trackPrefix}_{index:D6}.seg");

    private async Task<byte[]> FetchSegmentBytesWithRetryAsync(HlsSegmentItem item, CancellationToken ct)
    {
        const int maxAttempts = 10;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await WaitForRateLimitCooldownAsync(ct);

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, item.Url);
                ApplyHeaders(req);
                if (item.ByteRangeOffset.HasValue && item.ByteRangeLength.HasValue)
                {
                    long from = item.ByteRangeOffset.Value;
                    long to = from + item.ByteRangeLength.Value - 1;
                    req.Headers.Range = new RangeHeaderValue(from, to);
                }

                using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                if (resp.StatusCode == HttpStatusCode.TooManyRequests ||
                    resp.StatusCode == HttpStatusCode.ServiceUnavailable ||
                    resp.StatusCode == HttpStatusCode.BadGateway ||
                    resp.StatusCode == HttpStatusCode.GatewayTimeout)
                {
                    TriggerRateLimitCooldown(resp, attempt);
                    if (attempt < maxAttempts)
                        continue;
                }

                resp.EnsureSuccessStatusCode();
                byte[] data = await resp.Content.ReadAsByteArrayAsync(ct);
                if (data.Length > 0)
                {
                    return data;
                }

                if (attempt < maxAttempts)
                {
                    await Task.Delay(1000 * attempt, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                // Transient network or timeout error; wait and retry
                await Task.Delay(1500 * attempt + Random.Shared.Next(100, 600), ct);
            }
        }

        throw new HttpRequestException($"HLS segment #{item.Index} {maxAttempts} denemeye rağmen indirilemedi (Sunucu yoğun / 429). 'Tekrar Dene'ye basarak kaldığınız yerden devam edebilirsiniz.");
    }

    private static async Task<bool> TryRemuxWithFfmpegAsync(
        string videoPath, string audioPath, string outputPath, CancellationToken ct)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-y -i \"{videoPath}\" -i \"{audioPath}\" -c copy -movflags +faststart -f mp4 \"{outputPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };

            proc.Start();
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            _ = await stderrTask;
            return proc.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryRemuxSingleWithFfmpegAsync(
        string inputPath, string outputPath, bool isAudioOnly, CancellationToken ct)
    {
        try
        {
            string args = isAudioOnly && outputPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                ? $"-y -i \"{inputPath}\" -vn -acodec libmp3lame -q:a 2 \"{outputPath}\""
                : $"-y -i \"{inputPath}\" -c copy -movflags +faststart -f mp4 \"{outputPath}\"";

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };

            proc.Start();
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            _ = await stderrTask;
            return proc.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> FetchTextAsync(string url, CancellationToken ct)
    {
        const int maxAttempts = 6;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await WaitForRateLimitCooldownAsync(ct);

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyHeaders(req);
                using var resp = await _httpClient.SendAsync(req, ct);

                if (resp.StatusCode == HttpStatusCode.TooManyRequests ||
                    (int)resp.StatusCode >= 500)
                {
                    TriggerRateLimitCooldown(resp, attempt);
                    if (attempt < maxAttempts)
                        continue;
                }

                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                await Task.Delay(1500 * attempt, ct);
            }
        }

        using var finalReq = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(finalReq);
        using var finalResp = await _httpClient.SendAsync(finalReq, ct);
        finalResp.EnsureSuccessStatusCode();
        return await finalResp.Content.ReadAsStringAsync(ct);
    }

    private static string? SelectBestVariantUrl(Uri masterUri, string text, int? maxHeight = null)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        long bestBandwidth = -1;
        string? bestUrl = null;
        long bestFilteredBandwidth = -1;
        string? bestFilteredUrl = null;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                bool isAudioOnly = line.Contains("CODECS=\"mp4a", StringComparison.OrdinalIgnoreCase) &&
                                   !line.Contains("avc", StringComparison.OrdinalIgnoreCase) &&
                                   !line.Contains("hvc", StringComparison.OrdinalIgnoreCase) &&
                                   !line.Contains("vp9", StringComparison.OrdinalIgnoreCase) &&
                                   !line.Contains("av01", StringComparison.OrdinalIgnoreCase) &&
                                   !line.Contains("RESOLUTION=", StringComparison.OrdinalIgnoreCase);
                if (isAudioOnly) continue;

                long bw = 0;
                int bwIdx = line.IndexOf("BANDWIDTH=", StringComparison.OrdinalIgnoreCase);
                if (bwIdx >= 0)
                {
                    string sub = line[(bwIdx + "BANDWIDTH=".Length)..];
                    int comma = sub.IndexOf(',');
                    if (comma >= 0) sub = sub[..comma];
                    long.TryParse(sub.Trim(), out bw);
                }

                int height = 0;
                int resIdx = line.IndexOf("RESOLUTION=", StringComparison.OrdinalIgnoreCase);
                if (resIdx >= 0)
                {
                    string sub = line[(resIdx + "RESOLUTION=".Length)..];
                    int comma = sub.IndexOf(',');
                    if (comma >= 0) sub = sub[..comma];
                    int xIdx = sub.IndexOf('x', StringComparison.OrdinalIgnoreCase);
                    if (xIdx >= 0)
                    {
                        int.TryParse(sub[(xIdx + 1)..].Trim(), out height);
                    }
                }

                for (int j = i + 1; j < lines.Length; j++)
                {
                    string nextLine = lines[j].Trim();
                    if (!nextLine.StartsWith('#'))
                    {
                        string resolvedUrl = new Uri(masterUri, nextLine).ToString();
                        if (bw >= bestBandwidth || bestUrl is null)
                        {
                            bestBandwidth = bw;
                            bestUrl = resolvedUrl;
                        }
                        if (maxHeight.HasValue && (height == 0 || height <= maxHeight.Value))
                        {
                            if (bw >= bestFilteredBandwidth || bestFilteredUrl is null)
                            {
                                bestFilteredBandwidth = bw;
                                bestFilteredUrl = resolvedUrl;
                            }
                        }
                        break;
                    }
                }
            }
        }
        return bestFilteredUrl ?? bestUrl;
    }

    private static string? SelectAudioTrackUrl(Uri masterUri, string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string? defaultAudioUrl = null;
        string? fallbackAudioUrl = null;

        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("TYPE=AUDIO", StringComparison.OrdinalIgnoreCase))
            {
                int uriIdx = line.IndexOf("URI=\"", StringComparison.OrdinalIgnoreCase);
                if (uriIdx >= 0)
                {
                    int start = uriIdx + "URI=\"".Length;
                    int end = line.IndexOf('"', start);
                    if (end > start)
                    {
                        string uriPart = line[start..end];
                        string resolved = new Uri(masterUri, uriPart).ToString();
                        if (line.Contains("DEFAULT=YES", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("AUTOSELECT=YES", StringComparison.OrdinalIgnoreCase))
                        {
                            defaultAudioUrl = resolved;
                            break;
                        }
                        fallbackAudioUrl ??= resolved;
                    }
                }
            }
        }
        return defaultAudioUrl ?? fallbackAudioUrl;
    }

    public static List<HlsSegmentItem> ParseMediaPlaylist(Uri playlistUri, string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var items = new List<HlsSegmentItem>();
        long? nextRangeOffset = null;
        long? nextRangeLen = null;
        long runningByteOffset = 0;
        string? activeMapUrl = null;

        foreach (var raw in lines)
        {
            string line = raw.Trim();

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                string? mapUriPart = ExtractQuotedAttribute(line, "URI");
                if (!string.IsNullOrWhiteSpace(mapUriPart))
                {
                    string resolvedMapUrl = new Uri(playlistUri, mapUriPart).ToString();
                    long? mapRangeOffset = null;
                    long? mapRangeLen = null;

                    string? mapByteRange = ExtractQuotedAttribute(line, "BYTERANGE");
                    if (!string.IsNullOrWhiteSpace(mapByteRange))
                    {
                        var parts = mapByteRange.Split('@', 2);
                        if (long.TryParse(parts[0], out long mLen))
                        {
                            mapRangeLen = mLen;
                            mapRangeOffset = parts.Length > 1 && long.TryParse(parts[1], out long mOff) ? mOff : 0;
                        }
                    }

                    string mapKey = $"{resolvedMapUrl}|{mapRangeOffset}|{mapRangeLen}";
                    if (!string.Equals(activeMapUrl, mapKey, StringComparison.Ordinal))
                    {
                        activeMapUrl = mapKey;
                        items.Add(new HlsSegmentItem(items.Count, resolvedMapUrl, mapRangeOffset, mapRangeLen));
                    }
                }
                continue;
            }

            if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
            {
                string spec = line["#EXT-X-BYTERANGE:".Length..].Trim();
                var parts = spec.Split('@', 2);
                if (long.TryParse(parts[0], out long len))
                {
                    nextRangeLen = len;
                    if (parts.Length > 1 && long.TryParse(parts[1], out long off))
                    {
                        nextRangeOffset = off;
                        runningByteOffset = off + len;
                    }
                    else
                    {
                        nextRangeOffset = runningByteOffset;
                        runningByteOffset += len;
                    }
                }
                continue;
            }

            if (line.StartsWith('#'))
                continue;

            string resolved = new Uri(playlistUri, line).ToString();
            items.Add(new HlsSegmentItem(items.Count, resolved, nextRangeOffset, nextRangeLen));
            nextRangeOffset = null;
            nextRangeLen = null;
        }
        return items;
    }

    private static string? ExtractQuotedAttribute(string tagLine, string attrName)
    {
        string token = attrName + "=\"";
        int idx = tagLine.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            int start = idx + token.Length;
            int end = tagLine.IndexOf('"', start);
            if (end > start)
            {
                return tagLine[start..end];
            }
        }
        return null;
    }

    public void Dispose() => _httpClient.Dispose();
}
