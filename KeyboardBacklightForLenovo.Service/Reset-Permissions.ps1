$home = "$env:USERPROFILE"

# Take ownership of everything under your home dir
takeown /F $home /R /D Y

# Remove any ACEs for LOCAL SERVICE recursively
icacls $home /remove "NT AUTHORITY\LOCAL SERVICE" /T /C
