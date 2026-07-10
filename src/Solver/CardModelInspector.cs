using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace TokenSpire2.Solver;

/// <summary>
/// ONE-TIME runtime inspector that discovers CardModel's DynamicVar API via reflection.
/// After inspection, stored property names are used by CardEffectReader for accurate values.
/// </summary>
public static class CardModelInspector
{
    private static bool _inspected;

    /// <summary>Property name for DynamicVarSet on CardModel (discovered at runtime).</summary>
    public static string? DynamicVarSetPropertyName { get; private set; }

    /// <summary>Property name for PreviewValue on DynamicVar (discovered at runtime).</summary>
    public static string? PreviewValuePropertyName { get; private set; }

    /// <summary>Type of DynamicVarSet.</summary>
    public static Type? DynamicVarSetType { get; private set; }

    /// <summary>Type of DynamicVar.</summary>
    public static Type? DynamicVarType { get; private set; }

    /// <summary>Type of CardPreviewMode enum.</summary>
    public static Type? CardPreviewModeType { get; private set; }

    /// <summary>Whether inspection succeeded.</summary>
    public static bool IsReady =>
        DynamicVarSetPropertyName != null && DynamicVarSetType != null;

    /// <summary>
    /// Inspect CardModel using a real card instance. Safe to call multiple times.
    /// </summary>
    public static void TestWithCard(CardModel card, Creature? target)
    {
        if (_inspected) return;
        _inspected = true;

        try
        {
            var cardType = card.GetType();
            MainFile.Logger.Info($"[Inspector] Inspecting {cardType.FullName} (card: {card.Id.Entry})");

            // ── Find CardPreviewMode enum ──────────────────────────────────
            CardPreviewModeType = cardType.Assembly.GetType(
                "MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode");
            MainFile.Logger.Info($"[Inspector] CardPreviewMode: {(CardPreviewModeType != null ? $"found ({string.Join(", ", Enum.GetValues(CardPreviewModeType).Cast<object>())})" : "NOT FOUND")}");

            // ── Find DynamicVar type ────────────────────────────────────────
            DynamicVarType = cardType.Assembly.GetType(
                "MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar");
            MainFile.Logger.Info($"[Inspector] DynamicVar: {(DynamicVarType != null ? "found" : "NOT FOUND")}");

            // ── Find PreviewValue property ──────────────────────────────────
            if (DynamicVarType != null)
            {
                foreach (var name in new[] { "PreviewValue", "BaseValue", "EnchantedValue", "Value" })
                {
                    var pv = DynamicVarType.GetProperty(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (pv != null)
                    {
                        PreviewValuePropertyName = pv.Name;
                        MainFile.Logger.Info($"[Inspector] PreviewValue prop: '{pv.Name}' type={pv.PropertyType.Name}");
                        break;
                    }
                }
            }

            // ── Find DynamicVarSet property on CardModel ────────────────────
            // Search CardModel + all base types for DynamicVarSet-related property
            var searchType = cardType;
            while (searchType != null && DynamicVarSetPropertyName == null)
            {
                foreach (var name in new[] { "DynamicVars", "Vars", "VarSet", "_vars", "_dynamicVars", "DynamicVarSet", "PreviewVars" })
                {
                    var prop = searchType.GetProperty(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (prop != null)
                    {
                        DynamicVarSetPropertyName = prop.Name;
                        DynamicVarSetType = prop.PropertyType;
                        MainFile.Logger.Info($"[Inspector] DynamicVarSet prop: '{prop.Name}' type={prop.PropertyType.Name} on {searchType.Name}");
                        break;
                    }
                }
                searchType = searchType.BaseType;
            }

            if (DynamicVarSetPropertyName == null)
            {
                MainFile.Logger.Info("[Inspector] DynamicVarSet property NOT FOUND on CardModel or base types");

                // ── Fallback: dump all properties that look relevant ─────────
                var candidates = cardType.GetProperties(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var relevant = candidates.Where(p =>
                    p.Name.IndexOf("Var", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.Name.IndexOf("Dynamic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.Name.IndexOf("Preview", StringComparison.OrdinalIgnoreCase) >= 0);
                foreach (var p in relevant)
                    MainFile.Logger.Info($"[Inspector]   Candidate: {p.Name}: {p.PropertyType.Name}");
            }

            // ── Try calling UpdateDynamicVarPreview ─────────────────────────
            var updateMethod = cardType.GetMethod("UpdateDynamicVarPreview",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (updateMethod != null && DynamicVarSetPropertyName != null &&
                CardPreviewModeType != null && DynamicVarSetType != null)
            {
                var dvSetProp = cardType.GetProperty(DynamicVarSetPropertyName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (dvSetProp != null)
                {
                    var varSet = dvSetProp.GetValue(card);
                    if (varSet != null)
                    {
                        object? previewMode = null;
                        if (CardPreviewModeType.IsEnum)
                            previewMode = Enum.GetValues(CardPreviewModeType).GetValue(0);

                        try
                        {
                            updateMethod.Invoke(card, new[] { previewMode, target, varSet });
                            MainFile.Logger.Info("[Inspector] UpdateDynamicVarPreview SUCCEEDED!");

                            // Dump DynamicVarSet fields
                            foreach (var f in varSet.GetType().GetFields(
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            {
                                try
                                {
                                    var val = f.GetValue(varSet);
                                    if (val != null && DynamicVarType?.IsAssignableFrom(f.FieldType) == true)
                                    {
                                        var pvProp = DynamicVarType.GetProperty(PreviewValuePropertyName ?? "PreviewValue",
                                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                        var pv = pvProp?.GetValue(val);
                                        MainFile.Logger.Info($"[Inspector]   DynamicVar '{f.Name}' PreviewValue={pv}");
                                    }
                                }
                                catch { /* skip inaccessible fields */ }
                            }
                        }
                        catch (Exception ex)
                        {
                            MainFile.Logger.Info($"[Inspector] UpdateDynamicVarPreview FAILED: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                    else
                    {
                        MainFile.Logger.Info("[Inspector] DynamicVarSet value is NULL");
                    }
                }
            }
            else
            {
                MainFile.Logger.Info($"[Inspector] Missing: method={updateMethod != null} prop={DynamicVarSetPropertyName != null} preview={CardPreviewModeType != null} dvType={DynamicVarSetType != null}");
            }

            MainFile.Logger.Info($"[Inspector] Inspection complete. IsReady={IsReady}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[Inspector] Fatal error: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
