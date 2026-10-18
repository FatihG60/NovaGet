using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MyIdm.Engine.Models;

namespace MyIdm.Engine.Engine;

public sealed class EngineOptions
{
    public int MaxConnections { get; init; } = 8;
    public long MinSplitSizeBytes { get; init; } = 512 * 1024; // 512 KB minimum split size
    public int OverlapBytes { get; init; } = 64;               // 0x40 (64 bytes) IDM FUN_0058b320 overlap
    public int ReadBufferBytes { get; init; } = 64 * 1024;     // 64 KB pooled read buffer
    public string UserAgent { get; init; } = "MyIDM/1.0 (.NET 10 DynamicSegmentEngine)";
    public DownloadRequestHeaders? CustomHeaders { get; init; }
}

/// <summary>
/// Clean-room implementation of IDM's multi-segment download engine (modeled on FUN_0058b320):
/// - Initial probe with Range: bytes=0-
/// - Lock-free concurrent disk writes via RandomAccess.WriteAsync(SafeFileHandle)
/// - Dynamic in-flight work-stealing (splits the largest remaining active segment in half)
/// - 64-byte (0x40) overlap verification on resumed segments to prevent splice corruption
/// - Crash-safe .idm.part.json state persistence for Pause/Resume
/// </summary>
public sealed class DynamicSegmentEngine : IDisposable
{
    private readonly EngineOptions _options;
    private readonly HttpClient _httpClient;
    private readonly Lock _splitLock = new();

    private int _dynamicSplitsPerformed;
    private int _overlapVerificationsPassed;

    public int DynamicSplitsPerformed => Volatile.Read(ref _dynamicSplitsPerformed);
    public int OverlapVerificationsPassed => Volatile.Read(ref _overlapVerificationsPassed);

    public DynamicSegmentEngine(EngineOptions? options = null)
    {
        _options = options ?? new EngineOptions();
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Math.Max(32, _options.MaxConnections * 2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.None, // Raw byte offsets require identity encoding
            ConnectTimeout = TimeSpan.FromSeconds(15),
            UseCookies = false // We pass explicit Cookie headers from the browser extension when available
        };
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        string ua = !string.IsNullOrWhiteSpace(_options.CustomHeaders?.UserAgent)
            ? _options.CustomHeaders!.UserAgent!
            : _options.UserAgent;
        _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd(ua);
        _httpClient.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
    }

    public string GetCheckpointPath(string outputPath) => outputPath + ".idm.part.json";

