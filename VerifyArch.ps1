param(
  [string]$Expected,        # e.g. x64 | x86 | ARM64 | Any CPU
  [string]$Exe,             # optional: EXE to validate
  [string]$Msi              # optional: MSI to validate
)

function Normalize-Platform([string]$p){
  switch ($p.Trim().ToUpperInvariant()) {
    'X64'   { 'x64' }
    'X86'   { 'x86' }
    'ARM64' { 'arm64' }
    'ANY CPU' { 'anycpu' }
    default { $p.ToLowerInvariant() }
  }
}

function Get-ExeArch([string]$path) {
  $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
  try {
    $br = New-Object System.IO.BinaryReader($fs)
    $fs.Seek(0x3C, 'Begin') | Out-Null
    $peOff = $br.ReadInt32()
    $fs.Seek($peOff, 'Begin') | Out-Null
    if ($br.ReadUInt32() -ne 0x00004550) { throw "Not a PE file: $path" } # 'PE\0\0'
    $machine = $br.ReadUInt16()
    switch ($machine) {
      0x8664 { 'x64' }
      0x014c { 'x86' }
      0xAA64 { 'arm64' }
      default { "unknown(0x{0:X})" -f $machine }
    }
  } finally { $fs.Dispose() }
}

function Get-MsiArch([string]$path) {
  $installer = New-Object -ComObject WindowsInstaller.Installer
  try {
    $sum = $installer.SummaryInformation($path, 0)
    try {
      $template = $sum.Property(7)  # PID_TEMPLATE
      if     ($template -match '^x64')   { 'x64' }
      elseif ($template -match '^Intel') { 'x86' }
      elseif ($template -match '^Arm64') { 'arm64' }
      else { 'unknown' }
    } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($sum) }
  } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer) }
}

$expected = Normalize-Platform $Expected

if ($Exe) {
  $exeArch = Get-ExeArch $Exe
  if ($expected -ne 'anycpu' -and $exeArch -ne $expected) {
    Write-Error "ARCH MISMATCH: EXE '$Exe' is '$exeArch' but expected '$expected'"
    exit 1
  } else { Write-Host "OK: EXE '$Exe' is '$exeArch' (expected '$expected')" }
}

if ($Msi) {
  $msiArch = Get-MsiArch $Msi
  if ($msiArch -ne $expected) {
    Write-Error "ARCH MISMATCH: MSI '$Msi' is '$msiArch' but expected '$expected'"
    exit 1
  } else { Write-Host "OK: MSI '$Msi' is '$msiArch' (expected '$expected')" }
}
