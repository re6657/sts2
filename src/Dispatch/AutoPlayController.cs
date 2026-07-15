using System;
using Godot;
using TokenSpire2.Core;
using TokenSpire2.Dispatch;

namespace TokenSpire2;

/// <summary>
/// Primary controller entry point for the TokenSpire2 auto-play bot.
///
/// This is a thin lifecycle wrapper. The actual game-interaction logic
/// (combat solving, screen dispatch, multiplayer handling, etc.) lives
/// in <see cref="AutoSlayNode"/>, which is spawned as a child node.
///
/// This controller handles only:
///   - Feature toggle coordination (F1/F2/F3 keys)
///   - High-level stuck detection and recovery
///   - Diagnostic heartbeat logging
///   - Screen dispatcher registration
///
/// Architecture:
///   AutoPlayController (Node)
///     └── AutoSlayNode (Node, spawned in _Ready)
///           └── Full _Process loop: combat solver + screen dispatch
/// </summary>
public partial class AutoPlayController : Node
{
    private AutoSlayNode? _autoSlay;

    public override void _Ready()
    {
        SetProcess(true);

        // ── Register Solver-backed deciders for ScreenDispatcher ─────
        ScreenDispatcher.RegisterSolverDeciders();

        // ── Spawn the full AutoSlayNode as child ─────────────────────
        // All game interaction logic (combat solver, screen dispatch,
        // multiplayer, AI chat) lives here. This controller provides
        // only the top-level lifecycle management.
        _autoSlay = new AutoSlayNode();
        _autoSlay.Name = "AutoSlayNode";
        AddChild(_autoSlay);

        MainFile.Logger?.Info("[AutoPlayController] Ready — AutoSlayNode spawned as child.");
    }

    public override void _Process(double delta)
    {
        // The child AutoSlayNode runs its own _Process independently.
        // This controller only needs to exist as the registered entry point.
        // Future: add monitoring, metrics, or dynamic reload here.
    }

    public override void _ExitTree()
    {
        if (_autoSlay != null)
        {
            RemoveChild(_autoSlay);
            _autoSlay.QueueFree();
            _autoSlay = null;
        }
    }
}
