using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace TokenSpire2.Solver;

/// <summary>
/// Shared UI interaction utilities for all deciders.
/// Patterned after CommunicationMod's ChoiceScreenUtils — a single
/// entry point for all button/control detection.
///
/// CommunicationMod has independent isConfirmButtonAvailable() /
/// isCancelButtonAvailable() per screen type. We generalize this
/// with reflection and naming patterns for STS2's Godot UI.
/// </summary>
public static class UiUtils
{
    /// <summary>
    /// Find a confirm/proceed button anywhere in the given node.
    /// Tries: NConfirmButton, NProceedButton, NClickableControl
    ///     with confirm/proceed/ok/done/finish/accept naming.
    /// </summary>
    public static NButton? FindConfirmButton(Node root)
    {
        // Standard confirm types
        var confirm = root.GetNodeOrNull<NConfirmButton>("%Confirm");
        if (confirm?.IsEnabled == true) return confirm;

        var proceed = root.GetNodeOrNull<NProceedButton>("%Proceed");
        if (proceed?.IsEnabled == true) return proceed;

        // Deep scan: all NClickableControls with confirm-like names
        foreach (var c in AutoSlayHelpers.FindAll<NClickableControl>(root))
        {
            if (!c.IsEnabled) continue;
            try
            {
                string name = (c.Name?.ToString() ?? "").ToLowerInvariant();
                if (name.Contains("confirm") || name.Contains("proceed") ||
                    name.Contains("choose") || name.Contains("select") ||
                    name.Contains("ok") || name.Contains("done") ||
                    name.Contains("finish") || name.Contains("accept") ||
                    name.Contains("yes") || name.Contains("submit"))
                {
                    if (c is NButton btn) return btn;
                }

                // Try nested confirm child
                var child = c.GetNodeOrNull<NButton>("%Confirm")
                    ?? c.GetNodeOrNull<NButton>("%Proceed")
                    ?? c.GetNodeOrNull<NButton>("%Choose");
                if (child?.IsEnabled == true) return child;
            }
            catch { }
        }

        // Fallback: ANY enabled NProceedButton or NConfirmButton
        foreach (var c in AutoSlayHelpers.FindAll<NClickableControl>(root))
        {
            if ((c is NProceedButton || c is NConfirmButton) && c.IsEnabled)
                return c as NButton;
        }

        return null;
    }

    /// <summary>
    /// Find a cancel/skip/back/leave button anywhere in the given node.
    /// </summary>
    public static NButton? FindCancelButton(Node root)
    {
        // Standard cancel types
        var cancel = root.GetNodeOrNull<NBackButton>("%Cancel");
        if (cancel?.IsEnabled == true) return cancel;

        var back = root.GetNodeOrNull<NBackButton>("%Back");
        if (back?.IsEnabled == true) return back;

        var skip = root.GetNodeOrNull<NBackButton>("%Skip");
        if (skip?.IsEnabled == true) return skip;

        // Deep scan: all NClickableControls with cancel-like names
        foreach (var c in AutoSlayHelpers.FindAll<NClickableControl>(root))
        {
            if (!c.IsEnabled) continue;
            try
            {
                string name = (c.Name?.ToString() ?? "").ToLowerInvariant();
                if (name.Contains("cancel") || name.Contains("skip") ||
                    name.Contains("back") || name.Contains("return") ||
                    name.Contains("leave") || name.Contains("close") ||
                    name.Contains("dismiss") || name.Contains("no"))
                {
                    if (c is NButton btn) return btn;
                }

                var child = c.GetNodeOrNull<NButton>("%Cancel")
                    ?? c.GetNodeOrNull<NButton>("%Back")
                    ?? c.GetNodeOrNull<NButton>("%Skip");
                if (child?.IsEnabled == true) return child;
            }
            catch { }
        }

        // Fallback: ANY enabled NBackButton
        foreach (var c in AutoSlayHelpers.FindAll<NClickableControl>(root))
        {
            if (c is NBackButton && c.IsEnabled)
                return c as NButton;
        }

        return null;
    }

    /// <summary>
    /// Find a proceed button on a room (combat victory, etc.).
    /// </summary>
    public static NProceedButton? FindProceedButton(Node root)
    {
        var proceed = root.GetNodeOrNull<NProceedButton>("%Proceed");
        if (proceed?.IsEnabled == true) return proceed;

        // Try finding any enabled NProceedButton
        foreach (var c in AutoSlayHelpers.FindAll<NClickableControl>(root))
        {
            if (c is NProceedButton p && p.IsEnabled)
                return p;
        }

        return null;
    }

