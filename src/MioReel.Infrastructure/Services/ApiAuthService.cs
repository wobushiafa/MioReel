using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;
using MioReel.Core.Serialization;

namespace MioReel.Infrastructure.Services;

/// <summary>
/// 账号会话：与自有账号 API 通信完成账号密码登录/注册，并把签发的 token 落到本地。
/// 本地只保存自有 token，绝不保存用户密码。
/// </summary>
public sealed class ApiAuthService : IAuthService
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly string _sessionPath;
    private readonly object _gate = new();
    private UserProfile? _user;
    private string? _token;

    public event Action? AuthStateChanged;

    public ApiAuthService(HttpClient http, ISettingsService settings)
        : this(http, settings, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MioReel", "session.json")) { }

    public ApiAuthService(HttpClient http, ISettingsService settings, string sessionPath)
    {
        _http = http;
        _settings = settings;
        _sessionPath = sessionPath;
    }

    public UserProfile? CurrentUser
    {
        get { lock (_gate) return _user; }
    }

    public string? AccessToken
    {
        get { lock (_gate) return _token; }
    }

    public bool IsSignedIn
    {
        get { lock (_gate) return _user is not null && !string.IsNullOrWhiteSpace(_token); }
    }

    public Task<AuthResult> LoginAsync(string username, string password, CancellationToken ct = default) =>
        AuthenticateAsync("/api/auth/login", username, password,
            "无法连接账号服务，请检查服务地址是否正确且服务已启动", ct);

    public Task<AuthResult> RegisterAsync(string username, string password, CancellationToken ct = default) =>
        AuthenticateAsync("/api/auth/register", username, password,
            "无法连接账号服务，请检查服务地址是否正确且服务已启动", ct);

    private async Task<AuthResult> AuthenticateAsync(string path, string username, string password,
        string networkError, CancellationToken ct)
    {
        var payload = new AuthCredentials(username, password);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}{path}")
        {
            Content = JsonContent.Create(payload, MioReelJsonContext.Default.AuthCredentials)
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var message = resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "用户名或密码错误"
                : ExtractError(body, networkError);
            throw new InvalidOperationException(message);
        }
        var result = JsonSerializer.Deserialize(body, MioReelJsonContext.Default.AuthResult)
                     ?? throw new InvalidOperationException("账号服务返回的登录结果为空");
        if (string.IsNullOrWhiteSpace(result.Token) || result.User is null)
            throw new InvalidOperationException("账号服务返回的登录结果不完整");

        SaveSession(new StoredSession(result.Token, result.User, DateTimeOffset.UtcNow));
        lock (_gate)
        {
            _token = result.Token;
            _user = result.User;
        }
        AuthStateChanged?.Invoke();
        return result;
    }

    public async Task RestoreAsync(CancellationToken ct = default)
    {
        StoredSession? session;
        try
        {
            session = File.Exists(_sessionPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(_sessionPath), MioReelJsonContext.Default.StoredSession)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            session = null;
        }
        if (session is null || string.IsNullOrWhiteSpace(session.AccessToken) || session.User is null)
            return;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/api/auth/me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // token 过期或失效，静默清除
                ClearSessionFiles();
                return;
            }
            var profile = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(ct),
                MioReelJsonContext.Default.UserProfile) ?? session.User;
            lock (_gate)
            {
                _token = session.AccessToken;
                _user = profile;
            }
            AuthStateChanged?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            // 网络不通时保留本地会话，下次启动再验证，避免误登出
            Trace.TraceWarning("恢复登录会话失败：{0}", ex.Message);
        }
    }

    public void SignOut()
    {
        ClearSessionFiles();
        lock (_gate)
        {
            _token = null;
            _user = null;
        }
        AuthStateChanged?.Invoke();
    }

    private string ApiBase => (_settings.Current.AccountApiBaseUrl ?? string.Empty).Trim().TrimEnd('/');

    private void SaveSession(StoredSession session)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_sessionPath))!;
            Directory.CreateDirectory(dir);
            var tmp = _sessionPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(session, MioReelJsonContext.Default.StoredSession));
            File.Move(tmp, _sessionPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("保存登录会话失败：{0}", ex.Message);
        }
    }

    private void ClearSessionFiles()
    {
        try
        {
            if (File.Exists(_sessionPath)) File.Delete(_sessionPath);
            if (File.Exists(_sessionPath + ".tmp")) File.Delete(_sessionPath + ".tmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("清除登录会话失败：{0}", ex.Message);
        }
    }

    private static string ExtractError(string body, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("title", out var title))
                return title.GetString() ?? fallback;
            if (doc.RootElement.TryGetProperty("message", out var message))
                return message.GetString() ?? fallback;
        }
        catch { }
        return fallback;
    }
}
