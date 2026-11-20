using MioReel.Core.Models;

namespace MioReel.Core.Abstractions;

public interface IDramaCatalogService
{
    bool IsLastRequestLive { get; }
    Task<CatalogPageResult> GetCatalogPageAsync(DramaCategory category, int page = 1, int limit = 40, CancellationToken ct = default);
    Task<IReadOnlyList<DramaItem>> GetCatalogAsync(DramaCategory category, CancellationToken ct = default);
    Task<IReadOnlyList<DramaItem>> SearchAsync(string query, CancellationToken ct = default);
    Task<IReadOnlyList<EpisodeItem>> GetEpisodesAsync(string seriesId, CancellationToken ct = default);
    Task<ResolvedStreamInfo> ResolveEpisodeAsync(string seriesId, int episodeNo, int quality = 1080, CancellationToken ct = default);
    Task<DanmakuChunk> GetDanmakuAsync(string danmakuId, long startMs = 0, long durationMs = 300_000, CancellationToken ct = default) =>
        Task.FromResult(new DanmakuChunk(danmakuId, startMs, startMs + 30_000, []));
    Task<GatewayHealthResult> TestConnectionAsync(string gatewayBaseUrl, CancellationToken ct = default);
}

public interface IHistoryService
{
    IReadOnlyList<HistoryRecord> GetHistory();
    void RecordWatch(DramaItem drama, int episodeNo, int progressPercent, double positionSeconds = 0, double durationSeconds = 0);
    void Remove(string seriesId);
    void Clear();

    /// <summary>用云端合并结果整体替换本地历史（不触发任何云端回传）。</summary>
    void ReplaceAll(IReadOnlyList<HistoryRecord> records);
}

/// <summary>
/// 账号会话服务：账号密码登录/注册、会话持久化、登录状态广播。
/// 未登录时所有方法均可安全调用；与历史同步服务解耦。
/// </summary>
public interface IAuthService
{
    UserProfile? CurrentUser { get; }
    string? AccessToken { get; }
    bool IsSignedIn { get; }

    event Action? AuthStateChanged;

    /// <summary>用账号密码登录；成功后服务端签发 token，本方法负责落盘并广播。</summary>
    Task<AuthResult> LoginAsync(string username, string password, CancellationToken ct = default);

    /// <summary>注册新账号并直接登录；用户名已存在等错误以异常抛出。</summary>
    Task<AuthResult> RegisterAsync(string username, string password, CancellationToken ct = default);

    /// <summary>用本地已保存的 token 恢复会话；失败时静默回到未登录。</summary>
    Task RestoreAsync(CancellationToken ct = default);

    void SignOut();
}

/// <summary>
/// 云端历史同步：登录后将本地历史上传并与云端按剧集合并，返回合并结果。
/// 未登录时直接抛出 InvalidOperationException，由调用方决定是否提示。
/// </summary>
public interface IHistorySyncService
{
    bool IsEnabled { get; }
    Task<HistorySyncResult> UploadAllAsync(IReadOnlyList<HistoryRecord> local, CancellationToken ct = default);
    Task<HistorySyncResult> DownloadAllAsync(CancellationToken ct = default);
    Task PushOneAsync(HistoryRecord record, CancellationToken ct = default);
    Task RemoveOneAsync(string seriesId, CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);
}

public interface IDownloadService
{
    IReadOnlyList<DownloadTaskItem> GetDownloads();
    DownloadTaskItem Enqueue(DramaItem drama, int episodeCount);
    void TogglePause(string taskId);
    void Remove(string taskId);
    IReadOnlyList<EpisodeItem> GetOfflineEpisodes(string seriesId) => [];
    ResolvedStreamInfo? GetOfflineStream(string seriesId, int episodeNo) => null;
}

public interface ISettingsService
{
    AppSettings Current { get; }
    void Update(AppSettings settings);
}

/// <summary>
/// 客户端本地视频流解密与 HTTP Range 代理服务：将加密流或需自定义请求头的 CDN 直链转换为 FrameFlux 可直接播放的本地 HTTP 地址。
/// </summary>
public interface IMediaStreamProxyService : IDisposable
{
    Task<Uri> PreparePlayableUriAsync(ResolvedStreamInfo stream, CancellationToken ct = default);
    void ReleaseSession(Uri? proxyUri);
}

/// <summary>
/// 跨平台系统全局热键监听服务（Windows RegisterHotKey / Linux X11 XGrabKey）。
/// </summary>
public interface IGlobalHotkeyService : IDisposable
{
    bool IsSupported { get; }
    string BackendName { get; }
    string? PlatformNotice { get; }
    IReadOnlyDictionary<HotkeyAction, HotkeyRegistrationStatus> Statuses { get; }

    event Action<HotkeyAction>? HotkeyTriggered;
    event Action? StatusChanged;

    void ApplyBindings(bool enabled, IReadOnlyDictionary<HotkeyAction, HotkeyBinding> bindings);
}
