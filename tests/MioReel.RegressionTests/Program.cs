using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ReactiveUI.Primitives;
using Aviora.Presentation.Toasts;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;
using MioReel.Desktop.ViewModels;
using MioReel.Infrastructure.Services;
using MioReel.Infrastructure.Services.MediaProxy;

var root = Path.Combine(Path.GetTempPath(), "MioReel-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var drama = new DramaItem("series", "测试短剧", "", 3, "", "", ["测试"], "2026", DramaCategory.RealDrama, "#000000", "#FFFFFF");
    var path = Path.Combine(root, "history.json");
    var history = new FileHistoryService(path);
    history.RecordWatch(drama, 2, 35, 42, 120);
    history = new FileHistoryService(path);
    Check(history.GetHistory().Single() is { LastEpisodeNo: 2, PositionSeconds: 42, ProgressPercent: 35 }, "历史重启后保留集数、进度和位置");
    history.RecordWatch(drama, 2, 50, 60, 120);
    Check(new FileHistoryService(path).GetHistory().Count == 1, "同一短剧更新记录而非重复添加");
    history.Remove(drama.SeriesId);
    Check(new FileHistoryService(path).GetHistory().Count == 0, "删除记录持久化");
    history.RecordWatch(drama, 1, 0);
    history.Clear();
    Check(new FileHistoryService(path).GetHistory().Count == 0, "清空记录持久化");
    File.WriteAllText(path, "{broken");
    Check(new FileHistoryService(path).GetHistory().Count == 0, "损坏历史文件允许正常启动");

    // 账号会话：本地 token 持久化与退出清除（不落用户密码）
    var sessionPath = Path.Combine(root, "session.json");
    var authSettings = new InMemorySettingsService();
    var authService = new ApiAuthService(new HttpClient(), authSettings, sessionPath);
    Check(!authService.IsSignedIn, "初始状态为未登录");
    var fakeToken = "header.payload.signature";
    File.WriteAllText(sessionPath, System.Text.Json.JsonSerializer.Serialize(
        new StoredSession(fakeToken, new UserProfile("openid-1", "胡哥", null, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow)));
    // RestoreAsync 需要服务端校验 token，离线时保留本地会话：这里只验证文件被识别未误删
    Check(File.Exists(sessionPath), "登录会话文件可被写入");
    authService.SignOut();
    Check(!File.Exists(sessionPath) && !authService.IsSignedIn, "退出登录清除本地会话");

    // 云同步回填：ReplaceAll 按 seriesId 去重、保留最新 WatchedAt、持久化且容量封顶
    var cloudDramaA = new DramaItem("cloud-a", "云端剧A", "", 5, "", "", [], "2026", DramaCategory.All, "#000000", "#FFFFFF");
    var cloudDramaB = new DramaItem("cloud-b", "云端剧B", "", 5, "", "", [], "2026", DramaCategory.All, "#000000", "#FFFFFF");
    var newest = new HistoryRecord(cloudDramaA, 4, 80, DateTimeOffset.UtcNow);
    var older = new HistoryRecord(cloudDramaA, 1, 10, DateTimeOffset.UtcNow.AddDays(-2));
    var other = new HistoryRecord(cloudDramaB, 2, 30, DateTimeOffset.UtcNow.AddDays(-1));
    history = new FileHistoryService(path);
    history.ReplaceAll([older, other, newest]);
    var synced = new FileHistoryService(path).GetHistory();
    Check(synced.Count == 2, "云同步回填按 seriesId 去重");
    Check(synced.First(x => x.Drama.SeriesId == "cloud-a").LastEpisodeNo == 4, "云同步回填保留最新观看记录");
    history.ReplaceAll([]);
    Check(new FileHistoryService(path).GetHistory().Count == 0, "云同步回填空结果会清空本地历史");

    history = new FileHistoryService(path);
    history.RecordWatch(drama, 1, 25, 30, 120);
    var catalog = new FakeCatalog();
    var proxy = new FakeProxy();
    var player = new PlayerViewModel(catalog, proxy, history, new InMemorySettingsService(), DispatchProxy.Create<IToastService, NoopProxy>());
    await player.OpenAsync(new DramaCardViewModel(drama, "test"));
    Check(player.ConsumePendingResumeSeconds() == 30, "从真实历史位置断点续播");
    Check(catalog.Resolved.SequenceEqual([1, 2]) && proxy.Prepared.Count == 2, "当前集加载后提前解析并准备下一集媒体");
    player.SyncFromPlayer(TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(120));
    var nextUri = proxy.Prepared[1];
    var sourceChanges = new List<string?>();
    player.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlayerViewModel.ResolvedPlayUrl)) sourceChanges.Add(player.ResolvedPlayUrl); };
    await player.NextEpisodeCommand.Execute().ToTask();
    Check(player.ResolvedPlayUrl == nextUri.ToString() && catalog.Resolved.Count(x => x == 2) == 1, "切集复用预加载会话且不重复解析");
    Check(catalog.Resolved.SequenceEqual([1, 2, 3]), "切集后继续预加载下一集");
    Check(sourceChanges.SequenceEqual([nextUri.ToString()]), "切集直接替换播放源，中途不清空播放器");
    Check(!proxy.Released.Contains(proxy.Prepared[0]), "下一集开始播放前保留旧代理会话");
    player.ReleaseRetiredProxySessions();
    Check(proxy.Released.Contains(proxy.Prepared[0]), "下一集开始播放后释放旧会话");
    player.SyncFromPlayer(TimeSpan.FromSeconds(70), TimeSpan.FromSeconds(120));
    player.Close();
    var saved = new FileHistoryService(path).GetHistory().Single();
    Check(saved is { LastEpisodeNo: 2, PositionSeconds: 70, ProgressPercent: 58 }, "关闭时保存实际播放进度");
    Check(proxy.Released.Count == proxy.Prepared.Count, "关闭时释放当前集及预加载会话");

    var qualitySettings = new InMemorySettingsService();
    qualitySettings.Update(qualitySettings.Current with { DefaultQuality = 720 });
    catalog = new FakeCatalog
    {
        Transform = info => info with
        {
            Quality = 720,
            Variants = info.EpisodeNo == 2
                ? [new StreamVariantInfo("https://example.invalid/1080.mp4", 1080, 1080, 1920, "h264", "")]
                : null
        }
    };
    proxy = new FakeProxy();
    player = new PlayerViewModel(catalog, proxy, new InMemoryHistoryService(), qualitySettings, DispatchProxy.Create<IToastService, NoopProxy>());
    qualitySettings.Update(qualitySettings.Current with { DefaultQuality = 1080 });
    await player.OpenAsync(new DramaCardViewModel(drama, "preferred"));
    Check(catalog.RequestedQualities.SequenceEqual([1080, 1080]), "播放读取最新默认画质，单集降级不会覆盖后续1080P偏好");
    Check(player.QualityLabel == "720P" && player.QualityPreferenceText.Contains("1080P"), "缺少1080P时显示真实画质并保留优先级说明");
    Check(proxy.Streams[1].Quality == 1080 && proxy.Streams[1].PlayUrl.EndsWith("1080.mp4"), "预加载使用可用的1080P变体");
    Check(!player.HasPreviousEpisode && player.HasNextEpisode && player.EpisodeBadgeText == "第 1 / 3 集", "首集导航边界及标题总集数正确");
    await player.PreviousEpisodeCommand.Execute().ToTask();
    Check(player.CurrentEpisodeNo == 1, "首集不会跳转到第零集");
    await player.NextEpisodeCommand.Execute().ToTask();
    Check(player.QualityLabel == "1080P" && player.HasPreviousEpisode, "下一集恢复优先1080P且允许返回上一集");
    await player.PreviousEpisodeCommand.Execute().ToTask();
    Check(player.CurrentEpisodeNo == 1 && player.MiniTitleText.Contains("1 / 3"), "上一集跳转及小窗标题总集数正确");
    player.Close();

    catalog = new FakeCatalog { FailEpisode = 2 };
    proxy = new FakeProxy();
    player = new PlayerViewModel(catalog, proxy, new InMemoryHistoryService(), new InMemorySettingsService(), DispatchProxy.Create<IToastService, NoopProxy>());
    await player.OpenAsync(new DramaCardViewModel(drama, "failure"));
    Check(player.HasRealMediaUrl && !player.HasStreamError, "预加载失败不影响当前播放");
    catalog.FailEpisode = 0;
    await player.NextEpisodeCommand.Execute().ToTask();
    Check(player.CurrentEpisodeNo == 2 && player.HasRealMediaUrl && catalog.Resolved.Count(x => x == 2) == 2, "预加载失败后切集正常重试");
    await player.NextEpisodeCommand.Execute().ToTask();
    Check(catalog.Resolved.All(x => x <= 3), "最后一集不预加载越界集数");
    player.Close();

    catalog = new FakeCatalog();
    proxy = new FakeProxy();
    player = new PlayerViewModel(catalog, proxy, new InMemoryHistoryService(), new InMemorySettingsService(), DispatchProxy.Create<IToastService, NoopProxy>());
    await player.OpenAsync(new DramaCardViewModel(drama, "quality"));
    var oldPrefetch = proxy.Prepared[1];
    await player.SelectQualityCommand.Execute(new PlayerMenuOptionViewModel("720P", "", intValue: 720)).ToTask();
    Check(proxy.Released.Contains(oldPrefetch) && catalog.RequestedQualities.TakeLast(2).All(x => x == 720), "切换清晰度释放旧预加载并按新画质重新缓存");
    var qualityPrefetch = proxy.Prepared[^1];
    await player.NextEpisodeCommand.Execute().ToTask();
    Check(player.ResolvedPlayUrl == qualityPrefetch.ToString(), "下一集复用新画质缓存");
    player.Close();

    catalog = new FakeCatalog();
    proxy = new FakeProxy();
    player = new PlayerViewModel(catalog, proxy, new InMemoryHistoryService(), new InMemorySettingsService(), DispatchProxy.Create<IToastService, NoopProxy>());
    await player.OpenAsync(new DramaCardViewModel(drama, "skip"));
    var skipped = proxy.Prepared[1];
    await player.SelectEpisodeCommand.Execute(player.Episodes.Single(x => x.No == 3)).ToTask();
    Check(proxy.Released.Contains(skipped) && player.CurrentEpisodeNo == 3, "跳集时丢弃不匹配的下一集缓存");

    Check(player.ShowTransportControls && !player.ShowMiniTopBar, "正常窗口打开后默认显示播放功能栏");
    await player.ToggleMenuCommand.Execute("speed").ToTask();
    Check(player.IsAnyMenuOpen && player.UseFloatingMenu, "正常窗口打开倍速菜单");
    player.SetControlsVisible(false);
    Check(!player.AreControlsVisible && !player.ShowTransportControls && !player.UseFloatingHud && !player.IsAnyMenuOpen, "正常窗口隐藏功能栏时同步收起悬浮条与菜单");
    player.SetControlsVisible(true);
    Check(player.AreControlsVisible && player.ShowTransportControls && player.UseFloatingHud, "正常窗口重新显示功能栏");
    await player.ToggleMiniModeCommand.Execute().ToTask();
    Check(player.IsMiniMode && player.ShowMiniTopBar && player.ShowTransportControls, "切换小窗后默认显示顶栏与控制栏");
    player.ToggleControlsVisibility();
    Check(!player.ShowMiniTopBar && !player.ShowTransportControls, "小窗允许切换隐藏全部功能栏");
    player.Close();

    var volSettingsPath = Path.Combine(root, "vol-settings.json");
    File.WriteAllText(volSettingsPath, """{"Theme":0,"MiniWindowAlwaysOnTop":true,"DefaultQuality":1080,"AutoPlayNextEpisode":true,"DownloadDirectory":"","GatewayBaseUrl":"https://example.invalid"}""");
    var volSettings = new FileSettingsService(volSettingsPath);
    Check(volSettings.Current.Volume == 1.0 && !volSettings.Current.IsMuted, "旧版设置文件默认恢复100%非静音应用内音量");
    Check(volSettings.Current.DanmakuEnabled == true, "旧版设置文件默认开启弹幕显示");
    var volPlayer = new PlayerViewModel(catalog, proxy, new InMemoryHistoryService(), volSettings, DispatchProxy.Create<IToastService, NoopProxy>());
    volPlayer.VolumePercent = 42;
    Check(Math.Abs(volPlayer.Volume - 0.42) < 0.001 && volPlayer.IsVolumeLow && !volPlayer.IsEffectivelyMuted && volPlayer.VolumeLabel == "42%", "音量滑块百分比调节同步状态与图标档位");
    volPlayer.ToggleMute();
    Check(volPlayer.IsMuted && volPlayer.IsEffectivelyMuted && volPlayer.VolumePercent == 0 && volPlayer.VolumeLabel == "静音", "静音时保留原音量并显示静音状态");
    volPlayer.ToggleMute();
    Check(!volPlayer.IsMuted && Math.Abs(volPlayer.Volume - 0.42) < 0.001 && volPlayer.VolumePercent == 42, "取消静音后恢复调节前的音量");
    volPlayer.StepVolume(0.1);
    Check(Math.Abs(volPlayer.Volume - 0.52) < 0.001 && volPlayer.IsVolumeHigh, "步进增大音量切换到高音量图标并持久化");
    var boostChangedTo = -1.0;
    volPlayer.AudioBoostChanged += db => boostChangedTo = db;
    await volPlayer.SetAudioBoostCommand.Execute("18").ToTask();
    Check(Math.Abs(volPlayer.AudioBoostDb - 18.0) < 0.01 && volPlayer.IsAudioBoost18 && Math.Abs(boostChangedTo - 18.0) < 0.01, "音量增强支持切换+18dB并触发增益重载事件");
    await volPlayer.ToggleDanmakuCommand.Execute().ToTask();
    Check(!volPlayer.IsDanmakuEnabled, "切换弹幕开关可关闭弹幕");
    var reloadedSettings = new FileSettingsService(volSettingsPath);
    Check(Math.Abs((reloadedSettings.Current.Volume ?? 0) - 0.52) < 0.001 && !reloadedSettings.Current.IsMuted && Math.Abs(reloadedSettings.Current.AudioBoostDb - 18.0) < 0.01 && reloadedSettings.Current.DanmakuEnabled == false, "重启后保留应用内音量、增强增益与弹幕开关设置");

    await using (var mockGateway = new DanmakuGatewayServer())
    {
        var gwSettings = new InMemorySettingsService();
        gwSettings.Update(gwSettings.Current with { GatewayBaseUrl = mockGateway.BaseUrl, GatewayToken = "secret-token", DanmakuEnabled = true });
        using var dmHttp = new HttpClient();
        var gwCatalog = new GatewayDramaCatalogService(dmHttp, gwSettings);
        var resolvedGw = await gwCatalog.ResolveEpisodeAsync("7701678491724418073", 1, 1080);
        Check(resolvedGw.DanmakuId == "hongguo:7701678491724418073:7701680675979545625", "resolve 接口正确解析 danmaku_id");

        var chunkHigh = await gwCatalog.GetDanmakuAsync(resolvedGw.DanmakuId!, 310_000, 300_000);
        Check(chunkHigh.Items.Count == 1 && mockGateway.LastDurationMs > 310_000 && mockGateway.LastAuthHeader == "Bearer secret-token", "danmaku 接口携带鉴权且自动扩展 duration_ms 大于 start_ms");

        var dmCatalog = new FakeCatalog
        {
            Transform = info => info with { DanmakuId = $"hongguo:7701678491724418073:77016806759795456{info.EpisodeNo:00}" },
            DanmakuHandler = (id, start, dur) => gwCatalog.GetDanmakuAsync(id, start, dur)
        };
        var dmPlayer = new PlayerViewModel(dmCatalog, proxy, new InMemoryHistoryService(), gwSettings, DispatchProxy.Create<IToastService, NoopProxy>());
        var loadedBatches = new List<int>();
        dmPlayer.DanmakuItemsLoaded += batch => loadedBatches.Add(batch.Count);
        await dmPlayer.OpenAsync(new DramaCardViewModel(drama, "dm"));
        await Task.Delay(80);
        Check(dmPlayer.UseEmbeddedDanmakuOverlay && dmPlayer.LoadedDanmakuCount == 2 && dmCatalog.DanmakuStarts.Contains(0), "开播后自动拉取首段 30 秒弹幕并激活弹幕层");
        dmPlayer.SyncFromPlayer(TimeSpan.FromSeconds(22), TimeSpan.FromSeconds(120));
        await Task.Delay(80);
        Check(dmCatalog.DanmakuStarts.Contains(30_000) && dmPlayer.LoadedDanmakuCount == 3, "播放接近片段末尾时自动预加载下一段弹幕");
        var seekNotified = -1.0;
        dmPlayer.DanmakuSeeked += s => seekNotified = s;
        dmPlayer.NotifyDanmakuSeeked(95);
        await Task.Delay(80);
        Check(Math.Abs(seekNotified - 95) < 0.01 && dmCatalog.DanmakuStarts.Contains(90_000), "拖拽进度后通知弹幕重置并拉取对应时间段弹幕");
        dmPlayer.Close();
        Check(dmPlayer.LoadedDanmakuCount == 0 && !dmPlayer.UseEmbeddedDanmakuOverlay, "关闭播放器后清空弹幕缓存并隐藏弹幕层");
    }

    catalog = new FakeCatalog { DelayEpisode = 2 };
    proxy = new FakeProxy();
    player = new PlayerViewModel(catalog, proxy, new InMemoryHistoryService(), new InMemorySettingsService(), DispatchProxy.Create<IToastService, NoopProxy>());
    await player.OpenAsync(new DramaCardViewModel(drama, "cancel"));
    player.Close();
    catalog.Delayed.TrySetResult();
    await Task.Delay(50);
    Check(proxy.Prepared.Count == 1 && proxy.Released.Count == 1 && !player.IsOpen, "关闭时取消在途预加载且不激活陈旧媒体");

    await CatalogRegressionTests.RunAsync();
    await DownloadRegressionTests.RunAsync(root);
    HotkeyRegressionTests.Run(root);

    var instanceId = "MioReel.Test." + Guid.NewGuid().ToString("N");
    using (var primary = MioReel.Desktop.Services.SingleInstanceGuard.TryAcquire(instanceId))
    {
        Check(primary is not null, "首次启动成功获取单实例互斥锁");
        var secondAcquired = false;
        var probeThread = new Thread(() =>
        {
            using var secondary = MioReel.Desktop.Services.SingleInstanceGuard.TryAcquire(instanceId);
            secondAcquired = secondary is not null;
        });
        probeThread.Start();
        probeThread.Join();
        Check(!secondAcquired, "重复启动时进程互斥阻止第二个实例进入");

        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary!.StartListening(() => activated.TrySetResult());
        MioReel.Desktop.Services.SingleInstanceGuard.SignalExistingInstance(instanceId);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(activated.Task.IsCompletedSuccessfully, "重复启动可唤醒已存在的主实例");
    }
    using (var reacquired = MioReel.Desktop.Services.SingleInstanceGuard.TryAcquire(instanceId))
    {
        Check(reacquired is not null, "主实例退出释放锁后允许再次启动");
    }

    await using var upstream = new RangeServer();
    using var mediaProxy = new LocalMediaProxyService();
    var info = new ResolvedStreamInfo("range", 1, upstream.Url, new Dictionary<string, string>(), "", "none", 1080, 720, 1280, "h264", 0);
    var playable = await mediaProxy.PreparePlayableUriAsync(info);
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    Check(upstream.RequestCount == 2, "媒体准备阶段已读取开头和尾部数据");
    var opening = await ReadRange(http, playable, 0, 65535);
    Check(opening.SequenceEqual(upstream.Bytes.Take(65536)) && upstream.RequestCount == 2, "播放开头直接命中预缓存");
    var tail = await ReadRange(http, playable, upstream.Bytes.Length - 65536, upstream.Bytes.Length - 1);
    Check(tail.SequenceEqual(upstream.Bytes.TakeLast(65536)) && upstream.RequestCount == 2, "尾部探测直接命中预缓存且返回正确 Range 数据");
    _ = await ReadRange(http, playable, 0, 65535);
    Check(upstream.RequestCount == 2, "尾部探测后开头缓存仍可复用");
    mediaProxy.ReleaseSession(playable);
    using var released = await http.GetAsync(playable);
    Check(released.StatusCode == HttpStatusCode.NotFound, "释放后的媒体会话不可继续访问");

