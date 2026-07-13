using System;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace TokenSpire2.Core;

/// <summary>
/// Stateless screen detection. Migrated from GameStateDetector with
/// added multiplayer-screen detection.
///
/// Call <see cref="Detect"/> every frame to determine the current
/// <see cref="GameScreen"/>. Overlays are checked before base screens
/// so a card-reward overlay on top of combat returns OVERLAY_CARD_REWARD
/// rather than COMBAT.
/// </summary>
public static class ScreenDetector
{
    // ═══════════════════════════════════════════════════════════════
    // Frame stability tracking
    // ═══════════════════════════════════════════════════════════════

    private static GameScreen _previousScreen = GameScreen.NONE;
    private static int _stabilityCounter;
    private const int STABILITY_THRESHOLD = 3; // frames the same screen must persist

    /// <summary>
    /// Detect current game screen. Call every frame in _Process.
    /// Returns the detected screen.
    /// </summary>
    public static GameScreen Detect()
    {
        var screen = DetectInternal();

        // Stability: require N consecutive identical readings before reporting a change.
        if (screen == _previousScreen)
        {
            if (_stabilityCounter < STABILITY_THRESHOLD)
                _stabilityCounter++;
        }
        else
        {
            _stabilityCounter = 0;
            _previousScreen = screen;
        }

        return _stabilityCounter >= STABILITY_THRESHOLD ? screen : _previousScreen;
    }

    /// <summary>
    /// Raw detection — no stability filter. Use when you need an
    /// immediate reading regardless of flicker.
    /// </summary>
    public static GameScreen DetectRaw() => DetectInternal();

    /// <summary>
    /// True if the screen has been stable for at least <paramref name="frames"/>.
    /// </summary>
    public static bool IsStable(GameScreen screen, int frames = 10)
        => _previousScreen == screen && _stabilityCounter >= frames;

    /// <summary>Reset stability tracking (e.g. after scene change).</summary>
    public static void ResetStability()
    {
        _stabilityCounter = 0;
        _previousScreen = GameScreen.NONE;
    }

    // ═══════════════════════════════════════════════════════════════
    // Internal detection
    // ═══════════════════════════════════════════════════════════════

