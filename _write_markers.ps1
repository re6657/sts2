$MOD_DIR = "E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2"
$SESSION_ID = "coop-session"

@"
role=host
clientIndex=0
endpoint=127.0.0.1:9999
sessionId=$SESSION_ID
"@ | Set-Content -Path "$MOD_DIR\enable-local-broker-host.txt" -Encoding ascii

@"
role=client
clientIndex=1
endpoint=127.0.0.1:9999
sessionId=$SESSION_ID
"@ | Set-Content -Path "$MOD_DIR\enable-local-broker-client.txt" -Encoding ascii

Write-Host "Host marker:"
Get-Content "$MOD_DIR\enable-local-broker-host.txt"
Write-Host ""
Write-Host "Client marker:"
Get-Content "$MOD_DIR\enable-local-broker-client.txt"
