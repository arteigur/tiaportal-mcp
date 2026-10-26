# Starts TiaMcpServer over Streamable HTTP so agents on another machine (e.g. the Linux host of
# this Windows VM) can use TIA Portal. Run it in the Windows session where TIA Portal runs - not
# as a service: Openness attaches to TIA Portal in the logged-on user's session.
#
# One-time setup, from an administrator PowerShell (see README, "Remote access over HTTP"):
#   netsh http add urlacl url=http://+:8765/ user=$env:USERDOMAIN\$env:USERNAME
#   New-NetFirewallRule -DisplayName "TIA MCP (HTTP 8765)" -Direction Inbound -Protocol TCP -LocalPort 8765 -RemoteAddress <host-ip> -Action Allow
#
# The token is read from a file only this user can read, so it never shows in the process list.

param(
    [string]$Exe = "$PSScriptRoot\..\..\src\TiaMcpServer\bin\Release\net48\TiaMcpServer.exe",
    [string]$Url = "http://+:8765/mcp",
    [string]$TokenFile = "$env:USERPROFILE\.tia-mcp-token",
    # Server flags, as in the stdio configuration. Add --allow-online / --allow-download only if
    # the remote agents really need them.
    [string[]]$ServerArgs = @("--tia-major-version", "21", "--allow-write")
)

if (-not (Test-Path $TokenFile)) {
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    Set-Content -Path $TokenFile -Value $token -Encoding ascii -NoNewline
    icacls $TokenFile /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null
    Write-Host "New token written to $TokenFile - copy it to the agents' MCP configuration."
}

$env:TIA_MCP_HTTP_TOKEN = (Get-Content -Path $TokenFile -Raw).Trim()
& $Exe @ServerArgs --http $Url
