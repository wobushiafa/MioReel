using System.Text.Json.Serialization;
using MioReel.Core.Models;

namespace MioReel.Core.Serialization;

public sealed record GatewayEnvelope<T>(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("data")] T? Data);

public sealed record GatewayCatalogData(
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("page_count")] int PageCount,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("list")] IReadOnlyList<GatewayCatalogItem> List);

public sealed record GatewayCatalogItem(
    [property: JsonPropertyName("series_id")] string SeriesId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("cover")] string Cover,
    [property: JsonPropertyName("episode_count")] int EpisodeCount,
    [property: JsonPropertyName("remark")] string Remark,
    [property: JsonPropertyName("intro")] string Intro,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
    [property: JsonPropertyName("year")] string Year);

public sealed record GatewaySeriesRequest(
    [property: JsonPropertyName("series_id")] string SeriesId,
    [property: JsonPropertyName("source")] string Source = "hongguo");

public sealed record GatewaySearchData(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("list")] IReadOnlyList<GatewayCatalogItem> List);

public sealed record GatewayHealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("uptime_ms")] long UptimeMs);

public sealed record GatewaySeriesData(
    [property: JsonPropertyName("series_id")] string SeriesId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("cover")] string Cover,
    [property: JsonPropertyName("intro")] string Intro,
    [property: JsonPropertyName("total_episodes")] int TotalEpisodes,
    [property: JsonPropertyName("accessible_episodes")] int AccessibleEpisodes,
    [property: JsonPropertyName("episodes")] IReadOnlyList<GatewayEpisodeItem> Episodes);

public sealed record GatewayEpisodeItem(
    [property: JsonPropertyName("no")] int No,
    [property: JsonPropertyName("vid")] string Vid,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("available")] bool Available);

public sealed record GatewayResolveRequest(
    [property: JsonPropertyName("series_id")] string? SeriesId = null,
    [property: JsonPropertyName("episode_no")] int? EpisodeNo = null,
    [property: JsonPropertyName("vid")] string? Vid = null,
    [property: JsonPropertyName("quality")] int Quality = 0);

public sealed record GatewayResolveVariant(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("quality")] int Quality,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("codec")] string Codec,
    [property: JsonPropertyName("decryption_key")] string DecryptionKey);

public sealed record GatewayResolveData(
    [property: JsonPropertyName("series_id")] string SeriesId,
    [property: JsonPropertyName("series_title")] string SeriesTitle,
    [property: JsonPropertyName("episode_no")] int EpisodeNo,
    [property: JsonPropertyName("play_url")] string PlayUrl,
    [property: JsonPropertyName("play_headers")] Dictionary<string, string>? PlayHeaders,
    [property: JsonPropertyName("decryption_key")] string DecryptionKey,
    [property: JsonPropertyName("encryption")] string Encryption,
    [property: JsonPropertyName("quality")] int Quality,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("codec")] string Codec,
    [property: JsonPropertyName("expires_at")] long ExpiresAt,
    [property: JsonPropertyName("variants")] IReadOnlyList<GatewayResolveVariant>? Variants = null,
    [property: JsonPropertyName("danmaku_id")] string? DanmakuId = null);

public sealed record GatewayDanmakuData(
    [property: JsonPropertyName("episode_id")] string EpisodeId,
    [property: JsonPropertyName("start_ms")] long StartMs,
    [property: JsonPropertyName("next_ms")] long NextMs,
    [property: JsonPropertyName("items")] IReadOnlyList<GatewayDanmakuItem>? Items = null);

public sealed record GatewayDanmakuItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("time_ms")] long TimeMs,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("color")] string? Color = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GatewayEnvelope<GatewayCatalogData>))]
[JsonSerializable(typeof(GatewayEnvelope<GatewaySearchData>))]
[JsonSerializable(typeof(GatewayEnvelope<GatewaySeriesData>))]
[JsonSerializable(typeof(GatewayEnvelope<GatewayResolveData>))]
[JsonSerializable(typeof(GatewayEnvelope<GatewayDanmakuData>))]
[JsonSerializable(typeof(GatewayHealthResponse))]
[JsonSerializable(typeof(GatewaySeriesRequest))]
[JsonSerializable(typeof(GatewayResolveRequest))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(HotkeySettings))]
[JsonSerializable(typeof(HotkeyBinding))]
[JsonSerializable(typeof(List<HistoryRecord>))]
[JsonSerializable(typeof(HistoryRecord))]
[JsonSerializable(typeof(List<DownloadTaskItem>))]
[JsonSerializable(typeof(UserProfile))]
[JsonSerializable(typeof(AuthCredentials))]
[JsonSerializable(typeof(AuthResult))]
[JsonSerializable(typeof(HistorySyncResult))]
[JsonSerializable(typeof(StoredSession))]
public sealed partial class MioReelJsonContext : JsonSerializerContext;
