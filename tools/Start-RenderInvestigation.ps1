# SPDX-License-Identifier: AGPL-3.0-or-later
param(
    [ValidateRange(5, 30)][int]$SecondsPerPhase = 12,
    [string]$PluginDll = "$PSScriptRoot/../DynamicPortrait/bin/Release/DynamicPortrait.dll",
    [switch]$Foreground
)
$ErrorActionPreference = 'Stop'
$pluginPath = (Resolve-Path -LiteralPath $PluginDll).Path
$directory = Join-Path $env:APPDATA 'XIVLauncher/pluginConfigs/DynamicPortrait'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$request = @{ ExpiresUtc = [DateTime]::UtcNow.AddMinutes(3).ToString('o'); SecondsPerPhase = $SecondsPerPhase }
$requestPath = Join-Path $directory 'render-investigation.request.json'
if ($Foreground) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PortraitInvestigationWindow {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr window);
}
'@
    $previousWindow = [PortraitInvestigationWindow]::GetForegroundWindow()
    $gameWindow = (Get-Process ffxiv_dx11).MainWindowHandle
    [uint32]$ownerProcess = 0
    $ownerThread = [PortraitInvestigationWindow]::GetWindowThreadProcessId($previousWindow, [ref]$ownerProcess)
    $currentThread = [PortraitInvestigationWindow]::GetCurrentThreadId()
    $attached = [PortraitInvestigationWindow]::AttachThreadInput($currentThread, $ownerThread, $true)
    try {
        [PortraitInvestigationWindow]::BringWindowToTop($gameWindow) | Out-Null
        [PortraitInvestigationWindow]::SetForegroundWindow($gameWindow) | Out-Null
    }
    finally { if ($attached) { [PortraitInvestigationWindow]::AttachThreadInput($currentThread, $ownerThread, $false) | Out-Null } }
    Write-Output "Requested foreground: current=$([PortraitInvestigationWindow]::GetForegroundWindow()), game=$gameWindow"
}
$startedUtc = [DateTime]::UtcNow
[IO.File]::WriteAllText($requestPath, ($request | ConvertTo-Json))
# Dalamud's enabled dev-plugin watcher reloads on LastWrite changes.
(Get-Item -LiteralPath $pluginPath).LastWriteTimeUtc = [DateTime]::UtcNow
Write-Output "Requested finite investigation; reports: $directory/diagnostics"
if ($Foreground) {
    try {
        $deadline = $startedUtc.AddSeconds($SecondsPerPhase * 2 + 20)
        do {
            Start-Sleep -Milliseconds 500
            $report = Get-ChildItem -LiteralPath (Join-Path $directory 'diagnostics') -Filter 'render-investigation-*.json' -ErrorAction SilentlyContinue |
                Where-Object { $_.LastWriteTimeUtc -gt $startedUtc } | Select-Object -First 1
        } while (-not $report -and [DateTime]::UtcNow -lt $deadline)
        if ($report) { Write-Output "Completed: $($report.FullName)" }
        else { throw 'No completed investigation report before timeout. Check dalamud.log.' }
    }
    finally {
        # Restore focus only if the user has not already selected another window.
        if ([PortraitInvestigationWindow]::GetForegroundWindow() -eq $gameWindow) {
            [PortraitInvestigationWindow]::SetForegroundWindow($previousWindow) | Out-Null
        }
    }
}
