# Requires: PowerShell 5.1
param(
  [string]$Channel,
  [ValidateSet('windowsdesktop')]
  [string]$Runtime = 'windowsdesktop',
  [ValidateSet('x64', 'x86')]
  [string]$Architecture,
  [switch]$VerboseLog
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$StampedChannel = '__DOTNET_DESKTOP_RUNTIME_CHANNEL__'
$UnstampedChannelToken = [string]::Concat('__DOTNET', '_DESKTOP_RUNTIME_CHANNEL__')

function Write-Log {
  param([string]$Message)
  if ($VerboseLog) { Write-Host "[dotnet-shim] $Message" }
}

function Get-OsArch {
  if ([Environment]::Is64BitOperatingSystem) { return 'x64' } else { return 'x86' }
}

function Test-DotNetDesktopRuntimeInstalled {
  param(
    [Parameter(Mandatory)] [string]$MajorMinor,
    [Parameter(Mandatory)] [ValidateSet('x64', 'x86')] [string]$Arch
  )

  $uninstallRoots = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
  )

  foreach ($root in $uninstallRoots) {
    if (-not (Test-Path $root)) { continue }
    foreach ($sub in Get-ChildItem $root -ErrorAction SilentlyContinue) {
      $p = Get-ItemProperty $sub.PSPath -ErrorAction SilentlyContinue
      if (-not $p) { continue }

      $displayName = ($p | Select-Object -ExpandProperty DisplayName -ErrorAction SilentlyContinue)
      $displayVersion = ($p | Select-Object -ExpandProperty DisplayVersion -ErrorAction SilentlyContinue)

      if (-not $displayName -or -not $displayVersion) { continue }
      if ($displayName -notlike 'Microsoft Windows Desktop Runtime*') { continue }
      if ($displayVersion -notlike "$MajorMinor.*") { continue }
      if ($displayName -match "\((x64|x86)\)$" -and $Matches[1] -ne $Arch) { continue }

      return $true
    }
  }

  $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
  if ($dotnet) {
    $lines = & $dotnet.Source --list-runtimes 2>$null
    if ($lines -match "^Microsoft\.WindowsDesktop\.App\s+$MajorMinor\.\d+\s") {
      return $true
    }
  }

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
  return Invoke-RestMethod -UseBasicParsing -Uri $uri
}

function Select-WindowsDesktopFile {
  param($Manifest, [string]$Arch)

  $latest = $Manifest.'latest-release'
  if (-not $latest) { throw 'Unable to determine latest release from manifest.' }

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
  try {
    return $sha.ComputeHash($bytes)
  }
  finally {
    $sha.Dispose()
  }
}

function ConvertTo-HexString {
  param([byte[]]$Bytes)

  ($Bytes | ForEach-Object { $_.ToString('x2') }) -join ''
}

$exitCode = 0
try {
  if (-not $Architecture) { $Architecture = Get-OsArch }
  if ([string]::IsNullOrWhiteSpace($Channel)) { $Channel = $StampedChannel }
  if ([string]::IsNullOrWhiteSpace($Channel) -or $Channel -eq $UnstampedChannelToken) {
    throw 'The .NET Desktop Runtime channel was not stamped into this installer shim.'
  }

  $channelParts = $Channel -split '\.'
  if ($channelParts.Length -lt 2) { throw "Invalid .NET runtime channel '$Channel'." }
  $channelMajorMinor = $channelParts[0..1] -join '.'

  Write-Log "Channel: $Channel | Runtime: $Runtime | Arch: $Architecture"

  $runtimeInstalled = Test-DotNetDesktopRuntimeInstalled -MajorMinor $channelMajorMinor -Arch $Architecture
  if ($runtimeInstalled) {
    Write-Log ".NET $channelMajorMinor Windows Desktop Runtime already installed."
  }
  else {
    $manifest = Get-ReleaseManifest -Channel $Channel
    $file = Select-WindowsDesktopFile -Manifest $manifest -Arch $Architecture
    $url = $file.url
    $expectedHash = $file.hash
    if (-not $expectedHash) { $expectedHash = $file.sha512 }
    if (-not $expectedHash) { throw 'Manifest entry has no hash/sha512.' }

    $tempDir = Join-Path $env:TEMP 'dotnet-runtime'
    New-Item -Path $tempDir -ItemType Directory -Force | Out-Null
    $outFile = Join-Path $tempDir (Split-Path $url -Leaf)

    Write-Log "Downloading: $url"
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $outFile

    Write-Log 'Verifying SHA512...'
    $bytes = Get-FileSha512Bytes -Path $outFile
    $actualHex = ConvertTo-HexString -Bytes $bytes
    $ok = $false

    if ($expectedHash -match '^[0-9a-fA-F]{128}$') {
      if ($actualHex.ToLowerInvariant() -eq $expectedHash.ToLowerInvariant()) { $ok = $true }
    }
    else {
      $actualBase64 = [Convert]::ToBase64String($bytes)
      if ($actualBase64 -eq $expectedHash) { $ok = $true }
    }

    if (-not $ok) {
      Write-Log 'SHA512 mismatch.'
      Remove-Item -Force -ErrorAction SilentlyContinue $outFile
      $exitCode = 1
    }
    else {
      Write-Log 'Installing quietly...'
      $proc = Start-Process -FilePath $outFile -ArgumentList @('/install', '/quiet', '/norestart') -Wait -PassThru
      $exitCode = $proc.ExitCode
      Write-Log "Installer exit code: $exitCode"
    }
  }
}
catch {
  if ($VerboseLog) { Write-Host $_.Exception.Message }
  $exitCode = 1
}

exit $exitCode
