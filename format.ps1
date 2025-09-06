[CmdletBinding()]
param(
    [switch]$Check
)

$solution = Join-Path $PSScriptRoot 'KeyboardBacklightForLenovo.sln'
$dotnetArgs = @('format', $solution)
if ($Check) { $dotnetArgs += '--verify-no-changes' }

Write-Host "Running dotnet $($dotnetArgs -join ' ')"
dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$psFiles = Get-ChildItem -Recurse -Include *.ps1,*.psm1 -File
$psFormatter = $null
if ($psFiles) {
    if (Get-Module -ListAvailable -Name PSScriptAnalyzer) {
        Import-Module PSScriptAnalyzer
        $psFormatter = { param($text) Invoke-Formatter -ScriptDefinition $text -IndentationSize 2 }
    } else {
        Write-Warning 'PSScriptAnalyzer not installed; PowerShell files will have line endings normalized only.'
    }
    foreach ($file in $psFiles) {
        $content = Get-Content $file -Raw
        if ($psFormatter) { $content = & $psFormatter $content }
        $content = $content -replace "`r?`n", "`r`n"
        if (-not $content.EndsWith("`r`n")) { $content += "`r`n" }
        [System.IO.File]::WriteAllText($file.FullName, $content)
    }
}

$xmlPatterns = '*.xml','*.xaml','*.csproj','*.wixproj','*.props','*.targets','*.wxs','*.wxl','*.proj','*.vcxproj'
Get-ChildItem -Recurse -Include $xmlPatterns -File | ForEach-Object {
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
