using System.Collections.ObjectModel;
using Aviora.Presentation.Dialogs;
using Aviora.Presentation.Loadings;
using Aviora.Presentation.Toasts;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;
using MioReel.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace MioReel.Desktop.ViewModels;

public sealed class MainWindowViewModel : ReactiveObject
{
    private readonly IDramaCatalogService _catalog;
    private readonly IHistoryService _history;
    private readonly IDownloadService _downloads;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILoadingService _loading;
    private readonly IToastService _toast;
    private readonly IGlobalHotkeyService _globalHotkeys;
    private readonly IAuthService _auth;
    private readonly IHistorySyncService _historySync;

    private CancellationTokenSource? _searchDebounceCts;
    private CancellationTokenSource? _catalogCts;
    private CancellationTokenSource? _searchRequestCts;
    private CancellationTokenSource? _heroDisarmCts;
    private NavSection _currentSection = NavSection.Explore;
    private NavItemViewModel _selectedNavItem = null!;
    private WindowLayoutMode _layoutMode = WindowLayoutMode.Wide;
    private DramaCategory _selectedCategory = DramaCategory.All;
    private DramaCardViewModel? _spotlightDrama;
    private DramaCardViewModel? _resumeDrama;
    private string _searchQuery = string.Empty;
    private int _selectedDownloadTab;
    private bool _isDarkTheme = true;
    private bool _miniAlwaysOnTop = true;
    private int _defaultQuality = 1080;
    private bool _autoPlayNext = true;
    private string _downloadDirectory = string.Empty;
    private string _gatewayBaseUrl = string.Empty;
    private string _gatewayToken = string.Empty;
    private int _exploreColumns = 5;
    private int _searchColumns = 2;
    private int _historyColumns = 2;
    private int _catalogPage = 1;
    private bool _hasMoreCatalog;
    private bool _isLoadingCatalog;
    private bool _isLoadingMoreCatalog;
    private bool _isLiveSource;
    private string? _catalogErrorMessage;
    private GatewayFailureKind _catalogFailureKind = GatewayFailureKind.None;
    private bool _isTokenVisible;
    private bool _isHeroFlightEnabled;
    private bool _pendingHistoryRefresh;
    private bool _isSearching;
    private string? _searchErrorMessage;
    private HotkeySettings _hotkeys = HotkeySettings.CreateDefaults();
    private HotkeyAction? _activeRecordingAction;
    private string _accountApiBaseUrl = string.Empty;
    private bool _isSyncing;
    private long _lastCloudPushTick;

