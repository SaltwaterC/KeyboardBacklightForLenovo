$Name = "BacklightResetService"
$ErrorActionPreference = 'SilentlyContinue'
sc.exe stop $Name | Out-Null
sc.exe delete $Name | Out-Null
Write-Host "Removed $Name."

Remove-Item -Path "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\$Name" -Recurse -Force
