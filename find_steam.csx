using System.Reflection;
using System.Runtime.Loader;

var gamePath = @"E:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";

// Hook assembly resolution
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    var dllPath = Path.Combine(gamePath, name.Name + ".dll");
    if (File.Exists(dllPath))
    {
        try { return ctx.LoadFromAssemblyPath(dllPath); }
        catch { }
    }
    return null;
};

try
{
    var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(gamePath, "sts2.dll"));

    // Find DoSteamSpecificError
    foreach (var t in asm.GetTypes())
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (m.Name == "DoSteamSpecificError")
            {
                Console.WriteLine($"Type: {t.FullName}");
                Console.WriteLine($"  Method: {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                Console.WriteLine($"  IsStatic: {m.IsStatic}, IsPublic: {m.IsPublic}");
            }
        }
    }

    // Also find types with "Error" in name
    Console.WriteLine("\n=== Types containing 'Error' or 'Crash' ===");
    foreach (var t in asm.GetTypes())
    {
        if (t.Name.Contains("Error") || t.Name.Contains("Crash") || t.Name.Contains("Popup"))
        {
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Select(m => m.Name).Distinct().Take(20);
            // Only show interesting types
            if (methods.Any(m => m.Contains("Error") || m.Contains("Steam") || m.Contains("Crash") || m.Contains("Show")))
            {
                Console.WriteLine($"  {t.FullName}: [{string.Join(", ", methods)}]");
            }
        }
    }
}
catch (ReflectionTypeLoadException ex)
{
    Console.WriteLine("LoaderExceptions:");
    foreach (var e in ex.LoaderExceptions.Take(5))
        Console.WriteLine($"  {e?.Message}");

    // Try to get types that DID load
    Console.WriteLine("\n=== Types that loaded successfully ===");
    foreach (var t in ex.Types.Where(t => t != null))
    {
        foreach (var m in t!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (m.Name == "DoSteamSpecificError")
            {
                Console.WriteLine($"Type: {t.FullName}");
                Console.WriteLine($"  Method: {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
            }
        }
    }
}
