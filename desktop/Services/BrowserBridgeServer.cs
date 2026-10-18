using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MyIdm.Engine.Models;

namespace MyIdm.Desktop.Services;

public sealed class BrowserCaptureRequest
{
    public required string Url { get; set; }
    public string? Filename { get; set; }
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public int? Connections { get; set; }
    public bool IsHls { get; set; }
    public string? Quality { get; set; }
}

/// <summary>
/// Embedded localhost bridge server (http://127.0.0.1:10010) replacing IDM's ws://127.0.0.1:1001 & IDMMsgHost.exe:
/// - Receives download & HLS video stream capture requests from the Chrome/Edge MV3 extension
/// - Serves built-in demo endpoints (/demo/128mb.bin and /demo/stream.m3u8) for instant live testing in the UI
/// </summary>
public sealed class BrowserBridgeServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly byte[] _demoPayload;
    private int _demoRequestCounter;

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public BrowserBridgeServer(
        Action<BrowserCaptureRequest> onCaptureReceived,
        Func<int> getActiveDownloadsCount,
        int port = 10010)
    {
        Port = port;
        _demoPayload = new byte[128 * 1024 * 1024]; // 128 MB deterministic payload
        new Random(0x49444D31).NextBytes(_demoPayload);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
        });

        _app = builder.Build();

        // Allow Chrome/Edge/Firefox extensions to call localhost bridge directly
        _app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.Append("Access-Control-Allow-Origin", "*");
            ctx.Response.Headers.Append("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.Headers.Append("Access-Control-Allow-Headers", "Content-Type");
            if (ctx.Request.Method == "OPTIONS")
            {
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            await next();
        });

        _app.MapGet("/api/status", () => Results.Json(new
        {
            ok = true,
            app = "MyIDM Desktop (.NET 10)",
            version = "1.0.0",
            activeDownloads = getActiveDownloadsCount()
        }));

        _app.MapPost("/api/download", async (HttpContext ctx) =>
        {
            try
            {
                var req = await ctx.Request.ReadFromJsonAsync<BrowserCaptureRequest>();
                if (req is null || string.IsNullOrWhiteSpace(req.Url))
                {
                    return Results.BadRequest(new { ok = false, error = "Missing URL" });
                }

                onCaptureReceived(req);
                return Results.Json(new { ok = true, queued = req.Url });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
            }
        });

        // Built-in 128 MB HTTP Range Demo endpoint (throttled slightly so the user can visually watch dynamic splits & Pause/Resume!)
        _app.MapGet("/demo/128mb.bin", async (HttpContext ctx) =>
        {
            int reqNum = Interlocked.Increment(ref _demoRequestCounter);
            long totalLen = _demoPayload.LongLength;

            ctx.Response.Headers.AcceptRanges = "bytes";
            ctx.Response.Headers.ETag = "\"myidm-demo-128mb\"";

            string? rangeHeader = ctx.Request.Headers.Range.FirstOrDefault();
            long start = 0;
            long end = totalLen - 1;

            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                var parts = rangeHeader["bytes=".Length..].Split('-', 2);
                if (long.TryParse(parts[0], out long s)) start = s;
                if (parts.Length > 1 && long.TryParse(parts[1], out long e)) end = Math.Min(e, totalLen - 1);

                ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
                ctx.Response.Headers.ContentRange = $"bytes {start}-{end}/{totalLen}";
            }
            else
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
            }

            ctx.Response.ContentLength = end - start + 1;
            ctx.Response.ContentType = "application/octet-stream";

            // Pace chunks at ~35 MB/s total across 8 workers (~3.5 seconds for 128 MB) so the user can see live bars & test Pause/Resume!
            bool extraSlow = (reqNum % 4 == 2);
            const int chunkSize = 128 * 1024;
            long offset = start;

            while (offset <= end && !ctx.RequestAborted.IsCancellationRequested)
            {
                int toSend = (int)Math.Min(chunkSize, end - offset + 1);
                await ctx.Response.Body.WriteAsync(_demoPayload.AsMemory((int)offset, toSend), ctx.RequestAborted);
                offset += toSend;
                await Task.Delay(extraSlow ? 45 : 15, ctx.RequestAborted);
            }
        });

        // Built-in HLS (.m3u8) Master + Media Playlist Demo endpoint
        _app.MapGet("/demo/stream.m3u8", () =>
        {
            var sb = new StringBuilder();
            sb.AppendLine("#EXTM3U");
            sb.AppendLine("#EXT-X-VERSION:3");
            sb.AppendLine("#EXT-X-TARGETDURATION:2");
            for (int i = 0; i < 24; i++)
            {
                sb.AppendLine("#EXTINF:2.000,");
                sb.AppendLine($"http://127.0.0.1:{Port}/demo/seg_{i}.ts");
            }
            sb.AppendLine("#EXT-X-ENDLIST");
            return Results.Text(sb.ToString(), "application/vnd.apple.mpegurl");
        });

        _app.MapGet("/demo/seg_{index:int}.ts", async (int index, HttpContext ctx) =>
        {
            const int segBytes = 1024 * 1024; // 1 MB per TS segment (24 MB total)
            int start = (index * segBytes) % (_demoPayload.Length - segBytes);
            ctx.Response.ContentType = "video/mp2t";
            ctx.Response.ContentLength = segBytes;
            await Task.Delay(120, ctx.RequestAborted);
            await ctx.Response.Body.WriteAsync(_demoPayload.AsMemory(start, segBytes), ctx.RequestAborted);
        });
    }

    public Task StartAsync() => _app.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
