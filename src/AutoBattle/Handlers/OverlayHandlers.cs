using TokenSpire2.Core;
using TokenSpire2.Handlers;

namespace TokenSpire2.AutoBattle.Handlers;

/// <summary>Handles the GAME_OVER screen via GameOverHandler.</summary>
public class GameOverHandlerAdapter : IScreenHandler
{
    public GameScreen Screen => GameScreen.GAME_OVER;
    public double CooldownSeconds => 1.0;

    public double Handle(double delta)
    {
        var screen = ScreenNodes.GameOver;
        if (screen == null || !Godot.GodotObject.IsInstanceValid(screen)) return 0.5;
        return GameOverHandler.Handle(screen);
    }
}

/// <summary>Handles the COMBAT_VICTORY screen.</summary>
public class CombatVictoryHandler : IScreenHandler
{
    public GameScreen Screen => GameScreen.COMBAT_VICTORY;
    public double CooldownSeconds => 0.5;

    public double Handle(double delta)
    {
        var combatRoom = MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance;
        var btn = combatRoom?.ProceedButton;
        if (btn != null && btn.IsEnabled)
        {
            btn.ForceClick();
            return 1.5;
        }
        return 0.5;
    }
}

/// <summary>Handles OVERLAY_REWARDS via RewardsHandler.</summary>
public class RewardsHandlerAdapter : IScreenHandler
{
    public GameScreen Screen => GameScreen.OVERLAY_REWARDS;
    public double CooldownSeconds => 0.3;

    public double Handle(double delta)
    {
        var screen = ScreenNodes.Rewards;
        if (screen == null || !Godot.GodotObject.IsInstanceValid(screen)) return 0.5;
        return RewardsHandler.Handle(screen);
    }

    public void OnActivated() => RewardsHandler.ClearTried();
}

/// <summary>Handles OVERLAY_CARD_REWARD via CardRewardHandler.</summary>
public class CardRewardHandlerAdapter : IScreenHandler
{
    public GameScreen Screen => GameScreen.OVERLAY_CARD_REWARD;
    public double CooldownSeconds => 0.3;

    public double Handle(double delta)
    {
        var screen = ScreenNodes.CardReward;
        if (screen == null || !Godot.GodotObject.IsInstanceValid(screen)) return 0.5;
        return CardRewardHandler.Handle(screen, SharedRng.Instance);
    }
}

/// <summary>Handles OVERLAY_CHOOSE_CARD via ChooseACardHandler.</summary>
public class ChooseCardHandlerAdapter : IScreenHandler
{
    public GameScreen Screen => GameScreen.OVERLAY_CHOOSE_CARD;
    public double CooldownSeconds => 0.3;

    public double Handle(double delta)
    {
        var screen = ScreenNodes.ChooseCard;
        if (screen == null || !Godot.GodotObject.IsInstanceValid(screen)) return 0.5;
        return ChooseACardHandler.Handle(screen, SharedRng.Instance);
    }
}

/// <summary>Handles OVERLAY_CHOOSE_BUNDLE via ChooseABundleHandler.</summary>
public class BundleHandlerAdapter : IScreenHandler
{
    public GameScreen Screen => GameScreen.OVERLAY_CHOOSE_BUNDLE;
    public double CooldownSeconds => 0.3;

    public double Handle(double delta)
    {
        var screen = ScreenNodes.ChooseBundle;
        if (screen == null || !Godot.GodotObject.IsInstanceValid(screen)) return 0.5;
        return ChooseABundleHandler.Handle(screen, SharedRng.Instance);
    }
}

/// <summary>Handles OVERLAY_CHOOSE_RELIC via ChooseARelicHandler.</summary>
public class RelicHandlerAdapter : IScreenHandler
{
    public GameScreen Screen => GameScreen.OVERLAY_CHOOSE_RELIC;
    public double CooldownSeconds => 0.3;

    public double Handle(double delta)
    {
        var screen = ScreenNodes.ChooseRelic;
        if (screen == null || !Godot.GodotObject.IsInstanceValid(screen)) return 0.5;
        return ChooseARelicHandler.Handle(screen, SharedRng.Instance);
    }
}

/// <summary>Handles OVERLAY_SIMPLE_SELECT via SimpleCardSelectHandler.</summary>
public class SimpleCardSelectHandlerAdapter : IScreenHandler
{
    public GameScreen Screen => GameScreen.OVERLAY_SIMPLE_SELECT;
    public double CooldownSeconds => 0.2;

    public double Handle(double delta)
    {
        var screen = ScreenNodes.SimpleCardSelect;
        if (screen == null || !Godot.GodotObject.IsInstanceValid(screen)) return 0.5;
        return SimpleCardSelectHandler.Handle(screen, SharedRng.Instance);
    }
}

/// <summary>Handles OVERLAY_CRYSTAL_SPHERE via CrystalSphereHandler.</summary>
public class CrystalSphereHandlerAdapter : IScreenHandler
{
    public GameScreen Screen => GameScreen.OVERLAY_CRYSTAL_SPHERE;
    public double CooldownSeconds => 0.3;

    public double Handle(double delta)
    {
        var screen = ScreenNodes.CrystalSphere;
        if (screen == null || !Godot.GodotObject.IsInstanceValid(screen)) return 0.5;
        return CrystalSphereHandler.Handle(screen, SharedRng.Instance);
    }
}

/// <summary>Handles OVERLAY_DECK_GRID via CardGridHandler (upgrade/transform/enchant/remove).</summary>
public class CardGridHandlerAdapter : IScreenHandler
{
    public GameScreen Screen => GameScreen.OVERLAY_DECK_GRID;
    public double CooldownSeconds => 0.3;

    public double Handle(double delta)
    {
        var screen = ScreenNodes.DeckGrid;
        if (screen == null || !Godot.GodotObject.IsInstanceValid(screen)) return 0.5;
        return CardGridHandler.Handle(screen, SharedRng.Instance);
    }
}
