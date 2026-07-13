using TokenSpire2.Core;
using TokenSpire2.Handlers;

namespace TokenSpire2.AutoBattle.Handlers;

/// <summary>Handles the MAIN_MENU screen.</summary>
public class MainMenuHandler : IScreenHandler
{
    public GameScreen Screen => GameScreen.MAIN_MENU;
    public double CooldownSeconds => 0.5;

    public double Handle(double delta)
    {
        // Delegates to AutoSlayNode's HandleMainMenu logic.
        // For now, this is a placeholder — the actual main menu
        // handling is complex (abandon, character select, multiplayer
        // navigation) and lives in AutoSlayNode.HandleMainMenu.
        //
        // Once AutoSlayNode is fully decomposed, the logic moves here.
        return 1.0;
    }
}

/// <summary>Handles the COMBAT screen via CombatHandler.PlayOneCard.</summary>
public class CombatHandlerAdapter : IScreenHandler
{
    public GameScreen Screen => GameScreen.COMBAT;
    public double CooldownSeconds => 0.1;

    public double Handle(double delta)
    {
        var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
        if (cm == null || !cm.IsInProgress) return 0.5;

        return CombatHandler.PlayOneCard(cm, SharedRng.Instance);
    }

    public void OnDeactivated()
    {
        CombatHandler.OnCombatEnded();
    }
}

/// <summary>Handles the MAP screen via MapHandler.</summary>
public class MapHandlerAdapter : IScreenHandler
{
    public GameScreen Screen => GameScreen.MAP;
    public double CooldownSeconds => 0.3;

    public double Handle(double delta)
    {
        var map = ScreenNodes.Map;
        if (map == null || !map.IsOpen) return 0.5;
        return MapHandler.Handle(map, SharedRng.Instance);
    }
}

/// <summary>Handles the EVENT screen via EventRoomHandler.</summary>
public class EventRoomHandlerAdapter : IScreenHandler
{
    public GameScreen Screen => GameScreen.EVENT;
    public double CooldownSeconds => 0.5;

    public double Handle(double delta)
    {
        var room = ScreenNodes.EventRoom;
        if (room == null) return 0.5;
        return EventRoomHandler.Handle(room, SharedRng.Instance);
    }
}

/// <summary>Handles the TREASURE screen via TreasureRoomHandler.</summary>
public class TreasureRoomHandlerAdapter : IScreenHandler
{
    public GameScreen Screen => GameScreen.TREASURE;
    public double CooldownSeconds => 0.5;

    public double Handle(double delta)
    {
        var room = ScreenNodes.TreasureRoom;
        if (room == null) return 0.5;
        return TreasureRoomHandler.Handle(room);
    }
}

/// <summary>Handles the REST screen via RestSiteHandler.</summary>
public class RestSiteHandlerAdapter : IScreenHandler
{
    public GameScreen Screen => GameScreen.REST;
    public double CooldownSeconds => 0.5;

    public double Handle(double delta)
    {
        var room = ScreenNodes.RestSiteRoom;
        if (room == null) return 0.5;
        return RestSiteHandler.Handle(room, SharedRng.Instance);
    }
}

/// <summary>Handles the SHOP screen via ShopHandler.</summary>
public class ShopHandlerAdapter : IScreenHandler
{
    public GameScreen Screen => GameScreen.SHOP;
    public double CooldownSeconds => 1.0;
    private int _handling; // 0 = idle, 1 = handling (use int for Interlocked)

    public double Handle(double delta)
    {
        var room = ScreenNodes.MerchantRoom;
        if (room == null) return 0.5;

        // ShopHandler is async — fire-and-forget with guard
        if (System.Threading.Interlocked.CompareExchange(ref _handling, 1, 0) == 0)
        {
            ShopHandler.HandleAsync(room, SharedRng.Instance).ContinueWith(t =>
            {
                System.Threading.Interlocked.Exchange(ref _handling, 0);
                if (t.IsFaulted && t.Exception != null)
                {
                    MainFile.Logger?.Info($"[ShopHandlerAdapter] HandleAsync failed: {t.Exception.InnerException?.Message ?? t.Exception.Message}");
                }
            });
        }
        return System.Threading.Volatile.Read(ref _handling) == 1 ? 0.5 : 2.0;
    }

    public void OnActivated() { System.Threading.Interlocked.Exchange(ref _handling, 0); }
}
