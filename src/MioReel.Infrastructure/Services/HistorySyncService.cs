using System.Net.Http.Headers;
using System.Text.Json;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;
using MioReel.Core.Serialization;

namespace MioReel.Infrastructure.Services;

/// <summary>
/// 云端历史同步。所有请求都带自有 token；未登录时抛出明确异常，由 UI 决定是否提示。
/// </summary>
public sealed class HistorySyncService : IHistorySyncService
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly IAuthService _auth;

    public HistorySyncService(HttpClient http, ISettingsService settings, IAuthService auth)
    {
        _http = http;
        _settings = settings;
        _auth = auth;
    }

    public bool IsEnabled => _auth.IsSignedIn;

    public async Task<HistorySyncResult> UploadAllAsync(IReadOnlyList<HistoryRecord> local, CancellationToken ct = default)
    {
        var token = RequireToken();
        using var req = BuildRequest(HttpMethod.Post, "/api/history/merge", token);
        req.Content = JsonContent.Create(local.ToList(), MioReelJsonContext.Default.ListHistoryRecord);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(body, MioReelJsonContext.Default.HistorySyncResult)
               ?? new HistorySyncResult([], DateTimeOffset.UtcNow);
    }

    public async Task<HistorySyncResult> DownloadAllAsync(CancellationToken ct = default)
    {
        var token = RequireToken();
        using var req = BuildRequest(HttpMethod.Get, "/api/history", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(body, MioReelJsonContext.Default.HistorySyncResult)
               ?? new HistorySyncResult([], DateTimeOffset.UtcNow);
    }

    public async Task PushOneAsync(HistoryRecord record, CancellationToken ct = default)
    {
        var token = RequireToken();
        using var req = BuildRequest(HttpMethod.Post, "/api/history/push", token);
        req.Content = JsonContent.Create(record, MioReelJsonContext.Default.HistoryRecord);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task RemoveOneAsync(string seriesId, CancellationToken ct = default)
    {
        var token = RequireToken();
        using var req = BuildRequest(HttpMethod.Delete, $"/api/history/{Uri.EscapeDataString(seriesId)}", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        var token = RequireToken();
        using var req = BuildRequest(HttpMethod.Delete, "/api/history", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, string token)
    {
        var baseUrl = (_settings.Current.AccountApiBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("尚未配置账号服务地址，请在 设置 → 账号 中填写");
        var req = new HttpRequestMessage(method, baseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private string RequireToken()
    {
        if (!_auth.IsSignedIn || string.IsNullOrWhiteSpace(_auth.AccessToken))
            throw new InvalidOperationException("请先登录账号后再同步观看历史");
        return _auth.AccessToken!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        string message;
        try
        {
            using var doc = JsonDocument.Parse(body);
            message = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? body : body;
        }
        catch { message = body; }
        throw new HttpRequestException($"账号服务返回 HTTP {(int)resp.StatusCode}：{message}");
    }
}
