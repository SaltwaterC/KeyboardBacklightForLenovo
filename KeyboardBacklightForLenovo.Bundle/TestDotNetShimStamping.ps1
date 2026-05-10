# Requires: PowerShell 5.1
param(
  [Parameter(Mandatory)]
  [string]$ProjectDir,
  [Parameter(Mandatory)]
  [string]$Channel
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$source = [System.IO.Path]::Combine($ProjectDir, 'InstallDotNetDesktopRuntime.ps1')
if (-not (Test-Path -LiteralPath $source)) {
  throw "Shim source not found: $source"
}

$script = [System.IO.File]::ReadAllText($source)
$token = "'__DOTNET_DESKTOP_RUNTIME_CHANNEL__'"
if (-not $script.Contains($token)) {
  throw 'The installer shim source does not contain the .NET runtime channel token.'
}

$escapedChannel = $Channel.Replace("'", "''")
$stampedScript = $script.Replace($token, "'$escapedChannel'")

$stampedChannelPattern = [regex]::Escape('$StampedChannel') + '\s*=\s*' + [regex]::Escape("'$escapedChannel'")
if ($stampedScript -notmatch $stampedChannelPattern) {
  throw "The installer shim did not stamp the .NET runtime channel into `$StampedChannel."
}

$badGuardPattern = [regex]::Escape('$Channel') + '\s+-eq\s+' + [regex]::Escape("'$escapedChannel'")
if ($stampedScript -match $badGuardPattern) {
  throw 'The installer shim guard compares against the stamped channel and will reject a valid shim.'
}

if ($stampedScript -notmatch [regex]::Escape('$UnstampedChannelToken')) {
  throw 'The installer shim guard no longer checks the unstamped token sentinel.'
}

$errors = $null
$tokens = $null
[System.Management.Automation.Language.Parser]::ParseInput($stampedScript, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors) {
  $messages = ($errors | ForEach-Object { $_.Message }) -join '; '
  throw "The stamped installer shim is not valid PowerShell: $messages"
}

Write-Host "OK: .NET Desktop Runtime shim stamping guard accepts channel '$Channel'"