    public MainWindowViewModel(
        IDramaCatalogService catalog,
        IHistoryService history,
        IDownloadService downloads,
        ISettingsService settings,
        IDialogService dialogs,
        ILoadingService loading,
        IToastService toast,
        IGlobalHotkeyService globalHotkeys,
        PlayerViewModel player,
        IAuthService auth,
        IHistorySyncService historySync)
    {
        _catalog = catalog;
        _history = history;
        _downloads = downloads;
        _settings = settings;
        _dialogs = dialogs;
        _loading = loading;
        _toast = toast;
        _globalHotkeys = globalHotkeys;
        _auth = auth;
        _historySync = historySync;
        Player = player;
        Player.HistoryUpdated += OnPlayerHistoryUpdated;
        Player.ClosingRequested += OnPlayerClosingRequested;

        var s = settings.Current;
        _isDarkTheme = s.Theme == AppThemePreference.Dark;
        _miniAlwaysOnTop = s.MiniWindowAlwaysOnTop;
        _defaultQuality = s.DefaultQuality;
        _autoPlayNext = s.AutoPlayNextEpisode;
        _downloadDirectory = s.DownloadDirectory;
        _gatewayBaseUrl = s.GatewayBaseUrl;
        _gatewayToken = s.GatewayToken;
        _accountApiBaseUrl = s.AccountApiBaseUrl;
        _hotkeys = (s.Hotkeys ?? HotkeySettings.CreateDefaults()).EnsurePopulated();
        BuildHotkeyRows();

        _auth.AuthStateChanged += OnAuthStateChanged;

        RecentSearches = [];
        NavItems =
        [
            new(NavSection.Explore, "片库", "片库", "M10.75 3.45L3.75 9.1C3.28 9.48 3 10.05 3 10.66V18.8C3 19.9 3.9 20.8 5 20.8H8.6C9.15 20.8 9.6 20.35 9.6 19.8V15.4C9.6 14.52 10.32 13.8 11.2 13.8H12.8C13.68 13.8 14.4 14.52 14.4 15.4V19.8C14.4 20.35 14.85 20.8 15.4 20.8H19C20.1 20.8 21 19.9 21 18.8V10.66C21 10.05 20.72 9.48 20.25 9.1L13.25 3.45C12.52 2.86 11.48 2.86 10.75 3.45Z"),
            new(NavSection.Search, "搜索", "搜索", "M10.5 3.7C6.74 3.7 3.7 6.74 3.7 10.5C3.7 14.26 6.74 17.3 10.5 17.3C12.1 17.3 13.57 16.75 14.74 15.83L18.59 19.68C18.98 20.07 19.61 20.07 20 19.68C20.39 19.29 20.39 18.66 20 18.27L16.15 14.42C16.87 13.3 17.3 11.95 17.3 10.5C17.3 6.74 14.26 3.7 10.5 3.7ZM10.5 5.6C13.21 5.6 15.4 7.79 15.4 10.5C15.4 13.21 13.21 15.4 10.5 15.4C7.79 15.4 5.6 13.21 5.6 10.5C5.6 7.79 7.79 5.6 10.5 5.6Z"),
            new(NavSection.History, "浏览历史", "历史", "M12.5 3.2C8.05 3.2 4.38 6.53 3.84 10.83L2.75 9.74C2.36 9.35 1.73 9.35 1.34 9.74C0.95 10.13 0.95 10.76 1.34 11.15L4.04 13.85C4.43 14.24 5.06 14.24 5.45 13.85L8.15 11.15C8.54 10.76 8.54 10.13 8.15 9.74C7.76 9.35 7.13 9.35 6.74 9.74L5.83 10.65C6.45 7.54 9.2 5.2 12.5 5.2C16.26 5.2 19.3 8.24 19.3 12C19.3 15.76 16.26 18.8 12.5 18.8C10.81 18.8 9.27 18.18 8.08 17.16C7.66 16.8 7.03 16.85 6.67 17.27C6.31 17.69 6.36 18.32 6.78 18.68C8.32 20 10.32 20.8 12.5 20.8C17.36 20.8 21.3 16.86 21.3 12C21.3 7.14 17.36 3.2 12.5 3.2ZM12.5 7.8C11.95 7.8 11.5 8.25 11.5 8.8V12.4C11.5 12.76 11.69 13.09 12 13.27L14.8 14.87C15.28 15.14 15.89 14.97 16.16 14.49C16.43 14.01 16.26 13.4 15.78 13.13L13.5 11.82V8.8C13.5 8.25 13.05 7.8 12.5 7.8Z"),
            new(NavSection.Downloads, "离线下载", "下载", "M12 3.2C11.45 3.2 11 3.65 11 4.2V12.6L8.5 10.1C8.11 9.71 7.48 9.71 7.09 10.1C6.7 10.49 6.7 11.12 7.09 11.51L11.29 15.71C11.68 16.1 12.32 16.1 12.71 15.71L16.91 11.51C17.3 11.12 17.3 10.49 16.91 10.1C16.52 9.71 15.89 9.71 15.5 10.1L13 12.6V4.2C13 3.65 12.55 3.2 12 3.2ZM4.2 14.8C3.65 14.8 3.2 15.25 3.2 15.8V17.6C3.2 19.37 4.63 20.8 6.4 20.8H17.6C19.37 20.8 20.8 19.37 20.8 17.6V15.8C20.8 15.25 20.35 14.8 19.8 14.8C19.25 14.8 18.8 15.25 18.8 15.8V17.6C18.8 18.26 18.26 18.8 17.6 18.8H6.4C5.74 18.8 5.2 18.26 5.2 17.6V15.8C5.2 15.25 4.75 14.8 4.2 14.8Z"),
            new(NavSection.Settings, "设置", "设置", "M19.14 12.94C19.18 12.64 19.2 12.33 19.2 12C19.2 11.68 19.18 11.36 19.13 11.06L21.16 9.48C21.34 9.34 21.39 9.07 21.28 8.87L19.36 5.55C19.24 5.33 18.99 5.26 18.77 5.33L16.38 6.29C15.88 5.91 15.35 5.59 14.76 5.35L14.4 2.81C14.36 2.57 14.16 2.4 13.92 2.4H10.08C9.84 2.4 9.65 2.57 9.61 2.81L9.25 5.35C8.66 5.59 8.12 5.92 7.63 6.29L5.24 5.33C5.02 5.25 4.77 5.33 4.65 5.55L2.74 8.87C2.62 9.08 2.66 9.34 2.86 9.48L4.89 11.06C4.84 11.36 4.8 11.69 4.8 12C4.8 12.31 4.82 12.64 4.87 12.94L2.84 14.52C2.66 14.66 2.61 14.93 2.72 15.13L4.64 18.45C4.76 18.67 5.01 18.74 5.23 18.67L7.62 17.71C8.12 18.09 8.65 18.41 9.24 18.65L9.6 21.19C9.65 21.43 9.84 21.6 10.08 21.6H13.92C14.16 21.6 14.36 21.43 14.39 21.19L14.75 18.65C15.34 18.41 15.88 18.09 16.37 17.71L18.76 18.67C18.98 18.75 19.23 18.67 19.35 18.45L21.27 15.13C21.39 14.91 21.34 14.66 21.15 14.52L19.14 12.94ZM12 15.6C10.02 15.6 8.4 13.98 8.4 12C8.4 10.02 10.02 8.4 12 8.4C13.98 8.4 15.6 10.02 15.6 12C15.6 13.98 13.98 15.6 12 15.6Z")
        ];
        _selectedNavItem = NavItems[0];

        NavigateCommand = ReactiveCommand.Create<NavSection>(section => CurrentSection = section);
        SelectCategoryCommand = ReactiveCommand.CreateFromTask<DramaCategory>(SelectCategoryAsync);
        OpenDramaCommand = ReactiveCommand.CreateFromTask<DramaCardViewModel>(OpenDramaWithHeroAsync);
        OpenBatchDownloadCommand = ReactiveCommand.CreateFromTask<DramaItem>(OpenBatchDownloadDialogAsync);
        ExecuteSearchCommand = ReactiveCommand.CreateFromTask(ExecuteSearchAsync);
        ApplySearchTagCommand = ReactiveCommand.CreateFromTask<string>(async tag =>
        {
            SearchQuery = tag;
            await ExecuteSearchAsync();
        });
        ClearRecentSearchCommand = ReactiveCommand.Create(() =>
        {
            RecentSearches.Clear();
            this.RaisePropertyChanged(nameof(HasRecentSearches));
        });
        RemoveHistoryCommand = ReactiveCommand.Create<string>(seriesId =>
        {
            _history.Remove(seriesId);
            RefreshHistory();
            if (_historySync.IsEnabled)
                _ = FireAndForgetCloudAsync(() => _historySync.RemoveOneAsync(seriesId));
        });
        ClearHistoryCommand = ReactiveCommand.CreateFromTask(ConfirmClearHistoryAsync);
        SelectDownloadTabCommand = ReactiveCommand.Create<string>(tab =>
        {
            SelectedDownloadTab = tab == "done" ? 1 : 0;
            RefreshDownloads();
        });
        ToggleDownloadPauseCommand = ReactiveCommand.Create<string>(taskId =>
        {
            try { _downloads.TogglePause(taskId); }
            catch (Exception ex) { _toast.ShowError($"无法更新下载任务：{ex.Message}"); }
            RefreshDownloads();
        });
        RemoveDownloadCommand = ReactiveCommand.Create<string>(taskId =>
        {
            try { _downloads.Remove(taskId); }
            catch (Exception ex) { _toast.ShowError($"无法移除下载任务：{ex.Message}"); }
            RefreshDownloads();
        });
        SetThemeCommand = ReactiveCommand.Create<bool>(dark =>
        {
            IsDarkTheme = dark;
            SaveSettings();
            ThemeChanged?.Invoke(dark);
        });
        SetDefaultQualityCommand = ReactiveCommand.Create<string>(q =>
        {
            if (int.TryParse(q, out var val))
                DefaultQuality = val;
        });
        SetRenderModeCommand = ReactiveCommand.Create<VideoRenderMode>(mode => Player.SetRenderMode(mode));
        TestGatewayCommand = ReactiveCommand.CreateFromTask(TestGatewayAsync);
        ToggleTokenVisibilityCommand = ReactiveCommand.Create(() => { IsTokenVisible = !IsTokenVisible; });
        RefreshCatalogCommand = ReactiveCommand.CreateFromTask(() => SelectCategoryAsync(SelectedCategory));
        LoadMoreCatalogCommand = ReactiveCommand.CreateFromTask(LoadMoreCatalogAsync);
        OpenSettingsCommand = ReactiveCommand.Create(() => { CurrentSection = NavSection.Settings; });
        ClearTempCacheCommand = ReactiveCommand.Create(() => { _toast.ShowSuccess("已清理临时缓存"); });
        BeginRecordHotkeyCommand = ReactiveCommand.Create<HotkeyAction>(BeginRecordHotkey);
        ClearHotkeyCommand = ReactiveCommand.Create<HotkeyAction>(ClearHotkey);
        ResetHotkeysCommand = ReactiveCommand.Create(ResetHotkeys);
        OpenLoginCommand = ReactiveCommand.Create(OpenLoginWindow);
        SignOutCommand = ReactiveCommand.Create(SignOut);
        SyncNowCommand = ReactiveCommand.CreateFromTask(SyncNowAsync);

        _globalHotkeys.StatusChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshHotkeyStatuses);
        _globalHotkeys.ApplyBindings(_hotkeys.GlobalHotkeysEnabled, _hotkeys.GetGlobalBindings());

