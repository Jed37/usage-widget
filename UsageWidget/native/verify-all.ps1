$ErrorActionPreference = 'Stop'
foreach ($script in @('verify-layout.ps1', 'verify-features.ps1', 'verify.ps1')) {
  & powershell.exe -Sta -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $script)
  if ($LASTEXITCODE -ne 0) { throw "Verification failed: $script" }
}
