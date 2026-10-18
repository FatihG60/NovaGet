using System.IO;
using System.Text.Json;
using MyIdm.Desktop.ViewModels;
using MyIdm.Engine.Models;

namespace MyIdm.Desktop.Services;

public sealed class PersistedDownloadRecord
{
    public Guid JobId { get; set; }
    public string FileName { get; set; } = "";
    public string Url { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public int MaxConnections { get; set; } = 8;
    public bool IsHlsStream { get; set; }
    public string? Quality { get; set; }
    public bool IsCompleted { get; set; }
    public double ProgressPercent { get; set; }
    public string SizeText { get; set; } = "";
    public string StatusText { get; set; } = "";
    public string SegmentsSummary { get; set; } = "";
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class DownloadHistoryStore
{
    private static readonly string HistoryFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyIDM",
        "download_history.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public static void Save(IEnumerable<DownloadItemViewModel> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFilePath)!);

            var records = items.Select(vm => new PersistedDownloadRecord
            {
                JobId = vm.JobId,
                FileName = vm.FileName,
                Url = vm.Url,
                OutputPath = vm.OutputPath,
                MaxConnections = vm.MaxConnections,
                IsHlsStream = vm.IsHlsStream,
                Quality = vm.Quality,
                IsCompleted = vm.IsCompleted,
                ProgressPercent = vm.IsCompleted ? 100.0 : vm.ProgressPercent,
                SizeText = vm.SizeText,
                StatusText = vm.IsCompleted ? vm.StatusText : "Duraklatıldı (.part kaydedildi)",
                SegmentsSummary = vm.SegmentsSummary,
                Referrer = vm.Headers?.Referrer,
                Cookies = vm.Headers?.Cookies,
                UserAgent = vm.Headers?.UserAgent,
                SavedAtUtc = DateTime.UtcNow
            }).ToList();

            string json = JsonSerializer.Serialize(records, JsonOpts);
            File.WriteAllText(HistoryFilePath, json);
        }
        catch
        {
            // Ignore I/O serialization errors on background save
        }
    }

    public static List<DownloadItemViewModel> Load()
    {
        var result = new List<DownloadItemViewModel>();
        try
        {
            if (!File.Exists(HistoryFilePath)) return result;

            string json = File.ReadAllText(HistoryFilePath);
            var records = JsonSerializer.Deserialize<List<PersistedDownloadRecord>>(json);
            if (records is null) return result;

            foreach (var r in records)
            {
                if (string.IsNullOrWhiteSpace(r.Url) || string.IsNullOrWhiteSpace(r.OutputPath))
                    continue;

                var vm = new DownloadItemViewModel
                {
                    FileName = r.FileName,
                    Url = r.Url,
                    OutputPath = r.OutputPath,
                    MaxConnections = r.MaxConnections > 0 ? r.MaxConnections : 8,
                    IsHlsStream = r.IsHlsStream,
                    Quality = r.Quality ?? "best",
                    Headers = (!string.IsNullOrEmpty(r.Referrer) || !string.IsNullOrEmpty(r.Cookies) || !string.IsNullOrEmpty(r.UserAgent))
                        ? new DownloadRequestHeaders
                        {
                            Referrer = r.Referrer,
                            Cookies = r.Cookies,
                            UserAgent = r.UserAgent
                        }
                        : null
                };

                vm.RestoreSavedState(
                    isCompleted: r.IsCompleted,
                    progressPercent: r.ProgressPercent,
                    sizeText: r.SizeText,
                    statusText: r.StatusText,
                    segmentsSummary: r.SegmentsSummary);

                result.Add(vm);
            }
        }
        catch
        {
            // Ignore corrupted history file
        }
        return result;
    }
}
