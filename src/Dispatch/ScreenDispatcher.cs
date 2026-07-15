using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using TokenSpire2.Core;
using TokenSpire2.Deciders;
using TokenSpire2.Solver;

namespace TokenSpire2.Dispatch;

/// <summary>
/// Routes the current <see cref="GameScreen"/> to the appropriate handler.
///
/// Replaces the monolithic if/else chain in AutoSlayNode._Process()
/// with a registry of <see cref="IDecider"/> implementations.
///
/// For screens that have a Solver decider (DecisionEngine.Decide handles
/// MAP, EVENT, REST, SHOP, TREASURE, and all OVERLAY_* screens), we use
/// a built-in adapter. Screens without solver deciders (MAIN_MENU,
/// COMBAT_VICTORY, GAME_OVER, MULTIPLAYER_*, CHARACTER_SELECT) dispatch
/// to screen-specific handler methods.
/// </summary>
public static class ScreenDispatcher
{
    private static readonly Dictionary<GameScreen, IDecider> _deciders = new();
    /// <summary>
    /// Register a decider for a specific game screen.
    /// Call during initialization, before the first Dispatch().
    /// </summary>
    public static void Register(IDecider decider)
    {
        _deciders[decider.Screen] = decider;
        MainFile.Logger?.Info($"[ScreenDispatcher] Registered {decider.GetType().Name} for {decider.Screen}");
    }

    /// <summary>
    /// Register all Solver-backed deciders as a batch.
    /// These use <see cref="DecisionEngine.Decide"/> internally.
    /// </summary>
    public static void RegisterSolverDeciders()
    {
        foreach (var screen in SolverScreens)
        {
            Register(new SolverDeciderAdapter(screen));
        }
    }

    /// <summary>
    /// All game screens that have solver decider coverage via DecisionEngine.
    /// </summary>
    public static readonly GameScreen[] SolverScreens =
    {
        GameScreen.MAP,
        GameScreen.EVENT,
        GameScreen.REST,
        GameScreen.SHOP,
        GameScreen.TREASURE,
        GameScreen.OVERLAY_CARD_REWARD,
        GameScreen.OVERLAY_CHOOSE_CARD,
        GameScreen.OVERLAY_CHOOSE_BUNDLE,
        GameScreen.OVERLAY_CHOOSE_RELIC,
        GameScreen.OVERLAY_DECK_GRID,
        GameScreen.OVERLAY_SIMPLE_SELECT,
        GameScreen.OVERLAY_CRYSTAL_SPHERE,
    };

    /// <summary>
    /// Try to dispatch the current screen. Returns true if a decider
    /// was found and executed.
    /// </summary>
    public static bool TryDispatch(GameScreen screen, RunState state, double delta)
    {
        if (!_deciders.TryGetValue(screen, out var decider))
            return false;

        if (!decider.CanDecide(screen, delta))
            return false;

        return decider.Decide(state, delta);
    }

    /// <summary>
    /// Check whether a registered decider exists for this screen.
    /// </summary>
    public static bool HasDecider(GameScreen screen) => _deciders.ContainsKey(screen);

    /// <summary>
    /// Get the cooldown for a screen type (defaults to 0.3s if no decider registered).
    /// </summary>
    public static double GetCooldown(GameScreen screen)
    {
        return _deciders.TryGetValue(screen, out var d) ? d.CooldownSeconds : 0.3;
    }
}

/// <summary>
/// Adapter that wraps a Solver Decider (called via DecisionEngine.Decide)
/// as an IDecider implementation. Eliminates the need for per-screen
/// adapter classes — all 12 solver-backed screens use this single adapter.
/// </summary>
internal sealed class SolverDeciderAdapter : IDecider
{
    private readonly RunState _state = new();

    public GameScreen Screen { get; }
    public double CooldownSeconds { get; }

    public SolverDeciderAdapter(GameScreen screen)
    {
        Screen = screen;
        CooldownSeconds = screen switch
        {
            GameScreen.MAP => 2.0,
            GameScreen.EVENT => 1.0,
            GameScreen.REST => 1.0,
            GameScreen.SHOP => 0.5,
            GameScreen.TREASURE => 0.5,
            _ => 0.3, // overlays
        };
    }

    public bool CanDecide(GameScreen screen, double delta)
    {
        return StateStabilityDetector.IsStableForDecision(screen, delta);
    }

    public bool Decide(RunState state, double delta)
    {
        try
        {
            return DecisionEngine.Decide(Screen, delta);
        }
        catch (Exception ex)
        {
            MainFile.Logger?.Error($"[SolverDeciderAdapter] {Screen} failed: {ex.Message}");
            return false;
        }
    }
}
