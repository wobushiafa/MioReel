namespace MioReel.Core.Models;

/// <summary>
/// 短剧分类，与 media-gateway 网关目录分类契约保持一致。
/// </summary>
public enum DramaCategory
{
    All,
    RealDrama,
    ComicDrama,
    AiDrama
}

/// <summary>
/// 剧目条目（对应网关 catalog/search 返回结构，不含预先未知的视频分辨率）。
/// </summary>
public sealed record DramaItem(
    string SeriesId,
    string Title,
    string CoverUrl,
    int EpisodeCount,
    string Remark,
    string Intro,
    IReadOnlyList<string> Tags,
    string Year,
    DramaCategory Category,
    string AccentPrimaryHex,
    string AccentSecondaryHex)
{
    public string CategoryLabel => Category switch
    {
        DramaCategory.RealDrama => "真人剧",
        DramaCategory.ComicDrama => "漫剧",
        DramaCategory.AiDrama => "AI 剧",
        _ => "短剧"
    };

    public string TagsSummary => Tags.Count > 0 ? string.Join(" · ", Tags) : CategoryLabel;
}

/// <summary>
/// 单集元数据（对应网关 series 接口分集项）。
/// </summary>
public sealed record EpisodeItem(
    int No,
    string Vid,
    string Title,
    bool Available);

public sealed record StreamVariantInfo(
    string Url,
    int Quality,
    int Width,
    int Height,
    string Codec,
    string DecryptionKey);

/// <summary>
/// 视频流解析结果（包含播放直链、解密密钥以及真实的视频宽高）。
/// </summary>
public sealed record ResolvedStreamInfo(
    string SeriesId,
    int EpisodeNo,
    string PlayUrl,
    IReadOnlyDictionary<string, string> PlayHeaders,
    string DecryptionKey,
    string Encryption,
    int Quality,
    int Width,
    int Height,
    string Codec,
    long ExpiresAtUnixMs,
    IReadOnlyList<StreamVariantInfo>? Variants = null,
    string? DanmakuId = null)
{
    public bool IsLandscape => Width > Height && Height > 0;
}

/// <summary>
/// 单条弹幕条目（相对剧集起点的毫秒偏移、文本与颜色）。
/// </summary>
public sealed record DanmakuItem(
    string Id,
    long TimeMs,
    string Text,
    string ColorHex = "#FFFFFF");

/// <summary>
/// 网关返回的一段 30 秒弹幕片段。
/// </summary>
public sealed record DanmakuChunk(
    string EpisodeId,
    long StartMs,
    long NextMs,
    IReadOnlyList<DanmakuItem> Items);

/// <summary>
/// 浏览历史条目。
/// </summary>
public sealed record HistoryRecord(
    DramaItem Drama,
    int LastEpisodeNo,
    int ProgressPercent,
    DateTimeOffset WatchedAt,
    double PositionSeconds = 0,
    double DurationSeconds = 0)
{
    public string GroupTitle => WatchedAt.Date == DateTimeOffset.Now.Date ? "今天" : "更早";

    public string RelativeTimeText
    {
        get
        {
            var diff = DateTimeOffset.Now - WatchedAt;
            if (diff.TotalMinutes < 60) return $"{Math.Max(1, (int)diff.TotalMinutes)} 分钟前";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} 小时前";
            return $"{Math.Max(1, (int)diff.TotalDays)} 天前";
        }
    }
}

public enum DownloadTaskState
{
    Downloading,
    Paused,
    Completed,
    Failed
}

/// <summary>
/// 离线下载任务条目。
/// </summary>
public sealed record DownloadTaskItem(
    string TaskId,
    DramaItem Drama,
    string EpisodeRangeLabel,
    int ProgressPercent,
    string SpeedText,
    string SizeText,
    DownloadTaskState State,
    string DirectoryPath = "",
    int Quality = 1080,
    int[]? EpisodeNumbers = null,
    IReadOnlyList<OfflineEpisode>? Files = null,
    string? ErrorMessage = null);

public sealed record OfflineEpisode(int EpisodeNo, string FilePath, long Length, int Quality, int Width, int Height, string Codec);

public enum AppThemePreference
{
    Dark,
    Light
}

public enum VideoRenderMode
{
    Auto,
    GpuComposition,
    SoftwareBitmap,
    NativeSurface
}

public enum GatewayFailureKind
{
    None,
    InvalidConfig,
    Connection,
    Unauthorized,
    Forbidden,
    GatewayError,
    InvalidResponse
}

public sealed record CatalogPageResult(
    IReadOnlyList<DramaItem> Items,
    bool HasMore,
    bool IsLiveGateway);

public sealed record GatewayHealthResult(
    bool IsHealthy,
    string Message,
    GatewayFailureKind FailureKind = GatewayFailureKind.None);

public sealed class GatewayException : Exception
{
    public GatewayException(
        string userMessage,
        int? errorCode = null,
        string? requestId = null,
        Exception? inner = null,
        GatewayFailureKind failureKind = GatewayFailureKind.GatewayError)
        : base(userMessage, inner)
    {
        UserMessage = userMessage;
        ErrorCode = errorCode;
        RequestId = requestId;
        FailureKind = failureKind;
    }

    public string UserMessage { get; }
    public int? ErrorCode { get; }
    public string? RequestId { get; }
    public GatewayFailureKind FailureKind { get; }
}

/// <summary>
/// 客户端用户设置。
/// </summary>
public sealed record AppSettings(
    AppThemePreference Theme,
    bool MiniWindowAlwaysOnTop,
    int DefaultQuality,
    bool AutoPlayNextEpisode,
    string DownloadDirectory,
    string GatewayBaseUrl,
    string GatewayToken = "",
    VideoRenderMode RenderMode = VideoRenderMode.Auto,
    WindowPlacement? NormalWindow = null,
    WindowPlacement? MiniWindow = null,
    double PlaybackRate = 1.0,
    HotkeySettings? Hotkeys = null,
    double? Volume = null,
    bool IsMuted = false,
    double AudioBoostDb = 0.0,
    bool? DanmakuEnabled = null,
    string AccountApiBaseUrl = "http://47.114.100.80:9999");

public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool IsMaximized = false);
