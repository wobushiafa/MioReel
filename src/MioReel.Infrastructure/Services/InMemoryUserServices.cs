using MioReel.Core.Abstractions;
using MioReel.Core.Models;

namespace MioReel.Infrastructure.Services;

public sealed class InMemoryHistoryService : IHistoryService
{
    private readonly List<HistoryRecord> _records = [];

    public IReadOnlyList<HistoryRecord> GetHistory() =>
        _records.OrderByDescending(x => x.WatchedAt).ToList();

    public void RecordWatch(DramaItem drama, int episodeNo, int progressPercent, double positionSeconds = 0, double durationSeconds = 0)
    {
        _records.RemoveAll(x => x.Drama.SeriesId == drama.SeriesId);
        _records.Insert(0, new HistoryRecord(drama, episodeNo, progressPercent, DateTimeOffset.Now, positionSeconds, durationSeconds));
    }

    public void Remove(string seriesId) =>
        _records.RemoveAll(x => x.Drama.SeriesId == seriesId);

    public void Clear() => _records.Clear();

    public void ReplaceAll(IReadOnlyList<HistoryRecord> records)
    {
        _records.Clear();
        if (records is not null)
            _records.AddRange(records.Where(x => x?.Drama is not null));
    }
}

public sealed class InMemoryDownloadService : IDownloadService
{
    private readonly List<DownloadTaskItem> _tasks = [];

    public IReadOnlyList<DownloadTaskItem> GetDownloads() => _tasks.ToList();

    public DownloadTaskItem Enqueue(DramaItem drama, int episodeCount)
    {
        var task = new DownloadTaskItem(
            $"dl-{Guid.NewGuid():N}",
            drama,
            $"第 1-{episodeCount} 集",
            10,
            "14.2 MB/s",
            "260 MB",
            DownloadTaskState.Downloading);
        _tasks.Insert(0, task);
        return task;
    }

    public void TogglePause(string taskId)
    {
        var idx = _tasks.FindIndex(x => x.TaskId == taskId);
        if (idx < 0) return;
        var item = _tasks[idx];
        if (item.State == DownloadTaskState.Completed) return;
        _tasks[idx] = item with
        {
            State = item.State == DownloadTaskState.Paused
                ? DownloadTaskState.Downloading
                : DownloadTaskState.Paused
        };
    }

    public void Remove(string taskId) => _tasks.RemoveAll(x => x.TaskId == taskId);
}

public sealed class InMemorySettingsService : ISettingsService
{
    public AppSettings Current { get; private set; } = new(
        Theme: AppThemePreference.Dark,
        MiniWindowAlwaysOnTop: true,
        DefaultQuality: 1080,
        AutoPlayNextEpisode: true,
        DownloadDirectory: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "MioReel",
            "Downloads"),
        GatewayBaseUrl: Environment.GetEnvironmentVariable("MIOREEL_GATEWAY_URL") ?? "https://miaomiaotv.diaofanle.com",
        GatewayToken: Environment.GetEnvironmentVariable("MIOREEL_GATEWAY_TOKEN") ?? "248b4593caed441eb05ae0f124cf04cb");

    public void Update(AppSettings settings) => Current = settings;
}
