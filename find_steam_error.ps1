$asm = [System.Reflection.Assembly]::LoadFile('E:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll')
$types = $asm.GetTypes() | Where-Object {
    $_.GetMethods([System.Reflection.BindingFlags]'Public,NonPublic,Instance,Static') | Where-Object { $_.Name -eq 'DoSteamSpecificError' }
}
foreach ($t in $types) {
    Write-Output ('Type: ' + $t.FullName)
    foreach ($m in $t.GetMethods([System.Reflection.BindingFlags]'Public,NonPublic,Instance,Static')) {
        if ($m.Name -eq 'DoSteamSpecificError') {
            $parms = $m.GetParameters() | ForEach-Object { $_.ParameterType.Name + ' ' + $_.Name }
            Write-Output ('  Method: ' + $m.ReturnType.Name + ' ' + $m.Name + '(' + [string]::Join(', ', $parms) + ')')
        }
    }
}

# Also search for types containing "Error" or "Crash"
Write-Output "`n=== Types with 'Error' or 'Crash' in name ==="
foreach ($t in $asm.GetTypes()) {
    if ($t.Name -match 'Error|Crash|Steam.*Fail') {
        $methods = $t.GetMethods([System.Reflection.BindingFlags]'Public,NonPublic,Instance,Static') | ForEach-Object { $_.Name } | Select-Object -Unique
        Write-Output ('  ' + $t.FullName + ' [' + [string]::Join(', ', ($methods | Select-Object -First 10)) + ']')
    }
}