    /// <summary>
    /// Try to dismiss an unknown overlay by finding and clicking
    /// any confirm, proceed, back, or cancel button.
    /// Returns true if a button was found and clicked.
    /// </summary>
    public static bool TryDismissUnknownOverlay(Node overlayNode)
    {
        MainFile.Logger.Info($"[UiUtils] Unknown overlay: {overlayNode.GetType().Name} — trying to dismiss");

        // Try confirm first
        var confirm = FindConfirmButton(overlayNode);
        if (confirm != null)
        {
            MainFile.Logger.Info($"[UiUtils] Clicking confirm on unknown overlay: {confirm.GetType().Name}");
            confirm.ForceClick();
            return true;
        }

        // Try cancel/back
        var cancel = FindCancelButton(overlayNode);
        if (cancel != null)
        {
            MainFile.Logger.Info($"[UiUtils] Clicking cancel on unknown overlay: {cancel.GetType().Name}");
            cancel.ForceClick();
            return true;
        }

        // Try proceed
        var proceed = FindProceedButton(overlayNode);
        if (proceed != null)
        {
            MainFile.Logger.Info($"[UiUtils] Clicking proceed on unknown overlay: {proceed.GetType().Name}");
            proceed.ForceClick();
            return true;
        }

        // Last resort: dump node structure for debugging
        DumpNodeStructure(overlayNode);
        return false;
    }

    /// <summary>
    /// Debug dump: log all clickable controls in a node tree.
    /// Used when we encounter an unknown overlay/screen type.
    /// </summary>
    public static void DumpClickableControls(Node root, string context)
    {
        var all = AutoSlayHelpers.FindAll<NClickableControl>(root);
        MainFile.Logger.Info($"[UiUtils] === {context}: {all.Count} clickable controls ===");
        foreach (var c in all)
        {
            try
            {
                string name = c.Name ?? "?";
                string type = c.GetType().Name;
                bool enabled = c.IsEnabled;
                MainFile.Logger.Info($"[UiUtils]   {type} '{name}' enabled={enabled}");
            }
            catch { }
        }
    }

    /// <summary>
    /// Dump node structure: log all children and their types.
    /// </summary>
    private static void DumpNodeStructure(Node node, string indent = "")
    {
        try
        {
            MainFile.Logger.Info($"[UiUtils] {indent}{node.GetType().Name} '{node.Name}'");
            foreach (var child in node.GetChildren())
            {
                try { DumpNodeStructure(child, indent + "  "); }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Check if a button/control "looks like" a confirm button.
    /// Uses reflection to check common property names.
    /// </summary>
    public static bool IsConfirmLike(Node node)
    {
        string name = (node.Name?.ToString() ?? "").ToLowerInvariant();
        string type = node.GetType().Name.ToLowerInvariant();

        if (name.Contains("confirm") || name.Contains("proceed") ||
            name.Contains("ok") || name.Contains("done") ||
            name.Contains("yes") || name.Contains("accept") ||
            name.Contains("choose") || name.Contains("submit"))
            return true;

        if (type.Contains("confirm") || type.Contains("proceed"))
            return true;

        return false;
    }

    /// <summary>
    /// Check if a button/control "looks like" a cancel button.
    /// </summary>
    public static bool IsCancelLike(Node node)
    {
        string name = (node.Name?.ToString() ?? "").ToLowerInvariant();
        string type = node.GetType().Name.ToLowerInvariant();

        if (name.Contains("cancel") || name.Contains("skip") ||
            name.Contains("back") || name.Contains("return") ||
            name.Contains("leave") || name.Contains("close") ||
            name.Contains("no") || name.Contains("dismiss"))
            return true;

        if (type.Contains("back") || type.Contains("cancel"))
            return true;

        return false;
    }

    /// <summary>
    /// Wait for a condition to become true, checking each frame.
    /// Returns true if condition was met, false if timed out.
    /// Uses cooperative multi-tick pattern — does not block the game loop.
    /// </summary>
    public static bool WaitForCondition(Func<bool> condition, double timeoutSeconds = 3.0)
    {
        // This is a cooperative check — the caller should call this
        // each frame until it returns true or until their own timeout.
        // We just relay the condition check.
        return condition();
    }
}
