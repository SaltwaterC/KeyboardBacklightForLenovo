# Requires: PowerShell 5.1
param(
  [Parameter(Mandatory)]
  [string]$ProjectDir,
  [Parameter(Mandatory)]
  [ValidateSet('x64', 'x86')]
  [string]$Arch,
  [Parameter(Mandatory)]
  [string]$Channel
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$source = [System.IO.Path]::Combine($ProjectDir, 'InstallDotNetDesktopRuntime.ps1')
if (-not (Test-Path -LiteralPath $source)) {
  throw "Shim source not found: $source"
}

$externalDir = [System.IO.Path]::Combine($ProjectDir, 'External')
$objDir = [System.IO.Path]::Combine($ProjectDir, 'obj', 'DotNetShim')
New-Item -ItemType Directory -Force -Path $externalDir, $objDir | Out-Null

$script = [System.IO.File]::ReadAllText($source)
$token = "'__DOTNET_DESKTOP_RUNTIME_CHANNEL__'"
if (-not $script.Contains($token)) {
  throw 'The installer shim source does not contain the .NET runtime channel token.'
}

$escapedChannel = $Channel.Replace("'", "''")
$stampedScript = $script.Replace($token, "'$escapedChannel'")
$stampedSource = [System.IO.Path]::Combine($objDir, "InstallDotNetDesktopRuntime-$Arch-$Channel.ps1")
$output = [System.IO.Path]::Combine($externalDir, "InstallDotNetDesktopRuntime-$Arch.exe")
[System.IO.File]::WriteAllText($stampedSource, $stampedScript, [System.Text.UTF8Encoding]::new($false))

Import-Module ps2exe -MinimumVersion 1.0.16

$ps2exeArgs = @{
  inputFile    = $stampedSource
  outputFile   = $output
  title        = 'Install .NET Desktop Runtime'
  version      = '1.0.0'
  noConsole    = $true
  noOutput     = $true
  noError      = $true
  requireAdmin = $true
}

if ($Arch -eq 'x64') {
  $ps2exeArgs.x64 = $true
}
else {
  $ps2exeArgs.x86 = $true
}

Invoke-ps2exe @ps2exeArgs

if (-not (Test-Path -LiteralPath $output)) {
  throw "Failed to create .NET Desktop Runtime shim: $output"
}

Write-Host "Built .NET Desktop Runtime shim: $output"
