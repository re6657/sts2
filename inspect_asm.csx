using Mono.Cecil;
using System.Reflection;

var gameAssembly = @"E:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll";
using var asm = AssemblyDefinition.ReadAssembly(gameAssembly);

foreach (var type in asm.MainModule.Types.OrderBy(t => t.FullName))
{
    foreach (var method in type.Methods)
    {
        if (method.Name == "DoSteamSpecificError")
        {
            Console.WriteLine($"Type: {type.FullName}");
            Console.WriteLine($"  Method: {method.ReturnType.Name} {method.Name}({string.Join(", ", method.Parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
            Console.WriteLine($"  IsStatic: {method.IsStatic}, IsPublic: {method.IsPublic}");

            // Show all other methods in this type
            Console.WriteLine("  All methods in this type:");
            foreach (var m in type.Methods.Where(m => !m.IsGetter && !m.IsSetter).OrderBy(m => m.Name))
            {
                Console.WriteLine($"    {m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => $"{p.ParameterType.Name}"))})");
            }
        }
    }
}

// Also search for types that reference Steam crash error
Console.WriteLine("\n=== Searching for Steam error/crash types ===");
foreach (var type in asm.MainModule.Types.Where(t => t.FullName.Contains("Steam") || t.FullName.Contains("Error")))
{
    var steamMethods = type.Methods.Where(m => !m.IsGetter && !m.IsSetter && m.IsPublic)
        .Select(m => m.Name).Distinct().Take(15);
    if (steamMethods.Any(m => m.Contains("Error") || m.Contains("Steam") || m.Contains("Crash") || m.Contains("Show")))
    {
        Console.WriteLine($"  {type.FullName}: {string.Join(", ", steamMethods)}");
    }
}
