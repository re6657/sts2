using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TokenSpire2.Core;

namespace TokenSpire2.Multiplayer;

/// <summary>
/// Event-driven multiplayer state machine.
///
/// With SteamFix64 (winmm.dll + SteamFix64.dll + steam_api64.dll proxy),
/// the game's built-in Steam matchmaking works transparently over LAN.
/// This controller ONLY handles UI automation — clicking buttons in the
/// correct order. All networking is handled by the SteamFix64 DLL layer.
///
/// Key design:
///   1. DETECTS actual screen (never assumes "click → next state")
///   2. Recovery: max 3 attempts per state, then fallback to main menu
///   3. State-specific timeouts
/// </summary>
public class MpController
{
    private MpState _state = MpState.Inactive;
    private double _stateElapsed;
    private int _recoveryAttempts;
    private const int MAX_RECOVERY = 3;

    // ── Dependencies ──
    private readonly MpScreenHandler _ui = new();

    // ── State timeouts (seconds) ──
    private const double MAIN_MENU_TIMEOUT = 30.0;
    private const double ENTERING_MP_TIMEOUT = 15.0;
    private const double HOST_SUBMENU_TIMEOUT = 15.0;
    private const double JOINING_TIMEOUT = 120.0;
    private const double CHAR_SELECT_TIMEOUT = 90.0;
    private const double IN_LOBBY_TIMEOUT = 120.0;
    private const double FRIEND_LIST_TIMEOUT = 15.0;
    private const double DEFAULT_TIMEOUT = 45.0;

    // ── Events ──
    public event Action? OnDisconnected;

    /// <summary>
    /// Call every frame from AutoSlayNode._Process when CoopMode=true.
    /// </summary>
    /// <param name="screen">Current detected screen from ScreenDetector.Detect()</param>
    /// <param name="delta">Frame delta time in seconds</param>
    /// <returns>Delay in seconds before next action. 0 = act next frame.</returns>
    public double Update(GameScreen screen, double delta)
    {
        _stateElapsed += delta;

        // ── Step 1: Screen → State ──
        var detected = ScreenToMpState(screen);

        // ── Step 2: State change → reset ──
        if (detected != _state)
        {
            Log($"[MpController] {_state} → {detected} (screen={screen})");
            _state = detected;
            _stateElapsed = 0;
            _recoveryAttempts = 0;
            _ui.ClearClickCache();
            _brokerJoinTriggered = false;
            _brokerJoinDone = false;
            _brokerJoinFlowInvoked = false;
            _mainMenuDumped = false;
            _hostButtonInvoked = false;
            _joinButtonInvoked = false;
        }

        // ── Step 3: Timeout → recover ──
        if (IsTimedOut())
        {
            Log($"[MpController] TIMEOUT in {_state} after {_stateElapsed:F0}s (attempt {_recoveryAttempts + 1}/{MAX_RECOVERY})");
            return HandleTimeout();
        }

        // ── Step 4: Dispatch ──
        return _state switch
        {
            MpState.Inactive => 0.5,
            MpState.MainMenu => HandleMainMenu(),
            MpState.EnteringMultiplayer => HandleEnteringMultiplayer(),
            MpState.HostSubmenu => HandleHostSubmenu(),
            MpState.Joining => HandleJoining(),
            MpState.FriendList => HandleFriendList(),
            MpState.CharacterSelect => HandleCharacterSelect(),
            MpState.InLobby => HandleInLobby(),
            MpState.InGame => 0.0,
            MpState.Disconnected => HandleDisconnected(),
            _ => 0.5
        };
    }

    public void Reset()
    {
        _state = MpState.Inactive;
        _stateElapsed = 0;
        _recoveryAttempts = 0;
    }

    /// <summary>
    /// Click the combat End Turn button via the UI, which goes through
    /// the game's network action pipeline and syncs to other players.
    /// </summary>
    public bool ClickEndTurnButton() => _ui.ClickEndTurnButton();

    // ═══════════════════════════════════════════════════════════════
    // Screen → State mapping
    // ═══════════════════════════════════════════════════════════════

