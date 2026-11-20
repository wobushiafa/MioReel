using MioReel.Core.Abstractions;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace MioReel.Desktop.ViewModels;

/// <summary>
/// 账号密码登录窗口：输入用户名密码，登录或注册；成功后服务端签发自有 token。
/// 密码只在提交瞬间通过网络发送，不落盘、不写入日志。
/// </summary>
public sealed class LoginWindowViewModel : ReactiveObject, IDisposable
{
    private readonly IAuthService _auth;
    private CancellationTokenSource? _cts;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string? _errorText;
    private string? _successText;
    private bool _isBusy;
    private bool _isRegisterMode;
    private bool _disposed;

    public LoginWindowViewModel(IAuthService auth)
    {
        _auth = auth;
        SubmitCommand = ReactiveCommand.CreateFromTask(SubmitAsync);
        ToggleModeCommand = ReactiveCommand.Create(ToggleMode);
        CancelCommand = ReactiveCommand.Create(Cancel);
    }

    /// <summary>登录/注册成功，参数为是否注册。供主窗口弹出成功提示。</summary>
    public event Action<bool>? LoginSucceeded;
    public event Action? RequestClose;

    public ReactiveCommand<RxVoid, RxVoid> SubmitCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ToggleModeCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    public string Username
    {
        get => _username;
        set => this.RaiseAndSetIfChanged(ref _username, value);
    }

    public string Password
    {
        get => _password;
        set => this.RaiseAndSetIfChanged(ref _password, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _errorText, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public string? SuccessText
    {
        get => _successText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _successText, value);
            this.RaisePropertyChanged(nameof(HasSuccess));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public bool IsRegisterMode
    {
        get => _isRegisterMode;
        set => this.RaiseAndSetIfChanged(ref _isRegisterMode, value);
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public bool HasSuccess => !string.IsNullOrWhiteSpace(SuccessText);

    public string ModeTitle => IsRegisterMode ? "注册新账号" : "登录妙妙短剧";
    public string ModeSubtitle => IsRegisterMode
        ? "注册后即可在多台电脑同步观看历史"
        : "登录后即可在多台电脑同步观看历史";
    public string PrimaryButtonText => IsRegisterMode ? "注册并登录" : "登录";
    public string ToggleModeText => IsRegisterMode ? "已有账号？返回登录" : "没有账号？注册新用户";

    public void ToggleMode()
    {
        IsRegisterMode = !IsRegisterMode;
        ErrorText = null;
        SuccessText = null;
        this.RaisePropertyChanged(nameof(ModeTitle));
        this.RaisePropertyChanged(nameof(ModeSubtitle));
        this.RaisePropertyChanged(nameof(PrimaryButtonText));
        this.RaisePropertyChanged(nameof(ToggleModeText));
    }

    private Task SubmitAsync() => AuthenticateAsync(register: IsRegisterMode);

    private async Task AuthenticateAsync(bool register)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        ErrorText = null;
        SuccessText = null;

        var username = Username.Trim();
        var password = Password;
        if (string.IsNullOrWhiteSpace(username))
        {
            ErrorText = "请输入用户名";
            return;
        }
        if (username.Length is < 2 or > 32)
        {
            ErrorText = "用户名长度需为 2 到 32 个字符";
            return;
        }
        if (string.IsNullOrWhiteSpace(password))
        {
            ErrorText = "请输入密码";
            return;
        }
        if (password.Length is < 6 or > 128)
        {
            ErrorText = "密码长度需为 6 到 128 个字符";
            return;
        }

        IsBusy = true;
        try
        {
            if (register)
                await _auth.RegisterAsync(username, password, ct);
            else
                await _auth.LoginAsync(username, password, ct);

            // 成功后清空内存中的密码，避免滞留
            Password = string.Empty;
            SuccessText = register ? "注册成功，已自动登录，正在同步历史…" : "登录成功，正在同步历史…";
            LoginSucceeded?.Invoke(register);
            // 停留片刻让用户看清反馈，再关闭窗口
            await Task.Delay(900);
            RequestClose?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsBusy = false;
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        RequestClose?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
