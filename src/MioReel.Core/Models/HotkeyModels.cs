namespace MioReel.Core.Models;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Meta = 8
}

public enum HotkeyAction
{
    GlobalBossKey,
    GlobalPlayPause,
    PlayPause,
    SeekBackward,
    SeekForward,
    SpeedUp,
    SpeedDown,
    SpeedReset,
    PreviousEpisode,
    NextEpisode,
    ToggleMiniMode,
    RestoreWindow,
    VolumeUp,
    VolumeDown,
    ToggleMute
}

public sealed record HotkeyRegistrationStatus(
    HotkeyAction Action,
    bool IsRegistered,
    string? ErrorMessage = null);

/// <summary>
/// 平台无关的快捷键组合描述。
/// </summary>
public sealed record HotkeyBinding(
    HotkeyModifiers Modifiers,
    string KeyName)
{
    public static readonly HotkeyBinding Empty = new(HotkeyModifiers.None, string.Empty);

    public bool IsEmpty => string.IsNullOrWhiteSpace(KeyName);

    public bool HasAnyModifier => Modifiers != HotkeyModifiers.None;

    public string NormalizedKeyName => NormalizeKeyName(KeyName);

    public string DisplayText
    {
        get
        {
            if (IsEmpty)
                return "未设置";

            var parts = new List<string>(5);
            if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotkeyModifiers.Meta)) parts.Add(OperatingSystem.IsMacOS() ? "Cmd" : "Win");
            parts.Add(FormatKeyLabel(NormalizedKeyName));
            return string.Join(" + ", parts);
        }
    }

    public bool Matches(HotkeyModifiers modifiers, string keyName) =>
        !IsEmpty
        && Modifiers == modifiers
        && string.Equals(NormalizedKeyName, NormalizeKeyName(keyName), StringComparison.OrdinalIgnoreCase);

    public static string NormalizeKeyName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var trimmed = raw.Trim();
        return trimmed switch
        {
            "Return" => "Enter",
            "Back" => "BackSpace",
            "Prior" => "PageUp",
            "Next" => "PageDown",
            "OemOpenBrackets" => "OemOpenBrackets",
            "OemCloseBrackets" => "OemCloseBrackets",
            _ => trimmed
        };
    }

    public static string FormatKeyLabel(string normalizedKey) => normalizedKey switch
    {
        "Space" => "Space",
        "Left" => "←",
        "Right" => "→",
        "Up" => "↑",
        "Down" => "↓",
        "PageUp" => "PageUp",
        "PageDown" => "PageDown",
        "BackSpace" => "Backspace",
        "Enter" => "Enter",
        "Escape" => "Esc",
        "OemComma" => ",",
        "OemPeriod" => ".",
        "OemMinus" => "-",
        "OemPlus" => "=",
        "OemOpenBrackets" => "[",
        "OemCloseBrackets" => "]",
        "OemQuestion" => "/",
        "OemSemicolon" => ";",
        "OemQuotes" => "'",
        "OemPipe" => "\\",
        "OemTilde" => "`",
        _ when normalizedKey.Length == 2 && normalizedKey[0] == 'D' && char.IsDigit(normalizedKey[1])
            => normalizedKey[1].ToString(),
        _ => normalizedKey
    };
}

