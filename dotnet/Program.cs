using System.Text.Json;
using MyIdm.Engine.Benchmark;
using MyIdm.Engine.Engine;
using MyIdm.Engine.Models;

namespace MyIdm.Engine;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "benchmark";

        if (mode == "serve")
        {
            int port = args.Length > 1 ? int.Parse(args[1]) : 18080;
            int sizeMb = args.Length > 2 ? int.Parse(args[2]) : 128;
            await using var server = new LoopbackRangeServer(port, sizeMb, simulateSlowInitialWorker: true);
            await server.StartAsync();
            Console.WriteLine($"READY url={server.Url} sha256={server.ExpectedSha256Hex} bytes={server.TotalBytes}");
            await Task.Delay(Timeout.Infinite);
            return 0;
        }

        if (mode == "download" && args.Length >= 3)
        {
            string url = args[1];
            string outputPath = args[2];
            int connections = args.Length > 3 ? int.Parse(args[3]) : 8;

            using var engine = new DynamicSegmentEngine(new EngineOptions { MaxConnections = connections });
            var metrics = await engine.DownloadAsync(url, outputPath, RenderProgress);
            Console.WriteLine();
            Console.WriteLine(JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        // Default: Run full verification & benchmark suite (including Pause/Resume + 64-byte Overlap test)
        await RunFullVerificationAndBenchmarkAsync();
        return 0;
    }

    private static async Task RunFullVerificationAndBenchmarkAsync()
    {
        const int port = 18089;
        const int sizeMb = 128;
        string workDir = Path.Combine(AppContext.BaseDirectory, "bench_out");
        Directory.CreateDirectory(workDir);

        await using var server = new LoopbackRangeServer(port, sizeMb, simulateSlowInitialWorker: true);
        await server.StartAsync();

        Console.WriteLine($"[1/2] Testing Pause + Resume + 64-Byte (0x40) Overlap Boundary Verification...");
        string pauseResumeFile = Path.Combine(workDir, "test_pause_resume.bin");
        if (File.Exists(pauseResumeFile)) File.Delete(pauseResumeFile);
        if (File.Exists(pauseResumeFile + ".idm.part.json")) File.Delete(pauseResumeFile + ".idm.part.json");

        using (var engine1 = new DynamicSegmentEngine(new EngineOptions { MaxConnections = 4 }))
        using (var cts = new CancellationTokenSource())
        {
            // Cancel once ~25% of the file is downloaded to simulate user pressing Pause
            void OnProgress(IReadOnlyList<SegmentState> segs, long total)
            {
                long done = segs.Sum(s => s.DownloadedBytes);
                if (done >= total / 4 && !cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }
            }

            try
            {
                await engine1.DownloadAsync(server.Url, pauseResumeFile, OnProgress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("  -> Paused at ~25% and saved .idm.part.json checkpoint.");
            }
        }

        using (var engine2 = new DynamicSegmentEngine(new EngineOptions { MaxConnections = 8 }))
        {
            var resumeMetrics = await engine2.DownloadAsync(server.Url, pauseResumeFile);
            bool hashMatch = string.Equals(resumeMetrics.Sha256Hex, server.ExpectedSha256Hex, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  -> Resumed & Completed! 64-Byte Overlap Verifications Passed: {resumeMetrics.OverlapVerificationsPassed}");
            Console.WriteLine($"  -> Dynamic Work-Stealing Splits: {resumeMetrics.DynamicSplitsPerformed}");
            Console.WriteLine($"  -> SHA-256 Verified: {hashMatch} ({resumeMetrics.Sha256Hex})");
            if (!hashMatch || resumeMetrics.OverlapVerificationsPassed == 0)
                throw new InvalidOperationException("Pause/Resume overlap verification failed!");
        }

        Console.WriteLine($"\n[2/2] Running Clean 128 MB Multi-Segment Throughput Benchmark (8 Connections)...");
        string benchFile = Path.Combine(workDir, "bench_dotnet.bin");
        if (File.Exists(benchFile)) File.Delete(benchFile);

        using var benchEngine = new DynamicSegmentEngine(new EngineOptions { MaxConnections = 8 });
        var metrics = await benchEngine.DownloadAsync(server.Url, benchFile, RenderProgress);
        Console.WriteLine();

        bool benchHashMatch = string.Equals(metrics.Sha256Hex, server.ExpectedSha256Hex, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  -> SHA-256 Integrity Match: {benchHashMatch}");
        Console.WriteLine(JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RenderProgress(IReadOnlyList<SegmentState> segments, long totalBytes)
    {
        long downloaded = 0;
        for (int i = 0; i < segments.Count; i++)
            downloaded += segments[i].DownloadedBytes;

        double pct = totalBytes > 0 ? (downloaded * 100.0 / totalBytes) : 0;
        Console.Write($"\r  Progress: {pct,6:F2}% ({downloaded / (1024 * 1024),3} / {totalBytes / (1024 * 1024),3} MB) | Active Segments: {segments.Count}   ");
    }
}
