param(
  [string]$Owner = 'SaltwaterC',
  [string]$Repo = 'ScreenStateService',
  [string]$OutDir = $(Join-Path $PSScriptRoot 'External'),
  [ValidateSet('x64','x86')]
  [string]$Arch = 'x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

$targetFile = Join-Path $OutDir ("ScreenStateService-{0}.msi" -f $Arch)
if (Test-Path $targetFile) {
  Write-Host "Found local artifact: $targetFile (skipping download)" -ForegroundColor Yellow
  exit 0
}

$api = "https://api.github.com/repos/$Owner/$Repo/releases/latest"
Write-Host "Querying $api" -ForegroundColor Cyan

$resp = Invoke-WebRequest -UseBasicParsing -Uri $api
$json = $resp.Content | ConvertFrom-Json

if (-not $json) { throw "Failed to parse GitHub API response" }

# Pick matching arch MSI if present
$pattern = if ($Arch -eq 'x64') { '(?i)(x64|amd64).*\.msi$' } else { '(?i)(x86|win32).*\.msi$' }
$asset = $json.assets | Where-Object { $_.name -match $pattern } | Select-Object -First 1
if (-not $asset) {
  # Fallback: any MSI
  $asset = $json.assets | Where-Object { $_.name -match '(?i)\.msi$' } | Select-Object -First 1
}
if (-not $asset) { throw "No MSI asset found in latest release of $Owner/$Repo" }

Write-Host "Downloading $($asset.name) -> $targetFile" -ForegroundColor Green

Invoke-WebRequest -UseBasicParsing -Uri $asset.browser_download_url -OutFile $targetFile

Write-Host "Downloaded ScreenStateService MSI." -ForegroundColor Green
