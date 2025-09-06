[CmdletBinding()]
param(
  [switch]$Check
)

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

Write-Host "Running dotnet $($dotnetArgs -join ' ')"
dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Ensure-PSScriptAnalyzer
$psFiles = Get-ChildItem -Recurse -Include *.ps1, *.psm1 -File
$psFormatter = $null
if ($psFiles) {
  if (Get-Module -ListAvailable -Name PSScriptAnalyzer) {
    Import-Module PSScriptAnalyzer
    $psFormatter = { param($text) Invoke-Formatter -ScriptDefinition $text -Settings $psFormatSettings }
  }
  else {
    Write-Warning 'PSScriptAnalyzer not installed; PowerShell files will have line endings normalized only.'
  }
  foreach ($file in $psFiles) {
    Write-Host "Format ps1: ${file}"
    $content = Get-Content $file -Raw
    if ($psFormatter) { $content = & $psFormatter $content }
    $content = $content -replace "`r?`n", "`r`n"
    if (-not $content.EndsWith("`r`n")) { $content += "`r`n" }
    [System.IO.File]::WriteAllText($file.FullName, $content)
  }
}

$xmlPatterns = '*.xml', '*.xaml', '*.csproj', '*.wixproj', '*.props', '*.wxs', '*.wxl', '*.proj', '*.vcxproj'
Get-ChildItem -Recurse -Include $xmlPatterns -File | Where-Object { $_.FullName -notmatch '\\obj\\' } | ForEach-Object {
  Write-Host "Format xml: ${_}"
  $xml = [xml](Get-Content $_ -Raw)
  $settings = New-Object System.Xml.XmlWriterSettings
  $settings.Indent = $true
  $settings.IndentChars = '  '
  $settings.NewLineChars = "`r`n"
  $settings.NewLineHandling = 'Replace'
  $writer = [System.Xml.XmlWriter]::Create($_.FullName, $settings)
  $xml.Save($writer)
  $writer.Close()
}

if ($Check) {
  $status = git status --short
  if ($status) {
    Write-Error 'Files are not properly formatted' -ErrorAction Stop
  }
}