    private static MpState ScreenToMpState(GameScreen screen)
    {
        return screen switch
        {
            GameScreen.MAIN_MENU => MpState.MainMenu,
            GameScreen.MULTIPLAYER_SUBMENU => MpState.EnteringMultiplayer,
            GameScreen.MULTIPLAYER_HOST_SUBMENU => MpState.HostSubmenu,
            GameScreen.MULTIPLAYER_FRIEND_LIST => MpState.FriendList,
            GameScreen.CHARACTER_SELECT or GameScreen.CHARACTER_SELECT_MULTIPLAYER => MpState.CharacterSelect,
            GameScreen.LOBBY => MpState.InLobby,
            GameScreen.COMBAT or GameScreen.MAP or GameScreen.EVENT
                or GameScreen.TREASURE or GameScreen.REST or GameScreen.SHOP
                or GameScreen.COMBAT_VICTORY or GameScreen.GAME_OVER => MpState.InGame,
            // Unknown screens: stay Inactive — don't force any action.
            // Let the game's normal flow handle it.
            _ => MpState.Inactive,
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // State handlers — each returns delay before next action
    // ═══════════════════════════════════════════════════════════════

    private double HandleMainMenu()
    {
        // Try common variations of the multiplayer button (English + Chinese)
        if (_ui.ClickButton("Multiplayer"))  return 2.0;
        if (_ui.ClickButton("MultiPlayer"))  return 2.0;
        if (_ui.ClickButton("Coop"))         return 2.0;
        if (_ui.ClickButton("Co-op"))        return 2.0;
        if (_ui.ClickButton("多人"))         return 2.0;
        if (_ui.ClickButton("联机"))         return 2.0;

        // One-time button dump on first failure
        if (!_mainMenuDumped)
        {
            _mainMenuDumped = true;
            _ui.DumpVisibleButtons();
            Log("[MpController] Button dump complete — check log for button names.");
        }
        return 2.0;
    }

    private bool _hostButtonInvoked;
    private bool _joinButtonInvoked;
    private bool _brokerJoinFlowInvoked;

    private double HandleEnteringMultiplayer()
    {
        var cfg = AppConfig.Instance;
        if (cfg.IsHost)
        {
            // First, try ForceClick + EmitSignal on HostButton
            if (_ui.ClickButton("Host"))     return 2.0;

            // If the button click doesn't navigate, directly invoke the game's
            // SteamHostButtonPressed method on NMultiplayerSubmenu via reflection.
            // This bypasses any issues with the button's signal/slot wiring.
            if (!_hostButtonInvoked)
            {
                _hostButtonInvoked = true;
                if (TryInvokeSteamHostButtonPressed())
                {
                    Log("[MpController] Directly invoked SteamHostButtonPressed on NMultiplayerSubmenu.");
                    return 3.0; // give async hosting time
                }
            }

            if (_ui.ClickButton("Create"))   return 2.0;
            if (_ui.ClickButton("创建"))     return 2.0;
            if (_ui.ClickButton("主持"))     return 2.0;
        }
        else
        {
            // ═══════════════════════════════════════════════════════════
            // BROKER MODE (client): invoke JoinFlow.Begin directly.
            //
            // TriggerJoinFlowBegin calls JoinFlow.Begin(IClientConnectionInitializer, SceneTree)
            // via reflection. BrokerClientJoinFlowPatch.Prefix intercepts and
            // performs the broker handshake (TCP connect → InitialGameInfo →
            // JoinRequest → JoinResponse). The JoinResult flows back through
            // the game's normal JoinGameAsync pipeline → scene transition.
            //
            // This bypasses the Steam friend list entirely — no virtual friend
            // patches needed. Both instances share the same Steam account so
            // the friend list is always empty.
            // ═══════════════════════════════════════════════════════════

            // Trigger broker join directly (bypass friend list)
            if (!_brokerJoinTriggered)
            {
                _brokerJoinTriggered = true;
                if (TriggerJoinFlowBegin())
                {
                    Log("[MpController] Broker: TriggerJoinFlowBegin succeeded — waiting for lobby transition.");
                    return 3.0;
                }
                Log("[MpController] Broker: TriggerJoinFlowBegin FAILED — will retry.");
                _brokerJoinTriggered = false; // allow retry
                return 2.0;
            }

            // Fallback: try clicking Join button (opens friend list)
            if (_ui.ClickButton("Join"))     return 2.0;
            if (_ui.ClickButton("加入"))     return 2.0;
            if (_ui.ClickButton("参与"))     return 2.0;
        }

        // One-time button dump if nothing matched
        if (!_mainMenuDumped)
        {
            _mainMenuDumped = true;
            _ui.DumpVisibleButtons();
            Log("[MpController] EnteringMultiplayer: no matching button — dumped all.");
        }
        return 2.0;
    }

    /// <summary>
    /// Find NMultiplayerSubmenu in the scene tree and directly call
    /// OnHostPressed() via reflection. This is the callback connected to
    /// the HostButton's Released signal (NClickableControl.SignalName.Released).
    /// ForceClick() already emits Released, but if the navigation still fails,
    /// this direct call bypasses any button-state guards.
    /// </summary>
    private bool TryInvokeSteamHostButtonPressed()
    {
        try
        {
            var node = FindMultiplayerSubmenu();
            if (node == null)
            {
                Log("[MpController] NMultiplayerSubmenu not found in scene tree.");
                return false;
            }

            // Try OnHostPressed first (the actual signal callback)
            var method = node.GetType().GetMethod("OnHostPressed",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            if (method != null)
            {
                // Signature: void OnHostPressed(NButton _)
                // Pass null for the NButton parameter — it's unused in the method body.
                method.Invoke(node, new object?[] { null });
                return true;
            }

            // Fallback: try the old name
            method = node.GetType().GetMethod("SteamHostButtonPressed",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            if (method != null)
            {
                method.Invoke(node, null);
                return true;
            }

            Log("[MpController] OnHostPressed/SteamHostButtonPressed not found on NMultiplayerSubmenu.");
            return false;
        }
        catch (Exception ex)
        {
            Log($"[MpController] OnHostPressed invoke failed: {ex.Message}");
            return false;
        }
    }

    private bool TryInvokeJoinButtonPressed()
    {
        try
        {
            var node = FindMultiplayerSubmenu();
            if (node == null) return false;

            // The actual callback is OpenJoinFriendsScreen
            var method = node.GetType().GetMethod("OpenJoinFriendsScreen",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            if (method != null)
            {
                method.Invoke(node, new object?[] { null });
                return true;
            }

            // Fallback: JoinButtonPressed
            method = node.GetType().GetMethod("JoinButtonPressed",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            if (method != null)
            {
                method.Invoke(node, null);
                return true;
            }

            Log("[MpController] OpenJoinFriendsScreen/JoinButtonPressed not found.");
            return false;
        }
        catch (Exception ex)
        {
            Log($"[MpController] JoinButtonPressed invoke failed: {ex.Message}");
            return false;
        }
    }

    private static Godot.Node? FindMultiplayerSubmenu()
    {
        try
        {
            var tree = (Godot.SceneTree)Godot.Engine.GetMainLoop();
            var root = tree.Root;
            var rsc = root.GetNodeOrNull<Godot.Node>("Game/RootSceneContainer");
            if (rsc == null) return null;

            // Try direct child
            var direct = rsc.GetNodeOrNull<Godot.Node>("MultiplayerSubmenu");
            if (direct != null) return direct;

            // Try nested under MainMenu
            var mainMenu = rsc.GetNodeOrNull<Godot.Node>("MainMenu");
            if (mainMenu != null)
            {
                var nested = mainMenu.GetNodeOrNull<Godot.Node>("Submenus/MultiplayerSubmenu")
                    ?? mainMenu.GetNodeOrNull<Godot.Node>("MultiplayerSubmenu");
                if (nested != null) return nested;
            }

            // Fallback: recursive search by type name
            return FindNodeByTypeRecursive(rsc, "NMultiplayerSubmenu");
        }
        catch { return null; }
    }

    private static Godot.Node? FindNodeByTypeRecursive(Godot.Node node, string typeName)
    {
        if (node.GetType().Name == typeName)
            return node;
        foreach (var child in node.GetChildren())
        {
            if (child is Godot.Node childNode)
            {
                var found = FindNodeByTypeRecursive(childNode, typeName);
                if (found != null) return found;
            }
        }
        return null;
    }

    private double HandleHostSubmenu()
    {
        // Host config screen — click confirm/start button
        _ui.ClickFirstEnabledButton();
        return 2.0;
    }

    private bool _brokerJoinTriggered;
    private bool _brokerJoinDone;
    private bool _mainMenuDumped;

    private double HandleJoining()
    {
        // Client is transitioning through the join screen.
        // In SteamFix64 mode: joining happens automatically via Steam
        // matchmaking (intercepted by SteamFix64 DLL proxy at native level).
        // In broker mode: BrokerClientJoinFlowPatch intercepts JoinFlow.Begin.
        // Both cases: no action needed — just wait for state transition.
        return 1.0;
    }

    /// <summary>
    /// Friend list screen (Steam friend selection).
    /// In broker mode, we bypass this by invoking JoinFlow.Begin()
    /// via reflection. BrokerClientJoinFlowPatch intercepts the call.
    /// </summary>
    private double HandleFriendList()
    {
        // ── SteamFix64 Mode ────────────────────────────────────────
        // In SteamFix64 mode, the native DLL proxy (winmm.dll →
        // SteamFix64.dll) intercepts the Steam Friends/Lobby APIs.
        // LAN games appear as "friend" entries. Click them directly —
        // the game's normal join flow works unchanged.
        if (!AppConfig.Instance.BrokerEnabled)
        {
            // Strategy 1: Click on lobby/friend entries by type name.
            // NJoinFriendButton, NFriendListEntry, NJoinGameButton, etc.
            if (_ui.ClickButton("JoinFriend"))  return 2.0;
            if (_ui.ClickButton("Friend"))      return 2.0;
            if (_ui.ClickButton("Lobby"))       return 2.0;
            if (_ui.ClickButton("JoinGame"))    return 2.0;
            if (_ui.ClickButton("Game"))        return 2.0;

            // Strategy 2: Refresh button — may need to refresh to see host
            if (_ui.ClickButton("Refresh"))     return 2.0;
            if (_ui.ClickButton("刷新"))         return 2.0;

            // Strategy 3: Click first enabled button (friend/lobby entry)
            if (_ui.ClickFirstEnabledButton())
            {
                Log("[MpController] SteamFix64: clicked first enabled entry in friend list.");
                return 2.0;
            }

            // Strategy 4: Nothing worked — dump for diagnostics and escape
            if (!_mainMenuDumped)
            {
                _mainMenuDumped = true;
                _ui.DumpVisibleButtons();
                Log("[MpController] SteamFix64: FriendList dump complete — no clickable entries.");
            }

            Log("[MpController] SteamFix64: no entries in friend list — host may not be hosting yet. Pressing escape to retry.");
            _ui.PressEscape();
            return 2.0;
        }

        // ── Broker Mode ────────────────────────────────────────────
        // BrokerVirtualFriendSteamPatch injects "人机一号" into the friend
        // list. The player (human) or bot clicks it. No need to auto-invoke
        // JoinFlow.Begin — the game's normal JoinGameAsync flow handles it.
        // Just click the virtual friend entry.
        if (_ui.ClickButton("人机一号"))     return 2.0;
        if (_ui.ClickButton("Friend"))      return 2.0;
        if (_ui.ClickButton("JoinFriend"))  return 2.0;

        // Fallback: try clicking first enabled button (may be our virtual friend)
        if (_ui.ClickFirstEnabledButton())
        {
            Log("[MpController] Broker: clicked first enabled entry in friend list.");
            return 2.0;
        }

        // Nothing clickable — dump for diagnostics and retry
        if (!_mainMenuDumped)
        {
            _mainMenuDumped = true;
            _ui.DumpVisibleButtons();
            Log("[MpController] Broker: FriendList dump complete.");
        }

        Log("[MpController] Broker: no clickable entry, pressing escape.");
        _ui.PressEscape();
        return 2.0;
    }

    /// <summary>
    /// Invoke JoinFlow.Begin(IClientConnectionInitializer, SceneTree) directly
    /// via reflection. Uses Harmony AccessTools for reliable type resolution
    /// across all loaded assemblies.
    ///
    /// BrokerClientJoinFlowPatch.Prefix intercepts this call:
    ///   1. Creates TCP connection to broker
    ///   2. Performs broker handshake (InitialGameInfo → JoinRequest → JoinResponse)
    ///   3. Stores service in BrokerPendingNetGameServiceRegistry
    ///   4. Returns JoinResult → game transitions to character select / lobby
    /// </summary>
    private bool TriggerJoinFlowBegin()
    {
        try
        {
            // Use AccessTools (searches all loaded assemblies) — same as BrokerClientJoinFlowPatch
            var joinFlowType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow");
            if (joinFlowType == null)
            {
                Log("[MpController] TriggerJoinFlowBegin: JoinFlow type NOT FOUND.");
                return false;
            }

            var initializerType = AccessTools.TypeByName(
                "MegaCrit.Sts2.Core.Multiplayer.Connection.IClientConnectionInitializer");
            if (initializerType == null)
            {
                Log("[MpController] TriggerJoinFlowBegin: IClientConnectionInitializer type NOT FOUND.");
                return false;
            }

            // Use AccessTools.Method (same as BrokerClientJoinFlowPatch) instead of
            // Type.GetMethod. AccessTools matches by type name/fullname which works
            // across assembly load contexts. GetMethod requires exact CLR type identity.
            var beginMethod = AccessTools.Method(joinFlowType, "Begin",
                new[] { initializerType, typeof(Godot.SceneTree) });

            if (beginMethod == null)
            {
                // Dump all methods on JoinFlow for diagnostics
                var methods = joinFlowType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                foreach (var m in methods)
                {
                    var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    Log($"[MpController] JoinFlow method: {m.ReturnType.Name} {m.Name}({parms}) [Static={m.IsStatic}]");
                }
                Log("[MpController] TriggerJoinFlowBegin: JoinFlow.Begin NOT FOUND (dumped all methods above).");
                return false;
            }

            // Create placeholder initializer — the broker doesn't use Steam/ENet
            var initializer = new LocalCoop.Mod.Runtime.BrokerClientJoinFlow
                .PlaceholderClientConnectionInitializer();
            var sceneTree = (Godot.SceneTree)Godot.Engine.GetMainLoop();

            // JoinFlow.Begin is an INSTANCE method. Create a JoinFlow instance.
            // Try parameterless constructor first. JoinFlow is a simple class
            // that doesn't need special setup — the Harmony patch replaces all
            // its internal logic with the broker handshake.
            object? joinFlowInstance;
            try
            {
                joinFlowInstance = Activator.CreateInstance(joinFlowType);
            }
            catch (MissingMethodException)
            {
                // No parameterless constructor — try finding existing instance
                // in the scene tree or via static accessor
                Log("[MpController] JoinFlow has no parameterless constructor, searching for instance...");
                joinFlowInstance = FindJoinFlowInstance(joinFlowType);
                if (joinFlowInstance == null)
                {
                    Log("[MpController] Cannot create or find JoinFlow instance.");
                    return false;
                }
            }

            Log("[MpController] TriggerJoinFlowBegin: Invoking JoinFlow.Begin(...) → " +
                "will be intercepted by BrokerClientJoinFlowPatch.");
            beginMethod.Invoke(joinFlowInstance, new object[] { initializer, sceneTree });
            return true;
        }
        catch (Exception ex)
        {
            Log($"[MpController] TriggerJoinFlowBegin FAILED: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Search for an existing JoinFlow instance in memory.
    /// Fallback when Activator.CreateInstance fails (no parameterless ctor).
    /// </summary>
    private static object? FindJoinFlowInstance(Type joinFlowType)
    {
        // Strategy 1: Check if JoinFlow has a static Instance/Singleton property
        foreach (var prop in joinFlowType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (prop.Name.Contains("Instance") || prop.Name.Contains("Singleton") || prop.Name.Contains("Current"))
            {
                try
                {
                    var val = prop.GetValue(null);
                    if (val != null && joinFlowType.IsInstanceOfType(val))
                        return val;
                }
                catch { }
            }
        }

        // Strategy 2: Search scene tree for a node of type JoinFlow
        try
        {
            var tree = (Godot.SceneTree)Godot.Engine.GetMainLoop();
            var root = tree.Root;
            return FindNodeOfTypeRecursive(root, joinFlowType);
        }
        catch { }

        return null;
    }

    private static object? FindNodeOfTypeRecursive(Godot.Node node, Type targetType)
    {
        if (targetType.IsInstanceOfType(node))
            return node;
        foreach (var child in node.GetChildren())
        {
            if (child is Godot.Node childNode)
            {
                var found = FindNodeOfTypeRecursive(childNode, targetType);
                if (found != null) return found;
            }
        }
        return null;
    }

    private double HandleCharacterSelect()
    {
        var cfg = AppConfig.Instance;

        if (cfg.IsHumanPlayer)
            return 2.0; // idle: let human pick character

        // Bot: select character then embark
        var character = cfg.Character ?? "Ironclad";
        if (character == "RANDOM")
            character = "Ironclad";
        _ui.SelectCharacter(character);

        // Try embark
        _ui.ClickButton("Confirm");
        return 2.0;
    }

    private double HandleInLobby()
    {
        var cfg = AppConfig.Instance;

        if (cfg.IsHost && cfg.AutoStartEnabled)
        {
            _ui.ClickButton("Ready");
            return 2.0;
        }

        if (cfg.IsBot)
        {
            _ui.ClickButton("Ready");
            return 2.0;
        }

        return 3.0; // human: idle
    }

    private double HandleDisconnected()
    {
        Log("[MpController] Disconnected — returning to main menu.");
        _ui.PressEscape();
        _ui.PressEscape(); // double escape to clear overlays
        return 2.0;
    }

    // ═══════════════════════════════════════════════════════════════
    // Timeout + Recovery
    // ═══════════════════════════════════════════════════════════════

    private bool IsTimedOut()
    {
        double timeout = _state switch
        {
            MpState.MainMenu => MAIN_MENU_TIMEOUT,
            MpState.EnteringMultiplayer => ENTERING_MP_TIMEOUT,
            MpState.HostSubmenu => HOST_SUBMENU_TIMEOUT,
            MpState.Joining => JOINING_TIMEOUT,
            MpState.FriendList => FRIEND_LIST_TIMEOUT,
            MpState.CharacterSelect => CHAR_SELECT_TIMEOUT,
            MpState.InLobby => IN_LOBBY_TIMEOUT,
            _ => DEFAULT_TIMEOUT
        };
        return _stateElapsed > timeout;
    }

    private double HandleTimeout()
    {
        _recoveryAttempts++;

        if (_recoveryAttempts >= MAX_RECOVERY)
        {
            Log($"[MpController] Max recovery ({MAX_RECOVERY}) reached — returning to main menu.");
            _state = MpState.MainMenu;
            _stateElapsed = 0;
            _recoveryAttempts = 0;
            _ui.PressEscape();
            _ui.PressEscape();
            return 2.0;
        }

        // Recovery: go back one step
        _stateElapsed = 0;
        _ui.PressEscape();
        _ui.ClearClickCache();
        return 1.5;
    }

    private static void Log(string msg)
    {
        var fullMsg = $"[MpController] {msg}";
        try { MainFile.Logger?.Info(fullMsg); } catch { }
        try
        {
            var eventLogPath = TokenSpire2.Core.AppConfig.Instance.EventLogPath;
            if (!string.IsNullOrEmpty(eventLogPath))
                new LocalCoop.Mod.Runtime.BrokerEventLog(eventLogPath).Write(fullMsg);
        }
        catch { }
    }
}

/// <summary>Multiplayer state machine states.</summary>
public enum MpState
{
    Inactive,
    MainMenu,
    EnteringMultiplayer,
    HostSubmenu,
    Joining,
    FriendList,
    CharacterSelect,
    InLobby,
    InGame,
    Disconnected,
}