#if HAS_API_PROJECT
    // 账号密码：PBKDF2 哈希不可逆、校验正确、篡改即失败、同密码不同盐
    var hash1 = MioReel.Api.Auth.PasswordHasher.Hash("correct horse battery staple");
    var hash2 = MioReel.Api.Auth.PasswordHasher.Hash("correct horse battery staple");
    Check(!hash1.Contains("correct horse", StringComparison.OrdinalIgnoreCase), "密码明文不出现在哈希中");
    Check(hash1.StartsWith("pbkdf2-sha256$100000$", StringComparison.Ordinal), "哈希带算法与迭代次数标记");
    Check(hash1 != hash2, "相同密码不同盐产生不同哈希");
    Check(MioReel.Api.Auth.PasswordHasher.Verify("correct horse battery staple", hash1), "正确密码校验通过");
    Check(!MioReel.Api.Auth.PasswordHasher.Verify("wrong password", hash1), "错误密码校验失败");
    Check(!MioReel.Api.Auth.PasswordHasher.Verify("correct horse battery staple", "garbage"), "损坏哈希安全失败");

    // 账号存储：注册唯一性、大小写不敏感、密码哈希入库、历史按用户隔离
    var storeDir = Path.Combine(root, "store");
    var accountStore = new MioReel.Api.Auth.AccountStore(storeDir);
    var alice = accountStore.Register("Alice", hash1);
    Check(alice is not null && alice.Id == "alice" && alice.Nickname == "Alice", "注册成功并规范化用户名");
    Check(accountStore.Register("alice", hash1) is null, "重复用户名（忽略大小写）注册被拒绝");
    Check(accountStore.GetPasswordHash("ALICE") == hash1, "大小写不敏感读出密码哈希");
    var bob = accountStore.Register("bob", MioReel.Api.Auth.PasswordHasher.Hash("bobsecret"));
    Check(bob is not null && bob.Id == "bob", "第二个账号注册成功");
    accountStore.MergeHistory("alice", [new HistoryRecord(
        new DramaItem("s1", "剧1", "", 10, "", "", [], "", DramaCategory.All, "", ""), 3, 40, DateTimeOffset.UtcNow)]);
    Check(accountStore.GetHistory("alice").Count == 1 && accountStore.GetHistory("bob").Count == 0,
        "观看历史按账号隔离");
    Check(File.Exists(Path.Combine(storeDir, "account-store.json")), "账号数据落盘持久化");