        Player.WhenAnyValue(x => x.IsOpen)
            .Subscribe(isOpen =>
            {
                if (!isOpen)
                    ScheduleHeroDisarm();
            });
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.RenderMode))
            {
                this.RaisePropertyChanged(nameof(IsRenderAuto));
                this.RaisePropertyChanged(nameof(IsRenderGpu));
                this.RaisePropertyChanged(nameof(IsRenderSoftware));
                this.RaisePropertyChanged(nameof(IsRenderNative));
            }
        };

        _ = InitializeAsync();
    }

    public event Action<bool>? ThemeChanged;

    public PlayerViewModel Player { get; }

    public IReadOnlyList<NavItemViewModel> NavItems { get; }
    public ObservableCollection<DramaCardViewModel> ExploreCards { get; } = [];
    public ObservableCollection<DramaCardRowViewModel> ExploreRows { get; } = [];
    public ObservableCollection<DramaCardViewModel> SearchResults { get; } = [];
    public ObservableCollection<DramaCardRowViewModel> SearchRows { get; } = [];
    public ObservableCollection<string> RecentSearches { get; }
    public ObservableCollection<HistoryItemViewModel> TodayHistory { get; } = [];
    public ObservableCollection<HistoryItemViewModel> EarlierHistory { get; } = [];
    public ObservableCollection<HistoryRowEntry> HistoryRows { get; } = [];
    public ObservableCollection<DownloadItemViewModel> VisibleDownloads { get; } = [];
    public ObservableCollection<HotkeyRowViewModel> HotkeyRows { get; } = [];

    public ReactiveCommand<NavSection, RxVoid> NavigateCommand { get; }
    public ReactiveCommand<DramaCategory, RxVoid> SelectCategoryCommand { get; }
    public ReactiveCommand<DramaCardViewModel, RxVoid> OpenDramaCommand { get; }
    public ReactiveCommand<DramaItem, RxVoid> OpenBatchDownloadCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ExecuteSearchCommand { get; }
    public ReactiveCommand<string, RxVoid> ApplySearchTagCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ClearRecentSearchCommand { get; }
    public ReactiveCommand<string, RxVoid> RemoveHistoryCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ClearHistoryCommand { get; }
    public ReactiveCommand<string, RxVoid> SelectDownloadTabCommand { get; }
    public ReactiveCommand<string, RxVoid> ToggleDownloadPauseCommand { get; }
    public ReactiveCommand<string, RxVoid> RemoveDownloadCommand { get; }
    public ReactiveCommand<bool, RxVoid> SetThemeCommand { get; }
    public ReactiveCommand<string, RxVoid> SetDefaultQualityCommand { get; }
    public ReactiveCommand<VideoRenderMode, RxVoid> SetRenderModeCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> TestGatewayCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ToggleTokenVisibilityCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> RefreshCatalogCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> LoadMoreCatalogCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenSettingsCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ClearTempCacheCommand { get; }
    public ReactiveCommand<HotkeyAction, RxVoid> BeginRecordHotkeyCommand { get; }
    public ReactiveCommand<HotkeyAction, RxVoid> ClearHotkeyCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ResetHotkeysCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenLoginCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SignOutCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SyncNowCommand { get; }

    public HotkeySettings Hotkeys => _hotkeys;
    public HotkeyAction? ActiveRecordingAction => _activeRecordingAction;

    public bool GlobalHotkeysEnabled
    {
        get => _hotkeys.GlobalHotkeysEnabled;
        set
        {
            if (_hotkeys.GlobalHotkeysEnabled == value) return;
            _hotkeys = _hotkeys with { GlobalHotkeysEnabled = value };
            this.RaisePropertyChanged();
            SaveSettings();
            _globalHotkeys.ApplyBindings(_hotkeys.GlobalHotkeysEnabled, _hotkeys.GetGlobalBindings());
            RefreshHotkeyStatuses();
        }
    }

    public string GlobalHotkeyStatusSummary
    {
        get
        {
            if (!_globalHotkeys.IsSupported)
                return _globalHotkeys.PlatformNotice ?? "当前平台不支持系统全局热键";
            if (!GlobalHotkeysEnabled)
                return $"全局热键已关闭（引擎：{_globalHotkeys.BackendName}）";

            var registeredCount = _globalHotkeys.Statuses.Values.Count(s => s.IsRegistered);
            var errorCount = _globalHotkeys.Statuses.Values.Count(s => !string.IsNullOrWhiteSpace(s.ErrorMessage));
            var baseText = $"引擎：{_globalHotkeys.BackendName} · 已注册 {registeredCount} 项";
            if (errorCount > 0)
                baseText += $"（{errorCount} 项存在冲突或不支持）";
            if (!string.IsNullOrWhiteSpace(_globalHotkeys.PlatformNotice))
                baseText += $" · {_globalHotkeys.PlatformNotice}";
            return baseText;
        }
    }

    public NavSection CurrentSection
    {
        get => _currentSection;
        set
        {
            if (_currentSection == value) return;
            CancelRecordingHotkey();
            this.RaiseAndSetIfChanged(ref _currentSection, value);
            var matched = NavItems.FirstOrDefault(x => x.Section == value);
            if (matched is not null && !ReferenceEquals(_selectedNavItem, matched))
                this.RaiseAndSetIfChanged(ref _selectedNavItem, matched, nameof(SelectedNavItem));
            this.RaisePropertyChanged(nameof(IsExploreActive));
            this.RaisePropertyChanged(nameof(IsSearchActive));
            this.RaisePropertyChanged(nameof(IsHistoryActive));
            this.RaisePropertyChanged(nameof(IsDownloadsActive));
            this.RaisePropertyChanged(nameof(IsSettingsActive));
        }
    }

    public NavItemViewModel SelectedNavItem
    {
        get => _selectedNavItem;
        set
        {
            if (value is null || ReferenceEquals(_selectedNavItem, value)) return;
            this.RaiseAndSetIfChanged(ref _selectedNavItem, value);
            CurrentSection = value.Section;
        }
    }

    public WindowLayoutMode LayoutMode
    {
        get => _layoutMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _layoutMode, value);
            this.RaisePropertyChanged(nameof(ShowLeftSidebar));
            this.RaisePropertyChanged(nameof(ShowSidebarLabels));
            this.RaisePropertyChanged(nameof(ShowBottomNav));
            this.RaisePropertyChanged(nameof(SidebarWidth));
        }
    }

    public DramaCategory SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedCategory, value);
            this.RaisePropertyChanged(nameof(IsAllCategory));
            this.RaisePropertyChanged(nameof(IsRealCategory));
            this.RaisePropertyChanged(nameof(IsComicCategory));
            this.RaisePropertyChanged(nameof(IsAiCategory));
        }
    }

    public DramaCardViewModel? SpotlightDrama
    {
        get => _spotlightDrama;
        private set => this.RaiseAndSetIfChanged(ref _spotlightDrama, value);
    }

    public DramaCardViewModel? ResumeDrama
    {
        get => _resumeDrama;
        private set => this.RaiseAndSetIfChanged(ref _resumeDrama, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery == value) return;
            this.RaiseAndSetIfChanged(ref _searchQuery, value);
            this.RaisePropertyChanged(nameof(SearchHeaderTitle));
            this.RaisePropertyChanged(nameof(EmptySearchText));
            DebounceSearch();
        }
    }

    private void DebounceSearch()
    {
        _searchDebounceCts?.Cancel();
        var cts = _searchDebounceCts = new CancellationTokenSource();
        _ = Task.Delay(180, cts.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                    _ = ExecuteSearchAsync();
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    public int SelectedDownloadTab
    {
        get => _selectedDownloadTab;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedDownloadTab, value);
            this.RaisePropertyChanged(nameof(IsActiveDlTab));
            this.RaisePropertyChanged(nameof(IsDoneDlTab));
        }
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set => this.RaiseAndSetIfChanged(ref _isDarkTheme, value);
    }

    public bool MiniAlwaysOnTop
    {
        get => _miniAlwaysOnTop;
        set
        {
            this.RaiseAndSetIfChanged(ref _miniAlwaysOnTop, value);
            SaveSettings();
        }
    }

    public int DefaultQuality
    {
        get => _defaultQuality;
        set
        {
            this.RaiseAndSetIfChanged(ref _defaultQuality, value);
            this.RaisePropertyChanged(nameof(IsQuality1080));
            this.RaisePropertyChanged(nameof(IsQuality720));
            this.RaisePropertyChanged(nameof(IsQuality480));
            SaveSettings();
        }
    }

    public bool IsQuality1080 => DefaultQuality == 1080;
    public bool IsQuality720 => DefaultQuality == 720;
    public bool IsQuality480 => DefaultQuality == 480;

    public bool IsRenderAuto => Player.RenderMode == VideoRenderMode.Auto;
    public bool IsRenderGpu => Player.RenderMode == VideoRenderMode.GpuComposition;
    public bool IsRenderSoftware => Player.RenderMode == VideoRenderMode.SoftwareBitmap;
    public bool IsRenderNative => Player.RenderMode == VideoRenderMode.NativeSurface;

    public bool AutoPlayNext
    {
        get => _autoPlayNext;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoPlayNext, value);
            SaveSettings();
        }
    }

    public string DownloadDirectory
    {
        get => _downloadDirectory;
        set
        {
            if (string.Equals(_downloadDirectory, value, StringComparison.Ordinal)) return;
            this.RaiseAndSetIfChanged(ref _downloadDirectory, value);
            SaveSettings();
        }
    }

    public string GatewayBaseUrl
    {
        get => _gatewayBaseUrl;
        set
        {
            if (string.Equals(_gatewayBaseUrl, value, StringComparison.Ordinal)) return;
            this.RaiseAndSetIfChanged(ref _gatewayBaseUrl, value);
            SaveSettings();
        }
    }

    public string GatewayToken
    {
        get => _gatewayToken;
        set
        {
            if (string.Equals(_gatewayToken, value, StringComparison.Ordinal)) return;
            this.RaiseAndSetIfChanged(ref _gatewayToken, value);
            SaveSettings();
        }
    }

    public bool IsTokenVisible
    {
        get => _isTokenVisible;
        set
        {
            if (_isTokenVisible == value) return;
            this.RaiseAndSetIfChanged(ref _isTokenVisible, value);
            this.RaisePropertyChanged(nameof(TokenPasswordChar));
            this.RaisePropertyChanged(nameof(TokenVisibilityButtonText));
        }
    }

    public char TokenPasswordChar => IsTokenVisible ? '\0' : '•';
    public string TokenVisibilityButtonText => IsTokenVisible ? "隐藏" : "显示";

    // ---- 账号与云同步 ----
    public bool IsSignedIn => _auth.IsSignedIn;
    public bool IsSignedOut => !IsSignedIn;
    public string AccountNickname => _auth.CurrentUser?.Nickname ?? string.Empty;
    public string AccountStatusText => IsSignedIn ? $"已登录 · {AccountNickname}" : "未登录 · 历史仅保存在本机";

    public string AccountApiBaseUrl
    {
        get => _accountApiBaseUrl;
        set
        {
            if (string.Equals(_accountApiBaseUrl, value, StringComparison.Ordinal)) return;
            this.RaiseAndSetIfChanged(ref _accountApiBaseUrl, value);
            SaveSettings();
        }
    }

    public bool IsSyncing
    {
        get => _isSyncing;
        private set => this.RaiseAndSetIfChanged(ref _isSyncing, value);
    }

    public bool IsHeroFlightEnabled
    {
        get => _isHeroFlightEnabled;
        private set => this.RaiseAndSetIfChanged(ref _isHeroFlightEnabled, value);
    }

    public bool HasMoreCatalog
    {
        get => _hasMoreCatalog;
        private set => this.RaiseAndSetIfChanged(ref _hasMoreCatalog, value);
    }

    public bool IsLoadingCatalog
    {
        get => _isLoadingCatalog;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isLoadingCatalog, value);
            RaiseExploreStateProperties();
        }
    }

    public bool IsLoadingMoreCatalog
    {
        get => _isLoadingMoreCatalog;
        private set
        {
            if (_isLoadingMoreCatalog == value) return;
            this.RaiseAndSetIfChanged(ref _isLoadingMoreCatalog, value);
            this.RaisePropertyChanged(nameof(LoadMoreButtonText));
        }
    }

    public string LoadMoreButtonText => IsLoadingMoreCatalog ? "正在加载更多剧集..." : "加载更多剧集";

    public string? CatalogErrorMessage
    {
        get => _catalogErrorMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _catalogErrorMessage, value);
            RaiseExploreStateProperties();
            this.RaisePropertyChanged(nameof(CatalogErrorTitle));
            this.RaisePropertyChanged(nameof(SourceStatusText));
        }
    }

    public GatewayFailureKind CatalogFailureKind
    {
        get => _catalogFailureKind;
        private set
        {
            if (_catalogFailureKind == value) return;
            this.RaiseAndSetIfChanged(ref _catalogFailureKind, value);
            this.RaisePropertyChanged(nameof(CatalogErrorTitle));
            this.RaisePropertyChanged(nameof(SourceStatusText));
        }
    }

    public string CatalogErrorTitle => CatalogFailureKind switch
    {
        GatewayFailureKind.Unauthorized => "视频源需要访问令牌",
        GatewayFailureKind.Forbidden => "视频源拒绝访问",
        GatewayFailureKind.InvalidConfig => "视频源地址配置无效",
        GatewayFailureKind.GatewayError or GatewayFailureKind.InvalidResponse => "片库服务返回异常",
        _ => "暂未连接到视频源"
    };

    public bool HasCatalogError => !string.IsNullOrWhiteSpace(CatalogErrorMessage);
    public bool ShowExploreList => !IsLoadingCatalog && !HasCatalogError && ExploreCards.Count > 0;
    public bool ShowExploreEmpty => !IsLoadingCatalog && !HasCatalogError && ExploreCards.Count == 0;

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isSearching, value);
            RaiseSearchStateProperties();
        }
    }

    public string? SearchErrorMessage
    {
        get => _searchErrorMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _searchErrorMessage, value);
            RaiseSearchStateProperties();
        }
    }

    public bool HasSearchError => !string.IsNullOrWhiteSpace(SearchErrorMessage);
    public bool ShowSearchList => !IsSearching && !HasSearchError && SearchResults.Count > 0;
    public bool ShowSearchEmpty => !IsSearching && !HasSearchError && SearchResults.Count == 0;
    public bool HasRecentSearches => RecentSearches.Count > 0;
    public bool HasResumeDrama => ResumeDrama is not null;

    public bool IsLiveSource
    {
        get => _isLiveSource;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isLiveSource, value);
            this.RaisePropertyChanged(nameof(SourceStatusText));
        }
    }

    public string SourceStatusText => IsLiveSource
        ? "已连接在线片库"
        : CatalogFailureKind switch
        {
            GatewayFailureKind.Unauthorized => "需要访问令牌 (401)",
            GatewayFailureKind.Forbidden => "访问受限 (403)",
            GatewayFailureKind.InvalidConfig => "地址配置无效",
            _ => HasCatalogError ? "未连接视频源" : "等待连接"
        };

    public bool IsExploreActive => CurrentSection == NavSection.Explore;
    public bool IsSearchActive => CurrentSection == NavSection.Search;
    public bool IsHistoryActive => CurrentSection == NavSection.History;
    public bool IsDownloadsActive => CurrentSection == NavSection.Downloads;
    public bool IsSettingsActive => CurrentSection == NavSection.Settings;

    public bool ShowLeftSidebar => LayoutMode != WindowLayoutMode.Narrow;
    public bool ShowSidebarLabels => LayoutMode == WindowLayoutMode.Wide;
    public bool ShowBottomNav => LayoutMode == WindowLayoutMode.Narrow;
    public double SidebarWidth => LayoutMode == WindowLayoutMode.Wide ? 196 : 68;

    public bool IsAllCategory => SelectedCategory == DramaCategory.All;
    public bool IsRealCategory => SelectedCategory == DramaCategory.RealDrama;
    public bool IsComicCategory => SelectedCategory == DramaCategory.ComicDrama;
    public bool IsAiCategory => SelectedCategory == DramaCategory.AiDrama;

    public string CatalogCountText => $"共 {ExploreCards.Count} 部";
    public string SearchHeaderTitle => string.IsNullOrWhiteSpace(SearchQuery) ? "搜索片源" : $"“{SearchQuery.Trim()}” 的搜索结果";
    public string SearchResultCountText => $"{SearchResults.Count} 部";
    public string EmptySearchText => string.IsNullOrWhiteSpace(SearchQuery) ? "输入剧名或关键词开始搜索" : "未找到相关短剧";

    public bool IsActiveDlTab => SelectedDownloadTab == 0;
    public bool IsDoneDlTab => SelectedDownloadTab == 1;

    public int ActiveDownloadCount => _downloads.GetDownloads().Count(x => x.State != DownloadTaskState.Completed);
    public int CompletedDownloadCount => _downloads.GetDownloads().Count(x => x.State == DownloadTaskState.Completed);
    public string ActiveDlTabText => $"正在下载 ({ActiveDownloadCount})";
    public string DoneDlTabText => $"已完成 ({CompletedDownloadCount})";
    public bool HasActiveDownloads => ActiveDownloadCount > 0;
    public bool HasVisibleDownloads => VisibleDownloads.Count > 0;
    public string EmptyDownloadsText => SelectedDownloadTab == 1 ? "暂无已完成的下载任务" : "暂无正在下载的任务";

    private void RaiseExploreStateProperties()
    {
        this.RaisePropertyChanged(nameof(HasCatalogError));
        this.RaisePropertyChanged(nameof(ShowExploreList));
        this.RaisePropertyChanged(nameof(ShowExploreEmpty));
    }

    private void RaiseSearchStateProperties()
    {
        this.RaisePropertyChanged(nameof(HasSearchError));
        this.RaisePropertyChanged(nameof(ShowSearchList));
        this.RaisePropertyChanged(nameof(ShowSearchEmpty));
    }

    public int TotalHistoryCount => TodayHistory.Count + EarlierHistory.Count;
    public bool HasTodayHistory => TodayHistory.Count > 0;
    public bool HasEarlierHistory => EarlierHistory.Count > 0;
    public bool HasAnyHistory => TotalHistoryCount > 0;

    public void FocusSearch() => CurrentSection = NavSection.Search;

    private async Task InitializeAsync()
    {
        RefreshHistory();
        RefreshDownloads();
        // 恢复登录态会触发 AuthStateChanged → 自动拉取云端历史并合并
        await _auth.RestoreAsync();
        await SelectCategoryAsync(DramaCategory.All);
    }

    public void UpdateExploreColumns(double availableWidth)
    {
        var cols = Math.Max(1, (int)((availableWidth + 14) / 192));
        if (cols == _exploreColumns && ExploreRows.Count > 0) return;
        _exploreColumns = cols;
        RebuildExploreRows();
    }

    public void UpdateSearchColumns(double availableWidth)
    {
        var cols = Math.Max(1, (int)((availableWidth + 12) / 322));
        if (cols == _searchColumns && SearchRows.Count > 0) return;
        _searchColumns = cols;
        RebuildSearchRows();
    }

    public void UpdateHistoryColumns(double availableWidth)
    {
        var cols = Math.Max(1, (int)((availableWidth + 12) / 342));
        if (cols == _historyColumns && HistoryRows.Count > 0) return;
        _historyColumns = cols;
        RebuildHistoryRows();
    }

    private void RebuildExploreRows()
    {
        ExploreRows.Clear();
        for (var i = 0; i < ExploreCards.Count; i += _exploreColumns)
            ExploreRows.Add(new DramaCardRowViewModel(ExploreCards.Skip(i).Take(_exploreColumns).ToList()));
    }

    private int AppendExploreItems(
        IReadOnlyList<DramaItem> items,
        Dictionary<string, int> historyMap)
    {
        // Always deduplicate against the current collection, including the first page.
        var existingIds = ExploreCards.Select(x => x.Drama.SeriesId).ToHashSet(StringComparer.Ordinal);
        var startIndex = ExploreCards.Count;
        var added = 0;
        foreach (var item in items)
        {
            if (!existingIds.Add(item.SeriesId)) continue;
            historyMap.TryGetValue(item.SeriesId, out var watchedEp);
            ExploreCards.Add(new DramaCardViewModel(item, $"explore-{item.SeriesId}", watchedEp));
            added++;
        }

        if (added == 0)
            return 0;

        // 直接向末行追加卡片而非替换整行，防止已有卡片卸载/重挂触发 Hero 动画且保持滚动位置
        var cursor = startIndex;
        if (ExploreRows.Count > 0 && ExploreRows[^1].Items.Count < _exploreColumns)
        {
            var tailRow = ExploreRows[^1];
            while (cursor < ExploreCards.Count && tailRow.Items.Count < _exploreColumns)
            {
                tailRow.Items.Add(ExploreCards[cursor++]);
            }
        }

        for (var i = cursor; i < ExploreCards.Count; i += _exploreColumns)
            ExploreRows.Add(new DramaCardRowViewModel(ExploreCards.Skip(i).Take(_exploreColumns).ToList()));

        return added;
    }

    private Task OpenDramaWithHeroAsync(DramaCardViewModel card)
    {
        _heroDisarmCts?.Cancel();
        IsHeroFlightEnabled = true;
        ScheduleHeroDisarm();
        return Player.OpenAsync(card);
    }

    private void OnPlayerClosingRequested(bool wasMiniMode)
    {
        _heroDisarmCts?.Cancel();
        if (wasMiniMode)
        {
            // Closing from MiniMode triggers a 360->1260 window resize; keep Hero disabled so background list relayouts never fly.
            IsHeroFlightEnabled = false;
            return;
        }

        IsHeroFlightEnabled = true;
        ScheduleHeroDisarm();
    }

    private async void ScheduleHeroDisarm()
    {
        _heroDisarmCts?.Cancel();
        var cts = _heroDisarmCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(450, cts.Token);
            if (!cts.IsCancellationRequested)
            {
                IsHeroFlightEnabled = false;
                if (!Player.IsOpen && _pendingHistoryRefresh)
                {
                    _pendingHistoryRefresh = false;
                    RefreshHistory();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnPlayerHistoryUpdated()
    {
        // 播放进度变更时节流上传云端（约每 15 秒一次），失败不打扰用户。
        _ = PushLatestRecordToCloudAsync();

        // Defer rebuilding HistoryRows while Player is open or Hero return flight is animating so History cards stay intact.
        if (Player.IsOpen || IsHeroFlightEnabled)
        {
            _pendingHistoryRefresh = true;
            return;
        }

        _pendingHistoryRefresh = false;
        RefreshHistory();
    }

    private void RebuildSearchRows()
    {
        SearchRows.Clear();
        for (var i = 0; i < SearchResults.Count; i += _searchColumns)
            SearchRows.Add(new DramaCardRowViewModel(SearchResults.Skip(i).Take(_searchColumns).ToList()));
    }

    private void RebuildHistoryRows()
    {
        HistoryRows.Clear();
        if (TodayHistory.Count > 0)
        {
            HistoryRows.Add(new HistoryRowEntry("今天"));
            for (var i = 0; i < TodayHistory.Count; i += _historyColumns)
                HistoryRows.Add(new HistoryRowEntry(TodayHistory.Skip(i).Take(_historyColumns).ToList()));
        }

        if (EarlierHistory.Count > 0)
        {
            HistoryRows.Add(new HistoryRowEntry("更早"));
            for (var i = 0; i < EarlierHistory.Count; i += _historyColumns)
                HistoryRows.Add(new HistoryRowEntry(EarlierHistory.Skip(i).Take(_historyColumns).ToList()));
        }
    }

    private async Task SelectCategoryAsync(DramaCategory category)
    {
        _catalogCts?.Cancel();
        var cts = _catalogCts = new CancellationTokenSource();
        SelectedCategory = category;
        _catalogPage = 1;
        CatalogFailureKind = GatewayFailureKind.None;
        CatalogErrorMessage = null;
        IsLoadingCatalog = true;
        // The initial request and automatic prefetch own the pagination cursor until completion.
        IsLoadingMoreCatalog = true;

        try
        {
            var pageResult = await _catalog.GetCatalogPageAsync(category, _catalogPage, 100, cts.Token);
            if (cts.IsCancellationRequested) return;

            IsLiveSource = pageResult.IsLiveGateway;
            HasMoreCatalog = pageResult.HasMore;

            var historyMap = _history.GetHistory().ToDictionary(x => x.Drama.SeriesId, x => x.LastEpisodeNo);

            ExploreCards.Clear();
            ExploreRows.Clear();
            AppendExploreItems(pageResult.Items, historyMap);

            if (pageResult.Items.Count > 0 && (SpotlightDrama is null || category == DramaCategory.All))
            {
                historyMap.TryGetValue(pageResult.Items[0].SeriesId, out var watchedEp);
                SpotlightDrama = new DramaCardViewModel(pageResult.Items[0], $"spotlight-{pageResult.Items[0].SeriesId}", watchedEp);
                if (ResumeDrama is null)
                {
                    ResumeDrama = SpotlightDrama;
                    this.RaisePropertyChanged(nameof(HasResumeDrama));
                }
            }
            else if (pageResult.Items.Count == 0 && category == DramaCategory.All)
            {
                SpotlightDrama = null;
            }

            // 首屏立即显示后，若片源较少且仍有下一页，连续预取至多 48 部片源保证片库饱满
            IsLoadingCatalog = false;
            this.RaisePropertyChanged(nameof(CatalogCountText));
            RaiseExploreStateProperties();

            while (!cts.IsCancellationRequested && HasMoreCatalog && ExploreCards.Count < 48 && _catalogPage < 4)
            {
                var nextPage = _catalogPage + 1;
                var nextResult = await _catalog.GetCatalogPageAsync(category, nextPage, 100, cts.Token);
                if (cts.IsCancellationRequested) return;

                _catalogPage = nextPage;
                HasMoreCatalog = nextResult.HasMore;
                var added = AppendExploreItems(nextResult.Items, historyMap);
                this.RaisePropertyChanged(nameof(CatalogCountText));
                RaiseExploreStateProperties();
                if (added == 0 || nextResult.Items.Count == 0)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (GatewayException ex) when (ExploreCards.Count == 0)
        {
            IsLiveSource = false;
            HasMoreCatalog = false;
            ExploreCards.Clear();
            ExploreRows.Clear();
            SpotlightDrama = null;
            CatalogFailureKind = ex.FailureKind;
            CatalogErrorMessage = ex.UserMessage;
        }
        catch (GatewayException)
        {
            // 预取后续分页失败时保留已加载的首屏片源
            HasMoreCatalog = false;
        }
        catch (Exception) when (ExploreCards.Count == 0)
        {
            IsLiveSource = false;
            HasMoreCatalog = false;
            ExploreCards.Clear();
            ExploreRows.Clear();
            SpotlightDrama = null;
            CatalogFailureKind = GatewayFailureKind.Connection;
            CatalogErrorMessage = "加载片库失败，请检查视频源连接后重试";
        }
        catch (Exception)
        {
            HasMoreCatalog = false;
        }
        finally
        {
            if (ReferenceEquals(_catalogCts, cts))
            {
                IsLoadingCatalog = false;
                IsLoadingMoreCatalog = false;
                this.RaisePropertyChanged(nameof(CatalogCountText));
                RaiseExploreStateProperties();
            }
        }
    }

    private async Task LoadMoreCatalogAsync()
    {
        if (IsLoadingCatalog || IsLoadingMoreCatalog || !HasMoreCatalog) return;
        IsLoadingMoreCatalog = true;
        var activeCts = _catalogCts;
        var activeCategory = SelectedCategory;
        try
        {
            var historyMap = _history.GetHistory().ToDictionary(x => x.Drama.SeriesId, x => x.LastEpisodeNo);
            var batchAdded = 0;

            for (var step = 0; step < 2 && HasMoreCatalog && batchAdded < 24; step++)
            {
                if (activeCts is { IsCancellationRequested: true } || activeCategory != SelectedCategory)
                    return;

                var nextPage = _catalogPage + 1;
                var result = await _catalog.GetCatalogPageAsync(
                    activeCategory,
                    nextPage,
                    100,
                    activeCts?.Token ?? CancellationToken.None);

                if (activeCts is { IsCancellationRequested: true } || activeCategory != SelectedCategory)
                    return;

                IsLiveSource = result.IsLiveGateway;
                if (result.Items.Count == 0)
                {
                    HasMoreCatalog = false;
                    break;
                }

                _catalogPage = nextPage;
                HasMoreCatalog = result.HasMore;
                var added = AppendExploreItems(result.Items, historyMap);
                batchAdded += added;
                this.RaisePropertyChanged(nameof(CatalogCountText));
                RaiseExploreStateProperties();
                if (added == 0)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception) when (activeCts is { IsCancellationRequested: true })
        {
            return;
        }
        catch (GatewayException ex)
        {
            _toast.ShowError(ex.UserMessage);
        }
        catch (Exception)
        {
            _toast.ShowError("加载更多剧集失败，请稍后重试");
        }
        finally
        {
            if (ReferenceEquals(_catalogCts, activeCts))
                IsLoadingMoreCatalog = false;
        }
    }

    private async Task ExecuteSearchAsync()
    {
        _searchRequestCts?.Cancel();
        var cts = _searchRequestCts = new CancellationTokenSource();

        var q = SearchQuery.Trim();
        if (!string.IsNullOrEmpty(q) && !RecentSearches.Contains(q))
        {
            RecentSearches.Insert(0, q);
            this.RaisePropertyChanged(nameof(HasRecentSearches));
        }

        SearchErrorMessage = null;
        IsSearching = true;
        try
        {
            var hits = await _catalog.SearchAsync(q, cts.Token);
            if (cts.IsCancellationRequested) return;

            IsLiveSource = _catalog.IsLastRequestLive;
            var historyMap = _history.GetHistory().ToDictionary(x => x.Drama.SeriesId, x => x.LastEpisodeNo);

            SearchResults.Clear();
            foreach (var item in hits)
            {
                historyMap.TryGetValue(item.SeriesId, out var watchedEp);
                SearchResults.Add(new DramaCardViewModel(item, $"search-{item.SeriesId}", watchedEp));
            }
            RebuildSearchRows();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (GatewayException ex)
        {
            SearchResults.Clear();
            SearchRows.Clear();
            SearchErrorMessage = ex.UserMessage;
        }
        catch (Exception)
        {
            SearchResults.Clear();
            SearchRows.Clear();
            SearchErrorMessage = "搜索请求失败，请检查视频源服务状态";
        }
        finally
        {
            IsSearching = false;
            this.RaisePropertyChanged(nameof(SearchResultCountText));
            RaiseSearchStateProperties();
        }
    }

    private void RefreshHistory()
    {
        var records = _history.GetHistory();
        TodayHistory.Clear();
        EarlierHistory.Clear();

        foreach (var record in records)
        {
            var vm = new HistoryItemViewModel(record, $"history-{record.Drama.SeriesId}");
            if (record.GroupTitle == "今天")
                TodayHistory.Add(vm);
            else
                EarlierHistory.Add(vm);
        }
        RebuildHistoryRows();

        ResumeDrama = records.Count > 0
            ? new DramaCardViewModel(records[0].Drama, $"resume-{records[0].Drama.SeriesId}", records[0].LastEpisodeNo)
            : SpotlightDrama;

        this.RaisePropertyChanged(nameof(HasResumeDrama));
        this.RaisePropertyChanged(nameof(TotalHistoryCount));
        this.RaisePropertyChanged(nameof(HasTodayHistory));
        this.RaisePropertyChanged(nameof(HasEarlierHistory));
        this.RaisePropertyChanged(nameof(HasAnyHistory));
    }

    public void RefreshDownloadProgress() => RefreshDownloads();

    private void RefreshDownloads()
    {
        var all = _downloads.GetDownloads();
        var filtered = SelectedDownloadTab == 1
            ? all.Where(x => x.State == DownloadTaskState.Completed)
            : all.Where(x => x.State != DownloadTaskState.Completed);

        var tasks = filtered.ToArray();
        for (var i = VisibleDownloads.Count - 1; i >= 0; i--)
            if (!tasks.Any(x => x.TaskId == VisibleDownloads[i].TaskId)) VisibleDownloads.RemoveAt(i);
        for (var i = 0; i < tasks.Length; i++)
        {
            var task = tasks[i];
            var existing = VisibleDownloads.FirstOrDefault(x => x.TaskId == task.TaskId);
            if (existing is null) VisibleDownloads.Insert(i, new DownloadItemViewModel(task, $"dl-{task.Drama.SeriesId}-{task.TaskId}"));
            else existing.Update(task);
        }

        this.RaisePropertyChanged(nameof(ActiveDownloadCount));
        this.RaisePropertyChanged(nameof(CompletedDownloadCount));
        this.RaisePropertyChanged(nameof(ActiveDlTabText));
        this.RaisePropertyChanged(nameof(DoneDlTabText));
        this.RaisePropertyChanged(nameof(HasActiveDownloads));
        this.RaisePropertyChanged(nameof(HasVisibleDownloads));
        this.RaisePropertyChanged(nameof(EmptyDownloadsText));
    }

    private async Task ConfirmClearHistoryAsync()
    {
        await _dialogs.ShowAsync(session => new ConfirmDialogViewModel(
            "清空浏览历史",
            "确定要清空全部观看记录吗？清空后将同步删除云端记录，并不再保留断点续播位置。",
            () =>
            {
                _history.Clear();
                RefreshHistory();
                if (_historySync.IsEnabled)
                    _ = FireAndForgetCloudAsync(() => _historySync.ClearAllAsync());
                session.Close();
            },
            () => session.Close()));
    }

    private async Task OpenBatchDownloadDialogAsync(DramaItem? drama)
    {
        if (drama is null) return;
        await _dialogs.ShowAsync(session => new BatchDownloadDialogViewModel(
            drama,
            DefaultQuality,
            count =>
            {
                try
                {
                    _downloads.Enqueue(drama, count);
                    RefreshDownloads();
                    _toast.ShowSuccess($"已将《{drama.Title}》前 {count} 集加入离线下载");
                    session.Close();
                }
                catch (Exception ex) { _toast.ShowError($"无法创建下载任务：{ex.Message}"); }
            },
            () => session.Close()));
    }

    private async Task TestGatewayAsync()
    {
        SaveSettings();
        try
        {
            var health = await _loading.RunAsync(
                () => _catalog.TestConnectionAsync(GatewayBaseUrl),
                new LoadingRequest("正在校验服务连通性与鉴权..."));
            if (health.IsHealthy)
            {
                await SelectCategoryAsync(SelectedCategory);
                if (!string.IsNullOrWhiteSpace(SearchQuery) || IsSearchActive)
                    await ExecuteSearchAsync();
                else
                    SearchErrorMessage = null;
                _toast.ShowSuccess(health.Message);
            }
            else
            {
                IsLiveSource = false;
                CatalogFailureKind = health.FailureKind;
                CatalogErrorMessage = health.Message;
                _toast.ShowError(health.Message);
            }
        }
        catch (Exception)
        {
            _toast.ShowError("连接测试时发生异常，请检查地址与网络");
        }
    }

    // ---------- 账号与云同步 ----------

    private async Task FireAndForgetCloudAsync(Func<Task> action)
    {
        try { await action(); }
        catch { /* 后台同步失败不打扰用户 */ }
    }

    private void OnAuthStateChanged()
    {
        this.RaisePropertyChanged(nameof(IsSignedIn));
        this.RaisePropertyChanged(nameof(IsSignedOut));
        this.RaisePropertyChanged(nameof(AccountNickname));
        this.RaisePropertyChanged(nameof(AccountStatusText));
        if (IsSignedIn)
            _ = SyncFromCloudAsync(showToast: true);
        else
            _toast.ShowSuccess("已退出登录，观看历史仅保存在本机");
    }

    private void OpenLoginWindow()
    {
        if (IsSignedIn)
        {
            _toast.ShowSuccess("当前已登录：" + AccountNickname);
            return;
        }
        if (string.IsNullOrWhiteSpace(AccountApiBaseUrl))
        {
            _toast.ShowError("请先在下方填写账号服务地址");
            return;
        }
        SaveSettings();
        var vm = Program.Services.GetRequiredService<LoginWindowViewModel>();
        vm.LoginSucceeded += isRegister =>
            _toast.ShowSuccess(isRegister ? "注册成功，已自动登录" : "登录成功");
        var window = new LoginWindow(vm);
        window.Show();
    }

    private void SignOut() => _auth.SignOut();

    private async Task SyncNowAsync()
    {
        if (!IsSignedIn)
        {
            _toast.ShowError("请先登录账号后再同步");
            return;
        }
        await SyncFromCloudAsync(showToast: true);
    }

    /// <summary>登录后：先把本地历史并入云端，再把合并结果回填本地，实现多设备同步。</summary>
    private async Task SyncFromCloudAsync(bool showToast)
    {
        if (IsSyncing || !_historySync.IsEnabled) return;
        IsSyncing = true;
        try
        {
            var local = _history.GetHistory();
            var uploaded = await _historySync.UploadAllAsync(local);
            _history.ReplaceAll(uploaded.Records);
            RefreshHistory();
            _lastCloudPushTick = Environment.TickCount64;
            if (showToast)
                _toast.ShowSuccess($"观看历史已同步（共 {uploaded.Records.Count} 条）");
        }
        catch (Exception ex)
        {
            if (showToast)
                _toast.ShowError("同步观看历史失败：" + ex.Message);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>播放进度更新时，节流地把最新一条记录单条推送云端。</summary>
    private async Task PushLatestRecordToCloudAsync()
    {
        if (!_historySync.IsEnabled || IsSyncing) return;
        if (Environment.TickCount64 - _lastCloudPushTick < 15_000) return;
        var latest = _history.GetHistory().FirstOrDefault();
        if (latest is null) return;
        _lastCloudPushTick = Environment.TickCount64;
        try
        {
            await _historySync.PushOneAsync(latest);
        }
        catch
        {
            // 后台节流同步失败不打扰用户，下次进度更新会重试
        }
    }

    private void SaveSettings()
    {
        _settings.Update(_settings.Current with
        {
            Theme = IsDarkTheme ? AppThemePreference.Dark : AppThemePreference.Light,
            MiniWindowAlwaysOnTop = MiniAlwaysOnTop,
            DefaultQuality = DefaultQuality,
            AutoPlayNextEpisode = AutoPlayNext,
            DownloadDirectory = DownloadDirectory,
            GatewayBaseUrl = GatewayBaseUrl,
            GatewayToken = GatewayToken,
            AccountApiBaseUrl = AccountApiBaseUrl,
            Hotkeys = _hotkeys
        });
    }

    private void BuildHotkeyRows()
    {
        HotkeyRows.Clear();
        HotkeyRows.Add(new(HotkeyAction.GlobalBossKey, "暂停并隐藏 / 显示窗体", "在系统任意位置按下即可暂停当前播放并隐藏窗口，再次按下恢复显示", true, _hotkeys.GetBinding(HotkeyAction.GlobalBossKey)));
        HotkeyRows.Add(new(HotkeyAction.GlobalPlayPause, "全局播放 / 暂停", "即使窗口在后台未聚焦也能随时控制播放或暂停（建议搭配修饰键）", true, _hotkeys.GetBinding(HotkeyAction.GlobalPlayPause)));
        HotkeyRows.Add(new(HotkeyAction.PlayPause, "播放 / 暂停", "窗口聚焦且未在输入框打字时切换播放状态", false, _hotkeys.GetBinding(HotkeyAction.PlayPause)));
        HotkeyRows.Add(new(HotkeyAction.SeekBackward, "快退 5 秒", "视频向后跳转 5 秒", false, _hotkeys.GetBinding(HotkeyAction.SeekBackward)));
        HotkeyRows.Add(new(HotkeyAction.SeekForward, "快进 5 秒", "视频向前跳转 5 秒", false, _hotkeys.GetBinding(HotkeyAction.SeekForward)));
        HotkeyRows.Add(new(HotkeyAction.SpeedUp, "提高倍速", "按倍速档位递增（最高 3.0x）", false, _hotkeys.GetBinding(HotkeyAction.SpeedUp)));
        HotkeyRows.Add(new(HotkeyAction.SpeedDown, "降低倍速", "按倍速档位递减（最低 0.75x）", false, _hotkeys.GetBinding(HotkeyAction.SpeedDown)));
        HotkeyRows.Add(new(HotkeyAction.SpeedReset, "恢复正常倍速", "一键将播放倍速重置为 1.0x", false, _hotkeys.GetBinding(HotkeyAction.SpeedReset)));
        HotkeyRows.Add(new(HotkeyAction.PreviousEpisode, "上一集", "切换到当前短剧上一集", false, _hotkeys.GetBinding(HotkeyAction.PreviousEpisode)));
        HotkeyRows.Add(new(HotkeyAction.NextEpisode, "下一集", "切换到当前短剧下一集", false, _hotkeys.GetBinding(HotkeyAction.NextEpisode)));
        HotkeyRows.Add(new(HotkeyAction.ToggleMiniMode, "切换迷你窗口", "在常规大窗与迷你悬浮小窗之间切换", false, _hotkeys.GetBinding(HotkeyAction.ToggleMiniMode)));
        HotkeyRows.Add(new(HotkeyAction.RestoreWindow, "还原窗体", "退出迷你小窗并恢复标准窗口尺寸", false, _hotkeys.GetBinding(HotkeyAction.RestoreWindow)));
        HotkeyRows.Add(new(HotkeyAction.VolumeUp, "增大音量", "应用内音量提高一档（每次 5%）", false, _hotkeys.GetBinding(HotkeyAction.VolumeUp)));
        HotkeyRows.Add(new(HotkeyAction.VolumeDown, "减小音量", "应用内音量降低一档（每次 5%）", false, _hotkeys.GetBinding(HotkeyAction.VolumeDown)));
        HotkeyRows.Add(new(HotkeyAction.ToggleMute, "静音 / 取消静音", "一键切换应用内静音状态", false, _hotkeys.GetBinding(HotkeyAction.ToggleMute)));
        RefreshHotkeyStatuses();
    }

    private void RefreshHotkeyStatuses()
    {
        var statuses = _globalHotkeys.Statuses;
        foreach (var row in HotkeyRows)
        {
            if (row.IsGlobal && GlobalHotkeysEnabled && statuses.TryGetValue(row.Action, out var status))
                row.StatusError = status.ErrorMessage;
            else
                row.StatusError = null;
        }
        this.RaisePropertyChanged(nameof(GlobalHotkeyStatusSummary));
    }

    private void BeginRecordHotkey(HotkeyAction action)
    {
        foreach (var row in HotkeyRows)
            row.IsRecording = row.Action == action;
        _activeRecordingAction = action;
        this.RaisePropertyChanged(nameof(ActiveRecordingAction));
    }

    public void CancelRecordingHotkey()
    {
        if (_activeRecordingAction is null) return;
        _activeRecordingAction = null;
        foreach (var row in HotkeyRows)
            row.IsRecording = false;
        this.RaisePropertyChanged(nameof(ActiveRecordingAction));
    }

    public bool TryCommitRecordedHotkey(HotkeyBinding candidate)
    {
        if (_activeRecordingAction is not { } action)
            return false;

        if (HotkeySettings.IsGlobalAction(action) && !candidate.HasAnyModifier)
        {
            _toast.ShowError("全局热键需至少包含 Ctrl / Alt / Shift 修饰键，以免干扰日常输入");
            return false;
        }

        if (_hotkeys.FindConflict(action, candidate) is { } conflictAction)
        {
            var conflictTitle = HotkeyRows.FirstOrDefault(r => r.Action == conflictAction)?.Title ?? conflictAction.ToString();
            _toast.ShowError($"快捷键 {candidate.DisplayText} 已被「{conflictTitle}」使用");
            return false;
        }

        _hotkeys = _hotkeys.WithBinding(action, candidate);
        var row = HotkeyRows.FirstOrDefault(r => r.Action == action);
        if (row is not null)
            row.Binding = _hotkeys.GetBinding(action);

        CancelRecordingHotkey();
        SaveSettings();

        if (HotkeySettings.IsGlobalAction(action))
            _globalHotkeys.ApplyBindings(_hotkeys.GlobalHotkeysEnabled, _hotkeys.GetGlobalBindings());

        return true;
    }

    private void ClearHotkey(HotkeyAction action)
    {
        if (_activeRecordingAction == action)
            CancelRecordingHotkey();

        _hotkeys = _hotkeys.WithBinding(action, HotkeyBinding.Empty);
        var row = HotkeyRows.FirstOrDefault(r => r.Action == action);
        if (row is not null)
            row.Binding = HotkeyBinding.Empty;

        SaveSettings();
        if (HotkeySettings.IsGlobalAction(action))
            _globalHotkeys.ApplyBindings(_hotkeys.GlobalHotkeysEnabled, _hotkeys.GetGlobalBindings());
    }

    private void ResetHotkeys()
    {
        CancelRecordingHotkey();
        _hotkeys = HotkeySettings.CreateDefaults();
        foreach (var row in HotkeyRows)
            row.Binding = _hotkeys.GetBinding(row.Action);

        this.RaisePropertyChanged(nameof(GlobalHotkeysEnabled));
        SaveSettings();
        _globalHotkeys.ApplyBindings(_hotkeys.GlobalHotkeysEnabled, _hotkeys.GetGlobalBindings());
        RefreshHotkeyStatuses();
        _toast.ShowSuccess("快捷键已恢复默认设置");
    }
}
