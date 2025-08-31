param(
  [string]$Project = '..\Variables.props',
  [string]$Out = 'GeneratedVariables.wxi',
  # MSBuild properties to read (order is preserved in output)
  [string[]]$Properties = @('ProductVersion','Company','RootNamespace','Product'),
  # Optional mapping: MSBuildPropertyName -> WixDefineName
  [hashtable]$DefineMap = @{}
)

$ErrorActionPreference = 'Stop'

# Resolve script dir
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { [System.IO.Path]::GetDirectoryName($MyInvocation.MyCommand.Path) }

# Resolve csproj path
if (-not [System.IO.Path]::IsPathRooted($Project)) {
  $projPath = [System.IO.Path]::GetFullPath((Join-Path $scriptDir $Project))
} else {
  $projPath = $Project
}
if (-not (Test-Path -LiteralPath $projPath)) {
  throw "Couldn't find csproj at '$projPath'"
}

# Resolve output path
if (-not [System.IO.Path]::IsPathRooted($Out)) {
  $outPath = [System.IO.Path]::GetFullPath((Join-Path $scriptDir $Out))
} else {
  $outPath = $Out
}

[xml]$csproj = Get-Content -LiteralPath $projPath

function Get-MsBuildProperty([xml]$xml, [string]$name) {
  $node = $xml.SelectSingleNode("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='$name']")
  if ($node -and $node.InnerText) { return $node.InnerText.Trim() }
  return $null
}

# Collect values
$values = @{}
foreach ($propName in $Properties) {
  $value = Get-MsBuildProperty $csproj $propName
  if ($null -eq $value -or $value -eq '') {
    Write-Warning "Property '$propName' not found or empty in $projPath; skipping."
    continue
  }
  $values[$propName] = $value
}

if ($values.Count -eq 0) {
  throw "No properties were resolved; nothing to write."
}

# Build WiX include content
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<Include>')
foreach ($propName in $Properties) {
  if (-not $values.ContainsKey($propName)) { continue }

  # Determine define name
  $defineName = if ($DefineMap.ContainsKey($propName)) { $DefineMap[$propName] } else { $propName }

  # Escape XML special chars
  $raw = [string]$values[$propName]
  $escaped = $raw.Replace('&','&amp;').Replace('<','&lt;').Replace('>','&gt;').Replace('"','&quot;').Replace("'","&apos;")

  [void]$sb.AppendLine("  <?define $defineName=""$escaped"" ?>")
}
[void]$sb.AppendLine('</Include>')

# Write file
Set-Content -LiteralPath $outPath -Value $sb.ToString() -Encoding UTF8

# Log summary
Write-Host "Generated WiX variables to $outPath"
foreach ($propName in $Properties) {
  if (-not $values.ContainsKey($propName)) { continue }
  $defineName = if ($DefineMap.ContainsKey($propName)) { $DefineMap[$propName] } else { $propName }
  $val = $values[$propName]
  Write-Host ("  {0,-16} = {1}" -f $defineName, $val)
}
