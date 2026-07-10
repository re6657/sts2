using System.Linq;
using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;

namespace LocalCoop.Mod.Patches;

/// <summary>
/// Forces the lobby-to-gameplay transition when all players are ready in broker mode.
///
/// The game's native lobby code detects all-players-ready but then tries to set up
/// Steam/ENet game connections before calling BeginRunForAllPlayers. In broker mode,
/// we skip those native connection steps, so the transition never completes.
///
/// This patch monitors HandlePlayerReadyMessage on StartRunLobby. After the game
/// processes a ready message and all players are ready, it kick-starts the
/// transition by calling BeginRunForAllPlayers directly.
/// </summary>
[HarmonyPatch]
public static class BrokerForceLobbyTransitionPatch
{
    // Track which lobbies we've already triggered to avoid double-firing.
    private static readonly HashSet<object> TriggeredLobbies = new();

    /// <summary>
    /// Returns ALL methods on StartRunLobby that relate to player ready state.
    ///
    /// HandlePlayerReadyMessage: fires when a REMOTE player's ready message arrives.
    /// SetReady / SetPlayerReady / ToggleReady: fires when the LOCAL player readies up.
    ///
    /// We hook ALL of them because the remote-only hook misses the case where
    /// the host readies AFTER the client: HandlePlayerReadyMessage fires once
    /// when client readies (host not ready → skip), then never fires again
    /// when host readies (it's local) → game stuck in lobby forever.
    ///
    /// Harmony supports TargetMethods() returning IEnumerable{MethodBase}
    /// for multi-target patches.
    /// </summary>
    public static IEnumerable<MethodBase> TargetMethods()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby");
        if (type is null)
        {
            yield break;
        }

