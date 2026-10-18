using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MyIdm.Engine.Benchmark;

/// <summary>
/// High-speed in-memory deterministic HTTP Range server for benchmarking C# (.NET 10) and Rust (Tokio)
/// download engines under identical conditions (including uneven connection speeds to trigger dynamic work-stealing).
/// </summary>
public sealed class LoopbackRangeServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly byte[] _payload;
    private int _requestCounter;

    public string Url { get; }
    public string ExpectedSha256Hex { get; }
    public long TotalBytes => _payload.LongLength;

    public LoopbackRangeServer(int port, int sizeMegabytes = 64, bool simulateSlowInitialWorker = true)
    {
        _payload = new byte[sizeMegabytes * 1024 * 1024];
        // Fill with deterministic pseudo-random data so every byte offset has a unique verifiable pattern
        var rng = new Random(0x49444D31); // Seed: "IDM1"
        rng.NextBytes(_payload);

        ExpectedSha256Hex = Convert.ToHexStringLower(SHA256.HashData(_payload));
        Url = $"http://127.0.0.1:{port}/payload.bin";

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
        });

        _app = builder.Build();
        _app.MapGet("/payload.bin", async (HttpContext ctx) =>
        {
            int reqNum = Interlocked.Increment(ref _requestCounter);
            long totalLen = _payload.LongLength;

            ctx.Response.Headers.AcceptRanges = "bytes";
            ctx.Response.Headers.ETag = "\"idm-bench-v1\"";

            string? rangeHeader = ctx.Request.Headers.Range.FirstOrDefault();
            long start = 0;
            long end = totalLen - 1;

            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                var parts = rangeHeader["bytes=".Length..].Split('-', 2);
                if (long.TryParse(parts[0], out long parsedStart))
                    start = parsedStart;
                if (parts.Length > 1 && long.TryParse(parts[1], out long parsedEnd))
                    end = Math.Min(parsedEnd, totalLen - 1);

                ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
                ctx.Response.Headers.ContentRange = $"bytes {start}-{end}/{totalLen}";
            }
            else
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
            }

            long length = end - start + 1;
            ctx.Response.ContentLength = length;
            ctx.Response.ContentType = "application/octet-stream";

            // Optionally throttle request #2 slightly so other workers finish early and steal its remaining half!
            bool isSlowConnection = simulateSlowInitialWorker && reqNum == 2;
            const int chunkSize = 256 * 1024;
            long offset = start;

            while (offset <= end && !ctx.RequestAborted.IsCancellationRequested)
            {
                int toSend = (int)Math.Min(chunkSize, end - offset + 1);
                await ctx.Response.Body.WriteAsync(
                    _payload.AsMemory((int)offset, toSend), ctx.RequestAborted);
                offset += toSend;

                if (isSlowConnection)
                {
                    await Task.Delay(2, ctx.RequestAborted);
                }
            }
        });
    }

    public Task StartAsync() => _app.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
