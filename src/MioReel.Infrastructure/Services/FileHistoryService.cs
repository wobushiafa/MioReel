using System.Diagnostics;
using System.Text.Json;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;
using MioReel.Core.Serialization;

namespace MioReel.Infrastructure.Services;

public sealed class FileHistoryService : IHistoryService
{
    private const int MaxRecords = 500;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<HistoryRecord> _records;

    public FileHistoryService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MioReel", "history.json")) { }

    public FileHistoryService(string path)
    {
        _path = path;
        try
        {
            _records = File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), MioReelJsonContext.Default.ListHistoryRecord) ?? []
                : [];
            _records = _records.Where(x => x?.Drama is not null && !string.IsNullOrWhiteSpace(x.Drama.SeriesId))
                .OrderByDescending(x => x.WatchedAt).DistinctBy(x => x.Drama.SeriesId).Take(MaxRecords).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("读取浏览历史失败：{0}", ex.Message);
            _records = [];
        }
    }

    public IReadOnlyList<HistoryRecord> GetHistory()
    {
        lock (_gate) return _records.OrderByDescending(x => x.WatchedAt).ToList();
    }

    public void RecordWatch(DramaItem drama, int episodeNo, int progressPercent, double positionSeconds = 0, double durationSeconds = 0)
    {
        lock (_gate)
        {
            _records.RemoveAll(x => x.Drama.SeriesId == drama.SeriesId);
            _records.Insert(0, new HistoryRecord(drama, Math.Max(1, episodeNo), Math.Clamp(progressPercent, 0, 100),
                DateTimeOffset.Now, NormalizeSeconds(positionSeconds), NormalizeSeconds(durationSeconds)));
            if (_records.Count > MaxRecords) _records.RemoveRange(MaxRecords, _records.Count - MaxRecords);
            Save();
        }
    }

    public void Remove(string seriesId)
    {
        lock (_gate)
        {
            _records.RemoveAll(x => x.Drama.SeriesId == seriesId);
            Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _records.Clear();
            Save();
        }
    }

    public void ReplaceAll(IReadOnlyList<HistoryRecord> records)
    {
        lock (_gate)
        {
            var merged = new List<HistoryRecord>();
            if (records is not null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var record in records.OrderByDescending(x => x.WatchedAt))
                {
                    if (record?.Drama is null || string.IsNullOrWhiteSpace(record.Drama.SeriesId)) continue;
                    if (!seen.Add(record.Drama.SeriesId)) continue;
                    merged.Add(record);
                    if (merged.Count >= MaxRecords) break;
                }
            }
            _records.Clear();
            _records.AddRange(merged);
            Save();
        }
    }

    private static double NormalizeSeconds(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_records, MioReelJsonContext.Default.ListHistoryRecord));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("保存浏览历史失败：{0}", ex.Message);
        }
    }
}
