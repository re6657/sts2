using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;

namespace TokenSpire2.Multiplayer;

/// <summary>
/// Handles UI interactions for multiplayer screens.
/// All actions route through here for logging, retry, and click verification.
/// </summary>
public class MpScreenHandler
{
    // Time-based dedup: map button text → last click timestamp (stopwatch ticks).
    private readonly Dictionary<string, long> _recentClicks = new();
    private static readonly long CLICK_COOLDOWN_TICKS = System.TimeSpan.FromSeconds(3).Ticks;

    /// <summary>
    /// Click a button whose name or type-name contains the given substring.
    /// Tries multiple root paths and click strategies.
    /// </summary>
    public bool ClickButton(string buttonText)
    {
        var now = DateTime.UtcNow.Ticks;
        if (_recentClicks.TryGetValue(buttonText, out var lastClick)
            && now - lastClick < CLICK_COOLDOWN_TICKS)
            return false;

        try
        {
            foreach (var root in GetAllRootNodes())
            {
                foreach (var btn in FindAllButtons(root))
                {
                    if (!IsVisibleEnabled(btn)) continue;

                    string nodeName = btn.Name?.ToString() ?? "";
                    string typeName = btn.GetType().Name;
                    if (nodeName.Contains(buttonText, StringComparison.OrdinalIgnoreCase)
                        || typeName.Contains(buttonText, StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryClickNode(btn))
                        {
                            _recentClicks[buttonText] = now;
                            Log($"Clicked: type={typeName} name=\"{nodeName}\" (searched for \"{buttonText}\")");
                            return true;
                        }
                    }
                }
            }
            return false;
        }
        catch (Exception ex) { Log($"ClickButton error: {ex.Message}"); return false; }
    }

    /// <summary>Click the first visible enabled button found in the scene.</summary>
    public bool ClickFirstEnabledButton()
    {
        try
        {
            foreach (var root in GetAllRootNodes())
            {
                foreach (var btn in FindAllButtons(root))
                {
                    if (IsVisibleEnabled(btn))
                    {
                        if (TryClickNode(btn))
                        {
                            Log($"Clicked first enabled: type={btn.GetType().Name} name=\"{btn.Name}\"");
                            return true;
                        }
                    }
                }
            }
            return false;
        }
        catch (Exception ex) { Log($"ClickFirstEnabled error: {ex.Message}"); return false; }
    }

    /// <summary>Select a character on the character select screen.</summary>
    public bool SelectCharacter(string characterName)
    {
        Log($"SelectCharacter: \"{characterName}\"");
        try
        {
            foreach (var root in GetAllRootNodes())
            {
                foreach (var node in FindAllNodesRecursive(root))
                {
                    if (node.GetType().Name != "NCharacterSelectButton") continue;
                    if (!IsVisibleEnabled(node)) continue;

                    var charProp = node.GetType().GetProperty("Character");
                    var character = charProp?.GetValue(node);
                    var idProp = character?.GetType().GetProperty("Id");
                    var id = idProp?.GetValue(character);
                    var entryProp = id?.GetType().GetProperty("Entry");
                    var entry = entryProp?.GetValue(id) as string;

                    if (entry == characterName || characterName == "RANDOM")
                    {
                        TryClickNode(node);
                        Log($"Selected character: {entry ?? "RANDOM"}");
                        return true;
                    }
                }
            }
            return ClickButton(characterName);
        }
        catch (Exception ex) { Log($"SelectCharacter error: {ex.Message}"); return false; }
    }

    /// <summary>Press Escape key.</summary>
    public void PressEscape()
    {
        Log("PressEscape");
        try
        {
            var input = new InputEventKey { Keycode = Key.Escape, Pressed = true };
            Input.ParseInputEvent(input);
        }
        catch (Exception ex) { Log($"PressEscape error: {ex.Message}"); }
    }

    /// <summary>Clear click dedup cache (call when screen changes).</summary>
    public void ClearClickCache() => _recentClicks.Clear();

