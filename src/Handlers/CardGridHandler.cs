using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace TokenSpire2.Handlers;

/// <summary>
/// Handles two-phase card grid screens: DeckUpgrade, DeckTransform, DeckEnchant, DeckCardSelect.
/// Flow: select cards -> main confirm -> preview appears -> preview confirm -> screen closes.
/// Each call does one step, relying on the _Process tick loop to call again.
/// </summary>
public static class CardGridHandler
{
    // Known preview container node names across different screen types
    private static readonly string[] PreviewNames =
    {
        "%PreviewContainer",
        "%UpgradeSinglePreviewContainer",
        "%UpgradeMultiPreviewContainer",
        "%EnchantSinglePreviewContainer",
        "%EnchantMultiPreviewContainer",
    };

    private static int? _llmChoice;

    /// <summary>
    /// Set the LLM's chosen card index (1-based). Called before Handle() on the next tick.
    /// </summary>
    public static void SetLlmChoice(int choice) => _llmChoice = choice;

    /// <summary>
    /// Returns true if an LLM choice is pending (waiting to be applied by Handle()).
    /// </summary>
    public static bool HasPendingLlmChoice => _llmChoice.HasValue;

    public static double Handle(Node screen, System.Random rng)
    {
        if (!GodotObject.IsInstanceValid(screen)) return 0;

        // Phase 3: Preview visible — find and click the confirm inside it
        var visiblePreview = FindVisiblePreview(screen);
        if (visiblePreview != null)
        {
            var previewConfirm = visiblePreview.GetNodeOrNull<NConfirmButton>("Confirm")
                ?? visiblePreview.GetNodeOrNull<NConfirmButton>("%PreviewConfirm")
                ?? AutoSlayHelpers.FindFirst<NConfirmButton>(visiblePreview);
            if (previewConfirm?.IsEnabled == true)
            {
                MainFile.Logger.Info("[AutoSlay] Clicking preview confirm");
                previewConfirm.ForceClick();
                return 1.0;
            }
            // Preview visible but confirm not ready yet — wait
            return 0.3;
        }

        // Phase 2: Main confirm enabled (no preview yet) — click to trigger preview
        var mainConfirm = screen.GetNodeOrNull<NConfirmButton>("Confirm")
            ?? screen.GetNodeOrNull<NConfirmButton>("%Confirm");
        if (mainConfirm?.IsEnabled == true)
        {
            MainFile.Logger.Info("[AutoSlay] Clicking main confirm to show preview");
            mainConfirm.ForceClick();
            return 0.5;
        }

        // Phase 1: Select a card
        var cards = AutoSlayHelpers.FindAll<NGridCardHolder>(screen);
        if (cards.Count == 0) return 0.5;

        NGridCardHolder pick;
        if (_llmChoice.HasValue && _llmChoice.Value >= 1 && _llmChoice.Value <= cards.Count)
        {
            pick = cards[_llmChoice.Value - 1];
            MainFile.Logger.Info($"[AutoSlay/LLM] Selecting card {_llmChoice.Value} in grid ({cards.Count} available)");
            _llmChoice = null;
        }
        else
        {
            // ── Fallback: use scored selection (never random) ────────
            // Prefer non-basic, high-impact cards. NEVER pick Strike/Defend.
            pick = cards.OrderByDescending(c =>
            {
                try
                {
                    string id = c.CardModel?.Id.Entry?.ToUpperInvariant() ?? "";
                    bool isBasic = id == "STRIKE" || id.StartsWith("STRIKE_")
                                || id == "DEFEND" || id.StartsWith("DEFEND_");
                    if (isBasic) return -100;
                    int cost = c.CardModel?.EnergyCost.CostsX == true ? 3
                        : Math.Min(c.CardModel?.EnergyCost.Canonical ?? 1, 5);
                    bool upgraded = c.CardModel?.IsUpgraded == true;
                    return cost * 5 + (upgraded ? 10 : 0);
                }
                catch { return 0; }
            }).First();
            MainFile.Logger.Info($"[AutoSlay] Scored card selection in grid ({cards.Count} available)");
            _llmChoice = null;
        }

        var grid = AutoSlayHelpers.FindFirst<NCardGrid>(screen);
        if (grid != null)
        {
            MainFile.Logger.Info($"[AutoSlay] Emitting HolderPressed for {pick.CardModel?.Id.Entry ?? "?"}");
            grid.EmitSignal(NCardGrid.SignalName.HolderPressed, pick);
        }
        else
        {
            // Fallback: try click via EmitSignal if grid is missing
            MainFile.Logger.Warn($"[AutoSlay] NCardGrid not found — falling back to EmitSignal on card holder");
            try { pick?.EmitSignal(NCardGrid.SignalName.HolderPressed, pick); } catch { }
        }
        return 0.3;
    }

    private static Control? FindVisiblePreview(Node screen)
    {
        foreach (var name in PreviewNames)
        {
            var container = screen.GetNodeOrNull<Control>(name);
            if (container?.Visible == true)
                return container;
        }
        return null;
    }
}
