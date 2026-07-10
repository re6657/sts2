using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using TokenSpire2.Core;
using TokenSpire2.Handlers;
using Environment = System.Environment;

namespace TokenSpire2.AutoBattle.Handlers;

// ═══════════════════════════════════════════════════════════════
// Shared Random instance used by handlers that need RNG
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Provides a shared Random instance seeded from environment ticks.
/// Thread-safe via lock.
/// </summary>
internal static class SharedRng
{
    private static readonly Random _rng = new(Environment.TickCount);
    private static readonly object _lock = new();
    public static Random Instance { get { lock (_lock) return _rng; } }
}

// ═══════════════════════════════════════════════════════════════
// SCREEN NODE HELPERS — find nodes from static instances
// ═══════════════════════════════════════════════════════════════

/// <summary>Helpers to locate screen nodes for handler dispatch.</summary>
internal static class ScreenNodes
{
    public static NGameOverScreen? GameOver =>
        GodotObject.IsInstanceValid(NOverlayStack.Instance)
            ? NOverlayStack.Instance!.Peek() as NGameOverScreen : null;

    public static NRewardsScreen? Rewards =>
        GodotObject.IsInstanceValid(NOverlayStack.Instance)
            ? NOverlayStack.Instance!.Peek() as NRewardsScreen : null;

    public static NCardRewardSelectionScreen? CardReward =>
        GodotObject.IsInstanceValid(NOverlayStack.Instance)
            ? NOverlayStack.Instance!.Peek() as NCardRewardSelectionScreen : null;

    public static NChooseACardSelectionScreen? ChooseCard =>
        GodotObject.IsInstanceValid(NOverlayStack.Instance)
            ? NOverlayStack.Instance!.Peek() as NChooseACardSelectionScreen : null;

    public static NChooseABundleSelectionScreen? ChooseBundle =>
        GodotObject.IsInstanceValid(NOverlayStack.Instance)
            ? NOverlayStack.Instance!.Peek() as NChooseABundleSelectionScreen : null;

    public static NChooseARelicSelection? ChooseRelic =>
        GodotObject.IsInstanceValid(NOverlayStack.Instance)
            ? NOverlayStack.Instance!.Peek() as NChooseARelicSelection : null;

    public static NSimpleCardSelectScreen? SimpleCardSelect =>
        GodotObject.IsInstanceValid(NOverlayStack.Instance)
            ? NOverlayStack.Instance!.Peek() as NSimpleCardSelectScreen : null;

    public static NCrystalSphereScreen? CrystalSphere =>
        GodotObject.IsInstanceValid(NOverlayStack.Instance)
            ? NOverlayStack.Instance!.Peek() as NCrystalSphereScreen : null;

    public static Node? DeckGrid =>
        GodotObject.IsInstanceValid(NOverlayStack.Instance)
            ? NOverlayStack.Instance!.Peek() as Node : null;

    public static NMapScreen? Map => NMapScreen.Instance;

    public static NTreasureRoom? TreasureRoom =>
        GetRootNode()?.GetNodeOrNull<NTreasureRoom>("Run/RoomContainer/TreasureRoom");

    public static Node? EventRoom =>
        GetRootNode()?.GetNodeOrNull<Node>("Run/RoomContainer/EventRoom");

    public static NRestSiteRoom? RestSiteRoom =>
        GetRootNode()?.GetNodeOrNull<NRestSiteRoom>("Run/RoomContainer/RestSiteRoom");

    public static NMerchantRoom? MerchantRoom =>
        GetRootNode()?.GetNodeOrNull<NMerchantRoom>("Run/RoomContainer/MerchantRoom");

    private static Node? GetRootNode()
    {
        try { return ((SceneTree)Engine.GetMainLoop()).Root.GetNodeOrNull<Node>("Game/RootSceneContainer"); }
        catch { return null; }
    }
}
