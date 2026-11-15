$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\UsageWidget.exe'
if (-not (Test-Path -LiteralPath $exe)) { & (Join-Path $PSScriptRoot 'build.ps1') }
Start-Process -FilePath $exe
