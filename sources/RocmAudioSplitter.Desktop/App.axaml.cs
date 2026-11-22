using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RocmAudioSplitter.Desktop.ViewModels;
using RocmAudioSplitter.Desktop.Views;

namespace RocmAudioSplitter.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm
            };
            desktop.ShutdownRequested += async (_, _) => await vm.ShutdownAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}