    public async Task<DownloadMetrics> DownloadAsync(
        string url,
        string outputPath,
        Action<IReadOnlyList<SegmentState>, long>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var proc = Process.GetCurrentProcess();
        var cpuStart = proc.TotalProcessorTime;

        string checkpointPath = GetCheckpointPath(outputPath);
        DownloadCheckpoint checkpoint;

        if (File.Exists(checkpointPath) && File.Exists(outputPath))
        {
            var json = await File.ReadAllTextAsync(checkpointPath, cancellationToken);
            checkpoint = JsonSerializer.Deserialize<DownloadCheckpoint>(json)
                ?? throw new InvalidOperationException("Invalid checkpoint file.");

            if (_options.CustomHeaders is not null)
            {
                checkpoint.Headers = _options.CustomHeaders;
            }

            foreach (var seg in checkpoint.Segments)
            {
                if (!seg.IsCompleted && seg.CurrentOffset > seg.StartOffset)
                {
                    seg.NeedsOverlapVerification = true;
                }
                seg.IsActive = false;
            }
        }
        else
        {
            checkpoint = await ProbeAndInitializeAsync(url, outputPath, cancellationToken);
            await SaveCheckpointAsync(checkpoint, checkpointPath);
        }

        using SafeFileHandle fileHandle = File.OpenHandle(
            outputPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        if (RandomAccess.GetLength(fileHandle) != checkpoint.TotalBytes && checkpoint.TotalBytes > 0)
        {
            RandomAccess.SetLength(fileHandle, checkpoint.TotalBytes);
        }

        // Perform initial splits up to MaxConnections if starting fresh
        if (checkpoint.SupportsRanges)
        {
            lock (_splitLock)
            {
                while (checkpoint.Segments.Count < _options.MaxConnections)
                {
                    if (!TrySplitLargestSegmentLocked(checkpoint.Segments))
                        break;
                }
            }
            await SaveCheckpointAsync(checkpoint, checkpointPath);
        }

        onProgress?.Invoke(SnapshotSegments(checkpoint), checkpoint.TotalBytes);

        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monitorTask = RunCheckpointAndProgressLoopAsync(
            checkpoint, checkpointPath, onProgress, progressCts.Token);

        try
        {
            var workers = new List<Task>(_options.MaxConnections);
            int workerCount = checkpoint.SupportsRanges ? _options.MaxConnections : 1;

            for (int w = 0; w < workerCount; w++)
            {
                workers.Add(RunWorkerLoopAsync(checkpoint, fileHandle, cancellationToken));
            }

            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            await SaveCheckpointAsync(checkpoint, checkpointPath);
            throw;
        }
        finally
        {
            await progressCts.CancelAsync();
            try { await monitorTask; } catch (OperationCanceledException) { }
        }

        if (checkpoint.TotalBytes <= 0 && checkpoint.Segments.Count > 0)
        {
            checkpoint.TotalBytes = checkpoint.Segments[0].CurrentOffset;
            RandomAccess.SetLength(fileHandle, checkpoint.TotalBytes);
        }

        RandomAccess.FlushToDisk(fileHandle);
        onProgress?.Invoke(SnapshotSegments(checkpoint), checkpoint.TotalBytes);

        if (File.Exists(checkpointPath))
        {
            File.Delete(checkpointPath);
        }

        sw.Stop();
        proc.Refresh();
        var cpuEnd = proc.TotalProcessorTime;

        string sha256Hex = await ComputeFileSha256Async(outputPath, CancellationToken.None);

        return new DownloadMetrics
        {
            EngineName = "C# (.NET 10 SocketsHttpHandler + RandomAccess)",
            TotalBytes = checkpoint.TotalBytes,
            ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds,
            MaxConnections = _options.MaxConnections,
            DynamicSplitsPerformed = _dynamicSplitsPerformed,
            OverlapVerificationsPassed = _overlapVerificationsPassed,
            PeakWorkingSetBytes = proc.PeakWorkingSet64,
            CpuTimeMilliseconds = (cpuEnd - cpuStart).TotalMilliseconds,
            Sha256Hex = sha256Hex
        };
    }

    private IReadOnlyList<SegmentState> SnapshotSegments(DownloadCheckpoint checkpoint)
    {
        lock (_splitLock)
        {
            return checkpoint.Segments.ToList();
        }
    }

    private static bool IsLikelyMediaUrl(Uri? uri)
    {
        if (uri is null) return false;
        string path = uri.AbsolutePath.ToLowerInvariant();
        return path.EndsWith(".mp4") || path.EndsWith(".m4v") || path.EndsWith(".webm") ||
               path.EndsWith(".mkv") || path.EndsWith(".mov") || path.EndsWith(".flv") ||
               path.EndsWith(".ts") || path.EndsWith(".m4s") || path.EndsWith(".mp3") ||
               path.EndsWith(".m4a") || path.EndsWith(".aac") || path.EndsWith(".ogg") ||
               path.Contains("/video/") || path.Contains("/stream");
    }

    private void ApplyCustomHeaders(HttpRequestMessage req, DownloadRequestHeaders? headers)
    {
        req.Headers.TryAddWithoutValidation("Accept", "*/*");

        Uri? refUri = null;
        if (headers is not null &&
            !string.IsNullOrWhiteSpace(headers.Referrer) &&
            Uri.TryCreate(headers.Referrer, UriKind.Absolute, out var parsedRef))
        {
            refUri = parsedRef;
            req.Headers.Referrer = refUri;
        }

        if (headers is not null && !string.IsNullOrWhiteSpace(headers.Cookies))
        {
            req.Headers.TryAddWithoutValidation("Cookie", headers.Cookies);
        }

        // Send W3C Fetch Metadata headers expected by modern CDNs and WAF rules
        bool isMedia = IsLikelyMediaUrl(req.RequestUri);
        string fetchSite = "none";
        if (refUri is not null && req.RequestUri is not null)
        {
            fetchSite = string.Equals(refUri.Host, req.RequestUri.Host, StringComparison.OrdinalIgnoreCase)
                ? "same-origin"
                : "cross-site";
        }

        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", isMedia ? "video" : "document");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", isMedia ? "no-cors" : "navigate");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);

