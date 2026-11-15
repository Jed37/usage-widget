$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$output = Join-Path $PSScriptRoot 'bin'
if (-not (Test-Path -LiteralPath $PSScriptRoot)) { throw 'Native source directory is unavailable' }
if (-not (Test-Path -LiteralPath $output)) { [void][System.IO.Directory]::CreateDirectory($output) }
$icon = Join-Path $PSScriptRoot 'UsageWidget.ico'
$iconBuilder = Join-Path $PSScriptRoot 'build-icon.ps1'
if (-not (Test-Path -LiteralPath $icon) -or (Get-Item -LiteralPath $iconBuilder).LastWriteTimeUtc -gt (Get-Item -LiteralPath $icon).LastWriteTimeUtc) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $iconBuilder
    if ($LASTEXITCODE -ne 0) { throw 'Icon build failed' }
}
$references = @(
    'System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll',
    'System.Drawing.dll', 'System.Windows.Forms.dll',
    'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll',
    'WPF\UIAutomationTypes.dll'
) | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ /warn:4 /warnaserror+ @references `
    "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" `
    "/resource:$(Join-Path $PSScriptRoot 'Widget.xaml'),Widget.xaml" `
    "/resource:$icon,UsageWidget.ico" "/win32icon:$icon" `
    "/out:$(Join-Path $output 'UsageWidget.exe')" (Join-Path $PSScriptRoot 'UsageWidget.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
Get-Item -LiteralPath (Join-Path $output 'UsageWidget.exe') | Select-Object Name, Length
