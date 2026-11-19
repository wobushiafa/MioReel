using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Aviora.Controls;
using FrameFlux;
using FrameFlux.FFmpeg;
using Microsoft.Extensions.DependencyInjection;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;
using MioReel.Desktop.Controls;
using MioReel.Desktop.Services;
using MioReel.Desktop.Services.Hotkeys;
using MioReel.Desktop.ViewModels;

namespace MioReel.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = null!;
    private readonly ISettingsService _settings = null!;
    private WindowPlacement? _normalPlacement;
    private WindowPlacement? _miniPlacement;
    private bool _displayedMini;
    private bool _isClosing;
    private bool _awaitingSourcePlayback;
    private readonly DispatcherTimer _playbackSyncTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private double _normalWidth = 1260;
    private double _normalHeight = 780;
    private bool _isTransitioningMini;
    private double _lastObservedPosSeconds = -1;
    private int _eofStalledTicks;
    private bool _eofNotifiedForCurrentUrl;
    private bool _suppressMiniDragOnce;
    private IPointer? _stagePointer;
    private long _lastDownloadRefreshTick;
    private readonly DispatcherTimer _controlsExitHideTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _controlsIdleHideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _volumeOsdHideTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private Point? _lastStagePointerPos;
    private WinPoint? _lastNativeCursorPos;
    private bool _isAdjustingMiniAspect;
    private bool _isInWin32SizeMove;
    private bool _hasOpenedOnce;
    private bool _pausedByBossKey;
    private bool _wasNativeLeftMouseDown;
    private bool _isDockedMenuPointerCaptured;
    private DanmakuNativeOverlayWindow? _nativeDanmakuWindow;
    private CancellationTokenSource? _reloadOptionsCts;
    private double? _pendingOptionsReloadResumePos;
    private bool? _pendingOptionsReloadWasPlaying;
    private bool? _appliedSplitMiniMode;
    private WindowLayoutMode? _appliedSplitLayoutMode;
    private Win32Properties.CustomWndProcHookCallback? _wndProcHook;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out WinPoint lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out WinRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out WinRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();
    }

    [ActivatorUtilitiesConstructor]
    public MainWindow(
        MainWindowViewModel viewModel,
        ISettingsService settings,
        IGlobalHotkeyService globalHotkeys,
        IDrawerHostService drawerService,
        IDialogHostService dialogService,
        ILoadingHostService loadingService,
        IToastHostService toastService)
        : this()
    {
        _viewModel = viewModel;
        _settings = settings;
        _normalPlacement = settings.Current.NormalWindow;
        _miniPlacement = settings.Current.MiniWindow;

        globalHotkeys.HotkeyTriggered += OnGlobalHotkeyTriggered;
        Closing += (_, _) => globalHotkeys.HotkeyTriggered -= OnGlobalHotkeyTriggered;

        DrawerHost.Service = drawerService;
        DialogHost.Service = dialogService;
        LoadingHost.Service = loadingService;
        ToastHost.Service = toastService;

        FrameFluxPlayer.PlayerFactory = new FfmpegMediaPlayerFactory();
        // Media comes from our prepared loopback proxy. Avoid the backend's 3-second HTTP startup buffer.
        FrameFluxPlayer.OpenOptions = BuildOpenOptions();
        ApplyRenderMode(_viewModel.Player.RenderMode);
        ApplyPlayerVolume();
        EmbeddedDanmakuOverlay.SetPlayer(_viewModel.Player);
        FrameFluxPlayer.PlaybackStateChanged += OnFrameFluxPlaybackStateChanged;
        FrameFluxPlayer.PlaybackError += OnFrameFluxPlaybackError;
        FrameFluxPlayer.PropertyChanged += (_, e) =>
        {
            if (e.Property == FrameFlux.Avalonia.MediaView.EffectivePresentationModeProperty)
                _viewModel.Player.IsEffectiveNativeSurface =
                    FrameFluxPlayer.EffectivePresentationMode == MediaVideoPresentationMode.NativeSurface;
        };

        var captionMargins = new Thickness(16, 0, 16, 0);
        TitleBarContentGrid.Margin = captionMargins;
        PlayerTopBarContentGrid.Margin = captionMargins;

        _playbackSyncTimer.Tick += OnPlaybackSyncTick;
        _playbackSyncTimer.Start();
        _controlsExitHideTimer.Tick += OnControlsExitHideTick;
        _controlsIdleHideTimer.Tick += OnControlsIdleHideTick;
        _volumeOsdHideTimer.Tick += OnVolumeOsdHideTick;

        SizeChanged += OnWindowSizeChanged;
        PositionChanged += (_, _) =>
        {
            RememberWindowBounds();
            SyncNativeDanmakuOverlay();
        };
        PlayerViewportBox.LayoutUpdated += (_, _) =>
        {
            if (_viewModel.Player.UseNativeDanmakuOverlay)
                SyncNativeDanmakuOverlay();
        };
        Opened += (_, _) =>
        {
            if (_hasOpenedOnce) return;
            _hasOpenedOnce = true;
            if (OperatingSystem.IsWindows())
            {
                _wndProcHook = OnWin32WndProc;
                Win32Properties.AddWndProcHookCallback(this, _wndProcHook);
            }
            if (_normalPlacement is { } placement) ApplyWindowPlacement(placement, false);
            else RememberWindowBounds();
        };
        Closing += (_, _) =>
        {
            RememberWindowBounds();
            SaveWindowPlacements();
            _isClosing = true;
        };
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnWindowTunnelPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        DockedMenuPanel.AddHandler(PointerPressedEvent, (_, _) => _isDockedMenuPointerCaptured = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        DockedMenuPanel.AddHandler(PointerReleasedEvent, (_, _) => _isDockedMenuPointerCaptured = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        DockedMenuPanel.AddHandler(PointerCaptureLostEvent, (_, _) => _isDockedMenuPointerCaptured = false, RoutingStrategies.Bubble, handledEventsToo: true);
        Closed += (_, _) =>
        {
            _nativeDanmakuWindow?.Overlay.SetPlayer(null);
            _nativeDanmakuWindow?.Close();
            _nativeDanmakuWindow = null;
            EmbeddedDanmakuOverlay.SetPlayer(null);
            _reloadOptionsCts?.Cancel();
            _reloadOptionsCts?.Dispose();
            _reloadOptionsCts = null;
            _playbackSyncTimer.Stop();
            _controlsExitHideTimer.Stop();
            _controlsIdleHideTimer.Stop();
            _volumeOsdHideTimer.Stop();
            if (OperatingSystem.IsWindows() && _wndProcHook is not null)
                Win32Properties.RemoveWndProcHookCallback(this, _wndProcHook);
            _viewModel.Player.Close();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                UpdateMaximizeIcons();
            if (e.Property == WindowStateProperty || e.Property == IsVisibleProperty || e.Property == TopmostProperty)
                SyncNativeDanmakuOverlay();
        };
        _viewModel.Player.StreamDimensionsChanged += OnStreamDimensionsChanged;
        _viewModel.Player.MiniModeChanged += OnMiniModeChanged;
        _viewModel.Player.MiniPinChanged += pinned => Topmost = pinned;
        _viewModel.Player.RenderModeChanged += OnRenderModeChanged;
        _viewModel.Player.AudioBoostChanged += OnAudioBoostChanged;
        _viewModel.Player.SeekRequested += ts =>
        {
            _eofStalledTicks = 0;
            _eofNotifiedForCurrentUrl = false;
            _ = FrameFluxPlayer.SeekAsync(ts);
        };
        _viewModel.Player.PropertyChanged += OnPlayerPropertyChanged;
    }

    private void OnPlaybackSyncTick(object? sender, EventArgs e)
    {
        if (Environment.TickCount64 - _lastDownloadRefreshTick >= 500)
        {
            _lastDownloadRefreshTick = Environment.TickCount64;
            _viewModel.RefreshDownloadProgress();
        }
        if (!_viewModel.Player.IsOpen)
            return;

        if (_viewModel.Player.IsNativeSurfaceMode && !_viewModel.Player.IsAnyMenuOpen)
        {
            UpdateNativeSurfaceControlsVisibility();
        }

        var isLeftDown = OperatingSystem.IsWindows() && (GetAsyncKeyState(0x01) & 0x8000) != 0;
        var freshLeftClick = isLeftDown && !_wasNativeLeftMouseDown;
        _wasNativeLeftMouseDown = isLeftDown;
        if (!isLeftDown)
            _isDockedMenuPointerCaptured = false;

        // In NativeSurface mode, child HWND consumes clicks directly without raising MainWindow PointerPressed.
        // Dismiss any open menu popup only on a fresh click over the video surface itself (outside popup & bar).
        if (_viewModel.Player.IsAnyMenuOpen
            && _viewModel.Player.IsNativeSurfaceMode
            && freshLeftClick
            && !_isDockedMenuPointerCaptured
            && !DockedMenuPopup.IsPointerOverPopup
            && !IsCursorOverVisual(DockedMenuPanel)
            && IsCursorOverVisual(PlayerViewportBox)
            && !IsCursorOverVisual(DockedTransportBar))
        {
            _viewModel.Player.CloseMenu();
        }

        if (!_viewModel.Player.HasRealMediaUrl || _awaitingSourcePlayback)
            return;

        var pos = FrameFluxPlayer.Position;
        var dur = FrameFluxPlayer.Duration;
        _viewModel.Player.SyncFromPlayer(pos, dur);

        if (dur is { TotalSeconds: > 0.5 } durVal)
        {
            if (_viewModel.Player.ConsumePendingResumeSeconds() is { } resumeSec
                && resumeSec < durVal.TotalSeconds - 0.5)
            {
                _ = FrameFluxPlayer.SeekAsync(TimeSpan.FromSeconds(resumeSec));
                _viewModel.Player.NotifyDanmakuSeeked(resumeSec);
                _lastObservedPosSeconds = resumeSec;
                _eofStalledTicks = 0;
                return;
            }

            if (!_eofNotifiedForCurrentUrl
                && _viewModel.Player.IsPlaying
                && !_viewModel.Player.IsResolvingStream
                && FrameFluxPlayer.State == MediaPlaybackState.Playing)
            {
                var posSec = pos.TotalSeconds;
                if (posSec >= durVal.TotalSeconds - 0.15 && Math.Abs(posSec - _lastObservedPosSeconds) < 0.005)
                {
                    _eofStalledTicks++;
                    if (_eofStalledTicks >= 1)
                    {
                        _eofNotifiedForCurrentUrl = true;
                        _viewModel.Player.NotifyPlaybackEnded();
                    }
                }
                else
                {
                    _eofStalledTicks = 0;
                }
                _lastObservedPosSeconds = posSec;
            }
            else
            {
                _eofStalledTicks = 0;
            }
        }
    }

    private void ApplyRenderMode(VideoRenderMode mode)
    {
        FrameFluxPlayer.PresentationMode = mode switch
        {
            VideoRenderMode.GpuComposition => MediaVideoPresentationMode.GpuComposition,
            VideoRenderMode.SoftwareBitmap => MediaVideoPresentationMode.SoftwareBitmap,
            VideoRenderMode.NativeSurface => MediaVideoPresentationMode.NativeSurface,
            _ => MediaVideoPresentationMode.Automatic
        };
    }

    private MediaOpenOptions BuildOpenOptions() => new()
    {
        Network = new MediaNetworkOptions
        {
            InitialBufferDuration = TimeSpan.FromMilliseconds(150),
            RebufferDuration = TimeSpan.FromMilliseconds(750)
        },
        Audio = new MediaAudioOptions
        {
            GainDecibels = _viewModel.Player.AudioBoostDb
        }
    };

    private async void OnRenderModeChanged(VideoRenderMode mode)
    {
        ApplyRenderMode(mode);
        await ReloadCurrentMediaWithOptionsAsync();
    }

    private async void OnAudioBoostChanged(double _)
    {
        await ReloadCurrentMediaWithOptionsAsync();
    }

    private async Task ReloadCurrentMediaWithOptionsAsync()
    {
        _reloadOptionsCts?.Cancel();
        _reloadOptionsCts?.Dispose();
        var cts = _reloadOptionsCts = new CancellationTokenSource();
        var ct = cts.Token;

        try
        {
            var url = _viewModel.Player.ResolvedPlayUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                FrameFluxPlayer.OpenOptions = BuildOpenOptions();
                return;
            }

            // Preserve original position/play state across rapid back-to-back option clicks.
            _pendingOptionsReloadResumePos ??= FrameFluxPlayer.Position.TotalSeconds;
            _pendingOptionsReloadWasPlaying ??= _viewModel.Player.IsPlaying;
            var resumePos = _pendingOptionsReloadResumePos.Value;
            var wasPlaying = _pendingOptionsReloadWasPlaying.Value;

            _awaitingSourcePlayback = true;
            await FrameFluxPlayer.StopAsync();
            if (ct.IsCancellationRequested || _isClosing) return;

            FrameFluxPlayer.Source = null;
            _eofStalledTicks = 0;
            _eofNotifiedForCurrentUrl = false;
            _lastObservedPosSeconds = -1;
            FrameFluxPlayer.OpenOptions = BuildOpenOptions();
            FrameFluxPlayer.PlaybackRate = _viewModel.Player.PlaybackRate;
            ApplyPlayerVolume();
            FrameFluxPlayer.Source = MediaSource.FromUri(url);

            if (resumePos > 0.5)
            {
                await Task.Delay(250, ct);
                if (!ct.IsCancellationRequested && !_isClosing && _viewModel.Player.IsOpen && FrameFluxPlayer.Source is not null)
                    await FrameFluxPlayer.SeekAsync(TimeSpan.FromSeconds(resumePos));
            }

            if (!wasPlaying && !ct.IsCancellationRequested && !_isClosing && _viewModel.Player.IsOpen && FrameFluxPlayer.Source is not null)
            {
                await FrameFluxPlayer.PauseAsync();
                _awaitingSourcePlayback = false;
            }

            if (!ct.IsCancellationRequested)
            {
                _pendingOptionsReloadResumePos = null;
                _pendingOptionsReloadWasPlaying = null;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer option reload or window close.
        }
        catch
        {
            _awaitingSourcePlayback = false;
            _pendingOptionsReloadResumePos = null;
            _pendingOptionsReloadWasPlaying = null;
        }
    }

    private void UpdateNativeSurfaceControlsVisibility()
    {
        if (!OperatingSystem.IsWindows() || !GetCursorPos(out var pt))
            return;

        if (IsNativeCursorInActiveStage(pt))
        {
            _controlsExitHideTimer.Stop();
            var moved = _lastNativeCursorPos is null
                || _lastNativeCursorPos.Value.X != pt.X
                || _lastNativeCursorPos.Value.Y != pt.Y;
            if (moved)
            {
                _lastNativeCursorPos = pt;
                ShowPlayerControls();
            }
            else if (_viewModel.Player.AreControlsVisible && !_controlsIdleHideTimer.IsEnabled && !ShouldKeepControlsVisible())
            {
                _controlsIdleHideTimer.Start();
            }
            return;
        }

        _lastNativeCursorPos = pt;
        if (_viewModel.Player.AreControlsVisible && !_viewModel.Player.IsAnyMenuOpen)
            ScheduleControlsExitHide();
    }

    private bool IsNativeCursorInActiveStage(WinPoint pt)
    {
        if (_viewModel.Player.IsMiniMode)
            return IsPointOverVisual(PlayerStageWrap, pt)
                || DockedMenuPopup.IsPointerOverPopup
                || IsPointOverVisual(DockedMenuPanel, pt);

        if (IsPointOverVisual(PlayerViewportBox, pt))
            return true;

        if (!_viewModel.Player.AreControlsVisible)
            return false;

        if (IsPointOverVisual(DockedTransportBar, pt)
            || IsPointOverVisual(FloatingTransportBar, pt)
            || IsPointOverVisual(FloatingMenuPanel, pt)
            || IsPointOverVisual(DockedMenuPanel, pt)
            || DockedMenuPopup.IsPointerOverPopup)
            return true;

        // Bridge the small gap between PlayerViewportBox and DockedTransportBar when docked controls shrink the viewport.
        return IsPointOverVisual(PlayerStageWrap, pt) && IsPointHorizontallyWithin(PlayerViewportBox, pt);
    }

    private bool IsVisualReadyForScreenHitTest(Visual? visual)
    {
        if (!OperatingSystem.IsWindows() || visual is null || !IsVisible || !visual.IsEffectivelyVisible)
            return false;
        if (ReferenceEquals(visual, DockedMenuPanel) && !DockedMenuPopup.IsOpen)
            return false;
        return Avalonia.VisualTree.VisualExtensions.IsAttachedToVisualTree(visual)
            && TopLevel.GetTopLevel(visual)?.PlatformImpl is not null;
    }

    private bool IsPointOverVisual(Visual? visual, WinPoint pt)
    {
        if (!IsVisualReadyForScreenHitTest(visual))
            return false;
        if (visual!.Bounds.Width <= 0 || visual.Bounds.Height <= 0)
            return false;
        var tl = visual.PointToScreen(new Point(0, 0));
        var br = visual.PointToScreen(new Point(visual.Bounds.Width, visual.Bounds.Height));
        return pt.X >= tl.X && pt.X <= br.X && pt.Y >= tl.Y && pt.Y <= br.Y;
    }

    private bool IsPointHorizontallyWithin(Visual? visual, WinPoint pt)
    {
        if (!IsVisualReadyForScreenHitTest(visual))
            return false;
        if (visual!.Bounds.Width <= 0)
            return false;
        var tl = visual.PointToScreen(new Point(0, 0));
        var br = visual.PointToScreen(new Point(visual.Bounds.Width, visual.Bounds.Height));
        return pt.X >= tl.X && pt.X <= br.X;
    }

    private bool IsCursorOverVisual(Visual? visual) =>
        OperatingSystem.IsWindows() && GetCursorPos(out var pt) && IsPointOverVisual(visual, pt);

    private void OnDockedMenuPopupClosed(object? sender, EventArgs e)
    {
        _isDockedMenuPointerCaptured = false;
        _viewModel.Player.CloseMenu();
        ScheduleControlsExitHide();
        ResetControlsIdleTimer();
    }

    private void OnWindowTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel.Player.IsOpen)
        {
            if (_viewModel.Player.IsMiniMode && IsWithinPlayerStage(e.Source))
            {
                _stagePointer = e.Pointer;
                ShowPlayerControls(e.GetPosition(PlayerStageWrap));
            }
            else if (!_viewModel.Player.IsMiniMode && IsWithinPlayerViewportOrControls(e.Source))
            {
                _stagePointer = e.Pointer;
                ShowPlayerControls(e.GetPosition(PlayerStageWrap));
            }
        }
        if (!_viewModel.Player.IsAnyMenuOpen)
            return;

        if (IsWithinMenuOrTrigger(e.Source))
            return;

        _viewModel.Player.CloseMenu();
        if (_viewModel.Player.IsMiniMode)
            _suppressMiniDragOnce = true;
    }

    private bool IsWithinMenuOrTrigger(object? source)
    {
        for (var current = source as Visual; current is not null; current = Avalonia.VisualTree.VisualExtensions.GetVisualParent(current))
        {
            if (ReferenceEquals(current, FloatingMenuPanel) || ReferenceEquals(current, DockedMenuPanel))
                return true;
            if (current is Button { Name: "RenderModeBtn" or "SpeedSelectBtn" or "QualitySelectBtn" or "VolumeBtn" })
                return true;
        }
        return false;
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsOpen))
        {
            if (_viewModel.Player.IsOpen)
            {
                _lastStagePointerPos = null;
                _lastNativeCursorPos = null;
                ShowPlayerControls();
            }
            else
            {
                _pausedByBossKey = false;
                _controlsExitHideTimer.Stop();
                _controlsIdleHideTimer.Stop();
                _volumeOsdHideTimer.Stop();
                _viewModel.Player.IsVolumeOsdVisible = false;
            }
        }
        else if (e.PropertyName == nameof(PlayerViewModel.IsAnyMenuOpen))
        {
            if (_viewModel.Player.IsAnyMenuOpen)
            {
                _controlsExitHideTimer.Stop();
                _controlsIdleHideTimer.Stop();
            }
            else
            {
                ResetControlsIdleTimer();
                ScheduleControlsExitHide();
            }
        }

        if (e.PropertyName == nameof(PlayerViewModel.ResolvedPlayUrl))
        {
            _eofStalledTicks = 0;
            _eofNotifiedForCurrentUrl = false;
            _lastObservedPosSeconds = -1;

            var url = _viewModel.Player.ResolvedPlayUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                FrameFluxPlayer.Source = null;
                _awaitingSourcePlayback = false;
            }
            else
            {
                _awaitingSourcePlayback = true;
                FrameFluxPlayer.PlaybackRate = _viewModel.Player.PlaybackRate;
                ApplyPlayerVolume();
                FrameFluxPlayer.Source = MediaSource.FromUri(url);
            }
        }
        else if (e.PropertyName == nameof(PlayerViewModel.IsPlaying) && FrameFluxPlayer.Source is not null && !_awaitingSourcePlayback)
        {
            if (_viewModel.Player.IsPlaying)
                _ = FrameFluxPlayer.ResumeAsync();
            else
                _ = FrameFluxPlayer.PauseAsync();
        }
        else if (e.PropertyName == nameof(PlayerViewModel.PlaybackRate))
        {
            FrameFluxPlayer.PlaybackRate = _viewModel.Player.PlaybackRate;
        }
        else if (e.PropertyName is nameof(PlayerViewModel.Volume) or nameof(PlayerViewModel.IsMuted))
        {
            ApplyPlayerVolume();
            ShowVolumeOsd();
        }
        else if (e.PropertyName is nameof(PlayerViewModel.UseNativeDanmakuOverlay)
                 or nameof(PlayerViewModel.IsDanmakuEnabled)
                 or nameof(PlayerViewModel.HasRealMediaUrl)
                 or nameof(PlayerViewModel.IsOpen))
        {
            SyncNativeDanmakuOverlay();
        }
    }

    private void SyncNativeDanmakuOverlay()
    {
        if (!OperatingSystem.IsWindows()
            || _isClosing
            || !IsVisible
            || WindowState == WindowState.Minimized
            || !_viewModel.Player.UseNativeDanmakuOverlay
            || !IsVisualReadyForScreenHitTest(PlayerViewportBox)
            || PlayerViewportBox.Bounds.Width <= 1
            || PlayerViewportBox.Bounds.Height <= 1)
        {
            if (_nativeDanmakuWindow is { IsVisible: true })
                _nativeDanmakuWindow.Hide();
            return;
        }

        if (_nativeDanmakuWindow is null)
        {
            _nativeDanmakuWindow = new DanmakuNativeOverlayWindow();
            _nativeDanmakuWindow.Overlay.SetPlayer(_viewModel.Player);
        }

        var origin = PlayerViewportBox.PointToScreen(new Point(0, 0));
        _nativeDanmakuWindow.SyncBounds(origin, PlayerViewportBox.Bounds.Width, PlayerViewportBox.Bounds.Height, Topmost);
        if (!_nativeDanmakuWindow.IsVisible)
            _nativeDanmakuWindow.Show(this);
    }

    private void ApplyPlayerVolume()
    {
        FrameFluxPlayer.Volume = _viewModel.Player.Volume;
        FrameFluxPlayer.IsMuted = _viewModel.Player.IsMuted;
    }

    private void ShowVolumeOsd()
    {
        if (!_viewModel.Player.IsOpen)
            return;
        _viewModel.Player.IsVolumeOsdVisible = true;
        _volumeOsdHideTimer.Stop();
        _volumeOsdHideTimer.Start();
    }

    private void OnVolumeOsdHideTick(object? sender, EventArgs e)
    {
        _volumeOsdHideTimer.Stop();
        _viewModel.Player.IsVolumeOsdVisible = false;
    }

    private void OnFrameFluxPlaybackError(object? sender, MediaPlaybackErrorEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
            _viewModel.Player.ReportPlaybackError(e.Error?.Message));
    }

    private void OnFrameFluxPlaybackStateChanged(object? sender, MediaPlaybackStateChangedEventArgs e)
    {
        if (e.NewState == MediaPlaybackState.Playing)
        {
            _awaitingSourcePlayback = false;
            _viewModel.Player.ReleaseRetiredProxySessions();
        }
        if (_awaitingSourcePlayback) return;
        if (e.NewState == MediaPlaybackState.Stopped && e.OldState == MediaPlaybackState.Playing && !_eofNotifiedForCurrentUrl)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var dur = FrameFluxPlayer.Duration?.TotalSeconds ?? _viewModel.Player.DurationSeconds;
                var pos = Math.Max(FrameFluxPlayer.Position.TotalSeconds, _lastObservedPosSeconds);
                if (!_awaitingSourcePlayback && !_eofNotifiedForCurrentUrl && dur > 0.5 && pos >= dur - 0.5)
                {
                    _eofNotifiedForCurrentUrl = true;
                    _viewModel.Player.NotifyPlaybackEnded();
                }
            });
        }
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;
        var h = e.NewSize.Height;
        if (!_viewModel.Player.IsMiniMode && !_isTransitioningMini && w >= 600)
        {
            _normalWidth = w;
            _normalHeight = h;
        }

        _viewModel.LayoutMode = w switch
        {
            >= 920 => WindowLayoutMode.Wide,
            >= 620 => WindowLayoutMode.CompactRail,
            _ => WindowLayoutMode.Narrow
        };

        UpdatePlayerSplitLayout();
        if (!_isInWin32SizeMove)
            RememberWindowBounds();

        if (!OperatingSystem.IsWindows()
            && _viewModel.Player.IsMiniMode
            && !_isTransitioningMini
            && !_isAdjustingMiniAspect
            && !_isInWin32SizeMove
            && !_isClosing
            && WindowState == WindowState.Normal
            && w > 0
            && h > 0)
        {
            var ratio = WindowPlacementPolicy.GetVideoAspectRatio(_viewModel.Player.StreamWidth, _viewModel.Player.StreamHeight);
            if (Math.Abs((w / h) - ratio) > 0.025)
                Dispatcher.UIThread.Post(SyncMiniWindowToVideoAspect, DispatcherPriority.Render);
        }
    }

    private void UpdatePlayerSplitLayout()
    {
        if (PlayerSplitGrid is null || PlayerSidePanel is null) return;

        var isMini = _viewModel.Player.IsMiniMode;
        var layoutMode = _viewModel.LayoutMode;
        if (_appliedSplitMiniMode == isMini && (isMini || _appliedSplitLayoutMode == layoutMode))
            return;

        _appliedSplitMiniMode = isMini;
        _appliedSplitLayoutMode = layoutMode;

        if (isMini)
        {
            PlayerSplitGrid.ColumnSpacing = 0;
            PlayerSplitGrid.RowSpacing = 0;
            PlayerSplitGrid.Margin = new Thickness(0);
            PlayerStageWrap.CornerRadius = new CornerRadius(0);
            PlayerStageWrap.BorderThickness = new Thickness(0);
            PlayerViewportBox.CornerRadius = new CornerRadius(0);
            return;
        }

        PlayerSplitGrid.Margin = new Thickness(16);
        PlayerStageWrap.CornerRadius = new CornerRadius(18);
        PlayerStageWrap.BorderThickness = new Thickness(1);
        PlayerViewportBox.CornerRadius = new CornerRadius(16);

        if (layoutMode == WindowLayoutMode.Narrow)
        {
            PlayerSplitGrid.ColumnSpacing = 0;
            PlayerSplitGrid.RowSpacing = 14;
            Grid.SetColumn(PlayerSidePanel, 0);
            Grid.SetRow(PlayerSidePanel, 1);
            PlayerSidePanel.Width = double.NaN;
            PlayerSidePanel.Height = 220;
        }
        else
        {
            PlayerSplitGrid.ColumnSpacing = 16;
            PlayerSplitGrid.RowSpacing = 0;
            Grid.SetColumn(PlayerSidePanel, 1);
            Grid.SetRow(PlayerSidePanel, 0);
            PlayerSidePanel.Width = 310;
            PlayerSidePanel.Height = double.NaN;
        }
    }

    private void OnStreamDimensionsChanged()
    {
        FitPlayerViewport();
        if (_viewModel.Player.IsMiniMode && !_isTransitioningMini && !_isClosing)
            SyncMiniWindowToVideoAspect();
    }

    private void OnPlayerStageSizeChanged(object? sender, SizeChangedEventArgs e) => FitPlayerViewport();

    private void UpdateWindowMinSize(bool isMini)
    {
        if (!isMini)
        {
            MinWidth = WindowPlacementPolicy.NormalMinWidth;
            MinHeight = WindowPlacementPolicy.NormalMinHeight;
            return;
        }

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var scale = screen?.Scaling ?? RenderScaling;
        var workArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var ratio = WindowPlacementPolicy.GetVideoAspectRatio(_viewModel.Player.StreamWidth, _viewModel.Player.StreamHeight);
        var (minW, minH, _, _) = WindowPlacementPolicy.GetMiniSizeBounds(workArea, scale, ratio);
        MinWidth = minW;
        MinHeight = minH;
    }

    private void SyncMiniWindowToVideoAspect()
    {
        if (!_viewModel.Player.IsMiniMode || _isTransitioningMini || _isClosing || WindowState == WindowState.Minimized)
            return;

        UpdateWindowMinSize(true);
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var scale = screen?.Scaling ?? RenderScaling;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var workArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

        var curBounds = Bounds;
        var saved = curBounds.Width > 0 && curBounds.Height > 0
            ? new WindowPlacement(Position.X, Position.Y, curBounds.Width, curBounds.Height)
            : _miniPlacement;
        Point? center = curBounds.Width > 0 && curBounds.Height > 0
            ? new Point(Position.X + (curBounds.Width * scale) / 2.0, Position.Y + (curBounds.Height * scale) / 2.0)
            : null;

        var target = WindowPlacementPolicy.FitMiniForAspect(
            saved, Position, center, workArea, scale,
            _viewModel.Player.StreamWidth, _viewModel.Player.StreamHeight);

        ApplyMiniAspectPlacement(target);
    }

    private void ApplyMiniAspectPlacement(WindowPlacement target)
    {
        _isAdjustingMiniAspect = true;
        try
        {
            Width = target.Width;
            Height = target.Height;
            Position = new PixelPoint(target.X, target.Y);
        }
        finally
        {
            _isAdjustingMiniAspect = false;
        }
        _miniPlacement = target;
        FitPlayerViewport();
    }

    private IntPtr OnWin32WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const uint WmSizing = 0x0214;
        const uint WmEnterSizeMove = 0x0231;
        const uint WmExitSizeMove = 0x0232;

        if (msg == WmEnterSizeMove)
        {
            _isInWin32SizeMove = true;
            return IntPtr.Zero;
        }

        if (msg == WmExitSizeMove)
        {
            _isInWin32SizeMove = false;
            RememberWindowBounds();
            return IntPtr.Zero;
        }

        if (msg != WmSizing || lParam == IntPtr.Zero || !_viewModel.Player.IsMiniMode || _isTransitioningMini || _isClosing)
            return IntPtr.Zero;

        if (!GetWindowRect(hWnd, out var wr) || !GetClientRect(hWnd, out var cr))
            return IntPtr.Zero;

        var frameW = Math.Max(0, (wr.Right - wr.Left) - (cr.Right - cr.Left));
        var frameH = Math.Max(0, (wr.Bottom - wr.Top) - (cr.Bottom - cr.Top));
        var left = Marshal.ReadInt32(lParam, 0);
        var top = Marshal.ReadInt32(lParam, 4);
        var right = Marshal.ReadInt32(lParam, 8);
        var bottom = Marshal.ReadInt32(lParam, 12);

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var scale = screen?.Scaling ?? RenderScaling;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var workArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var ratio = WindowPlacementPolicy.GetVideoAspectRatio(_viewModel.Player.StreamWidth, _viewModel.Player.StreamHeight);
        var (minDipW, minDipH, maxDipW, maxDipH) = WindowPlacementPolicy.GetMiniSizeBounds(workArea, scale, ratio);

        var minCW = Math.Max(1, (int)Math.Round(minDipW * scale));
        var minCH = Math.Max(1, (int)Math.Round(minDipH * scale));
        var maxCW = Math.Max(minCW, (int)Math.Floor(maxDipW * scale));
        var maxCH = Math.Max(minCH, (int)Math.Floor(maxDipH * scale));

        var edge = (int)wParam;
        var proposedCW = (right - left) - frameW;
        var proposedCH = (bottom - top) - frameH;

        var (cw, ch) = WindowPlacementPolicy.SnapMiniDimensions(
            proposedCW, proposedCH, edge,
            minCW, minCH, maxCW, maxCH, ratio);

        var newOuterW = cw + frameW;
        var newOuterH = ch + frameH;

        if (edge is WindowPlacementPolicy.EdgeLeft or WindowPlacementPolicy.EdgeTopLeft or WindowPlacementPolicy.EdgeBottomLeft)
            left = right - newOuterW;
        else
            right = left + newOuterW;

        if (edge is WindowPlacementPolicy.EdgeTop or WindowPlacementPolicy.EdgeTopLeft or WindowPlacementPolicy.EdgeTopRight)
            top = bottom - newOuterH;
        else
            bottom = top + newOuterH;

        Marshal.WriteInt32(lParam, 0, left);
        Marshal.WriteInt32(lParam, 4, top);
        Marshal.WriteInt32(lParam, 8, right);
        Marshal.WriteInt32(lParam, 12, bottom);
        handled = true;
        return new IntPtr(1);
    }

    private void FitPlayerViewport()
    {
        if (PlayerViewportHost is null || PlayerViewportBox is null) return;

        var pad = _viewModel.Player.IsMiniMode ? 0 : (_viewModel.Player.IsNativeSurfaceMode ? 12 : 20);
        var availW = Math.Max(120, PlayerViewportHost.Bounds.Width - pad);
        var availH = Math.Max(120, PlayerViewportHost.Bounds.Height - pad);
        var sw = Math.Max(1, _viewModel.Player.StreamWidth);
        var sh = Math.Max(1, _viewModel.Player.StreamHeight);
        var ratio = (double)sw / sh;

        var fitW = availW;
        var fitH = fitW / ratio;
        if (fitH > availH)
        {
            fitH = availH;
            fitW = fitH * ratio;
        }

        var targetW = Math.Round(fitW);
        var targetH = Math.Round(fitH);
        if (Math.Abs(PlayerViewportBox.Width - targetW) >= 0.5)
            PlayerViewportBox.Width = targetW;
        if (Math.Abs(PlayerViewportBox.Height - targetH) >= 0.5)
            PlayerViewportBox.Height = targetH;
    }

    private void OnMiniModeChanged(bool isMini, bool isLandscape)
    {
        if (_isClosing) return;
        _controlsExitHideTimer.Stop();
        _controlsIdleHideTimer.Stop();
        _stagePointer = null;
        _lastStagePointerPos = null;
        _lastNativeCursorPos = null;
        RememberWindowBounds();
        SaveWindowPlacements();
        _isTransitioningMini = true;
        _displayedMini = isMini;

        WindowPlacement target;
        double targetScale = 1;
        if (isMini)
        {
            var refPt = _miniPlacement is { } mp ? new PixelPoint(mp.X, mp.Y) : Position;
            var screen = Screens.ScreenFromPoint(refPt) ?? Screens.Primary;
            targetScale = screen?.Scaling ?? RenderScaling;
            if (!double.IsFinite(targetScale) || targetScale <= 0) targetScale = 1;
            var workArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            target = WindowPlacementPolicy.FitMiniForAspect(
                _miniPlacement, Position, null, workArea, targetScale,
                _viewModel.Player.StreamWidth, _viewModel.Player.StreamHeight);
            if (OperatingSystem.IsWindows() && !IsScreenCursorWithinPlacement(target, targetScale))
                _viewModel.Player.SetControlsVisible(false);
        }
        else
        {
            target = _normalPlacement ?? new WindowPlacement(Position.X, Position.Y, _normalWidth, _normalHeight);
        }

        WindowState = WindowState.Normal;
        if (isMini)
        {
            WindowDecorations = Avalonia.Controls.WindowDecorations.BorderOnly;
            Topmost = _viewModel.Player.IsMiniPinned;
        }
        else
        {
            Topmost = false;
            WindowDecorations = Avalonia.Controls.WindowDecorations.Full;
        }
        UpdateWindowMinSize(isMini);
        ApplyWindowPlacement(target, isMini);
        UpdatePlayerSplitLayout();
        FitPlayerViewport();
        Dispatcher.UIThread.Post(() =>
        {
            if (_displayedMini != isMini || _isClosing) return;
            UpdateWindowMinSize(isMini);
            ApplyWindowPlacement(target, isMini);
            UpdatePlayerSplitLayout();
            FitPlayerViewport();
            _isTransitioningMini = false;
            RememberWindowBounds();
            if (isMini)
            {
                if (IsCursorInActiveStage())
                {
                    if (OperatingSystem.IsWindows() && GetCursorPos(out var pt))
                        _lastNativeCursorPos = pt;
                    ShowPlayerControls();
                }
                else
                {
                    _controlsExitHideTimer.Stop();
                    _controlsIdleHideTimer.Stop();
                    _viewModel.Player.SetControlsVisible(false);
                }
            }
            else
            {
                ResetControlsIdleTimer();
            }
        }, DispatcherPriority.Background);
    }

    private static bool IsScreenCursorWithinPlacement(WindowPlacement placement, double scale)
    {
        if (!OperatingSystem.IsWindows() || !GetCursorPos(out var pt))
            return false;
        var right = placement.X + (int)Math.Ceiling(placement.Width * scale);
        var bottom = placement.Y + (int)Math.Ceiling(placement.Height * scale);
        return pt.X >= placement.X && pt.X <= right && pt.Y >= placement.Y && pt.Y <= bottom;
    }

    private void RememberWindowBounds()
    {
        if (_isTransitioningMini || _isAdjustingMiniAspect || _isClosing || !IsVisible || WindowState == WindowState.Minimized) return;
        if (!_displayedMini && WindowState == WindowState.Maximized)
        {
            if (_normalPlacement is { } normal) _normalPlacement = normal with { IsMaximized = true };
            return;
        }
        var placement = new WindowPlacement(Position.X, Position.Y, Bounds.Width, Bounds.Height);
        if (_displayedMini) _miniPlacement = placement;
        else _normalPlacement = placement;
    }

    private void SaveWindowPlacements() => _settings.Update(_settings.Current with
    {
        NormalWindow = _normalPlacement,
        MiniWindow = _miniPlacement
    });

    private void ApplyWindowPlacement(WindowPlacement placement, bool mini)
    {
        var screen = Screens.ScreenFromPoint(new PixelPoint(placement.X, placement.Y)) ?? Screens.Primary;
        if (screen is not null) placement = WindowPlacementPolicy.Fit(placement, screen.WorkingArea, screen.Scaling, mini);
        Width = placement.Width;
        Height = placement.Height;
        Position = new PixelPoint(placement.X, placement.Y);
        WindowState = !mini && placement.IsMaximized ? WindowState.Maximized : WindowState.Normal;
    }

    private static bool IsInteractiveControl(object? source)
    {
        for (var current = source as Visual; current is not null; current = Avalonia.VisualTree.VisualExtensions.GetVisualParent(current))
        {
            if (current is Button or TextBox)
                return true;
        }
        return false;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsInteractiveControl(e.Source))
            return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
            e.Handled = true;
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsInteractiveControl(e.Source) || _viewModel.Player.IsMiniMode)
            return;
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnMinimizeWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeToggleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close();

    private void UpdateMaximizeIcons()
    {
        var isMax = WindowState == WindowState.Maximized;
        WinMaxIcon.IsVisible = !isMax;
        WinRestoreIcon.IsVisible = isMax;
        PlayerMaxIcon.IsVisible = !isMax;
        PlayerRestoreIcon.IsVisible = isMax;
    }

    private void OnStagePointerEntered(object? sender, PointerEventArgs e)
    {
        _stagePointer = e.Pointer;
        if (_isTransitioningMini) return;
        if (_viewModel.Player.IsOpen && _viewModel.Player.IsMiniMode
            && (!OperatingSystem.IsWindows() || IsCursorInActiveStage()))
            ShowPlayerControls(e.GetPosition(PlayerStageWrap));
    }

    private void OnStagePointerExited(object? sender, PointerEventArgs e)
    {
        _stagePointer = e.Pointer;
        if (_viewModel.Player.IsOpen)
            ScheduleControlsExitHide();
    }

    private void OnViewportPointerEntered(object? sender, PointerEventArgs e)
    {
        _stagePointer = e.Pointer;
        if (_isTransitioningMini) return;
        if (_viewModel.Player.IsOpen && !_viewModel.Player.IsMiniMode)
            ShowPlayerControls(e.GetPosition(PlayerStageWrap));
    }

    private void OnViewportPointerExited(object? sender, PointerEventArgs e)
    {
        _stagePointer = e.Pointer;
        if (_viewModel.Player.IsOpen && !_viewModel.Player.IsMiniMode)
            ScheduleControlsExitHide();
    }

    private void OnStageControlPointerEntered(object? sender, PointerEventArgs e)
    {
        _stagePointer = e.Pointer;
        if (!_viewModel.Player.IsOpen) return;
        _controlsExitHideTimer.Stop();
        _controlsIdleHideTimer.Stop();
    }

    private void OnStageControlPointerExited(object? sender, PointerEventArgs e)
    {
        _stagePointer = e.Pointer;
        if (!_viewModel.Player.IsOpen) return;
        ScheduleControlsExitHide();
        ResetControlsIdleTimer();
    }

    private void OnStagePointerMoved(object? sender, PointerEventArgs e)
    {
        _stagePointer = e.Pointer;
        if (_isTransitioningMini || !_viewModel.Player.IsOpen || _viewModel.Player.IsNativeSurfaceMode)
            return;

        var pos = e.GetPosition(PlayerStageWrap);
        if (_lastStagePointerPos is { } last
            && Math.Abs(pos.X - last.X) < 1
            && Math.Abs(pos.Y - last.Y) < 1)
            return;

        _lastStagePointerPos = pos;
        if (_viewModel.Player.IsMiniMode)
        {
            if (!OperatingSystem.IsWindows() || IsCursorInActiveStage())
                ShowPlayerControls(pos);
        }
        else if (IsWithinPlayerViewportOrControls(e.Source))
            ShowPlayerControls(pos);
        else if (_viewModel.Player.AreControlsVisible)
            ScheduleControlsExitHide();
    }

    private void ShowPlayerControls(Point? pointerPos = null)
    {
        if (pointerPos is { } pos)
            _lastStagePointerPos = pos;
        _controlsExitHideTimer.Stop();
        _viewModel.Player.SetControlsVisible(true);
        ResetControlsIdleTimer();
    }

    private void ResetControlsIdleTimer()
    {
        _controlsIdleHideTimer.Stop();
        if (!_viewModel.Player.IsOpen || _isClosing || ShouldKeepControlsVisible())
            return;
        if (_viewModel.Player.AreControlsVisible)
            _controlsIdleHideTimer.Start();
    }

    private void ScheduleControlsExitHide()
    {
        if (_viewModel.Player.IsOpen && _viewModel.Player.AreControlsVisible && !_controlsExitHideTimer.IsEnabled)
            _controlsExitHideTimer.Start();
    }

    private bool ShouldKeepControlsVisible(bool forExitCheck = false)
    {
        if (_viewModel.Player.IsAnyMenuOpen || _isDockedMenuPointerCaptured || DockedMenuPopup.IsPointerOverPopup)
            return true;
        if (_viewModel.Player.HasStreamError)
            return true;
        if (IsWithinPlayerStage(_stagePointer?.Captured))
            return true;
        if (!forExitCheck && _viewModel.Player.AreControlsVisible && IsPointerOverControls())
            return true;
        return false;
    }

    private bool IsPointerOverControls()
    {
        if (OperatingSystem.IsWindows() && GetCursorPos(out var pt))
        {
            return IsPointOverVisual(FloatingTransportBar, pt)
                || IsPointOverVisual(DockedTransportBar, pt)
                || IsPointOverVisual(FloatingMenuPanel, pt)
                || IsPointOverVisual(DockedMenuPanel, pt)
                || IsPointOverVisual(FloatingMiniTopBar, pt)
                || IsPointOverVisual(DockedMiniTopBar, pt)
                || DockedMenuPopup.IsPointerOverPopup;
        }

        return FloatingTransportBar.IsPointerOver
            || DockedTransportBar.IsPointerOver
            || FloatingMenuPanel.IsPointerOver
            || FloatingMiniTopBar.IsPointerOver
            || DockedMiniTopBar.IsPointerOver
            || DockedMenuPopup.IsPointerOverPopup;
    }

    private bool IsCursorInActiveStage()
    {
        if (OperatingSystem.IsWindows() && GetCursorPos(out var pt))
            return IsNativeCursorInActiveStage(pt);

        if (_viewModel.Player.IsMiniMode)
            return PlayerStageWrap.IsPointerOver || DockedMenuPopup.IsPointerOverPopup;

        return PlayerViewportBox.IsPointerOver
            || (_viewModel.Player.AreControlsVisible && IsPointerOverControls());
    }

    private bool IsWithinPlayerStage(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = Avalonia.VisualTree.VisualExtensions.GetVisualParent(visual))
            if (ReferenceEquals(visual, PlayerStageWrap)) return true;
        return false;
    }

    private bool IsWithinPlayerViewportOrControls(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = Avalonia.VisualTree.VisualExtensions.GetVisualParent(visual))
        {
            if (ReferenceEquals(visual, PlayerViewportBox)
                || ReferenceEquals(visual, DockedTransportBar)
                || ReferenceEquals(visual, FloatingTransportBar)
                || ReferenceEquals(visual, FloatingMenuPanel)
                || ReferenceEquals(visual, DockedMenuPanel))
                return true;
        }
        return false;
    }

    private void OnControlsExitHideTick(object? sender, EventArgs e)
    {
        _controlsExitHideTimer.Stop();
        if (!_viewModel.Player.IsOpen || _isClosing) return;

        // Button/slider capture can produce PointerExited before release. Keep the control
        // alive until its click/drag completes, even if the pointer has left the window.
        if (IsWithinPlayerStage(_stagePointer?.Captured))
        {
            ScheduleControlsExitHide();
            return;
        }
        if (ShouldKeepControlsVisible(forExitCheck: true)) return;
        if (!IsCursorInActiveStage())
        {
            _controlsIdleHideTimer.Stop();
            _viewModel.Player.SetControlsVisible(false);
        }
    }

    private void OnControlsIdleHideTick(object? sender, EventArgs e)
    {
        _controlsIdleHideTimer.Stop();
        if (!_viewModel.Player.IsOpen || !_viewModel.Player.AreControlsVisible || _isClosing)
            return;

        if (ShouldKeepControlsVisible())
        {
            _controlsIdleHideTimer.Start();
            return;
        }

        _viewModel.Player.SetControlsVisible(false);
    }

    private void OnStageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!_viewModel.Player.IsMiniMode || IsInteractiveControl(e.Source))
            return;
        ToggleControlsAndSyncIdle();
        e.Handled = true;
    }

    private void OnStagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_suppressMiniDragOnce)
        {
            _suppressMiniDragOnce = false;
            return;
        }
        if (!_viewModel.Player.IsOpen || IsInteractiveControl(e.Source))
            return;

        if (!_viewModel.Player.IsMiniMode)
        {
            if (IsWithinPlayerViewportOrControls(e.Source))
                ShowPlayerControls(e.GetPosition(PlayerStageWrap));
            return;
        }

        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            ToggleControlsAndSyncIdle();
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void ToggleControlsAndSyncIdle()
    {
        if (OperatingSystem.IsWindows() && GetCursorPos(out var pt))
            _lastNativeCursorPos = pt;
        _viewModel.Player.ToggleControlsVisibility();
        if (_viewModel.Player.AreControlsVisible)
            ResetControlsIdleTimer();
        else
            _controlsIdleHideTimer.Stop();
    }

    private void OnStagePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!_viewModel.Player.IsOpen || !_viewModel.Player.IsMiniMode || _isTransitioningMini || _isClosing)
            return;
        if (IsWithinMenuOrTrigger(e.Source) || Math.Abs(e.Delta.Y) < 0.01)
            return;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var scale = screen?.Scaling ?? RenderScaling;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var workArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

        var current = new WindowPlacement(Position.X, Position.Y, Bounds.Width, Bounds.Height);
        var steps = Math.Clamp(e.Delta.Y, -3, 3);
        var zoomFactor = Math.Pow(1.08, steps);
        var target = WindowPlacementPolicy.ScaleMiniPlacement(
            current, zoomFactor, workArea, scale,
            _viewModel.Player.StreamWidth, _viewModel.Player.StreamHeight);

        UpdateWindowMinSize(true);
        ApplyMiniAspectPlacement(target);
        e.Handled = true;
    }

    private void OnGlobalSearchGotFocus(object? sender, FocusChangedEventArgs e)
    {
        _viewModel.FocusSearch();
    }

    private void OnGlobalHotkeyTriggered(HotkeyAction action)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosing) return;
            switch (action)
            {
                case HotkeyAction.GlobalBossKey:
                    ToggleBossHide();
                    break;
                case HotkeyAction.GlobalPlayPause:
                    if (_viewModel.Player.IsOpen)
                        _viewModel.Player.IsPlaying = !_viewModel.Player.IsPlaying;
                    break;
            }
        });
    }

    public void ToggleBossHide()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            if (_viewModel.Player.IsOpen && _viewModel.Player.IsPlaying)
            {
                _pausedByBossKey = true;
                _viewModel.Player.IsPlaying = false;
            }
            else
            {
                _pausedByBossKey = false;
            }
            RememberWindowBounds();
            SaveWindowPlacements();
            Hide();
            return;
        }

        RestoreVisibleWindow();
    }

    public void BringToForeground()
    {
        if (_isClosing) return;
        RestoreVisibleWindow();
        if (OperatingSystem.IsWindows())
        {
            if (!Topmost)
            {
                Topmost = true;
                Topmost = false;
            }
            if (TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero)
                SetForegroundWindow(hwnd);
        }
    }

    private void RestoreVisibleWindow()
    {
        var savedMini = _miniPlacement;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        if (_viewModel.Player.IsMiniMode)
        {
            WindowDecorations = Avalonia.Controls.WindowDecorations.BorderOnly;
            Topmost = _viewModel.Player.IsMiniPinned;
            UpdateWindowMinSize(true);
            if (savedMini is { } mp)
                ApplyWindowPlacement(mp, true);
            UpdatePlayerSplitLayout();
            FitPlayerViewport();
        }
        Activate();
        if (_pausedByBossKey && _viewModel.Player.IsOpen && _viewModel.Player.HasRealMediaUrl)
        {
            _pausedByBossKey = false;
            _viewModel.Player.IsPlaying = true;
        }
    }

    private void RestoreWindowFromHotkey()
    {
        RestoreVisibleWindow();

        if (_viewModel.Player.IsMiniMode)
        {
            ((System.Windows.Input.ICommand)_viewModel.Player.ToggleMiniModeCommand).Execute(null);
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
        }
        else if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel.ActiveRecordingAction is not null)
            return;
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;
        if (!KeyNameMapper.TryFromAvalonia(e.Key, e.KeyModifiers, out var pressed))
            return;

        var hotkeys = _viewModel.Hotkeys;
        var modifiers = pressed.Modifiers;
        var keyName = pressed.NormalizedKeyName;

        if (hotkeys.GetBinding(HotkeyAction.RestoreWindow).Matches(modifiers, keyName))
        {
            RestoreWindowFromHotkey();
            e.Handled = true;
            return;
        }

        if (_viewModel.Player.IsOpen)
        {
            if (hotkeys.GetBinding(HotkeyAction.PlayPause).Matches(modifiers, keyName))
            {
                _viewModel.Player.IsPlaying = !_viewModel.Player.IsPlaying;
                ShowPlayerControls();
                e.Handled = true;
                return;
            }
            if (hotkeys.GetBinding(HotkeyAction.SeekBackward).Matches(modifiers, keyName))
            {
                _viewModel.Player.SeekBy(-5);
                ShowPlayerControls();
                e.Handled = true;
                return;
            }
            if (hotkeys.GetBinding(HotkeyAction.SeekForward).Matches(modifiers, keyName))
            {
                _viewModel.Player.SeekBy(5);
                ShowPlayerControls();
                e.Handled = true;
                return;
            }
            if (hotkeys.GetBinding(HotkeyAction.SpeedUp).Matches(modifiers, keyName))
            {
                _viewModel.Player.StepSpeed(1);
                ShowPlayerControls();
                e.Handled = true;
                return;
            }
            if (hotkeys.GetBinding(HotkeyAction.SpeedDown).Matches(modifiers, keyName))
            {
                _viewModel.Player.StepSpeed(-1);
                ShowPlayerControls();
                e.Handled = true;
                return;
            }
            if (hotkeys.GetBinding(HotkeyAction.SpeedReset).Matches(modifiers, keyName))
            {
                _viewModel.Player.ResetSpeed();
                ShowPlayerControls();
                e.Handled = true;
                return;
            }
            if (hotkeys.GetBinding(HotkeyAction.PreviousEpisode).Matches(modifiers, keyName))
            {
                ((System.Windows.Input.ICommand)_viewModel.Player.PreviousEpisodeCommand).Execute(null);
                ShowPlayerControls();
                e.Handled = true;
                return;
            }
            if (hotkeys.GetBinding(HotkeyAction.NextEpisode).Matches(modifiers, keyName))
            {
                ((System.Windows.Input.ICommand)_viewModel.Player.NextEpisodeCommand).Execute(null);
                ShowPlayerControls();
                e.Handled = true;
                return;
            }
            if (hotkeys.GetBinding(HotkeyAction.VolumeUp).Matches(modifiers, keyName))
            {
                _viewModel.Player.StepVolume(0.05);
                ShowPlayerControls();
                e.Handled = true;
                return;
            }
            if (hotkeys.GetBinding(HotkeyAction.VolumeDown).Matches(modifiers, keyName))
            {
                _viewModel.Player.StepVolume(-0.05);
                ShowPlayerControls();
                e.Handled = true;
                return;
            }
            if (hotkeys.GetBinding(HotkeyAction.ToggleMute).Matches(modifiers, keyName))
            {
                _viewModel.Player.ToggleMute();
                ShowPlayerControls();
                e.Handled = true;
                return;
            }
            if (hotkeys.GetBinding(HotkeyAction.ToggleMiniMode).Matches(modifiers, keyName))
            {
                ((System.Windows.Input.ICommand)_viewModel.Player.ToggleMiniModeCommand).Execute(null);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (_viewModel.Player.IsAnyMenuOpen)
                    _viewModel.Player.CloseMenu();
                else if (_viewModel.Player.IsMiniMode)
                    ((System.Windows.Input.ICommand)_viewModel.Player.ToggleMiniModeCommand).Execute(null);
                else
                    _viewModel.Player.Close();
                e.Handled = true;
            }
        }
    }
}
