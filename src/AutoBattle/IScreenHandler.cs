using TokenSpire2.Core;

namespace TokenSpire2.AutoBattle;

/// <summary>
/// Contract for handlers that process a specific game screen.
///
/// Each handler claims one or more <see cref="GameScreen"/> values.
/// The <see cref="ScreenDispatcher"/> queries <see cref="CanHandle"/>
/// and delegates to the first matching handler each frame.
///
/// Cooldown prevents the same handler from firing repeatedly
/// in the same frame (e.g. while waiting for an animation).
/// </summary>
public interface IScreenHandler
{
    /// <summary>Primary screen this handler manages.</summary>
    GameScreen Screen { get; }

    /// <summary>
    /// True if this handler can process the given screen.
    /// Default implementation checks <c>screen == Screen</c>.
    /// Override for handlers that cover multiple screens.
    /// </summary>
    bool CanHandle(GameScreen screen) => screen == Screen;

    /// <summary>
    /// Process the current screen. Called every frame while
    /// this handler is active.
    /// </summary>
    /// <param name="delta">Frame delta time in seconds.</param>
    /// <returns>Delay in seconds before the next action.
    /// Return 0 to act again next frame. Return a positive
    /// value to wait (e.g. for animation to complete).</returns>
    double Handle(double delta);

    /// <summary>
    /// Minimum cooldown between Handle calls, in seconds.
    /// Prevents button-spam when a screen transition is in progress.
    /// </summary>
    double CooldownSeconds { get; }

    /// <summary>Called when this handler becomes the active handler.</summary>
    void OnActivated() { }

    /// <summary>Called when this handler is no longer the active handler.</summary>
    void OnDeactivated() { }
}