/// <summary>
/// 客户端全局热键与窗口内快捷键配置。
/// </summary>
public sealed record HotkeySettings(
    bool GlobalHotkeysEnabled = true,
    HotkeyBinding? GlobalBossKey = null,
    HotkeyBinding? GlobalPlayPause = null,
    HotkeyBinding? PlayPause = null,
    HotkeyBinding? SeekBackward = null,
    HotkeyBinding? SeekForward = null,
    HotkeyBinding? SpeedUp = null,
    HotkeyBinding? SpeedDown = null,
    HotkeyBinding? SpeedReset = null,
    HotkeyBinding? PreviousEpisode = null,
    HotkeyBinding? NextEpisode = null,
    HotkeyBinding? ToggleMiniMode = null,
    HotkeyBinding? RestoreWindow = null,
    HotkeyBinding? VolumeUp = null,
    HotkeyBinding? VolumeDown = null,
    HotkeyBinding? ToggleMute = null)
{
    public static HotkeySettings CreateDefaults() => new(
        GlobalHotkeysEnabled: true,
        GlobalBossKey: new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, "H"),
        GlobalPlayPause: HotkeyBinding.Empty,
        PlayPause: new(HotkeyModifiers.None, "Space"),
        SeekBackward: new(HotkeyModifiers.None, "Left"),
        SeekForward: new(HotkeyModifiers.None, "Right"),
        SpeedUp: new(HotkeyModifiers.None, "Up"),
        SpeedDown: new(HotkeyModifiers.None, "Down"),
        SpeedReset: new(HotkeyModifiers.None, "Z"),
        PreviousEpisode: new(HotkeyModifiers.None, "PageUp"),
        NextEpisode: new(HotkeyModifiers.None, "PageDown"),
        ToggleMiniMode: new(HotkeyModifiers.None, "M"),
        RestoreWindow: new(HotkeyModifiers.Ctrl, "R"),
        VolumeUp: new(HotkeyModifiers.Ctrl, "Up"),
        VolumeDown: new(HotkeyModifiers.Ctrl, "Down"),
        ToggleMute: new(HotkeyModifiers.Ctrl, "M"));

    public HotkeySettings EnsurePopulated()
    {
        var defaults = CreateDefaults();
        return new(
            GlobalHotkeysEnabled: GlobalHotkeysEnabled,
            GlobalBossKey: NormalizeBinding(GlobalBossKey, defaults.GlobalBossKey!),
            GlobalPlayPause: NormalizeBinding(GlobalPlayPause, defaults.GlobalPlayPause!),
            PlayPause: NormalizeBinding(PlayPause, defaults.PlayPause!),
            SeekBackward: NormalizeBinding(SeekBackward, defaults.SeekBackward!),
            SeekForward: NormalizeBinding(SeekForward, defaults.SeekForward!),
            SpeedUp: NormalizeBinding(SpeedUp, defaults.SpeedUp!),
            SpeedDown: NormalizeBinding(SpeedDown, defaults.SpeedDown!),
            SpeedReset: NormalizeBinding(SpeedReset, defaults.SpeedReset!),
            PreviousEpisode: NormalizeBinding(PreviousEpisode, defaults.PreviousEpisode!),
            NextEpisode: NormalizeBinding(NextEpisode, defaults.NextEpisode!),
            ToggleMiniMode: NormalizeBinding(ToggleMiniMode, defaults.ToggleMiniMode!),
            RestoreWindow: NormalizeBinding(RestoreWindow, defaults.RestoreWindow!),
            VolumeUp: NormalizeBinding(VolumeUp, defaults.VolumeUp!),
            VolumeDown: NormalizeBinding(VolumeDown, defaults.VolumeDown!),
            ToggleMute: NormalizeBinding(ToggleMute, defaults.ToggleMute!));
    }

    public HotkeyBinding GetBinding(HotkeyAction action) => action switch
    {
        HotkeyAction.GlobalBossKey => GlobalBossKey ?? HotkeyBinding.Empty,
        HotkeyAction.GlobalPlayPause => GlobalPlayPause ?? HotkeyBinding.Empty,
        HotkeyAction.PlayPause => PlayPause ?? HotkeyBinding.Empty,
        HotkeyAction.SeekBackward => SeekBackward ?? HotkeyBinding.Empty,
        HotkeyAction.SeekForward => SeekForward ?? HotkeyBinding.Empty,
        HotkeyAction.SpeedUp => SpeedUp ?? HotkeyBinding.Empty,
        HotkeyAction.SpeedDown => SpeedDown ?? HotkeyBinding.Empty,
        HotkeyAction.SpeedReset => SpeedReset ?? HotkeyBinding.Empty,
        HotkeyAction.PreviousEpisode => PreviousEpisode ?? HotkeyBinding.Empty,
        HotkeyAction.NextEpisode => NextEpisode ?? HotkeyBinding.Empty,
        HotkeyAction.ToggleMiniMode => ToggleMiniMode ?? HotkeyBinding.Empty,
        HotkeyAction.RestoreWindow => RestoreWindow ?? HotkeyBinding.Empty,
        HotkeyAction.VolumeUp => VolumeUp ?? HotkeyBinding.Empty,
        HotkeyAction.VolumeDown => VolumeDown ?? HotkeyBinding.Empty,
        HotkeyAction.ToggleMute => ToggleMute ?? HotkeyBinding.Empty,
        _ => HotkeyBinding.Empty
    };

    public HotkeySettings WithBinding(HotkeyAction action, HotkeyBinding binding)
    {
        var normalized = string.IsNullOrWhiteSpace(binding.KeyName)
            ? HotkeyBinding.Empty
            : new HotkeyBinding(binding.Modifiers, HotkeyBinding.NormalizeKeyName(binding.KeyName));

        return action switch
        {
            HotkeyAction.GlobalBossKey => this with { GlobalBossKey = normalized },
            HotkeyAction.GlobalPlayPause => this with { GlobalPlayPause = normalized },
            HotkeyAction.PlayPause => this with { PlayPause = normalized },
            HotkeyAction.SeekBackward => this with { SeekBackward = normalized },
            HotkeyAction.SeekForward => this with { SeekForward = normalized },
            HotkeyAction.SpeedUp => this with { SpeedUp = normalized },
            HotkeyAction.SpeedDown => this with { SpeedDown = normalized },
            HotkeyAction.SpeedReset => this with { SpeedReset = normalized },
            HotkeyAction.PreviousEpisode => this with { PreviousEpisode = normalized },
            HotkeyAction.NextEpisode => this with { NextEpisode = normalized },
            HotkeyAction.ToggleMiniMode => this with { ToggleMiniMode = normalized },
            HotkeyAction.RestoreWindow => this with { RestoreWindow = normalized },
            HotkeyAction.VolumeUp => this with { VolumeUp = normalized },
            HotkeyAction.VolumeDown => this with { VolumeDown = normalized },
            HotkeyAction.ToggleMute => this with { ToggleMute = normalized },
            _ => this
        };
    }

    public static bool IsGlobalAction(HotkeyAction action) =>
        action is HotkeyAction.GlobalBossKey or HotkeyAction.GlobalPlayPause;

    public IReadOnlyDictionary<HotkeyAction, HotkeyBinding> GetGlobalBindings() =>
        new Dictionary<HotkeyAction, HotkeyBinding>
        {
            [HotkeyAction.GlobalBossKey] = GetBinding(HotkeyAction.GlobalBossKey),
            [HotkeyAction.GlobalPlayPause] = GetBinding(HotkeyAction.GlobalPlayPause)
        };

    public HotkeyAction? FindConflict(HotkeyAction targetAction, HotkeyBinding candidate)
    {
        if (candidate.IsEmpty)
            return null;

        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            if (action == targetAction)
                continue;
            // Global hotkeys conflict against other globals and also against matching window combos;
            // window hotkeys conflict against other window hotkeys and active global hotkeys.
            var existing = GetBinding(action);
            if (existing.IsEmpty)
                continue;
            if (existing.Matches(candidate.Modifiers, candidate.NormalizedKeyName))
                return action;
        }
        return null;
    }

    private static HotkeyBinding NormalizeBinding(HotkeyBinding? value, HotkeyBinding fallback)
    {
        if (value is null)
            return fallback;
        if (value.IsEmpty)
            return HotkeyBinding.Empty;
        return new HotkeyBinding(value.Modifiers, HotkeyBinding.NormalizeKeyName(value.KeyName));
    }
}
