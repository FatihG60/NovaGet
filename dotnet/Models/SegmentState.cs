using System.Text.Json.Serialization;

namespace MyIdm.Engine.Models;

/// <summary>
/// Thread-safe segment state supporting lock-free atomic updates during in-flight work-stealing.
/// Modeled after IDM's per-connection worker structure in FUN_0058b320 (+0x60 Start, +0x68/+0x78 End).
/// </summary>
public sealed class SegmentState
{
    private long _currentOffset;
    private long _endOffset;
    private int _isCompleted;
    private int _isActive;
    private int _overlapVerified;

    public int Id { get; set; }
    public long StartOffset { get; set; }

    public long CurrentOffset
    {
        get => Interlocked.Read(ref _currentOffset);
        set => Interlocked.Exchange(ref _currentOffset, value);
    }

    /// <summary>
    /// Inclusive end byte offset for this segment.
    /// Can be shrunk in-flight by the work-stealing coordinator when splitting a slow segment.
    /// </summary>
    public long EndOffset
    {
        get => Interlocked.Read(ref _endOffset);
        set => Interlocked.Exchange(ref _endOffset, value);
    }

    public bool IsCompleted
    {
        get => Volatile.Read(ref _isCompleted) == 1;
        set => Volatile.Write(ref _isCompleted, value ? 1 : 0);
    }

    [JsonIgnore]
    public bool IsActive
    {
        get => Volatile.Read(ref _isActive) == 1;
        set => Volatile.Write(ref _isActive, value ? 1 : 0);
    }

    [JsonIgnore]
    public bool OverlapVerified
    {
        get => Volatile.Read(ref _overlapVerified) == 1;
        set => Volatile.Write(ref _overlapVerified, value ? 1 : 0);
    }

    /// <summary>
    /// Indicates whether this segment is resuming from a previously persisted checkpoint
    /// and therefore requires IDM-style 64-byte overlap boundary verification.
    /// </summary>
    [JsonIgnore]
    public bool NeedsOverlapVerification { get; set; }

    [JsonIgnore]
    public long DownloadedBytes => Math.Max(0, CurrentOffset - StartOffset);

    [JsonIgnore]
    public long TotalSegmentBytes => Math.Max(0, EndOffset - StartOffset + 1);

    [JsonIgnore]
    public long RemainingBytes => Math.Max(0, EndOffset - CurrentOffset + 1);

    [JsonIgnore]
    public double ProgressPercent => TotalSegmentBytes > 0
        ? Math.Clamp(DownloadedBytes * 100.0 / TotalSegmentBytes, 0.0, 100.0)
        : 0.0;

    public long AdvanceCurrentOffset(long delta) => Interlocked.Add(ref _currentOffset, delta);
}

public sealed class DownloadRequestHeaders
{
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public Dictionary<string, string>? ExtraHeaders { get; set; }
}

public sealed class DownloadCheckpoint
{
    public required string Url { get; set; }
    public required string OutputPath { get; set; }
    public long TotalBytes { get; set; }
    public bool SupportsRanges { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public DownloadRequestHeaders? Headers { get; set; }
    public List<SegmentState> Segments { get; set; } = [];
}

public sealed class DownloadMetrics
{
    public required string EngineName { get; init; }
    public long TotalBytes { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public double ThroughputMBps => ElapsedMilliseconds > 0
        ? (TotalBytes / (1024.0 * 1024.0)) / (ElapsedMilliseconds / 1000.0)
        : 0;
    public int MaxConnections { get; init; }
    public int DynamicSplitsPerformed { get; init; }
    public int OverlapVerificationsPassed { get; init; }
    public long PeakWorkingSetBytes { get; init; }
    public double CpuTimeMilliseconds { get; init; }
    public required string Sha256Hex { get; init; }
}

public sealed class HlsDetectedException : Exception
{
    public string Url { get; }
    public HlsDetectedException(string url, string message = "HLS playlist stream detected.") : base(message)
    {
        Url = url;
    }
}
