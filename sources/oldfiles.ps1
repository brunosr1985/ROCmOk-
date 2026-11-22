# Execute dentro de: C:\Users\bruno\Downloads\sources-20261117T114549Z-1-001\sources
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# 1. Troca Watermark -> PlaceholderText no MainWindow.axaml (Avalonia 11.3+)
$axamlPath = "$PWD\RocmAudioSplitter.Desktop\Views\MainWindow.axaml"
$axamlCode = [System.IO.File]::ReadAllText($axamlPath)
$axamlCode = $axamlCode.Replace("Watermark=", "PlaceholderText=")
[System.IO.File]::WriteAllText($axamlPath, $axamlCode, $utf8NoBom)

# 2. Adiciona guarda OperatingSystem.IsWindows() no ShutdownAsync()
$vmPath = "$PWD\RocmAudioSplitter.Desktop\ViewModels\MainWindowViewModel.cs"
$vmCode = [System.IO.File]::ReadAllText($vmPath)
$vmCode = $vmCode.Replace(
    "if (_hotkeyService is not null)",
    "if (OperatingSystem.IsWindows() && _hotkeyService is not null)")
[System.IO.File]::WriteAllText($vmPath, $vmCode, $utf8NoBom)

dotnet build -c Release