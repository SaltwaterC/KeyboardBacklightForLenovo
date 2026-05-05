param(
  [string]$VariablesPath = 'Variables.props'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = if ($PSScriptRoot) { $PSScriptRoot } else { [System.IO.Path]::GetDirectoryName($MyInvocation.MyCommand.Path) }
$variables = if ([System.IO.Path]::IsPathRooted($VariablesPath)) {
  $VariablesPath
}
else {
  Join-Path $root $VariablesPath
}

if (-not (Test-Path -LiteralPath $variables)) {
  throw "Variables file not found: $variables"
}

[xml]$xml = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $variables), [System.Text.UTF8Encoding]::new($false))

function Get-MsBuildProperty {
  param(
    [xml]$Xml,
    [string]$Name
  )

  $node = $Xml.SelectSingleNode("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='$Name']")
  if ($node -and $node.InnerText) { return $node.InnerText.Trim() }
  throw "Required property '$Name' was not found in $variables"
}

$year = Get-MsBuildProperty -Xml $xml -Name 'CopyrightYear'
$holder = Get-MsBuildProperty -Xml $xml -Name 'CopyrightHolder'
$notice = "Copyright (c) $year $holder"

$licenseText = @"
MIT License

$notice

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"@

function ConvertTo-RtfEscapedText {
  param([string]$Text)

  $escapedLines = foreach ($line in ($Text -split "`r?`n")) {
    $builder = New-Object System.Text.StringBuilder
    foreach ($char in $line.ToCharArray()) {
      $codePoint = [int][char]$char
      switch ($char) {
        '\' { [void]$builder.Append('\\') }
        '{' { [void]$builder.Append('\{') }
        '}' { [void]$builder.Append('\}') }
        default {
          if ($codePoint -le 127) {
            [void]$builder.Append($char)
          }
          else {
            if ($codePoint -gt 32767) { $codePoint -= 65536 }
            [void]$builder.Append("\u${codePoint}?")
          }
        }
      }
    }
    $builder.ToString()
  }

  return ($escapedLines | ForEach-Object { "$_\par" }) -join "`r`n"
}

$licenseMdPath = Join-Path $root 'LICENSE.md'
$installerRtfPath = Join-Path $root 'KeyboardBacklightForLenovo.Installer\LICENSE.rtf'
$bundleRtfPath = Join-Path $root 'KeyboardBacklightForLenovo.Bundle\LICENSE.rtf'

$licenseText = $licenseText -replace "`r?`n", "`r`n"
if (-not $licenseText.EndsWith("`r`n")) { $licenseText += "`r`n" }
[System.IO.File]::WriteAllText($licenseMdPath, $licenseText, [System.Text.UTF8Encoding]::new($false))

$rtfBody = ConvertTo-RtfEscapedText -Text $licenseText
$rtfText = "{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs18`r`n$rtfBody`r`n}`r`n"
[System.IO.File]::WriteAllText($installerRtfPath, $rtfText, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText($bundleRtfPath, $rtfText, [System.Text.UTF8Encoding]::new($false))

Write-Host "Generated license files with: $notice"
