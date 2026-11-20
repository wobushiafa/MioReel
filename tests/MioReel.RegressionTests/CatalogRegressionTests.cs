using System.Reflection;
using Aviora.Presentation.Dialogs;
using Aviora.Presentation.Loadings;
using Aviora.Presentation.Toasts;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;
using MioReel.Desktop.ViewModels;
using MioReel.Infrastructure.Services;
using ReactiveUI.Primitives;
using MioReel.Desktop.Services;
using MioReel.Desktop.Services.Hotkeys;
using Avalonia;

internal static class CatalogRegressionTests
{
    public static async Task RunAsync()
    {
        Avalonia.Skia.SkiaPlatform.Initialize();
        Assert(CatalogPrefetchPolicy.ShouldPrefetch(3000, 600, 800), "片库剩余两屏时提前加载");
        Assert(!CatalogPrefetchPolicy.ShouldPrefetch(6000, 600, 800), "远离阈值时不连续拉取全部片库");
        Assert(CatalogPrefetchPolicy.ShouldPrefetch(300, 0, 800), "片库不满一屏时主动补足数据");
        var gate = new CatalogPrefetchGate();
        Assert(gate.TryStart(4000, 3000, 800), "首次进入预加载阈值允许加载一次");
        Assert(Enumerable.Range(1, 100).All(i => !gate.TryStart(4000 + i * 100, 3000 + i * 100, 800)),
            "停在底部时列表增长和滚动锚定不会连续请求");
        gate.RequestLoad();
        Assert(gate.TryStart(5000, 4000, 800) && !gate.TryStart(6000, 5000, 800), "新的滚动操作只允许一次后续加载");
        gate.RequestLoad();
        gate.RequestLoad();
        Assert(gate.TryStart(5000, 4000, 800) && !gate.TryStart(5000, 4000, 800), "加载中的多个滚动输入合并为一个待处理请求");
        gate.RequestLoad();
        Assert(!gate.TryStart(10000, 0, 800) && gate.TryStart(10000, 8500, 800), "提前收到滚动输入仍需达到阈值才加载");
        gate.Reset();
        Assert(gate.TryStart(4000, 3000, 800), "刷新片库后恢复一次初始预加载机会");
        var settings = new InMemorySettingsService();
        settings.Update(settings.Current with
        {
            NormalWindow = new WindowPlacement(100, 120, 1260, 780),
            MiniWindow = new WindowPlacement(1500, 240, 360, 640)
        });
        var source = new CatalogSource((_, page, _) => Task.FromResult(new CatalogPageResult(
            page == 1 ? Items(1, 60).Concat(Items(1, 3)).ToList() : Items(50, 30), page == 1, true)));
        var vm = Create(source);
        await WaitUntil(() => !vm.IsLoadingCatalog);
        Assert(vm.ExploreCards.Count == 60, "片库首屏过滤同页重复剧目");
        await vm.LoadMoreCatalogCommand.Execute().ToTask();
        Assert(vm.ExploreCards.Count == 79, "分页过滤跨页重叠剧目");
        AssertRows(vm, "分页追加后显示行和剧目列表一致");
        vm.UpdateExploreColumns(600);
        AssertRows(vm, "窗口列数变化后没有重复或遗漏");

        var pendingPage = new TaskCompletionSource<CatalogPageResult>();
        source = new CatalogSource((_, page, _) => page == 1
            ? Task.FromResult(new CatalogPageResult(Items(1, 30), true, true))
            : pendingPage.Task);
        vm = Create(source);
        await WaitUntil(() => source.Requests.Count == 2);
        var more = vm.LoadMoreCatalogCommand.Execute().ToTask();
        Assert(source.Requests.Count(x => x.Page == 2) == 1, "首屏预取期间加载更多不会重复请求同页");
        pendingPage.SetResult(new CatalogPageResult(Items(25, 30), false, true));
        await more;
        await WaitUntil(() => !vm.IsLoadingMoreCatalog && !vm.IsLoadingCatalog);
        Assert(vm.ExploreCards.Count == 54, "自动预取与加载更多交错时剧目保持唯一");
        AssertRows(vm, "自动预取后显示行没有重复剧目");

        var stalePage = new TaskCompletionSource<CatalogPageResult>();
        var refreshedPage = new TaskCompletionSource<CatalogPageResult>();
        var firstPageRequests = 0;
        source = new CatalogSource((_, page, _) => page == 2 ? stalePage.Task
            : ++firstPageRequests == 1
                ? Task.FromResult(new CatalogPageResult(Items(1, 60), true, true))
                : refreshedPage.Task);
        vm = Create(source);
        more = vm.LoadMoreCatalogCommand.Execute().ToTask();
        await vm.LoadMoreCatalogCommand.Execute().ToTask();
        Assert(source.Requests.Count(x => x.Page == 2) == 1, "连续触发加载更多只发送一次分页请求");
        var refresh = vm.RefreshCatalogCommand.Execute().ToTask();
        stalePage.SetResult(new CatalogPageResult(Items(50, 30), true, true));
        await more;
        Assert(vm.IsLoadingCatalog && vm.IsLoadingMoreCatalog, "过期分页请求结束不会解除刷新中的加载锁");
        await vm.LoadMoreCatalogCommand.Execute().ToTask();
        Assert(source.Requests.Count == 3, "刷新期间不会启动额外分页请求");
        refreshedPage.SetResult(new CatalogPageResult(Items(101, 60), false, true));
        await refresh;
        Assert(vm.ExploreCards.Select(x => x.Drama.SeriesId).SequenceEqual(Items(101, 60).Select(x => x.SeriesId)), "刷新后丢弃旧分页响应");
        AssertRows(vm, "刷新后的显示行只包含新列表");
        vm = Create(source, settings);
        vm.DefaultQuality = 720;
        Assert(settings.Current.NormalWindow?.X == 100 && settings.Current.MiniWindow?.X == 1500, "修改画质设置不会覆盖大小窗各自的位置记录");
        var fitted = WindowPlacementPolicy.Fit(new WindowPlacement(4000, -900, 360, 640), new PixelRect(0, 0, 1920, 1080), 1, true);
        Assert(fitted.X == 1560 && fitted.Y == 0, "显示器变化后小窗还原到可见工作区");
        fitted = WindowPlacementPolicy.Fit(new WindowPlacement(-1800, 100, 900, 600), new PixelRect(-1920, 0, 1920, 1080), 1.5, false);
        Assert(fitted.X == -1800 && fitted.Y == 100, "窗口位置兼容负坐标及不同缩放比例");

        var workArea = new PixelRect(0, 0, 1920, 1080);
        var bounds16x9 = WindowPlacementPolicy.GetMiniSizeBounds(workArea, 1.0, 16.0 / 9.0);
        Assert(bounds16x9.MinHeight == 162 && bounds16x9.MinWidth == 288, "横屏小窗最小尺寸按16:9等比缩放");
        var bounds9x16 = WindowPlacementPolicy.GetMiniSizeBounds(workArea, 1.0, 9.0 / 16.0);
        Assert(bounds9x16.MinWidth == 288 && bounds9x16.MinHeight == 512, "竖屏小窗最小尺寸按9:16等比缩放");

        var miniLandscape = WindowPlacementPolicy.FitMiniForAspect(
            new WindowPlacement(1500, 240, 360, 640), new PixelPoint(100, 100), null, workArea, 1.0, 1920, 1080);
        Assert(miniLandscape.Width == 640 && miniLandscape.Height == 360 && miniLandscape.X == 1280, "竖屏小窗切横屏视频时按视频比例重构尺寸并贴齐工作区");

        var scaledUp = WindowPlacementPolicy.ScaleMiniPlacement(miniLandscape, 1.25, workArea, 1.0, 1920, 1080);
        Assert(scaledUp.Width == 800 && scaledUp.Height == 450, "小窗放大保持视频16:9尺寸比例");
        var scaledDown = WindowPlacementPolicy.ScaleMiniPlacement(miniLandscape, 0.2, workArea, 1.0, 1920, 1080);
        Assert(scaledDown.Width == 288 && scaledDown.Height == 162, "小窗缩小到最小边界仍保持16:9比例");

        var snappedRight = WindowPlacementPolicy.SnapMiniDimensions(
            480, 600, WindowPlacementPolicy.EdgeRight, 288, 512, 607, 1080, 9.0 / 16.0);
        Assert(snappedRight.ClientWidth == 480 && snappedRight.ClientHeight == 853, "拖动小窗右边沿按竖屏视频比例联动高度");
        var snappedTop = WindowPlacementPolicy.SnapMiniDimensions(
            400, 270, WindowPlacementPolicy.EdgeTop, 288, 162, 1920, 1080, 16.0 / 9.0);
        Assert(snappedTop.ClientHeight == 270 && snappedTop.ClientWidth == 480, "拖动小窗上边沿按横屏视频比例联动宽度");
        var corner1 = WindowPlacementPolicy.SnapMiniDimensions(
            370, 642, WindowPlacementPolicy.EdgeBottomRight, 288, 512, 607, 1080, 9.0 / 16.0);
        var corner2 = WindowPlacementPolicy.SnapMiniDimensions(
            371, 643, WindowPlacementPolicy.EdgeBottomRight, 288, 512, 607, 1080, 9.0 / 16.0);
        Assert(corner2.ClientWidth >= corner1.ClientWidth && corner2.ClientHeight >= corner1.ClientHeight, "拖动小窗角点连续放大保持单调递增不抖动");
    }

