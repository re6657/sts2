Add-Type -Path "$env:USERPROFILE\.nuget\packages\ilspycmd\8.2.0.7535\tools\net6.0\any\Mono.Cecil.dll"

$gameAsm = "E:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAsm)

# Search for the string "DoSteamSpecificError" in all resources
Write-Output "=== Searching Resources ==="
foreach ($res in $asm.MainModule.Resources) {
    Write-Output "  Resource: $($res.Name) ($($res.ResourceType))"
}

# Search for the string in any method body
Write-Output "`n=== Searching method bodies for 'DoSteamSpecific' ==="
foreach ($type in $asm.MainModule.Types) {
    foreach ($method in $type.Methods) {
        if ($method.HasBody) {
            foreach ($instr in $method.Body.Instructions) {
                if ($instr.Operand -is [string] -and $instr.Operand.ToString().Contains("DoSteamSpecific")) {
                    Write-Output "  STRING REF in $($type.FullName).$($method.Name) (IL_$($instr.Offset.ToString('X4'))): $($instr.Operand)"
                }
            }
        }
    }
}

# Search for lazy-initialized field names or attributes containing the string
Write-Output "`n=== Searching fields and custom attributes ==="
foreach ($type in $asm.MainModule.Types) {
    foreach ($field in $type.Fields) {
        if ($field.Name.Contains("SteamSpecific") -or $field.Name.Contains("DoSteam")) {
            Write-Output "  FIELD: $($type.FullName).$($field.Name)"
        }
    }
    if ($type.HasCustomAttributes) {
        foreach ($attr in $type.CustomAttributes) {
            if ($attr.ToString() -match "SteamSpecific|DoSteam") {
                Write-Output "  ATTR: $($type.FullName): $($attr.AttributeType.FullName)"
            }
        }
    }
}

# Also try to search method names containing "Steam" in any type
Write-Output "`n=== All methods containing 'Steam' in name ==="
foreach ($type in $asm.MainModule.Types) {
    foreach ($method in $type.Methods) {
        if ($method.Name -match "Steam" -and -not $method.IsGetter -and -not $method.IsSetter) {
            Write-Output "  $($type.FullName).$($method.Name)"
        }
    }
}

$asm.Dispose()
