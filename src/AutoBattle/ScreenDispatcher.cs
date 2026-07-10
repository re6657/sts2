using System;
using System.Collections.Generic;
using TokenSpire2.Core;

namespace TokenSpire2.AutoBattle;

/// <summary>
/// Holds an ordered list of <see cref="IScreenHandler"/> implementations.
/// Each frame, iterates through handlers to find the first one that
/// <see cref="IScreenHandler.CanHandle"/> the current screen, then
/// delegates to it.
///
/// This replaces the giant switch statement in AutoSlayNode._Process.
/// </summary>
public class ScreenDispatcher
{
    private readonly List<IScreenHandler> _handlers = new();
    private IScreenHandler? _activeHandler;
    private GameScreen _lastScreen = GameScreen.NONE;
    private double _cooldownRemaining;

    /// <summary>Register a handler. Handlers are checked in registration order.</summary>
    public void Register(IScreenHandler handler)
    {
        _handlers.Add(handler);
    }

    /// <summary>Register multiple handlers at once.</summary>
    public void RegisterAll(IEnumerable<IScreenHandler> handlers)
    {
        _handlers.AddRange(handlers);
    }

    /// <summary>The currently active handler, or null if none.</summary>
    public IScreenHandler? ActiveHandler => _activeHandler;

    /// <summary>
    /// Dispatch the current screen to the appropriate handler.
    /// Call every frame from _Process.
    /// </summary>
    /// <param name="screen">Current detected screen.</param>
    /// <param name="delta">Frame delta time.</param>
    /// <returns>Delay in seconds before the next action.</returns>
    public double Dispatch(GameScreen screen, double delta)
    {
        // Cooldown: skip this frame if still waiting
        if (_cooldownRemaining > 0)
        {
            _cooldownRemaining -= delta;
            if (_cooldownRemaining > 0)
                return _cooldownRemaining;
        }

        // Screen changed → deactivate old handler
        if (screen != _lastScreen && _activeHandler != null)
        {
            _activeHandler.OnDeactivated();
            _activeHandler = null;
        }
        _lastScreen = screen;

        // Find a handler that can handle this screen
        IScreenHandler? next = null;
        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(screen))
            {
                next = handler;
                break;
            }
        }

        // Handler changed → activate new one
        if (next != _activeHandler)
        {
            _activeHandler?.OnDeactivated();
            _activeHandler = next;
            _activeHandler?.OnActivated();
        }

        // Delegate
        if (_activeHandler != null)
        {
            double delay = _activeHandler.Handle(delta);
            _cooldownRemaining = Math.Max(delay, _activeHandler.CooldownSeconds);
            return _cooldownRemaining;
        }

        // No handler for this screen — wait and retry
        _cooldownRemaining = 0.5;
        return _cooldownRemaining;
    }

    /// <summary>Reset all state (e.g. on new run).</summary>
    public void Reset()
    {
        _activeHandler?.OnDeactivated();
        _activeHandler = null;
        _lastScreen = GameScreen.NONE;
        _cooldownRemaining = 0;
    }
}
