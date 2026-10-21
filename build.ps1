param([switch]$Test,[switch]$StoreOnly)
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $bin -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Security.dll','System.Net.Http.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')
$compilerArgs = @('/nologo','/utf8output','/target:winexe','/platform:anycpu','/optimize+','/codepage:65001','/main:DesktopNotes.LinearProgramV2',('/out:' + (Join-Path $bin 'DesktopNotes.exe')),('/win32icon:' + (Join-Path $PSScriptRoot 'assets\app.ico')),('/win32manifest:' + (Join-Path $PSScriptRoot 'src\app.manifest')))
foreach ($reference in $references) { $compilerArgs += '/reference:' + (Join-Path $framework $reference) }
$compilerArgs += Join-Path $PSScriptRoot 'src\DesktopNotes.cs'
$compilerArgs += Join-Path $PSScriptRoot 'src\LinearIntegration.cs'
$compilerArgs += Join-Path $PSScriptRoot 'src\LinearProgramV2.cs'
$compilerArgs += Join-Path $PSScriptRoot 'src\ApocalypseBriefs.cs'
$compilerArgs += Join-Path $PSScriptRoot 'src\OrcaAgents.cs'
$compilerArgs += Join-Path $PSScriptRoot 'src\Version.cs'
& (Join-Path $framework 'csc.exe') @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Write-Output (Join-Path $bin 'DesktopNotes.exe')
if ($Test) {
    $testArgs = @('/nologo','/utf8output','/target:winexe','/platform:anycpu','/codepage:65001',('/out:' + (Join-Path $bin 'Verify.exe')),('/reference:' + (Join-Path $bin 'DesktopNotes.exe')),('/win32manifest:' + (Join-Path $PSScriptRoot 'src\app.manifest')))
    foreach ($reference in $references) { $testArgs += '/reference:' + (Join-Path $framework $reference) }
    $testArgs += Join-Path $PSScriptRoot 'tests\Verify.cs'
    & (Join-Path $framework 'csc.exe') @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    $resultDir = Join-Path $bin ('test-' + (Get-Date -Format 'yyyyMMdd-HHmmssfff'))
    $testCommand = '"' + $resultDir + '"'
    if ($StoreOnly) { $testCommand += ' --store-only' }
    $process = Start-Process -FilePath (Join-Path $bin 'Verify.exe') -ArgumentList $testCommand -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { Stop-Process -Id $process.Id; throw 'Verification timed out.' }
    Get-Content -LiteralPath (Join-Path $resultDir 'results.txt') -Encoding UTF8
    if ($process.ExitCode -ne 0) { throw 'Verification failed.' }
    if (-not $StoreOnly) { & (Join-Path $PSScriptRoot 'tests\NativeWindow.ps1') -Executable (Join-Path $bin 'DesktopNotes.exe') -DataDirectory (Join-Path $resultDir 'native') }
}
