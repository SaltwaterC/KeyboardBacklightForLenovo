param(
    # Optional: explicitly pass the service EXE path; otherwise we auto-pick a *Service.exe in this folder
    [string] $ServiceExePath
)

Write-Host "=== Backlight service dev installer ==="

# --- Admin check ---
$currIdentity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$currPrincipal = [Security.Principal.WindowsPrincipal]$currIdentity
if (-not $currPrincipal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    throw "This script must run elevated (as Administrator)."
}

# --- Resolve service EXE ---
$scriptDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ServiceExePath)) {
    # Prefer a single *Service.exe in the same folder
    $candidates = Get-ChildItem -LiteralPath $scriptDir -Filter "*Service.exe" -File | Select-Object -Expand FullName
    if ($candidates.Count -eq 0) { throw "No *Service.exe found in '$scriptDir'. Pass -ServiceExePath." }
    if ($candidates.Count -gt 1) { throw "Multiple *Service.exe found: `n$($candidates -join "`n")`nPass -ServiceExePath to disambiguate." }
    $ServiceExePath = $candidates
}
if (-not (Test-Path -LiteralPath $ServiceExePath)) {
    throw "Service EXE not found: $ServiceExePath"
}

$exeFull = (Resolve-Path -LiteralPath $ServiceExePath).Path
$exeName = [System.IO.Path]::GetFileNameWithoutExtension($exeFull)

# Service name & display name derived from EXE (no invented names!)
$ServiceName        = $exeName
$ServiceDisplayName = $exeName

Write-Host "Service EXE: $exeFull"
Write-Host "Service Name: $ServiceName"

# --- (Re)create service ---
$existing = Get-Service -ErrorAction SilentlyContinue -Name $ServiceName
if ($existing) {
    Write-Host "Service '$ServiceName' exists. Replacing..."
    try { Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue } catch {}
    sc.exe delete "$ServiceName" | Out-Null
    Start-Sleep -Milliseconds 600
}

New-Service -Name $ServiceName `
            -BinaryPathName "`"$exeFull`"" `
            -DisplayName $ServiceDisplayName `
            -Description "Keyboard backlight reset helper." `
            -StartupType Automatic | Out-Null

Write-Host "Service created."

# --- Event Log source (match the *service* name) ---
$eventSource = $ServiceName     # <— important: source == this service
$eventLog    = "Application"

# Resolve .NET v4 Full InstallPath (like your WiX <RegistrySearch>)
$dotNetPath = $null
try {
    $dotNetPath = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" -Name InstallPath -ErrorAction Stop).InstallPath
} catch {
    # Fallbacks if registry key missing (rare on modern Windows, but safe to have)
    $framework64 = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
    $framework32 = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319"
    if (Test-Path $framework64) { $dotNetPath = $framework64 }
    elseif (Test-Path $framework32) { $dotNetPath = $framework32 }
    else { throw "Cannot locate .NET v4 InstallPath; EventMessageFile cannot be set." }
}
$eventMsgDll = Join-Path $dotNetPath "EventLogMessages.dll"

# Create/point the event source
if (-not [System.Diagnostics.EventLog]::SourceExists($eventSource)) {
    Write-Host "Creating EventLog source '$eventSource' in '$eventLog'..."
    New-EventLog -LogName $eventLog -Source $eventSource -ErrorAction Stop
}
# Set EventMessageFile = [DOTNET4X_FULLPATH]EventLogMessages.dll
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\$eventLog\$eventSource"
Set-ItemProperty -Path $regPath -Name "EventMessageFile" -Value $eventMsgDll -ErrorAction Stop

Write-Host "Event source configured: $eventSource -> $eventMsgDll"

# --- Start service (optional; comment out if you prefer manual start) ---
try {
    Start-Service -Name $ServiceName -ErrorAction Stop
    Write-Host "Service started."
} catch {
    Write-Warning "Service created but not started: $($_.Exception.Message)"
}

Write-Host "Done."