    private static MainWindowViewModel Create(IDramaCatalogService source, ISettingsService? settingsOverride = null)
    {
        var history = new InMemoryHistoryService();
        var settings = settingsOverride ?? new InMemorySettingsService();
        var toast = DispatchProxy.Create<IToastService, NoopProxy>();
        var player = new PlayerViewModel(source, new ProxyStub(), history, settings, toast);
        return new MainWindowViewModel(source, history, new InMemoryDownloadService(), settings,
            DispatchProxy.Create<IDialogService, NoopProxy>(), DispatchProxy.Create<ILoadingService, NoopProxy>(), toast,
            new NullGlobalHotkeyService("test"), player, new StubAuthService(), new StubHistorySyncService());
    }

    private static List<DramaItem> Items(int first, int count) => Enumerable.Range(first, count)
        .Select(i => new DramaItem($"series-{i}", $"剧目 {i}", "", 3, "", "", [], "2026", DramaCategory.All, "#000000", "#FFFFFF")).ToList();

    private static void AssertRows(MainWindowViewModel vm, string description) => Assert(
        vm.ExploreRows.SelectMany(x => x.Items).SequenceEqual(vm.ExploreCards)
        && vm.ExploreCards.Select(x => x.Drama.SeriesId).Distinct().Count() == vm.ExploreCards.Count, description);

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class ProxyStub : IMediaStreamProxyService
    {
        public Task<Uri> PreparePlayableUriAsync(ResolvedStreamInfo stream, CancellationToken ct = default) => throw new NotSupportedException();
        public void ReleaseSession(Uri? uri) { }
        public void Dispose() { }
    }

