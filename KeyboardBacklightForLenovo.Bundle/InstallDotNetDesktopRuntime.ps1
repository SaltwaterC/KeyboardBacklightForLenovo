# Requires: PowerShell 5.1
param(
  [string]$Channel = '8.0',
  [ValidateSet('windowsdesktop')]
  [string]$Runtime = 'windowsdesktop',
  [ValidateSet('x64', 'x86')]
  [string]$Architecture,
  [switch]$VerboseLog
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Log {
  param([string]$Message)
  if ($VerboseLog) { Write-Host "[dotnet-shim] $Message" }
}

function Get-OsArch {
  if ([Environment]::Is64BitOperatingSystem) { return 'x64' } else { return 'x86' }
}

function Test-DotNetDesktopRuntimeInstalled {
  param(
    [Parameter(Mandatory)] [string]$MajorMinor, # e.g. '8.0'
    [Parameter(Mandatory)] [ValidateSet('x64', 'x86')] [string]$Arch
  )

  # --- 1) ARP / Uninstall keys (both views) ---
  $archLabel = if ($Arch -eq 'x64') { '(x64)' } else { '(x86)' }
  $uninstallRoots = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
  )

  foreach ($root in $uninstallRoots) {
    if (-not (Test-Path $root)) { continue }
    foreach ($sub in Get-ChildItem $root -ErrorAction SilentlyContinue) {
      $p = Get-ItemProperty $sub.PSPath -ErrorAction SilentlyContinue
      if (-not $p) { continue }

      # Safely fetch props (no-throw if missing)
      $dn = ($p | Select-Object -ExpandProperty DisplayName -ErrorAction SilentlyContinue)
      $dv = ($p | Select-Object -ExpandProperty DisplayVersion -ErrorAction SilentlyContinue)

      if (-not $dn -or -not $dv) { continue }
      # We only care about Microsoft Windows Desktop Runtime entries
      if ($dn -notlike 'Microsoft Windows Desktop Runtime*') { continue }
      # Version must match requested major.minor (e.g. 8.0.*)
      if ($dv -notlike "$MajorMinor.*") { continue }
      # Architecture check: most entries suffix the name with "(x64)/(x86)"
      if ($dn -match "\((x64|x86)\)$") {
        if ($Matches[1] -ne $Arch) { continue }
      }
      return $true
    }
  }

  # --- 2) dotnet --list-runtimes ---
  $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
  if ($dotnet) {
    $lines = & $dotnet.Source --list-runtimes 2>$null
    if ($lines -match "^Microsoft\.WindowsDesktop\.App\s+$MajorMinor\.\d+\s") {
      return $true
    }
  }

  # --- 3) sharedfx registry (present on many machines, not all) ---
  $sharedFxKey = if ($Arch -eq 'x64') {
    'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App'
  }
  else {
    'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x86\sharedfx\Microsoft.WindowsDesktop.App'
  }
  if (Test-Path $sharedFxKey) {
    $props = (Get-ItemProperty $sharedFxKey -ErrorAction SilentlyContinue).PSObject.Properties |
      Where-Object { $_.MemberType -eq 'NoteProperty' }
    foreach ($prop in $props) {
      if ($prop.Name -like "$MajorMinor.*") { return $true }
    }
  }

  return $false
}

function Get-ReleaseManifest {
  param([string]$Channel)
  $uri = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/$Channel/releases.json"
  Write-Log "Downloading release metadata: $uri"
  $json = Invoke-RestMethod -UseBasicParsing -Uri $uri
  return $json
}

function Select-WindowsDesktopFile {
  param($Manifest, [string]$Arch)
  $latest = $Manifest.'latest-release'
  if (-not $latest) { throw "Unable to determine latest release from manifest." }
  $release = $Manifest.releases | Where-Object { $_.'release-version' -eq $latest } | Select-Object -First 1
  if (-not $release) { throw "Release '$latest' not found in manifest." }
  if (-not $release.windowsdesktop) { throw "Release '$latest' has no windowsdesktop section." }

  $file = $release.windowsdesktop.files |
    Where-Object { $_.rid -eq "win-$Arch" -and ($_.name -like '*.exe') -and ($_.url -match 'windowsdesktop-runtime') } |
    Select-Object -First 1

  if (-not $file) { throw "No windowsdesktop-runtime exe found for win-$Arch in release '$latest'." }
  return $file
}

function Get-FileSha512Bytes {
  param([string]$Path)
  $bytes = [System.IO.File]::ReadAllBytes($Path)
  $sha = [System.Security.Cryptography.SHA512]::Create()
  return $sha.ComputeHash($bytes)
}

function To-HexString {
  param([byte[]]$Bytes)
  ($Bytes | ForEach-Object { $_.ToString('x2') }) -join ''
}

try {
  if (-not $Architecture) { $Architecture = Get-OsArch }
  $channelMajorMinor = ($Channel -split '\.')[0..1] -join '.'

  Write-Log "Channel: $Channel | Runtime: $Runtime | Arch: $Architecture"

  if (Test-DotNetDesktopRuntimeInstalled -MajorMinor $channelMajorMinor -Arch $Architecture) {
    Write-Log ".NET $channelMajorMinor Windows Desktop Runtime already installed."
    exit 0
  }

  $manifest = Get-ReleaseManifest -Channel $Channel
  $file = Select-WindowsDesktopFile -Manifest $manifest -Arch $Architecture
  $url = $file.url
  $expectedHash = $file.hash
  if (-not $expectedHash) { $expectedHash = $file.sha512 }
  if (-not $expectedHash) { throw "Manifest entry has no hash/sha512." }

  $tempDir = Join-Path $env:TEMP 'dotnet-runtime'
  New-Item -Path $tempDir -ItemType Directory -Force | Out-Null
  $outFile = Join-Path $tempDir (Split-Path $url -Leaf)

  Write-Log "Downloading: $url"
  Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $outFile

  Write-Log "Verifying SHA512..."
  $bytes = Get-FileSha512Bytes -Path $outFile
  $actualHex = To-HexString -Bytes $bytes
  # Compare case-insensitive; expected may be hex or base64 in some channels
  $ok = $false
  if ($expectedHash -match '^[0-9a-fA-F]{128}$') {
    if ($actualHex.ToLowerInvariant() -eq $expectedHash.ToLowerInvariant()) { $ok = $true }
  }
  else {
    $actualB64 = [Convert]::ToBase64String($bytes)
    if ($actualB64 -eq $expectedHash) { $ok = $true }
  }
  if (-not $ok) {
    Write-Log "SHA512 mismatch."
    Remove-Item -Force -ErrorAction SilentlyContinue $outFile
    exit 1
  }

  Write-Log "Installing quietly..."
  $args = @('/install', '/quiet', '/norestart')
  $proc = Start-Process -FilePath $outFile -ArgumentList $args -Wait -PassThru
  $code = $proc.ExitCode
  Write-Log "Installer exit code: $code"
  exit $code
}
catch {
  if ($VerboseLog) { Write-Host $_.Exception.Message }
  exit 1
}
