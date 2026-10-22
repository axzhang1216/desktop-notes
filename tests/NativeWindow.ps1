param([Parameter(Mandatory=$true)][string]$Executable,[Parameter(Mandatory=$true)][string]$DataDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DesktopNotesNativeCheck {
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
}
'@
New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
$dataPath = Join-Path $DataDirectory 'notes.json'
$process = Start-Process -FilePath $Executable -ArgumentList ('--data "' + $dataPath + '"') -WindowStyle Hidden -PassThru
function Find-Control([string]$Id) {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($script:windowHandle -ne [IntPtr]::Zero) {
            $script:appRoot = [System.Windows.Automation.AutomationElement]::FromHandle($script:windowHandle)
        }
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)
        $element = $script:appRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
        if ($element) { return $element }
        Start-Sleep -Milliseconds 50
    }
    throw ('Control unavailable: ' + $Id)
}
function Invoke-Control([string]$Id) { (Find-Control $Id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 100 }
try {
    for ($attempt = 0; $attempt -lt 60; $attempt++) { $process.Refresh(); if ($process.MainWindowHandle -ne 0) { break }; if ($process.HasExited) { throw 'App exited before its window appeared.' }; Start-Sleep -Milliseconds 100 }
    if ($process.MainWindowHandle -eq 0) { throw 'App window did not appear.' }
    $script:windowHandle = $process.MainWindowHandle
    $null = [DesktopNotesNativeCheck]::ShowWindow($script:windowHandle,4)
    $script:appRoot = [System.Windows.Automation.AutomationElement]::FromHandle($script:windowHandle)
    $null = Find-Control 'ChatInput'
    $null = Find-Control 'ChatSend'
    Write-Output 'PASS native executable starts on the chat view'
    for ($toggle = 0; $toggle -lt 6; $toggle++) {
        Invoke-Control 'Pin'
        $expected = ($toggle % 2 -eq 0)
        for ($attempt = 0; $attempt -lt 40; $attempt++) { $actual = (([DesktopNotesNativeCheck]::GetWindowLong($script:windowHandle,-20) -band 8) -ne 0); if ($actual -eq $expected) { break }; Start-Sleep -Milliseconds 50 }
        if ($actual -ne $expected) { throw ('Native pin state mismatch on toggle ' + $toggle) }
        Write-Output ('PASS native executable pin toggle ' + $toggle)
        if ($expected) {
            $null = [DesktopNotesNativeCheck]::ShowWindow($script:windowHandle,6)
            Start-Sleep -Milliseconds 100
            $null = [DesktopNotesNativeCheck]::ShowWindow($script:windowHandle,9)
            Start-Sleep -Milliseconds 200
            if (([DesktopNotesNativeCheck]::GetWindowLong($script:windowHandle,-20) -band 8) -eq 0) { throw 'Pinned state was lost after restore.' }
            Write-Output ('PASS native executable minimize/restore ' + $toggle)
        }
    }
} finally {
    if (-not $process.HasExited) { $null = $process.CloseMainWindow(); if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id } }
}
