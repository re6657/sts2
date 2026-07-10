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

namespace TokenSpire2.Solver;

/// <summary>
/// Unified screen/room detection — pattern after CommunicationMod's
/// ChoiceScreenUtils.getCurrentChoiceType(). Detects what screen the game
/// is currently showing via scene-tree queries and singleton checks.
/// </summary>
public enum GameScreen
{
    NONE,
    MAIN_MENU,
    COMBAT,
    MAP,
    EVENT,
    TREASURE,
    REST,
    SHOP,
    COMBAT_VICTORY,    // combat room proceed button visible
    GAME_OVER,
    // Overlay screens (appear on top of other screens)
    OVERLAY_CARD_REWARD,     // NCardRewardSelectionScreen
    OVERLAY_REWARDS,         // NRewardsScreen
    OVERLAY_CHOOSE_CARD,     // NChooseACardSelectionScreen
    OVERLAY_CHOOSE_BUNDLE,   // NChooseABundleSelectionScreen
    OVERLAY_CHOOSE_RELIC,    // NChooseARelicSelection
    OVERLAY_DECK_GRID,       // Upgrade/Transform/Enchant/Remove
    OVERLAY_SIMPLE_SELECT,   // NSimpleCardSelectScreen
    OVERLAY_CRYSTAL_SPHERE,  // NCrystalSphereScreen
}

public static class GameStateDetector
{
    /// <summary>
    /// Detect current game screen. Call every frame in _Process.
    /// Returns the detected screen and whether an overlay is on top.
    /// </summary>
    public static GameScreen Detect()
    {
        // ── Overlays (checked first, they stack on top of other screens) ──
        if (NOverlayStack.Instance?.ScreenCount > 0)
        {
            var overlay = NOverlayStack.Instance.Peek();
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

        // ── Rooms (checked by scene tree node presence) ──
        var node = GetRootNode();
        if (node != null)
        {
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

    /// <summary>True if the current screen is a non-combat overlay that needs a decision.</summary>
    public static bool IsOverlayScreen(GameScreen screen)
    {
        return screen >= GameScreen.OVERLAY_CARD_REWARD;
    }

    /// <summary>True if this screen type needs a strategic decision (not just mechanical clicking).</summary>
    public static bool NeedsDecision(GameScreen screen)
    {
        return screen switch
        {
            GameScreen.MAP => true,
            GameScreen.EVENT => true,
            GameScreen.REST => true,
            GameScreen.SHOP => true,
            GameScreen.OVERLAY_CARD_REWARD => true,
            GameScreen.OVERLAY_CHOOSE_CARD => true,
            GameScreen.OVERLAY_CHOOSE_BUNDLE => true,
            GameScreen.OVERLAY_CHOOSE_RELIC => true,
            GameScreen.OVERLAY_DECK_GRID => true,
            GameScreen.OVERLAY_SIMPLE_SELECT => true,
            GameScreen.OVERLAY_CRYSTAL_SPHERE => true,
            _ => false,
        };
    }

    private static Node? GetRootNode()
    {
        try
        {
            return ((SceneTree)Engine.GetMainLoop()).Root
                .GetNodeOrNull<Node>("Game/RootSceneContainer");
        }
        catch { return null; }
    }
}
