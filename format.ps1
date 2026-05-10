[CmdletBinding()]
param(
  [switch]$Check
)

$binaryExtensions = @(
  '.dll',
  '.exe',
  '.gif',
  '.ico',
  '.jpg',
  '.jpeg',
  '.msi',
  '.pdb',
  '.png',
  '.rtf'
)

function Get-TrackedTextFiles {
  git ls-files | Where-Object {
    $extension = [System.IO.Path]::GetExtension($_).ToLowerInvariant()
    $binaryExtensions -notcontains $extension
  }
}

function Test-CrlfLineEndings {
  param([string]$Path)

  $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path))
  for ($i = 0; $i -lt $bytes.Length; $i++) {
    if ($bytes[$i] -eq 10 -and ($i -eq 0 -or $bytes[$i - 1] -ne 13)) {
      return $false
    }
  }
  return $true
}

function Normalize-CrlfLineEndings {
  param([string]$Path)

  $resolved = Resolve-Path -LiteralPath $Path
  $content = [System.IO.File]::ReadAllText($resolved)
  $content = Normalize-CrlfText -Text $content
  [System.IO.File]::WriteAllText($resolved, $content, [System.Text.UTF8Encoding]::new($false))
}

function Normalize-CrlfText {
  param([string]$Text)

  $normalized = $Text -replace "(`r`n|`n|`r)", "`r`n"
  if (-not $normalized.EndsWith("`r`n")) { $normalized += "`r`n" }
  return $normalized
}

function Format-XmlText {
  param([string]$Path)

  $xml = [xml]([System.IO.File]::ReadAllText($Path))
  $settings = New-Object System.Xml.XmlWriterSettings
  $settings.Indent = $true
  $settings.IndentChars = '  '
  $settings.NewLineChars = "`r`n"
  $settings.NewLineHandling = 'Replace'
  $settings.Encoding = [System.Text.UTF8Encoding]::new($false)

  $stream = [System.IO.MemoryStream]::new()
  $writer = [System.Xml.XmlWriter]::Create($stream, $settings)
  $xml.Save($writer)
  $writer.Close()

  return [System.Text.Encoding]::UTF8.GetString($stream.ToArray())
}

function Ensure-PSScriptAnalyzer {
  if (-not (Get-Module -ListAvailable -Name PSScriptAnalyzer)) {
    Write-Host "Installing PSScriptAnalyzer module..."
    # Ensure NuGet provider is available
    if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
      Install-PackageProvider -Name NuGet -Force | Out-Null
    }
    Install-Module -Name PSScriptAnalyzer -Scope CurrentUser -Force -AllowClobber -Repository PSGallery | Out-Null
  }
}

$psFormatSettings = 
@{
  IncludeRules = @(
    'PSPlaceOpenBrace',
    'PSPlaceCloseBrace',
    'PSUseConsistentWhitespace',
    'PSUseConsistentIndentation',
    'PSAlignAssignmentStatement',
    'PSUseCorrectCasing'
  )

  Rules        = @{
    PSPlaceOpenBrace           = @{
      Enable             = $true
      OnSameLine         = $true
      NewLineAfter       = $true
      IgnoreOneLineBlock = $true
    }

    PSPlaceCloseBrace          = @{
      Enable             = $true
      NewLineAfter       = $true
      IgnoreOneLineBlock = $true
      NoEmptyLineBefore  = $false
    }

    PSUseConsistentIndentation = @{
      Enable              = $true
      Kind                = 'space'
      PipelineIndentation = 'IncreaseIndentationForFirstPipeline'
      IndentationSize     = 2
    }

    PSUseConsistentWhitespace  = @{
      Enable                                  = $true
      CheckInnerBrace                         = $true
      CheckOpenBrace                          = $true
      CheckOpenParen                          = $true
      CheckOperator                           = $true
      CheckPipe                               = $true
      CheckPipeForRedundantWhitespace         = $false
      CheckSeparator                          = $true
      CheckParameter                          = $false
      IgnoreAssignmentOperatorInsideHashTable = $true
    }

    PSAlignAssignmentStatement = @{
      Enable         = $true
      CheckHashtable = $true
    }

    PSUseCorrectCasing         = @{
      Enable             = $true
    }
  }
}

$solution = Join-Path $PSScriptRoot 'KeyboardBacklightForLenovo.sln'
$dotnetArgs = @('format', $solution)
if ($Check) { $dotnetArgs += '--verify-no-changes' }
$initialStatus = if ($Check) { git status --short }

Write-Host "Running dotnet $($dotnetArgs -join ' ')"
dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Ensure-PSScriptAnalyzer
$formatFailures = [System.Collections.Generic.List[string]]::new()
$psFiles = Get-ChildItem -Recurse -Include *.ps1, *.psm1 -File
$psFormatter = $null
if ($psFiles) {
  if (Get-Module -ListAvailable -Name PSScriptAnalyzer) {
    Import-Module PSScriptAnalyzer
    $psFormatter = { param([string]$text) Invoke-Formatter -ScriptDefinition $text -Settings $psFormatSettings -ErrorAction Stop }
  }
  else {
    Write-Warning 'PSScriptAnalyzer not installed; PowerShell files will have line endings normalized only.'
  }
  foreach ($file in $psFiles) {
    Write-Host "Format ps1: ${file}"
    $content = [System.IO.File]::ReadAllText($file.FullName)
    $originalContent = $content
    $content = Normalize-CrlfText -Text $content
    if ($psFormatter) { $content = & $psFormatter $content }
    $content = Normalize-CrlfText -Text $content
    if ($Check) {
      if ($content -cne $originalContent) { $formatFailures.Add($file.FullName) }
    }
    else {
      [System.IO.File]::WriteAllText($file.FullName, $content, [System.Text.UTF8Encoding]::new($false))
    }
  }
}

$xmlPatterns = '*.xml', '*.xaml', '*.csproj', '*.wixproj', '*.props', '*.wxs', '*.wxl', '*.proj', '*.vcxproj'
$xmlFiles = Get-ChildItem -Recurse -Include $xmlPatterns -File | Where-Object { $_.FullName -notmatch '\\obj\\' }
foreach ($file in $xmlFiles) {
  Write-Host "Format xml: ${file}"
  $content = Normalize-CrlfText -Text (Format-XmlText -Path $file.FullName)
  if ($Check) {
    $originalContent = [System.IO.File]::ReadAllText($file.FullName)
    if ($content -cne $originalContent) { $formatFailures.Add($file.FullName) }
  }
  else {
    [System.IO.File]::WriteAllText($file.FullName, $content, [System.Text.UTF8Encoding]::new($false))
  }
}

$lineEndingFailures = @()
foreach ($file in Get-TrackedTextFiles) {
  if ($Check) {
    if (-not (Test-CrlfLineEndings -Path $file)) {
      $lineEndingFailures += $file
    }
  }
  else {
    Normalize-CrlfLineEndings -Path $file
  }
}

if ($lineEndingFailures) {
  Write-Error "Files do not use CRLF line endings:`n$($lineEndingFailures -join "`n")" -ErrorAction Stop
}

if ($formatFailures.Count -gt 0) {
  $formatFailureMessage = ($formatFailures | ForEach-Object { "  $_" }) -join "`n"
  Write-Host "Format failure count: $($formatFailures.Count)"
  Write-Host $formatFailureMessage
  Write-Error "Files are not properly formatted:`n$formatFailureMessage" -ErrorAction Stop
}

if ($Check) {
  $status = git status --short
  if (($status -join "`n") -cne ($initialStatus -join "`n")) {
    Write-Error 'Formatter check changed the working tree' -ErrorAction Stop
  }
}