    /// <summary>
    /// Find the combat UI's End Turn button (NEndTurnButton on NCombatUi)
    /// and click it via ForceClick(). This goes through the game's normal
    /// action pipeline and syncs to other players over the network —
    /// unlike PlayerCmd.EndTurn() which is local-only.
    /// </summary>
    public bool ClickEndTurnButton()
    {
        try
        {
            foreach (var root in GetAllRootNodes())
            {
                var combatUi = FindNCombatUi(root);
                if (combatUi == null) continue;

                // Strategy 1: Get EndTurnButton property (Mono/C# auto-property backing field _endTurnButton)
                var endTurnBtn = GetEndTurnButtonFromCombatUi(combatUi);
                if (endTurnBtn != null)
                {
                    var btnType = endTurnBtn.GetType();
                    var forceClick = btnType.GetMethod("ForceClick",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (forceClick != null)
                    {
                        forceClick.Invoke(endTurnBtn, null);
                        Log($"Clicked EndTurnButton via NCombatUi.EndTurnButton on {btnType.Name}");
                        return true;
                    }
                }

                // Strategy 2: Search NCombatUi children for NEndTurnButton by type
                var allNodes = FindAllNodesRecursive(combatUi);
                foreach (var node in allNodes)
                {
                    if (node.GetType().Name == "NEndTurnButton")
                    {
                        var fc = node.GetType().GetMethod("ForceClick",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (fc != null)
                        {
                            fc.Invoke(node, null);
                            Log($"Clicked EndTurnButton via direct NEndTurnButton find");
                            return true;
                        }
                    }
                }
            }
            Log("ClickEndTurnButton: NCombatUi or NEndTurnButton not found in any root");
            return false;
        }
        catch (Exception ex) { Log($"ClickEndTurnButton error: {ex.Message}"); return false; }
    }

    /// <summary>Find NCombatUi node in the scene tree by type name.</summary>
    private static Node? FindNCombatUi(Node root)
    {
        try
        {
            var allNodes = FindAllNodesRecursive(root);
            foreach (var node in allNodes)
            {
                if (node.GetType().Name == "NCombatUi")
                    return node;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Get the EndTurnButton from NCombatUi via reflection.</summary>
    private static Node? GetEndTurnButtonFromCombatUi(Node combatUi)
    {
        try
        {
            var type = combatUi.GetType();

            // Try C# auto-property: get_EndTurnButton() method
            var getter = type.GetMethod("get_EndTurnButton",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (getter != null)
            {
                var result = getter.Invoke(combatUi, null);
                if (result is Node node)
                    return node;
            }

            // Try backing field: _endTurnButton
            var field = type.GetField("_endTurnButton",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                var val = field.GetValue(combatUi);
                if (val is Node node)
                    return node;
            }

            // Try property via PropertyInfo
            var prop = type.GetProperty("EndTurnButton",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                var val = prop.GetValue(combatUi);
                if (val is Node node)
                    return node;
            }
        }
        catch { }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Node discovery
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Try multiple root paths — the UI may be under different parents.</summary>
    private static List<Node> GetAllRootNodes()
    {
        var roots = new List<Node>();
        try
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            var root = tree.Root;

            // Path 1: Game/RootSceneContainer (most common)
            var rsc = root.GetNodeOrNull<Node>("Game/RootSceneContainer");
            if (rsc != null) roots.Add(rsc);

            // Path 2: Root directly
            roots.Add(root);

            // Path 3: Any top-level child that might contain UI
            foreach (var child in root.GetChildren())
            {
                if (child is Node childNode && childNode != rsc)
                    roots.Add(childNode);
            }
        }
        catch { }
        return roots;
    }

    /// <summary>Find all clickable nodes.</summary>
    private static List<Node> FindAllButtons(Node root)
    {
        var buttons = new List<Node>();
        FindButtonsRecursive(root, buttons);
        return buttons;
    }

    private static void FindButtonsRecursive(Node node, List<Node> result)
    {
        var typeName = node.GetType().Name;

        // Multiple detection strategies:
        bool hasPressedSignal = false;
        try { hasPressedSignal = node.HasSignal("pressed"); } catch { }
        bool isButtonType = typeName.Contains("Button", StringComparison.OrdinalIgnoreCase);
        bool hasForceClick = node.GetType().GetMethod("ForceClick",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;

        if (hasPressedSignal || isButtonType || hasForceClick)
            result.Add(node);

        foreach (var child in node.GetChildren())
        {
            if (child is Node childNode)
                FindButtonsRecursive(childNode, result);
        }
    }

    private static List<Node> FindAllNodesRecursive(Node root)
    {
        var nodes = new List<Node>();
        CollectNodes(root, nodes);
        return nodes;
    }

    private static void CollectNodes(Node node, List<Node> result)
    {
        result.Add(node);
        foreach (var child in node.GetChildren())
        {
            if (child is Node childNode)
                CollectNodes(childNode, result);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Click strategies
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Try multiple strategies to click a node.</summary>
    /// <remarks>
    /// STS2 buttons respond to different mechanisms:
    /// - NMainMenuTextButton: ForceClick() works. EmitSignal("pressed") triggers
    ///   a BACK/cancel behavior, undoing the navigation.
    /// - NSubmenuButton: ForceClick() alone may not trigger the game's handler.
    ///   EmitSignal("pressed") is needed.
    /// Strategy: ForceClick first (works for main menu). For NSubmenuButton
    /// types, also emit "pressed" signal. Never emit pressed for other types.
    /// </remarks>
    private static bool TryClickNode(Node node)
    {
        var type = node.GetType();
        string typeName = type.Name;
        bool anySucceeded = false;

        // Strategy 1: ForceClick() — STS2 NButton extension.
        // This is the primary mechanism for most STS2 button types.
        try
        {
            var forceClick = type.GetMethod("ForceClick",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (forceClick != null)
            {
                forceClick.Invoke(node, null);
                anySucceeded = true;
            }
        }
        catch (Exception ex) { Log($"  ForceClick failed on {typeName}: {ex.InnerException?.Message ?? ex.Message}"); }

        // Strategy 2: EmitSignal("pressed") — ONLY for NSubmenuButton types.
        // NMainMenuTextButton's pressed handler does a BACK navigation, so we
        // skip it for those. NSubmenuButton needs it to trigger game handler.
        if (typeName == "NSubmenuButton" && node.HasSignal("pressed"))
        {
            try
            {
                node.EmitSignal("pressed");
                anySucceeded = true;
            }
            catch (Exception ex) { Log($"  EmitSignal(pressed) failed on {typeName}: {ex.Message}"); }
        }

        // Strategy 3: Call Press() or Click() method via reflection
        try
        {
            foreach (var name in new[] { "Press", "Click", "OnPress", "OnClick", "ButtonPressed" })
            {
                var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (method != null)
                {
                    method.Invoke(node, null);
                    anySucceeded = true;
                }
            }
        }
        catch (Exception ex) { Log($"  Method click failed on {typeName}: {ex.Message}"); }

        // Strategy 4: GrabFocus + Enter key
        try
        {
            var grabFocus = type.GetMethod("GrabFocus", Type.EmptyTypes);
            if (grabFocus != null)
            {
                grabFocus.Invoke(node, null);
                var input = new InputEventKey { Keycode = Key.Enter, Pressed = true };
                Input.ParseInputEvent(input);
                anySucceeded = true;
            }
        }
        catch { }

        return anySucceeded;
    }

    /// <summary>Check if a node is visible and enabled using reflection.</summary>
    private static bool IsVisibleEnabled(Node node)
    {
        try
        {
            var type = node.GetType();
            bool visible = true, disabled = false;
            try { visible = (bool)(type.GetProperty("Visible")?.GetValue(node) ?? true); } catch { }
            try { disabled = (bool)(type.GetProperty("Disabled")?.GetValue(node) ?? false); } catch { }
            return visible && !disabled;
        }
        catch { return true; } // If we can't check, assume clickable
    }

    // ═══════════════════════════════════════════════════════════════
    // Diagnostics
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Comprehensive diagnostic dump of all UI nodes.</summary>
    public void DumpVisibleButtons()
    {
        try
        {
            foreach (var root in GetAllRootNodes())
            {
                Log($"DumpVisibleButtons: root=\"{root.Name}\" type={root.GetType().Name}");

                // ── Strategy 1: Find buttons ──
                var all = FindAllButtons(root);
                Log($"  Buttons found: {all.Count}");

                // ── Strategy 2: Dump all unique types ──
                var allNodes = FindAllNodesRecursive(root);
                var typeCounts = new Dictionary<string, int>();
                foreach (var n in allNodes)
                {
                    var tn = n.GetType().Name;
                    typeCounts.TryGetValue(tn, out int c);
                    typeCounts[tn] = c + 1;
                }
                Log($"  Total nodes: {allNodes.Count}, Unique types: {typeCounts.Count}");
                foreach (var kv in typeCounts.OrderByDescending(kv => kv.Value).Take(25))
                    Log($"    TYPE count={kv.Value}: {kv.Key}");

                // ── Strategy 3: List all button-like nodes ──
                foreach (var btn in all.Take(30))
                {
                    bool vis = true, dis = false;
                    try { vis = (bool)(btn.GetType().GetProperty("Visible")?.GetValue(btn) ?? true); } catch { }
                    try { dis = (bool)(btn.GetType().GetProperty("Disabled")?.GetValue(btn) ?? false); } catch { }
                    string visStr = vis ? "VIS" : "HID";
                    string enaStr = dis ? "DIS" : "ENA";
                    string hasPressed = "";
                    try { hasPressed = btn.HasSignal("pressed") ? " SIG: pressed" : ""; } catch { }
                    string hasForce = btn.GetType().GetMethod("ForceClick",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null ? " FORCECLICK" : "";
                    Log($"    BTN [{visStr}/{enaStr}] name=\"{btn.Name}\" type={btn.GetType().Name}{hasPressed}{hasForce}");
                }
                if (all.Count > 30)
                    Log($"    ... and {all.Count - 30} more buttons");
            }
        }
        catch (Exception ex) { Log($"DumpVisibleButtons error: {ex.Message}"); }
    }

    private static void Log(string msg)
    {
        var fullMsg = $"[MpScreenHandler] {msg}";
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
