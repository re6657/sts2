using HarmonyLib;
using System.Reflection;

namespace LocalCoop.Mod.Patches;

internal static class RunIdentityDiagnosticsPatchTarget
{
    public static Type? FindType(string fullName, string simpleName)
    {
        try
        {
            return AccessTools.TypeByName(fullName)
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(GetLoadableTypes)
                    .FirstOrDefault(type => string.Equals(type.Name, simpleName, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TokenSpire2] FindType({simpleName}) failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static IEnumerable<MethodBase> FindMethods(string fullName, string simpleName, IReadOnlySet<string> methodNames)
    {
        var type = FindType(fullName, simpleName);
        if (type is null)
        {
            yield break;
        }

        foreach (var method in AccessTools.GetDeclaredMethods(type).Where(method =>
                     methodNames.Contains(method.Name)
                     && !method.IsAbstract
                     && !method.ContainsGenericParameters
                     && !method.IsGenericMethodDefinition))
        {
            yield return method;
        }
    }

    public static MethodBase? FindMethod(string fullName, string simpleName, string methodName)
    {
        return FindMethods(fullName, simpleName, new HashSet<string>(StringComparer.Ordinal) { methodName }).SingleOrDefault();
    }

    public static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null)!;
        }
    }
}
