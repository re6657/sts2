using TokenSpire2.Core;
using TokenSpire2.Solver;

namespace TokenSpire2.Deciders;

/// <summary>
/// Unified interface for all game-screen deciders.
///
/// Each implementation handles exactly one <see cref="GameScreen"/>,
/// replacing the old split between src/Handlers/ (random-based wrappers)
/// and src/Solver/ (scoring heuristics).
///
/// Deciders use only deterministic decision-making:
///   - <see cref="RunState"/> for game-state snapshots
///   - <see cref="Tiebreaker"/> for FNV-1a deterministic selection
///   - <see cref="DecisionLogger"/> for audit trails
///
/// NEVER use System.Random in a Decider — multiplayer lockstep requires
/// identical decisions on all instances.
/// </summary>
public interface IDecider
{
    /// <summary>
    /// Which game screen this decider handles.
    /// </summary>
    GameScreen Screen { get; }

    /// <summary>
    /// Minimum cooldown (seconds) between decisions for this screen type.
    /// Prevents rapid-fire clicks during transitions.
    /// </summary>
    double CooldownSeconds { get; }

    /// <summary>
    /// Whether the game state is stable enough for this decider to make
    /// a decision. Default: delegates to <see cref="StateStabilityDetector"/>.
    /// </summary>
    bool CanDecide(GameScreen screen, double delta)
    {
        return StateStabilityDetector.IsStableForDecision(screen, delta);
    }

    /// <summary>
    /// Execute a decision for the current screen. Called once per tick
    /// when <see cref="CanDecide"/> returns true and cooldown has elapsed.
    ///
    /// Returns true if a decision was made (action was taken), false if
    /// the screen needs more time (e.g. animations still playing).
    /// </summary>
    bool Decide(RunState state, double delta);
}