    private sealed class StubAuthService : IAuthService
    {
        public UserProfile? CurrentUser => null;
        public string? AccessToken => null;
        public bool IsSignedIn => false;
        public event Action? AuthStateChanged { add { } remove { } }
        public Task<AuthResult> LoginAsync(string username, string password, CancellationToken ct = default) =>
            Task.FromResult(new AuthResult("token", new UserProfile(username, username, null, DateTimeOffset.UtcNow)));
        public Task<AuthResult> RegisterAsync(string username, string password, CancellationToken ct = default) =>
            Task.FromResult(new AuthResult("token", new UserProfile(username, username, null, DateTimeOffset.UtcNow)));
        public Task RestoreAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void SignOut() { }
    }

    private sealed class StubHistorySyncService : IHistorySyncService
    {
        public bool IsEnabled => false;
        public Task<HistorySyncResult> UploadAllAsync(IReadOnlyList<HistoryRecord> local, CancellationToken ct = default) =>
            Task.FromResult(new HistorySyncResult(local, DateTimeOffset.UtcNow));
        public Task<HistorySyncResult> DownloadAllAsync(CancellationToken ct = default) =>
            Task.FromResult(new HistorySyncResult([], DateTimeOffset.UtcNow));
        public Task PushOneAsync(HistoryRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveOneAsync(string seriesId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CatalogSource(Func<DramaCategory, int, CancellationToken, Task<CatalogPageResult>> load) : IDramaCatalogService
    {
        public List<(DramaCategory Category, int Page)> Requests { get; } = [];
        public bool IsLastRequestLive => true;
        public Task<CatalogPageResult> GetCatalogPageAsync(DramaCategory category, int page = 1, int limit = 40, CancellationToken ct = default)
        {
            Requests.Add((category, page));
            return load(category, page, ct);
        }
        public Task<IReadOnlyList<DramaItem>> GetCatalogAsync(DramaCategory category, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DramaItem>> SearchAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EpisodeItem>> GetEpisodesAsync(string seriesId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ResolvedStreamInfo> ResolveEpisodeAsync(string seriesId, int episodeNo, int quality = 1080, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GatewayHealthResult> TestConnectionAsync(string gatewayBaseUrl, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
