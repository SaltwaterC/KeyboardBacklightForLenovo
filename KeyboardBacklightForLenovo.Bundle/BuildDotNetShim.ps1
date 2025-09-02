param(
  [string]$ProjectDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Ensure-Ps2Exe {
  if (-not (Get-Module -ListAvailable -Name ps2exe)) {
    Write-Host "[build-shim] Installing ps2exe module..."
    # Ensure NuGet provider is available
    if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
      Install-PackageProvider -Name NuGet -Force | Out-Null
    }
    Install-Module -Name ps2exe -Scope CurrentUser -Force -AllowClobber -Repository PSGallery | Out-Null
  }
}

function Compile-Shim {
  param([string]$In, [string]$Out)
  Write-Host "[build-shim] Compiling shim: $In -> $Out"
  # Import module in current session
  Import-Module ps2exe -ErrorAction Stop
  if (-not (Test-Path (Split-Path $Out -Parent))) { New-Item -ItemType Directory -Path (Split-Path $Out -Parent) -Force | Out-Null }
  Invoke-ps2exe -inputFile $In -outputFile $Out -noConsole -iconFile $null -Title 'Install .NET Desktop Runtime' -version '1.0.0'
}

$proj = if ($ProjectDir) { $ProjectDir } else { $PSScriptRoot }
# Sanitize path in case MSBuild passed a trailing backslash before the closing quote
$proj = $proj.Trim('"')
$proj = [System.IO.Path]::GetFullPath($proj)

$src = [System.IO.Path]::Combine($proj, 'InstallDotNetDesktopRuntime.ps1')
$dstDir = [System.IO.Path]::Combine($proj, 'External')
if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
$dst = [System.IO.Path]::Combine($dstDir, 'InstallDotNetDesktopRuntime.exe')

Ensure-Ps2Exe
Compile-Shim -In $src -Out $dst
