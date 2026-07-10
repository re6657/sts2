$env:TOKENSPIRE2_ROLE = 'host'
Write-Host "Launching game..."
$proc = Start-Process -FilePath 'E:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe' -PassThru -WindowStyle Normal
Start-Sleep -Seconds 20
if ($proc.HasExited) {
    Write-Host "Game EXITED with code: $($proc.ExitCode)"
} else {
    Write-Host "Game RUNNING (PID: $($proc.Id))"
}
# Check for logs
Get-ChildItem "E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2\*coop*.txt" -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "Log: $($_.Name) ($($_.Length) bytes)" }