    private static GameScreen DetectInternal()
    {
        // ── Overlays (checked first, they stack on top of other screens) ──
        // M25: cache Instance to prevent TOCTOU between ScreenCount check and Peek()
        var overlayStack = NOverlayStack.Instance;
        if (overlayStack?.ScreenCount > 0)
        {
            var overlay = overlayStack.Peek();
            var overlayNode = overlay as Node;
            if (overlayNode != null)
            {
                if (overlay is NCardRewardSelectionScreen)
                    return GameScreen.OVERLAY_CARD_REWARD;
                if (overlay is NRewardsScreen)
                    return GameScreen.OVERLAY_REWARDS;
                if (overlay is NGameOverScreen)
                    return GameScreen.GAME_OVER;
                if (overlay is NChooseACardSelectionScreen)
                    return GameScreen.OVERLAY_CHOOSE_CARD;
                if (overlay is NChooseABundleSelectionScreen)
                    return GameScreen.OVERLAY_CHOOSE_BUNDLE;
                if (overlay is NChooseARelicSelection)
                    return GameScreen.OVERLAY_CHOOSE_RELIC;
                if (overlay is NDeckUpgradeSelectScreen or NDeckTransformSelectScreen
                    or NDeckEnchantSelectScreen or NDeckCardSelectScreen)
                    return GameScreen.OVERLAY_DECK_GRID;
                if (overlay is NSimpleCardSelectScreen)
                    return GameScreen.OVERLAY_SIMPLE_SELECT;
                if (overlay is NCrystalSphereScreen)
                    return GameScreen.OVERLAY_CRYSTAL_SPHERE;
            }
        }

        // ── Combat (check before map/rooms) ──
        var cm = CombatManager.Instance;
        if (cm != null && cm.IsInProgress)
            return GameScreen.COMBAT;

        // ── Map ──
        var mapScreen = NMapScreen.Instance;
        if (mapScreen != null && mapScreen.IsOpen)
            return GameScreen.MAP;

        // ── Multiplayer screens (checked before rooms) ──
        var node = GetRootNode();
        if (node != null)
        {
            // Character select screen — check multiple possible node names.
            // In the main menu, the node may be named "CharacterSelectScreen" or "CharacterSelect".
            // In a run, it's at "Run/RoomContainer/CharacterSelectRoom".
            if (node.GetNodeOrNull<Node>("Run/RoomContainer/CharacterSelectRoom") != null
                || node.GetNodeOrNull<Control>("CharacterSelect") != null
                || node.GetNodeOrNull<Control>("CharacterSelectScreen") != null)
            {
                return GameScreen.CHARACTER_SELECT;
            }

            // Multiplayer screens — checked in order of specificity.
            // NMainMenuSubmenuStack keeps all pushed submenus in the tree,
            // so more-specific screens must be checked BEFORE generic ones.
            //
            // Example: When HostSubmenu is pushed on top of MultiplayerSubmenu,
            // BOTH nodes exist. We must detect HostSubmenu first.
            var mainMenuNode = node.GetNodeOrNull<Control>("MainMenu");

            // Host submenu (most specific — on top of MultiplayerSubmenu)
            if (node.GetNodeOrNull<Control>("MultiplayerHostSubmenu") != null
                || mainMenuNode?.GetNodeOrNull<Control>("Submenus/MultiplayerHostSubmenu") != null
                || mainMenuNode?.GetNodeOrNull<Control>("MultiplayerHostSubmenu") != null)
                return GameScreen.MULTIPLAYER_HOST_SUBMENU;

            // Friend list screen (Steam friends for joining) — before generic submenu
            if (IsFriendListScreen(node))
                return GameScreen.MULTIPLAYER_FRIEND_LIST;

            // Generic multiplayer submenu (checked LAST — all other MP screens
            // are pushed on top of it and must be detected first)
            if (node.GetNodeOrNull<Control>("MultiplayerSubmenu") != null
                || mainMenuNode?.GetNodeOrNull<Control>("Submenus/MultiplayerSubmenu") != null
                || mainMenuNode?.GetNodeOrNull<Control>("MultiplayerSubmenu") != null)
                return GameScreen.MULTIPLAYER_SUBMENU;

            // Lobby screen (all players connected, waiting for ready/embark)
            var lobbyNode = node.GetNodeOrNull<Node>("Run/RoomContainer/LobbyRoom")
                ?? node.GetNodeOrNull<Control>("Lobby");
            if (lobbyNode != null)
                return GameScreen.LOBBY;

            // ── Rooms (checked by scene tree node presence) ──
            if (node.GetNodeOrNull<Node>("Run/RoomContainer/EventRoom") != null)
                return GameScreen.EVENT;
            if (node.GetNodeOrNull<NTreasureRoom>("Run/RoomContainer/TreasureRoom") != null)
                return GameScreen.TREASURE;
            if (node.GetNodeOrNull<NRestSiteRoom>("Run/RoomContainer/RestSiteRoom") != null)
                return GameScreen.REST;
            if (node.GetNodeOrNull<NMerchantRoom>("Run/RoomContainer/MerchantRoom") != null)
                return GameScreen.SHOP;
        }

        // ── Combat victory (proceed button visible) ──
        var combatRoom = NCombatRoom.Instance;
        if (combatRoom?.ProceedButton?.IsEnabled == true)
            return GameScreen.COMBAT_VICTORY;

        // ── Main menu ──
        if (node != null)
        {
            var mainMenu = node.GetNodeOrNull<Control>("MainMenu");
            if (mainMenu != null && mainMenu.IsVisibleInTree())
                return GameScreen.MAIN_MENU;
        }

        return GameScreen.NONE;
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static Node? GetRootNode()
    {
        try
        {
            return ((SceneTree)Engine.GetMainLoop()).Root
                .GetNodeOrNull<Node>("Game/RootSceneContainer");
        }
        catch { return null; }
    }

    /// <summary>
    /// Detect the Steam friend list screen shown after clicking "Join Game."
    /// Searches for the characteristic "Refresh" / "刷新" button that only
    /// appears on this screen.
    /// </summary>
    private static bool _friendListCached;
    private static ulong _friendListCacheFrame;
    private const ulong FRIEND_LIST_CACHE_FRAMES = 60; // H21: cache for ~1s to avoid per-frame full tree walk

    private static bool IsFriendListScreen(Node root)
    {
        // H21: cache the result for 60 frames to avoid per-frame full scene-tree recursion
        ulong frame = Engine.GetProcessFrames();
        if (frame - _friendListCacheFrame < FRIEND_LIST_CACHE_FRAMES)
            return _friendListCached;

        _friendListCacheFrame = frame;
        try
        {
            foreach (var child in root.GetChildren())
            {
                if (child is not Node node) continue;
                foreach (var btn in FindAllButtons(node))
                {
                    if (btn is Button button && button.Visible && !button.Disabled
                        && (button.Text.Contains("刷新", StringComparison.OrdinalIgnoreCase)
                            || button.Text.Contains("Refresh", StringComparison.OrdinalIgnoreCase)))
                    {
                        _friendListCached = true;
                        return true;
                    }
                }
            }
        }
        catch { }
        _friendListCached = false;
        return false;
    }

    private static List<Button> FindAllButtons(Node node)
    {
        var buttons = new List<Button>();
        CollectButtons(node, buttons);
        return buttons;
    }

    private static void CollectButtons(Node node, List<Button> result)
    {
        if (node is Button btn)
            result.Add(btn);
        // M24: copy children before iterating — nodes may be added/removed during traversal
        var children = new List<Node>();
        foreach (var child in node.GetChildren())
            if (child is Node childNode) children.Add(childNode);
        foreach (var childNode in children)
            CollectButtons(childNode, result);
    }
}
