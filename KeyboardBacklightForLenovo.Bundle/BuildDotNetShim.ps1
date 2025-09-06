param(
  [string]$ProjectDir,
  [ValidateSet('x86','x64')]
  [string]$Arch
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
  param(
    [string]$In,
    [string]$Out,
    [string]$Arch
  )
  Write-Host "[build-shim] Compiling shim: $In -> $Out ($Arch)"
  # Import module in current session
  Import-Module ps2exe -ErrorAction Stop
  if (-not (Test-Path (Split-Path $Out -Parent))) {
    New-Item -ItemType Directory -Path (Split-Path $Out -Parent) -Force | Out-Null
  }

  $ps2args = @{
    inputFile  = $In
    outputFile = $Out
    noConsole  = $true
    title      = 'Install .NET Desktop Runtime'
    version    = '1.0.0'
  }

  switch ($Arch.ToLowerInvariant()) {
    'x64' { $ps2args.x64 = $true }
    'x86' { $ps2args.x86 = $true }
    default { throw "Unsupported arch: $Arch" }
  }

  Invoke-ps2exe @ps2args
}

$proj = if ($ProjectDir) { $ProjectDir } else { $PSScriptRoot }
# When invoked from cmd.exe with a trailing backslash, the closing quote can be
# swallowed and subsequent arguments (like -Arch) get appended to the project
# path. Detect this pattern and recover the real values so GetFullPath doesn't
# choke on the unexpected characters.
if ($proj -match '^(?<dir>.+?)\s+-Arch\s+(?<arch>.+)$') {
  $proj = $Matches.dir
  if (-not $PSBoundParameters.ContainsKey('Arch')) {
    $Arch = $Matches.arch.Trim('"')
  }
}
# Sanitize path in case MSBuild passed a trailing backslash before the closing quote
$proj = $proj.Trim('"')
$proj = [System.IO.Path]::GetFullPath($proj)

if (-not $Arch) { throw 'Arch parameter is required' }

$src = [System.IO.Path]::Combine($proj, 'InstallDotNetDesktopRuntime.ps1')
$dstDir = [System.IO.Path]::Combine($proj, 'External')
if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
$dst = [System.IO.Path]::Combine($dstDir, "InstallDotNetDesktopRuntime-$Arch.exe")

Ensure-Ps2Exe
Compile-Shim -In $src -Out $dst -Arch $Arch

$verify = [System.IO.Path]::Combine((Split-Path $proj -Parent), 'VerifyArch.ps1')
& $verify -Expected $Arch -Exe $dst

# Ensure deterministic success code for MSBuild integration
exit 0
