using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RocmAudioSplitter.Desktop.ViewModels;

namespace RocmAudioSplitter.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnBrowseModelClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Vocal Separation ONNX Model",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ONNX Models") { Patterns = new[] { "*.onnx" } }
            }
        });

        if (files.Count > 0 && DataContext is MainWindowViewModel vm)
        {
            vm.ModelPath = files[0].Path.LocalPath;
        }
    }
}