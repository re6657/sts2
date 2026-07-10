Add-Type -Path "$env:USERPROFILE\.nuget\packages\ilspycmd\8.2.0.7535\tools\net6.0\any\Mono.Cecil.dll"

$gameAsm = "E:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAsm)

# Look at NGame.OnSteamNoLongerRunning and SteamInitializer.SteamNoLongerRunning
foreach ($type in $asm.MainModule.Types) {
    if ($type.FullName -eq "MegaCrit.Sts2.Core.Nodes.NGame") {
        Write-Output "=== NGame methods ==="
        foreach ($m in $type.Methods | Where-Object { -not $_.IsGetter -and -not $_.IsSetter } | Sort-Object Name) {
            $p = $m.Parameters | ForEach-Object { "$($_.ParameterType.FullName) $($_.Name)" }
            Write-Output "  $($m.ReturnType.FullName) $($m.Name)($([string]::Join(', ', $p))) IsStatic=$($m.IsStatic) IsPublic=$($m.IsPublic)"
        }
    }
    if ($type.FullName -eq "MegaCrit.Sts2.Core.Platform.Steam.SteamInitializer") {
        Write-Output "=== SteamInitializer methods ==="
        foreach ($m in $type.Methods | Where-Object { -not $_.IsGetter -and -not $_.IsSetter } | Sort-Object Name) {
            $p = $m.Parameters | ForEach-Object { "$($_.ParameterType.FullName) $($_.Name)" }
            Write-Output "  $($m.ReturnType.FullName) $($m.Name)($([string]::Join(', ', $p))) IsStatic=$($m.IsStatic) IsPublic=$($m.IsPublic)"
        }
        Write-Output "=== SteamInitializer events ==="
        foreach ($e in $type.Events) {
            Write-Output "  Event: $($e.Name) Type=$($e.EventType.FullName)"
        }
    }
    # Also check NErrorPopup
    if ($type.FullName -eq "MegaCrit.Sts2.Core.Nodes.CommonUi.NErrorPopup") {
        Write-Output "=== NErrorPopup methods ==="
        foreach ($m in $type.Methods | Where-Object { -not $_.IsGetter -and -not $_.IsSetter } | Sort-Object Name) {
            $p = $m.Parameters | ForEach-Object { "$($_.ParameterType.FullName) $($_.Name)" }
            Write-Output "  $($m.ReturnType.FullName) $($m.Name)($([string]::Join(', ', $p))) IsStatic=$($m.IsStatic) IsPublic=$($m.IsPublic)"
        }
    }
}

$asm.Dispose()
