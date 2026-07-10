Add-Type -Path "$env:USERPROFILE\.nuget\packages\ilspycmd\8.2.0.7535\tools\net6.0\any\Mono.Cecil.dll"
Add-Type -Path "$env:USERPROFILE\.nuget\packages\ilspycmd\8.2.0.7535\tools\net6.0\any\Mono.Cecil.Mdb.dll"
Add-Type -Path "$env:USERPROFILE\.nuget\packages\ilspycmd\8.2.0.7535\tools\net6.0\any\Mono.Cecil.Pdb.dll"

$gameAsm = "E:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAsm)

$found = $false
foreach ($type in $asm.MainModule.Types) {
    foreach ($method in $type.Methods) {
        if ($method.Name -eq "DoSteamSpecificError") {
            $found = $true
            Write-Output "Type: $($type.FullName)"
            Write-Output "  ReturnType: $($method.ReturnType.FullName)"
            Write-Output "  IsStatic: $($method.IsStatic)"
            Write-Output "  IsPublic: $($method.IsPublic)"
            $parms = $method.Parameters | ForEach-Object { "$($_.ParameterType.FullName) $($_.Name)" }
            Write-Output "  Parameters: $([string]::Join(', ', $parms))"

            Write-Output "`n  All methods in this type:"
            foreach ($m in $type.Methods | Where-Object { -not $_.IsGetter -and -not $_.IsSetter } | Sort-Object Name) {
                $p = $m.Parameters | ForEach-Object { $_.ParameterType.Name }
                Write-Output "    $($m.ReturnType.Name) $($m.Name)($([string]::Join(', ', $p)))"
            }
        }
    }
}

if (-not $found) {
    Write-Output "DoSteamSpecificError NOT FOUND in main module. Checking nested types..."

    # Also check nested types
    function CheckType($t) {
        foreach ($m in $t.Methods) {
            if ($m.Name -eq "DoSteamSpecificError") {
                Write-Output "FOUND in: $($t.FullName)"
                Write-Output "  ReturnType: $($m.ReturnType.FullName)"
                Write-Output "  IsStatic: $($m.IsStatic)"
            }
        }
        foreach ($nested in $t.NestedTypes) {
            CheckType $nested
        }
    }
    foreach ($t in $asm.MainModule.Types) { CheckType $t }
}

$asm.Dispose()