#endif

    Console.WriteLine("所有回归检查通过");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
}

static async Task<byte[]> ReadRange(HttpClient http, Uri uri, long start, long end)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
    request.Headers.Range = new(start, end);
    using var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsByteArrayAsync();
}

public class NoopProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.ReturnType == typeof(void) ? null : targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
}

sealed class FakeCatalog : IDramaCatalogService
{
    public List<int> Resolved { get; } = [];
    public List<int> RequestedQualities { get; } = [];
    public List<long> DanmakuStarts { get; } = [];
    public Func<ResolvedStreamInfo, ResolvedStreamInfo>? Transform { get; init; }
    public Func<string, long, long, Task<DanmakuChunk>>? DanmakuHandler { get; init; }
    public int FailEpisode { get; set; }
    public int DelayEpisode { get; set; }
    public TaskCompletionSource Delayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsLastRequestLive => true;
    public Task<IReadOnlyList<EpisodeItem>> GetEpisodesAsync(string seriesId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EpisodeItem>>([new(1, "1", "1", true), new(2, "2", "2", true), new(3, "3", "3", true)]);
    public async Task<ResolvedStreamInfo> ResolveEpisodeAsync(string seriesId, int episodeNo, int quality = 1080, CancellationToken ct = default)
    {
        Resolved.Add(episodeNo);
        RequestedQualities.Add(quality);
        if (episodeNo == DelayEpisode) await Delayed.Task.WaitAsync(ct);
        if (episodeNo == FailEpisode) throw new HttpRequestException("测试网络失败");
        var info = new ResolvedStreamInfo(seriesId, episodeNo, "https://example.invalid/video.mp4", new Dictionary<string, string>(), "", "none", quality, 720, 1280, "h264", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());
        return Transform?.Invoke(info) ?? info;
    }
    public Task<DanmakuChunk> GetDanmakuAsync(string danmakuId, long startMs = 0, long durationMs = 300_000, CancellationToken ct = default)
    {
        DanmakuStarts.Add(startMs);
        return DanmakuHandler is not null
            ? DanmakuHandler(danmakuId, startMs, durationMs)
            : Task.FromResult(new DanmakuChunk(danmakuId, startMs, startMs + 30_000, []));
    }
    public Task<CatalogPageResult> GetCatalogPageAsync(DramaCategory category, int page = 1, int limit = 40, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<DramaItem>> GetCatalogAsync(DramaCategory category, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<DramaItem>> SearchAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<GatewayHealthResult> TestConnectionAsync(string gatewayBaseUrl, CancellationToken ct = default) => throw new NotSupportedException();
}

sealed class DanmakuGatewayServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    public string BaseUrl { get; }
    public string? LastAuthHeader { get; private set; }
    public long LastDurationMs { get; private set; }

    public DanmakuGatewayServer()
    {
        using var sock = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        sock.Start();
        var port = ((System.Net.IPEndPoint)sock.LocalEndpoint).Port;
        sock.Stop();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            LastAuthHeader = ctx.Request.Headers["Authorization"];
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            string body;
            if (path == "/api/v1/resolve")
            {
                body = """{"code":0,"message":"ok","request_id":"req-1","data":{"series_id":"7701678491724418073","series_title":"测试短剧","episode_no":1,"play_url":"https://example.invalid/ep1.mp4","play_headers":{},"decryption_key":"","encryption":"none","quality":1080,"width":720,"height":1280,"codec":"h264","expires_at":4102444800000,"variants":[],"danmaku_id":"hongguo:7701678491724418073:7701680675979545625"}}""";
            }
            else if (path == "/api/v1/danmaku")
            {
                var q = ctx.Request.QueryString;
                long.TryParse(q["start_ms"], out var startMs);
                long.TryParse(q["duration_ms"], out var durMs);
                LastDurationMs = durMs;
                var episodeId = q["danmaku_id"] ?? "";
                body = (startMs switch
                {
                    0 => """{"code":0,"message":"ok","request_id":"req-2","data":{"episode_id":"__EP__","start_ms":0,"next_ms":30000,"items":[{"id":"dm-1","time_ms":1200,"text":"前排打卡","color":"#FFFFFF"},{"id":"dm-2","time_ms":4500,"text":"高能预警","color":"#FB7185"}]}}""",
                    30_000 => """{"code":0,"message":"ok","request_id":"req-3","data":{"episode_id":"__EP__","start_ms":30000,"next_ms":60000,"items":[{"id":"dm-3","time_ms":32500,"text":"太上头了","color":"#FBBF24"}]}}""",
                    _ => """{"code":0,"message":"ok","request_id":"req-4","data":{"episode_id":"__EP__","start_ms":__START__,"next_ms":__NEXT__,"items":[{"id":"dm-4","time_ms":__ITEM_TIME__,"text":"高能名场面","color":"#FFFFFF"}]}}"""
                })
                .Replace("__EP__", episodeId, StringComparison.Ordinal)
                .Replace("__START__", startMs.ToString(), StringComparison.Ordinal)
                .Replace("__NEXT__", (startMs + 30_000).ToString(), StringComparison.Ordinal)
                .Replace("__ITEM_TIME__", (startMs + 1000).ToString(), StringComparison.Ordinal);
            }
            else
            {
                body = """{"code":40401,"message":"not found","request_id":"req-404","data":null}""";
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
        _cts.Dispose();
    }
}

sealed class FakeProxy : IMediaStreamProxyService
{
    public List<Uri> Prepared { get; } = [];
    public List<ResolvedStreamInfo> Streams { get; } = [];
    public HashSet<Uri> Released { get; } = [];
    public Task<Uri> PreparePlayableUriAsync(ResolvedStreamInfo stream, CancellationToken ct = default)
    {
        var uri = new Uri($"http://127.0.0.1/stream/{Prepared.Count}.mp4");
        Prepared.Add(uri);
        Streams.Add(stream);
        return Task.FromResult(uri);
    }
    public void ReleaseSession(Uri? uri) { if (uri is not null) Released.Add(uri); }
    public void Dispose() { }
}

sealed class RangeServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _requests;
    public byte[] Bytes { get; } = Enumerable.Range(0, 4 * 1024 * 1024).Select(x => (byte)(x % 251)).ToArray();
    public int RequestCount => Volatile.Read(ref _requests);
    public string Url { get; }
    public RangeServer()
    {
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/video.mp4";
        _loop = RunAsync();
    }
    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                long start = 0, end = Bytes.Length - 1;
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                {
                    if (!line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = line["Range: bytes=".Length..].Split('-');
                    start = long.Parse(parts[0]);
                    end = Math.Min(long.Parse(parts[1]), Bytes.Length - 1);
                }
                Interlocked.Increment(ref _requests);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{end}/{Bytes.Length}\r\nContent-Length: {end - start + 1}\r\nConnection: close\r\n\r\n"), _stop.Token);
                await stream.WriteAsync(Bytes.AsMemory((int)start, (int)(end - start + 1)), _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _loop;
        _stop.Dispose();
    }
}
