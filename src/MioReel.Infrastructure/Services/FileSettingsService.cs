using System.Text.Json;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;
using MioReel.Core.Serialization;

namespace MioReel.Infrastructure.Services;

/// <summary>
/// 基于 NativeAOT Source Generator 的本地配置持久化服务（settings.json）。
/// </summary>
public sealed class FileSettingsService : ISettingsService
{
    private readonly string _settingsFilePath;

    public FileSettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MioReel",
            "settings.json"))
    {
    }

    public FileSettingsService(string settingsFilePath)
    {
        _settingsFilePath = settingsFilePath;
        Current = LoadOrDefault();
    }

    public AppSettings Current { get; private set; }

    public void Update(AppSettings settings)
    {
        var normalized = Normalize(settings);
        Current = normalized;
        SaveSafe(normalized);
    }

    private AppSettings LoadOrDefault()
    {
        var defaults = CreateDefault();
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize(json, MioReelJsonContext.Default.AppSettings);
                if (loaded is not null)
                    defaults = Normalize(loaded, defaults);
            }
        }
        catch
        {
            // Ignore malformed or unreadable user settings and fall back cleanly.
        }

        var envUrl = Environment.GetEnvironmentVariable("MIOREEL_GATEWAY_URL");
        var envToken = Environment.GetEnvironmentVariable("MIOREEL_GATEWAY_TOKEN");
        if (!string.IsNullOrWhiteSpace(envUrl))
            defaults = defaults with { GatewayBaseUrl = envUrl.Trim() };
        if (!string.IsNullOrWhiteSpace(envToken))
            defaults = defaults with { GatewayToken = envToken.Trim() };

        return defaults;
    }

    private void SaveSafe(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, MioReelJsonContext.Default.AppSettings);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // Keep runtime state updated even if disk write fails in restricted environments.
        }
    }

    private static AppSettings CreateDefault() => new(
        Theme: AppThemePreference.Dark,
        MiniWindowAlwaysOnTop: true,
        DefaultQuality: 1080,
        AutoPlayNextEpisode: true,
        DownloadDirectory: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "MioReel",
            "Downloads"),
        GatewayBaseUrl: "https://miaomiaotv.diaofanle.com",
        GatewayToken: "248b4593caed441eb05ae0f124cf04cb",
        Hotkeys: HotkeySettings.CreateDefaults(),
        Volume: 1.0,
        IsMuted: false,
        DanmakuEnabled: true);

    private static AppSettings Normalize(AppSettings value, AppSettings? fallback = null)
    {
        var baseFallback = fallback ?? CreateDefault();
        return value with
        {
            DefaultQuality = value.DefaultQuality > 0 ? value.DefaultQuality : baseFallback.DefaultQuality,
            PlaybackRate = double.IsFinite(value.PlaybackRate) && value.PlaybackRate is >= 0.5 and <= 3 ? value.PlaybackRate : 1.0,
            Volume = value.Volume is { } vol && double.IsFinite(vol) ? Math.Clamp(vol, 0.0, 1.0) : (baseFallback.Volume ?? 1.0),
            AudioBoostDb = double.IsFinite(value.AudioBoostDb) ? Math.Clamp(value.AudioBoostDb, 0.0, 24.0) : 0.0,
            DanmakuEnabled = value.DanmakuEnabled ?? baseFallback.DanmakuEnabled ?? true,
            DownloadDirectory = string.IsNullOrWhiteSpace(value.DownloadDirectory)
                ? baseFallback.DownloadDirectory
                : value.DownloadDirectory.Trim(),
            GatewayBaseUrl = string.IsNullOrWhiteSpace(value.GatewayBaseUrl)
                ? baseFallback.GatewayBaseUrl
                : value.GatewayBaseUrl.Trim(),
            GatewayToken = (value.GatewayToken ?? string.Empty).Trim(),
            AccountApiBaseUrl = string.IsNullOrWhiteSpace(value.AccountApiBaseUrl)
                ? baseFallback.AccountApiBaseUrl
                : value.AccountApiBaseUrl.Trim(),
            Hotkeys = (value.Hotkeys ?? baseFallback.Hotkeys ?? HotkeySettings.CreateDefaults()).EnsurePopulated()
        };
    }
}
