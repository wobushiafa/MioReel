namespace MioReel.Core.Models;

/// <summary>
/// 当前登录用户的基本资料（来自账号服务，不含密码等任何敏感字段）。
/// </summary>
public sealed record UserProfile(
    string Id,
    string Nickname,
    string? AvatarUrl,
    DateTimeOffset CreatedAt);

/// <summary>
/// 账号密码登录/注册的请求凭据。密码只在 TLS 传输中发送，客户端不落盘。
/// </summary>
public sealed record AuthCredentials(
    string Username,
    string Password);

/// <summary>
/// 登录/注册成功后账号服务签发的会话：自有 JWT + 用户资料。
/// </summary>
public sealed record AuthResult(
    string Token,
    UserProfile User);

/// <summary>
/// 云端历史同步结果：服务端合并后返回的最终记录集。
/// </summary>
public sealed record HistorySyncResult(
    IReadOnlyList<HistoryRecord> Records,
    DateTimeOffset SyncedAt);

/// <summary>
/// 本地持久化的登录会话（token 来自自有账号服务）。只保存 token，不保存密码。
/// </summary>
public sealed record StoredSession(
    string AccessToken,
    UserProfile User,
    DateTimeOffset SavedAt);