        if (headers?.ExtraHeaders is not null)
        {
            foreach (var kv in headers.ExtraHeaders)
            {
                req.Headers.Remove(kv.Key);
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }
    }

    /// <summary>
    /// Sends the initial probe request with "Range: bytes=0-" (matching IDM FUN_0058b320 line 568)
    /// to determine total size, range capability, ETag, and Last-Modified headers.
    /// If Content-Type or body indicates an HLS playlist, throws HlsDetectedException to trigger the HLS downloader.
    /// If the server does not send Content-Length, enables progressive single-stream download.
    /// </summary>
    private async Task<DownloadCheckpoint> ProbeAndInitializeAsync(
        string url, string outputPath, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(0, null);
        ApplyCustomHeaders(req, _options.CustomHeaders);

        HttpResponseMessage resp = await _httpClient.SendAsync(
            req, HttpCompletionOption.ResponseHeadersRead, ct);

        // Fallback if CDN requires CORS/XHR fetch context instead of <video> no-cors context
        if (resp.StatusCode == HttpStatusCode.Forbidden && _options.CustomHeaders?.Referrer is { } refStr &&
            Uri.TryCreate(refStr, UriKind.Absolute, out var refUri))
        {
            resp.Dispose();
            _options.CustomHeaders.ExtraHeaders ??= new Dictionary<string, string>();
            _options.CustomHeaders.ExtraHeaders["Sec-Fetch-Dest"] = "empty";
            _options.CustomHeaders.ExtraHeaders["Sec-Fetch-Mode"] = "cors";
            _options.CustomHeaders.ExtraHeaders["Origin"] = $"{refUri.Scheme}://{refUri.Authority}";

            using var retryReq = new HttpRequestMessage(HttpMethod.Get, url);
            retryReq.Headers.Range = new RangeHeaderValue(0, null);
            ApplyCustomHeaders(retryReq, _options.CustomHeaders);
            resp = await _httpClient.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        using (resp)
        {
            resp.EnsureSuccessStatusCode();

        // 1. Detect if Content-Type is HLS playlist
        string? mediaType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (mediaType is not null && (mediaType.Contains("mpegurl") || mediaType.Contains("vnd.apple.mpegurl")))
        {
            throw new HlsDetectedException(url, "HLS playlist detected via Content-Type header.");
        }

        bool supportsRanges = resp.StatusCode == HttpStatusCode.PartialContent ||
                              resp.Headers.AcceptRanges.Contains("bytes");

        long? totalBytesNullable = resp.Content.Headers.ContentRange?.Length
                                  ?? resp.Content.Headers.ContentLength;

        // 2. If server did not provide Content-Length, inspect payload or enable progressive streaming
        if (!totalBytesNullable.HasValue)
        {
            await using var peekStream = await resp.Content.ReadAsStreamAsync(ct);
            byte[] peekBuffer = new byte[512];
            int peekRead = await peekStream.ReadAsync(peekBuffer.AsMemory(0, 512), ct);
            if (peekRead > 0)
            {
                string peekStr = System.Text.Encoding.UTF8.GetString(peekBuffer, 0, peekRead);
                if (peekStr.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                {
                    throw new HlsDetectedException(url, "HLS playlist detected via #EXTM3U payload.");
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

            return new DownloadCheckpoint
            {
                Url = url,
                OutputPath = outputPath,
                TotalBytes = -1,
                SupportsRanges = false,
                ETag = resp.Headers.ETag?.Tag,
                LastModified = resp.Content.Headers.LastModified?.ToString("R"),
                Headers = _options.CustomHeaders,
                Segments =
                [
                    new SegmentState
                    {
                        Id = 1,
                        StartOffset = 0,
                        CurrentOffset = 0,
                        EndOffset = long.MaxValue - 1,
                        IsCompleted = false
                    }
                ]
            };
        }

        long totalBytes = totalBytesNullable.Value;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        return new DownloadCheckpoint
        {
            Url = url,
            OutputPath = outputPath,
            TotalBytes = totalBytes,
            SupportsRanges = supportsRanges,
            ETag = resp.Headers.ETag?.Tag,
            LastModified = resp.Content.Headers.LastModified?.ToString("R"),
            Headers = _options.CustomHeaders,
            Segments =
            [
                new SegmentState
                {
                    Id = 1,
                    StartOffset = 0,
                    CurrentOffset = 0,
                    EndOffset = totalBytes - 1,
                    IsCompleted = false
                }
            ]
        };
        }
    }

    private async Task RunWorkerLoopAsync(
        DownloadCheckpoint checkpoint,
        SafeFileHandle fileHandle,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SegmentState? segment = AcquireNextOrSplitSegment(checkpoint);
            if (segment is null)
                break;

            try
            {
                const int maxAttempts = 5;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        await DownloadSegmentAsync(checkpoint, segment, fileHandle, ct);
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception) when (attempt < maxAttempts)
                    {
                        segment.NeedsOverlapVerification = true;
                        await Task.Delay(1500 * attempt + Random.Shared.Next(100, 500), ct);
                    }
                }
            }
            finally
            {
                segment.IsActive = false;
            }
        }
    }

    private SegmentState? AcquireNextOrSplitSegment(DownloadCheckpoint checkpoint)
    {
        lock (_splitLock)
        {
            var pending = checkpoint.Segments.FirstOrDefault(s => !s.IsCompleted && !s.IsActive);
            if (pending is not null)
            {
                pending.IsActive = true;
                return pending;
            }

            if (!checkpoint.SupportsRanges)
                return null;

            if (TrySplitLargestSegmentLocked(checkpoint.Segments, out SegmentState? newSegment) &&
                newSegment is not null)
            {
                newSegment.IsActive = true;
                return newSegment;
            }

            return null;
        }
    }

    private bool TrySplitLargestSegmentLocked(List<SegmentState> segments)
        => TrySplitLargestSegmentLocked(segments, out _);

    private bool TrySplitLargestSegmentLocked(
        List<SegmentState> segments, out SegmentState? createdSegment)
    {
        createdSegment = null;
        SegmentState? largest = null;
        long maxRemaining = 0;

        foreach (var seg in segments)
        {
            if (seg.IsCompleted) continue;
            long rem = seg.RemainingBytes;
            if (rem > maxRemaining)
            {
                maxRemaining = rem;
                largest = seg;
            }
        }

        if (largest is null || maxRemaining < _options.MinSplitSizeBytes * 2)
            return false;

        long current = largest.CurrentOffset;
        long oldEnd = largest.EndOffset;
        long remaining = oldEnd - current + 1;
        if (remaining < _options.MinSplitSizeBytes * 2)
            return false;

        long splitOffset = current + (remaining / 2);

        // Atomically shrink the first segment's EndOffset in-flight
        largest.EndOffset = splitOffset - 1;

        var newSeg = new SegmentState
        {
            Id = segments.Count + 1,
            StartOffset = splitOffset,
            CurrentOffset = splitOffset,
            EndOffset = oldEnd,
            IsCompleted = false
        };

        segments.Add(newSeg);
        Interlocked.Increment(ref _dynamicSplitsPerformed);
        createdSegment = newSeg;
        return true;
    }

    private async Task DownloadSegmentAsync(
        DownloadCheckpoint checkpoint,
        SegmentState segment,
        SafeFileHandle fileHandle,
        CancellationToken ct)
    {
        long startCur = segment.CurrentOffset;
        long end = segment.EndOffset;
        if (startCur > end)
        {
            segment.IsCompleted = true;
            return;
        }

        int overlap = 0;
        if (segment.NeedsOverlapVerification &&
            checkpoint.SupportsRanges &&
            (startCur - segment.StartOffset) >= _options.OverlapBytes)
        {
            overlap = _options.OverlapBytes;
        }

        long requestFrom = startCur - overlap;
        using var req = new HttpRequestMessage(HttpMethod.Get, checkpoint.Url);
        ApplyCustomHeaders(req, checkpoint.Headers ?? _options.CustomHeaders);

        if (checkpoint.SupportsRanges)
        {
            long requestTo = (end + _options.OverlapBytes < checkpoint.TotalBytes - 1)
                ? end + _options.OverlapBytes
                : end;
            req.Headers.Range = new RangeHeaderValue(requestFrom, requestTo);

            if (!string.IsNullOrEmpty(checkpoint.ETag))
            {
                req.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(checkpoint.ETag));
            }
        }

        using var resp = await _httpClient.SendAsync(
            req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.ReadBufferBytes);

        try
        {
            if (overlap > 0)
            {
                int overlapRead = 0;
                while (overlapRead < overlap)
                {
                    int n = await netStream.ReadAsync(
                        buffer.AsMemory(overlapRead, overlap - overlapRead), ct);
                    if (n == 0)
                        throw new IOException($"Unexpected EOF during {overlap}-byte overlap verification.");
                    overlapRead += n;
                }

                byte[] diskBytes = new byte[overlap];
                int diskRead = await RandomAccess.ReadAsync(fileHandle, diskBytes, requestFrom, ct);
                if (diskRead != overlap ||
                    !buffer.AsSpan(0, overlap).SequenceEqual(diskBytes))
                {
                    throw new IOException(
                        $"Segment #{segment.Id} failed 64-byte overlap boundary check at offset {requestFrom}.");
                }

                segment.NeedsOverlapVerification = false;
                segment.OverlapVerified = true;
                Interlocked.Increment(ref _overlapVerificationsPassed);
            }

            while (!ct.IsCancellationRequested)
            {
                long current = segment.CurrentOffset;
                long dynamicEnd = segment.EndOffset;
                long remainingAllowed = dynamicEnd - current + 1;
                if (remainingAllowed <= 0)
                {
                    segment.IsCompleted = true;
                    break;
                }

                int toRead = (int)Math.Min(_options.ReadBufferBytes, remainingAllowed);
                int bytesRead = await netStream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                if (bytesRead == 0)
                {
                    if (segment.CurrentOffset > segment.EndOffset || !checkpoint.SupportsRanges)
                    {
                        segment.EndOffset = Math.Max(segment.StartOffset, segment.CurrentOffset - 1);
                        segment.IsCompleted = true;
                    }
                    break;
                }

                dynamicEnd = segment.EndOffset;
                int bytesToCommit = (int)Math.Min(bytesRead, Math.Max(0, dynamicEnd - current + 1));
                if (bytesToCommit > 0)
                {
                    await RandomAccess.WriteAsync(
                        fileHandle,
                        buffer.AsMemory(0, bytesToCommit),
                        current,
                        ct);
                    long updatedOffset = segment.AdvanceCurrentOffset(bytesToCommit);
                    if (updatedOffset > segment.EndOffset)
                    {
                        segment.IsCompleted = true;
                        break;
                    }
                }
                else
                {
                    segment.IsCompleted = true;
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task RunCheckpointAndProgressLoopAsync(
        DownloadCheckpoint checkpoint,
        string checkpointPath,
        Action<IReadOnlyList<SegmentState>, long>? onProgress,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(15));
        int tick = 0;
        while (await timer.WaitForNextTickAsync(ct))
        {
            onProgress?.Invoke(SnapshotSegments(checkpoint), checkpoint.TotalBytes);
            if (++tick % 20 == 0)
            {
                await SaveCheckpointAsync(checkpoint, checkpointPath);
            }
        }
    }

    private readonly SemaphoreSlim _checkpointGate = new(1, 1);

    private async Task SaveCheckpointAsync(DownloadCheckpoint checkpoint, string checkpointPath)
    {
        if (!await _checkpointGate.WaitAsync(100)) return;
        try
        {
            string tempPath = checkpointPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string json;
            lock (_splitLock)
            {
                json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = true });
            }
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, checkpointPath, overwrite: true);
        }
        catch
        {
            // Transient file lock during checkpointing should never crash the engine
        }
        finally
        {
            _checkpointGate.Release();
        }
    }

    public static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct)
    {
        await using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexStringLower(hash);
    }

    public void Dispose() => _httpClient.Dispose();
}
