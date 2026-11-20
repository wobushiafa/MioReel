using Avalonia.Controls;
using MioReel.Desktop.ViewModels;

namespace MioReel.Desktop.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
    }

    public LoginWindow(LoginWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose += Close;
        Closing += (_, _) => viewModel.Dispose();
    }
}