        // Hook EXACT-MATCH methods that indicate a player's ready state changed.
        // Previously we used .Contains("Ready") which caught initialization
        // methods like RegisterReadyHandler / IsPlayerReady / GetReadyPlayers,
        // causing premature firing before any player actually readied.
        //
        // HandlePlayerReadyMessage: fires when a REMOTE player readies.
        // SetReady / ToggleReady / SetPlayerReady: fires when LOCAL player readies.
        var targetNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "HandlePlayerReadyMessage",
            "SetReady",
            "ToggleReady",
            "SetPlayerReady",
        };

        foreach (var method in AccessTools.GetDeclaredMethods(type))
        {
            if (targetNames.Contains(method.Name))
            {
                yield return method;
            }
        }
    }

    public static void Postfix(object __instance, MethodBase __originalMethod)
    {
        var settings = LoadSettings();
        if (!settings.Enabled)
        {
            return;
        }

        var log = new BrokerEventLog(settings.EventLogPath);

        try
        {
            log.Write($"Broker lobby transition: triggered by {__originalMethod.Name} on {settings.ClientId}.");
            lock (TriggeredLobbies)
            {
                if (TriggeredLobbies.Contains(__instance))
                {
                    return;
                }
            }

            // Check if all players are ready
            if (!AreAllPlayersReady(__instance, log.Write))
            {
                log.Write($"Broker lobby transition: not all players ready yet on {settings.ClientId}.");
                return;
            }

            // Small delay to let the game finish processing the ready message
            log.Write($"Broker lobby transition: all players ready! Forcing BeginRunForAllPlayers on {settings.ClientId}.");

            // IMPORTANT: only the HOST should trigger BeginRunForAllPlayers
            // Both instances can detect "all ready" but only host executes
            var role = settings.Config?.Role;
            if (role != BrokerClientRole.Host)
            {
                log.Write($"Broker lobby transition: skipping BeginRunForAllPlayers because role={role} (not host).");
                return;
            }

            lock (TriggeredLobbies)
            {
                TriggeredLobbies.Add(__instance);
            }

            ForceBeginRunForAllPlayers(__instance, log.Write);
        }
        catch (Exception ex)
        {
            log.Write($"Broker lobby transition failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool AreAllPlayersReady(object lobby, Action<string>? log)
    {
        var type = lobby.GetType();

        // Try common property names first
        foreach (var name in new[] { "AreAllPlayersReady", "AllPlayersReady", "IsEveryoneReady", "AllReady" })
        {
            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop?.GetValue(lobby) is bool propVal)
            {
                log?.Invoke($"Broker lobby transition: {name} = {propVal}.");
                return propVal;
            }

            var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method?.Invoke(lobby, null) is bool methodVal)
            {
                log?.Invoke($"Broker lobby transition: {name}() = {methodVal}.");
                return methodVal;
            }
        }

        // Fallback: count ready players from known field names
        var playersField = FindField(type, "_players", "_lobbyPlayers", "_playerSlots", "_slots");
        var playersList = playersField?.GetValue(lobby);
        if (playersList is System.Collections.IEnumerable enumerable)
        {
            int total = 0, ready = 0;
            foreach (var player in enumerable)
            {
                if (player is null) continue;
                total++;
                var readyProp = player.GetType().GetProperty("IsReady",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (readyProp?.GetValue(player) is true) ready++;
            }
            log?.Invoke($"Broker lobby transition: {ready}/{total} players ready.");
            return total >= 2 && ready >= total;
        }

        // ── Diagnostic: one-time dump of ALL lobby properties, fields, and methods ──
        DumpLobbyInternals(lobby, log);
        log?.Invoke("Broker lobby transition: unable to determine ready state via reflection; returning false.");
        return false;
    }

    private static void ForceBeginRunForAllPlayers(object lobby, Action<string>? log)
    {
        var type = lobby.GetType();

        // Find BeginRunForAllPlayers — prefer parameterless version
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.Name == "BeginRunForAllPlayers" && method.GetParameters().Length == 0)
            {
                log?.Invoke($"Broker lobby transition: calling {method.Name}() on lobby.");
                method.Invoke(lobby, null);
                return;
            }
        }

        // If no parameterless version, try all versions with proper default values
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.Name == "BeginRunForAllPlayers")
            {
                var parameters = method.GetParameters();
                log?.Invoke($"Broker lobby transition: calling {method.Name} with {parameters.Length} params: [{string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"))}]");

                // Create proper default values for each parameter type.
                // Use Activator.CreateInstance for ALL types — value types get defaults
                // (0, false, etc.), reference types with parameterless constructors get
                // empty instances (e.g. empty List<T>), and types without parameterless
                // constructors fall back to null.
                var args = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var paramType = parameters[i].ParameterType;
                    try
                    {
                        args[i] = Activator.CreateInstance(paramType);
                    }
                    catch
                    {
                        // Types without a parameterless constructor (e.g. string)
                        // throw MissingMethodException — pass null for those.
                        args[i] = null;
                    }
                }

                try
                {
                    method.Invoke(lobby, args);
                    log?.Invoke("Broker lobby transition: BeginRunForAllPlayers succeeded.");
                }
                catch (TargetInvocationException tie)
                {
                    log?.Invoke($"Broker lobby transition: BeginRunForAllPlayers threw {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}");
                    throw; // re-throw so outer catch logs it
                }
                return;
            }
        }

        log?.Invoke("Broker lobby transition: BeginRunForAllPlayers not found on lobby!");
    }

    // ── One-time diagnostic dump flag ──
    private static bool _lobbyInternalsDumped;

    /// <summary>
    /// One-time diagnostic: dump ALL properties, fields, and methods of the lobby
    /// instance so we can find the correct names for AreAllPlayersReady detection.
    /// </summary>
    private static void DumpLobbyInternals(object lobby, Action<string>? log)
    {
        if (_lobbyInternalsDumped) return;
        _lobbyInternalsDumped = true;

        try
        {
            var type = lobby.GetType();
            log?.Invoke($"=== StartRunLobby INTERNALS DUMP (type={type.FullName}) ===");

            // ── All properties ──
            log?.Invoke("--- Properties ---");
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                try
                {
                    var val = prop.GetValue(lobby);
                    var valStr = val?.ToString() ?? "null";
                    if (valStr.Length > 120) valStr = valStr[..120] + "...";
                    log?.Invoke($"  PROP [{prop.PropertyType.Name}] {prop.Name} = {valStr}");
                }
                catch
                {
                    log?.Invoke($"  PROP [{prop.PropertyType.Name}] {prop.Name} = <error reading>");
                }
            }

            // ── All fields ──
            log?.Invoke("--- Fields ---");
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                try
                {
                    var val = field.GetValue(lobby);
                    var valStr = val?.ToString() ?? "null";
                    if (valStr.Length > 120) valStr = valStr[..120] + "...";
                    log?.Invoke($"  FIELD [{field.FieldType.Name}] {field.Name} = {valStr}");
                }
                catch
                {
                    log?.Invoke($"  FIELD [{field.FieldType.Name}] {field.Name} = <error reading>");
                }
            }

            // ── Boolean methods (no params) ──
            log?.Invoke("--- Boolean Methods (0 params) ---");
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.ReturnType == typeof(bool) && method.GetParameters().Length == 0)
                {
                    try
                    {
                        var result = method.Invoke(lobby, null);
                        log?.Invoke($"  METHOD bool {method.Name}() = {result}");
                    }
                    catch
                    {
                        log?.Invoke($"  METHOD bool {method.Name}() = <error invoking>");
                    }
                }
            }

            // ── Methods containing "Ready" or "Player" ──
            log?.Invoke("--- Methods with 'Ready' or 'Player' in name ---");
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name.Contains("Ready") || method.Name.Contains("Player"))
                {
                    var parms = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    log?.Invoke($"  METHOD [{method.ReturnType.Name}] {method.Name}({parms})");
                }
            }

            log?.Invoke("=== END StartRunLobby INTERNALS DUMP ===");
        }
        catch (Exception ex)
        {
            log?.Invoke($"DumpLobbyInternals error: {ex.Message}");
        }
    }

    private static FieldInfo? FindField(Type type, params string[] names)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var name in names)
            {
                var field = current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field is not null)
                {
                    return field;
                }
            }
        }

        return null;
    }

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }
}
