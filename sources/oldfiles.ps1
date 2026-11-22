# Execute inside: C:\Users\bruno\Downloads\sources-20261117T114549Z-1-001\sources
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

New-Item -ItemType Directory -Force -Path ".github\workflows" | Out-Null
New-Item -ItemType Directory -Force -Path ".github\ISSUE_TEMPLATE" | Out-Null

# 1. MIT LICENSE (2026 Bruno dos Santos Rodrigues / BrunoTech Soluções em Sistemas)
[System.IO.File]::WriteAllText("$PWD\LICENSE", @'
MIT License

Copyright (c) 2026 Bruno dos Santos Rodrigues (BrunoTech Soluções em Sistemas)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
'@, $utf8NoBom)

# 2. GitHub Actions CI Workflow (.github/workflows/dotnet-build.yml)
[System.IO.File]::WriteAllText("$PWD\.github\workflows\dotnet-build.yml", @'
name: .NET 10 Build & Release Check

on:
  push:
    branches: [ main, master ]
  pull_request:
    branches: [ main, master ]

jobs:
  build:
    runs-on: windows-latest

    steps:
      - name: Checkout repository
        uses: actions/checkout@v4

      - name: Setup .NET 10 SDK
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore dependencies
        run: dotnet restore

      - name: Build Release (Zero Warnings)
        run: dotnet build -c Release --no-restore

      - name: Publish Self-Contained Win-x64 Artifact
        run: |
          dotnet publish RocmAudioSplitter.Desktop\RocmAudioSplitter.Desktop.csproj `
            -c Release `
            -r win-x64 `
            --self-contained true `
            -p:PublishSingleFile=false `
            -o .\publish\win-x64

      - name: Upload Build Artifact
        uses: actions/upload-artifact@v4
        with:
          name: RocmAudioSplitter-win-x64
          path: .\publish\win-x64\
          retention-days: 14
'@, $utf8NoBom)

# 3. Bug Report Issue Template (.github/ISSUE_TEMPLATE/bug_report.yml)
[System.IO.File]::WriteAllText("$PWD\.github\ISSUE_TEMPLATE\bug_report.yml", @'
name: Bug Report
description: Report an audio routing, ROCm/DirectML inference, or UI issue
title: "[Bug]: "
labels: ["bug", "triage"]
body:
  - type: markdown
    attributes:
      value: |
        Thanks for reporting an issue! Please attach your diagnostic log from `%AppData%\RocmAudioSplitter\logs\` if available.
  - type: textarea
    id: description
    attributes:
      label: What happened?
      description: Clear description of the bug and expected behavior.
    validations:
      required: true
  - type: input
    id: hardware
    attributes:
      label: GPU, ROCm / Driver & Windows Build
      placeholder: "e.g., Radeon RX 9060 XT, Adrenalin 26.x / ROCm 7.2, Windows 11 24H2"
    validations:
      required: true
  - type: dropdown
    id: capture_mode
    attributes:
      label: Audio Capture Mode
      options:
        - Process Isolation ([App] Chrome / Spotify / Edge)
        - System Master Output (All Apps)
    validations:
      required: true
  - type: textarea
    id: logs
    attributes:
      label: Diagnostic Log Snippet
      description: Paste relevant lines from engine_YYYY-MM-DD.log (accessible via the Logs button in the app).
      render: text
'@, $utf8NoBom)

# 4. Feature Request Issue Template (.github/ISSUE_TEMPLATE/feature_request.yml)
[System.IO.File]::WriteAllText("$PWD\.github\ISSUE_TEMPLATE\feature_request.yml", @'
name: Feature Request
description: Propose a new streamer workflow feature, DSP module, or model integration
title: "[Enhancement]: "
labels: ["enhancement"]
body:
  - type: textarea
    id: problem
    attributes:
      label: Streamer / Musician Use Case
      description: Describe the live streaming or performance scenario this solves.
    validations:
      required: true
  - type: textarea
    id: proposal
    attributes:
      label: Proposed Solution & Latency Impact
      description: Outline how it should work and confirm it preserves low-latency real-time execution.
    validations:
      required: true
'@, $utf8NoBom)

# 5. Update README.md clone URL and add live GitHub Actions build badge
$readmePath = "$PWD\README.md"
if (Test-Path $readmePath) {
    $readme = [System.IO.File]::ReadAllText($readmePath)
    $readme = $readme.Replace("https://github.com/<your-username>/RocmAudioSplitter.git", "https://github.com/brunosr1985/ROCmOk-.git")
    $readme = $readme.Replace("cd RocmAudioSplitter", "cd ROCmOk-")
    if ($readme -notmatch "dotnet-build.yml") {
        $badge = "[![Build](https://github.com/brunosr1985/ROCmOk-/actions/workflows/dotnet-build.yml/badge.svg)](https://github.com/brunosr1985/ROCmOk-/actions/workflows/dotnet-build.yml)`r`n"
        $readme = $readme.Replace("[![.NET]", $badge + "[![.NET]")
    }
    [System.IO.File]::WriteAllText($readmePath, $readme, $utf8NoBom)
}

git add LICENSE .github README.md
git commit -m "chore: add MIT license, GitHub Actions CI build workflow, issue templates, and repo links"
git push