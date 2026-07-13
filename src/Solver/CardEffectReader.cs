using System;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace TokenSpire2.Solver;

/// <summary>
/// Reads ACCURATE card damage/block values from the game engine's DynamicVar system.
/// Uses runtime reflection to call UpdateDynamicVarPreview() and read PreviewValue.
/// Falls back to hardcoded estimates if reflection fails.
/// </summary>
public static class CardEffectReader
{
    private static PropertyInfo? _dvSetProp;
    private static MethodInfo? _updatePreviewMethod;
    private static PropertyInfo? _previewValueProp;
    private static bool _initAttempted;
    private static bool _loggedEngineStatus;

    /// <summary>Accurate card effect data read from the game engine.</summary>
    public struct CardEffects
    {
        public int BaseDamage;
        public int BaseBlock;
        public int VulnerableStacks;
        public int WeakStacks;
        public bool GrantsStrength;
        public int StrengthAmount;
        public bool GrantsDexterity;
        public int DexterityAmount;
        public int EnergyGain;
        public int PoisonStacks;
        public int HpCost;
        public bool IsAoe;
        public bool IsPower;
        public bool IsXCost;
        public bool IsOrbEvoke; // Defect: this card evokes orbs (Dualcast, Multi-Cast, Recursion)
        /// <summary>
        /// True if BaseDamage/BaseBlock were computed by the game engine's DynamicVar system.
        /// These values ALREADY include Strength, Dexterity, relic effects, and enchant modifiers.
        /// When false, values are from the hardcoded fallback and do NOT include combat modifiers.
        /// </summary>
        public bool FromGameEngine;
        public string DebugInfo; // non-empty when reflection was used
    }

    /// <summary>
    /// Read card effects using the game's own DynamicVar computation.
    /// This accounts for ALL modifiers: strength, upgrades, enchants, relics, etc.
    /// </summary>
    /// <param name="card">The card to evaluate.</param>
    /// <param name="target">Optional target creature for damage calculation.</param>
    /// <returns>Accurate card effects, or fallback estimates.</returns>
    public static CardEffects ReadEffects(CardModel card, Creature? target = null)
    {
        var fx = new CardEffects();

        // ── Basic metadata (always accurate) ──
        fx.IsXCost = card.EnergyCost.CostsX;
        fx.IsPower = card.Type == CardType.Power;
        fx.IsAoe = card.TargetType == TargetType.AllEnemies
                || card.TargetType == TargetType.RandomEnemy;
        string idUpper = card.Id.Entry.ToUpperInvariant();

        // ── Try game engine computation via reflection ──
        // Trigger one-time inspection on first call
        CardModelInspector.TestWithCard(card, target);
        TryInitReflection();

        if (_dvSetProp != null && _updatePreviewMethod != null)
        {
            try
            {
                var varSet = _dvSetProp.GetValue(card);
                if (varSet != null)
                {
                    // Call UpdateDynamicVarPreview with CardPreviewMode.Normal (enum value 0)
                    object? previewMode = null;
                    var previewType = CardModelInspector.CardPreviewModeType;
                    if (previewType?.IsEnum == true)
                        previewMode = Enum.GetValues(previewType).GetValue(0);

                    _updatePreviewMethod.Invoke(card, new[] { previewMode, target, varSet });

                    // Extract damage and block from DynamicVarSet fields
                    ReadFromVarSet(varSet, ref fx);
                    fx.FromGameEngine = true;
                    // ── Orb-evoke detection for Defect cards ──
                    // These cards have no direct damage DynamicVar; their value comes
                    // from evoking orbs. Mark them so the solver can estimate evoke damage.
                    fx.IsOrbEvoke = idUpper switch
                    {
                        "DUALCAST" or "MULTI_CAST" or "RECURSION" or "FISSION" => true,
                        _ => false,
                    };
                    fx.DebugInfo = $"reflection: dmg={fx.BaseDamage} blk={fx.BaseBlock}";
                    if (!_loggedEngineStatus)
                    {
                        _loggedEngineStatus = true;
                        MainFile.Logger.Info($"[CardEffectReader] ✅ Game engine DynamicVar ACTIVE — card values include Strength/Dexterity/Relics/Enchants. Solver will NOT add them again.");
                    }
                }
            }
            catch (Exception ex)
            {
                fx.DebugInfo = $"reflection_err: {ex.GetType().Name}";
            }
        }

        // ── Secondary enchant detection ───────────────────────────────
        // If reflection gave 0 values, try reading enchant data directly
        // from the CardModel. Enchants can double damage/block or add effects.
        bool reflectionFoundValues = fx.BaseDamage > 0 || fx.BaseBlock > 0;
        if (!reflectionFoundValues)
        {
            TryReadEnchantFromCard(card, ref fx, out bool enchantFound);
            if (enchantFound)
                reflectionFoundValues = fx.BaseDamage > 0 || fx.BaseBlock > 0;
        }

        // ── Fallback to hardcoded estimates ──
        // Only use fallback when reflection returned zero values OR when the card
        // is a well-known basic card whose values we trust more than reflection.

        if (!reflectionFoundValues && !fx.IsPower)
        {
            // Log when fallback is used — silent underestimation can cause solver errors.
            // Only log once per card ID to avoid spam.
            if (!_fallbackLogged.Contains(idUpper))
            {
                _fallbackLogged.Add(idUpper);
                MainFile.Logger.Warn($"[CardEffectReader] Using hardcoded fallback for '{idUpper}' — reflection returned 0/0");
            }
            FallbackEstimate(card, ref fx);
        }
        else if (reflectionFoundValues)
        {
            // Validate: for well-known basic cards, reflection sometimes returns
            // incorrect values (e.g., Strike read as 4 instead of 6). Check against
            // our hardcoded table and override if the reflection value seems wrong.
            ValidateAgainstFallback(idUpper, card, ref fx);
        }

        return fx;
    }

    /// <summary>
    /// Try to read enchant data directly from the CardModel.
    /// Enchanted cards in STS2 have an Enchant component or marker that
    /// modifies base damage/block (e.g., double damage, -1 cost, etc.).
    /// This is a fallback when DynamicVar reflection returns 0 values.
    /// </summary>
    private static void TryReadEnchantFromCard(CardModel card, ref CardEffects fx, out bool enchantFound)
    {
        enchantFound = false;
        try
        {
            // Check for enchant-related properties on CardModel
            var cardType = card.GetType();
            object? enchantData = null;

            // Try known enchant property names
            foreach (var name in new[] { "Enchantment", "Enchant", "_enchant", "EnchantData",
                "EnchantComponent", "CurrentEnchant", "AppliedEnchant" })
            {
                try
                {
                    var prop = cardType.GetProperty(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (prop != null)
                    {
                        var val = prop.GetValue(card);
                        if (val != null)
                        {
                            enchantData = val;
                            break;
                        }
                    }
                }
                catch { }
            }

            if (enchantData == null) return;

            // Read enchant multiplier/type
            var enchantType = enchantData.GetType();
            double damageMult = 1.0;
            double blockMult = 1.0;
            int bonusDamage = 0;
            int bonusBlock = 0;

            // Try reading damage/block multiplier from enchant data
            foreach (var propName in new[] { "DamageMultiplier", "DamageMult", "DmgMult",
                "DamageBonus", "BonusDamage", "ExtraDamage", "BlockMultiplier", "BlockMult",
                "BlkMult", "BlockBonus", "BonusBlock", "ExtraBlock" })
            {
                try
                {
                    var prop = enchantType.GetProperty(propName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (prop != null)
                    {
                        var val = prop.GetValue(enchantData);
                        if (val != null)
                        {
                            double dv = Convert.ToDouble(val);
                            if (dv > 0)
                            {
                                if (propName.ToLower().Contains("multiplier") || propName.ToLower().EndsWith("mult"))
                                {
                                    if (propName.ToLower().StartsWith("dmg") || propName.ToLower().Contains("damage"))
                                        damageMult = dv;
                                    else
                                        blockMult = dv;
                                }
                                else
                                {
                                    if (propName.ToLower().StartsWith("dmg") || propName.ToLower().Contains("damage"))
                                        bonusDamage = (int)dv;
                                    else
                                        bonusBlock = (int)dv;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // Also try reading the enchant ID/name to determine effect
            string? enchantId = null;
            try
            {
                foreach (var propName in new[] { "Id", "Name", "EnchantId", "EnchantName", "Type" })
                {
                    try
                    {
                        var prop = enchantType.GetProperty(propName,
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (prop != null)
                        {
                            var val = prop.GetValue(enchantData);
                            if (val != null) { enchantId = val.ToString(); break; }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // If we found enchant data, apply it to base values
            if (damageMult > 1.0 || blockMult > 1.0 || bonusDamage > 0 || bonusBlock > 0
                || enchantId != null)
            {
                // Get base values from hardcoded table
                var baseFx = new CardEffects();
                FallbackEstimate(card, ref baseFx);

                fx.BaseDamage = Math.Max(fx.BaseDamage,
                    (int)(baseFx.BaseDamage * damageMult) + bonusDamage);
                fx.BaseBlock = Math.Max(fx.BaseBlock,
                    (int)(baseFx.BaseBlock * blockMult) + bonusBlock);
                fx.DebugInfo = $"enchant: dmg={fx.BaseDamage} blk={fx.BaseBlock} mult={damageMult}x enchant={enchantId ?? "?"}";
                enchantFound = true;

                MainFile.Logger.Info(
                    $"[CardEffectReader] Enchant detected: {enchantId ?? "?"} " +
                    $"dmgMult={damageMult}x blockMult={blockMult}x " +
                    $"→ dmg={fx.BaseDamage} blk={fx.BaseBlock}");
            }
        }
        catch { /* non-critical */ }
    }

    /// <summary>
    /// Diagnostic: compare reflection values against hardcoded fallback.
    /// Logs warnings when they differ significantly, but does NOT override
    /// — we trust the game engine's DynamicVar for STS2-specific values.
    /// </summary>
    // Throttle diagnostic logs — each card ID only logs once per run
    private static readonly System.Collections.Generic.HashSet<string> _diagnosticLogged = new();
    private static readonly System.Collections.Generic.HashSet<string> _fallbackLogged = new();

    private static void ValidateAgainstFallback(string idUpper, CardModel card, ref CardEffects fx)
    {
        var fallback = new CardEffects();
        FallbackEstimate(card, ref fallback);

        bool firstTimeForCard = !_diagnosticLogged.Contains(idUpper);

        // Log discrepancies for diagnostic purposes (don't override)
        // IMPORTANT: Only log once per card ID — this method is called
        // thousands of times per combat turn (every DFS state evaluation),
        // and repeated logging causes 10-20s delays.
        if (firstTimeForCard && fallback.BaseDamage > 0)
        {
            int diff = Math.Abs(fx.BaseDamage - fallback.BaseDamage);
            if (diff >= 2 && fx.BaseDamage != fallback.BaseDamage)
            {
                MainFile.Logger.Info(
                    $"[CardEffectReader] DIAGNOSTIC {idUpper} damage mismatch: " +
                    $"reflection={fx.BaseDamage} fallback={fallback.BaseDamage} " +
                    $"(keeping reflection)");
            }
        }
        if (firstTimeForCard && fallback.BaseBlock > 0)
        {
            int diff = Math.Abs(fx.BaseBlock - fallback.BaseBlock);
            if (diff >= 2 && fx.BaseBlock != fallback.BaseBlock)
            {
                MainFile.Logger.Info(
                    $"[CardEffectReader] DIAGNOSTIC {idUpper} block mismatch: " +
                    $"reflection={fx.BaseBlock} fallback={fallback.BaseBlock} " +
                    $"(keeping reflection)");
            }
        }

        _diagnosticLogged.Add(idUpper);
        // Still fill in status effect flags that reflection missed
        if (fallback.VulnerableStacks > 0 && fx.VulnerableStacks == 0)
            fx.VulnerableStacks = fallback.VulnerableStacks;
        if (fallback.WeakStacks > 0 && fx.WeakStacks == 0)
            fx.WeakStacks = fallback.WeakStacks;
        if (fallback.PoisonStacks > 0 && fx.PoisonStacks == 0)
            fx.PoisonStacks = fallback.PoisonStacks;
        if (fallback.StrengthAmount > 0 && fx.StrengthAmount == 0)
            fx.StrengthAmount = fallback.StrengthAmount;
        if (fallback.HpCost > 0 && fx.HpCost == 0)
            fx.HpCost = fallback.HpCost;
        if (fallback.EnergyGain > 0 && fx.EnergyGain == 0)
            fx.EnergyGain = fallback.EnergyGain;

        fx.DebugInfo = $"reflection: dmg={fx.BaseDamage} blk={fx.BaseBlock}";
    }

    /// <summary>
    /// Pre-compute card effects for all cards in hand. This avoids calling
    /// reflection separately for each card during search branching.
    /// Returns a lookup from CardModel to its effects.
    /// </summary>
    public static System.Collections.Generic.Dictionary<CardModel, CardEffects> Precompute(
        System.Collections.Generic.IEnumerable<CardModel> hand,
        Creature? exampleTarget = null)
    {
        var results = new System.Collections.Generic.Dictionary<CardModel, CardEffects>();
        foreach (var card in hand)
        {
            if (!results.ContainsKey(card))
                results[card] = ReadEffects(card, exampleTarget);
        }
        return results;
    }

    // ── Reflection helpers ────────────────────────────────────────────────────

    private static void TryInitReflection()
    {
        if (_initAttempted) return;
        _initAttempted = true;

        try
        {
            // Use inspector-discovered property name, or try common names
            var propName = CardModelInspector.DynamicVarSetPropertyName;
            if (propName != null)
            {
                _dvSetProp = typeof(CardModel).GetProperty(propName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_dvSetProp == null)
            {
                // Search CardModel hierarchy
                var t = typeof(CardModel);
                while (t != null)
                {
                    foreach (var name in new[] { "DynamicVars", "Vars", "VarSet", "DynamicVarSet", "PreviewVars" })
                    {
                        _dvSetProp = t.GetProperty(name,
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (_dvSetProp != null) break;
                    }
                    if (_dvSetProp != null) break;
                    t = t.BaseType;
                }
            }

            _updatePreviewMethod = typeof(CardModel).GetMethod("UpdateDynamicVarPreview",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (CardModelInspector.DynamicVarType != null)
            {
                var previewName = CardModelInspector.PreviewValuePropertyName ?? "PreviewValue";
                _previewValueProp = CardModelInspector.DynamicVarType.GetProperty(previewName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_previewValueProp == null)
                {
                    // Try BaseValue as fallback
                    _previewValueProp = CardModelInspector.DynamicVarType.GetProperty("BaseValue",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
            }
        }
        catch { /* stay in fallback mode */ }
    }

    private static void ReadFromVarSet(object varSet, ref CardEffects fx)
    {
        if (varSet == null) return;

        try
        {
            var dvType = CardModelInspector.DynamicVarType;
            var setType = varSet.GetType();
            int highestDmg = 0, highestBlk = 0;

            // Check fields
            foreach (var f in setType.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (dvType != null && dvType.IsAssignableFrom(f.FieldType))
                {
                    var dv = f.GetValue(varSet);
                    if (dv == null) continue;

                    var name = f.Name.ToLowerInvariant();
                    int val = ReadDynamicVarValue(dv);

                    if (IsDamageField(name))
                        highestDmg = Math.Max(highestDmg, val);
                    else if (IsBlockField(name))
                        highestBlk = Math.Max(highestBlk, val);
                }
            }

            // Check properties
            foreach (var p in setType.GetProperties(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (dvType != null && dvType.IsAssignableFrom(p.PropertyType))
                {
                    try
                    {
                        var dv = p.GetValue(varSet);
                        if (dv == null) continue;

                        var name = p.Name.ToLowerInvariant();
                        int val = ReadDynamicVarValue(dv);

                        if (IsDamageField(name))
                            highestDmg = Math.Max(highestDmg, val);
                        else if (IsBlockField(name))
                            highestBlk = Math.Max(highestBlk, val);
                    }
                    catch { /* skip properties that throw */ }
                }
            }

            fx.BaseDamage = highestDmg;
            fx.BaseBlock = highestBlk;
        }
        catch { /* fall through */ }
    }

    private static int ReadDynamicVarValue(object dv)
    {
        try
        {
            // Try PreviewValue first (should include enchants after UpdateDynamicVarPreview)
            var prop = _previewValueProp;
            if (prop != null)
            {
                var val = prop.GetValue(dv);
                if (val != null)
                {
                    int v = Convert.ToInt32(val);
                    if (v > 0) return v;
                }
            }

            // Fallback: try EnchantedValue (enchant-specific damage)
            try
            {
                var enchProp = dv.GetType().GetProperty("EnchantedValue",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (enchProp != null)
                {
                    var val = enchProp.GetValue(dv);
                    if (val != null)
                    {
                        int v = Convert.ToInt32(val);
                        if (v > 0) return v;
                    }
                }
            }
            catch { }

            // Fallback: try Value property
            try
            {
                var valProp = dv.GetType().GetProperty("Value",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (valProp != null)
                {
                    var val = valProp.GetValue(dv);
                    if (val != null)
                    {
                        int v = Convert.ToInt32(val);
                        if (v > 0) return v;
                    }
                }
            }
            catch { }
        }
        catch { }
        return 0;
    }

    private static bool IsDamageField(string name)
    {
        return name.Contains("damage") || name.Contains("dmg") || name == "d";
    }

    private static bool IsBlockField(string name)
    {
        return (name.Contains("block") || name.Contains("blk") || name == "b")
               && !IsDamageField(name);
    }

    // ── Fallback estimates (same as old solver, but kept as last resort) ──────

    private static void FallbackEstimate(CardModel card, ref CardEffects fx)
    {
        var id = card.Id.Entry.ToUpperInvariant();

        switch (id)
        {
            // ═══════════════════ IRONCLAD ═══════════════════
            case "STRIKE_IRONCLAD": fx.BaseDamage = 6; break;
            case "DEFEND_IRONCLAD": fx.BaseBlock = 5; break;
            case "BASH": fx.BaseDamage = 8; fx.VulnerableStacks = 2; break;
            case "ANGER": fx.BaseDamage = 6; break;
            case "ARMAMENTS": fx.BaseBlock = 5; break;
            case "BLOOD_WALL": fx.BaseBlock = 16; break;
            case "BODY_SLAM": fx.BaseDamage = 0; break;
            case "HEADBUTT": fx.BaseDamage = 9; break;
            case "IRON_WAVE": fx.BaseDamage = 5; fx.BaseBlock = 5; break;
            case "POMMEL_STRIKE": fx.BaseDamage = 9; break;
            case "SHRUG_IT_OFF": fx.BaseBlock = 8; break;
            case "THUNDERCLAP": fx.BaseDamage = 4; fx.IsAoe = true; fx.VulnerableStacks = 1; break;
            case "TRUE_GRIT": fx.BaseBlock = 7; break;
            case "TWIN_STRIKE": fx.BaseDamage = 10; break;
            case "UPPERCUT": fx.BaseDamage = 13; fx.VulnerableStacks = 1; fx.WeakStacks = 1; break;
            case "HEMOKINESIS": fx.BaseDamage = 14; fx.HpCost = 3; break; // costs HP to play
            case "BLUDGEON": fx.BaseDamage = 32; break;
            case "FEED": fx.BaseDamage = 10; break;
            case "IMPERVIOUS": fx.BaseBlock = 30; break;
            case "WHIRLWIND": fx.BaseDamage = 5; fx.IsAoe = true; fx.IsXCost = true; break;
            case "FLAME_BARRIER": fx.BaseBlock = 12; break;
            case "TAUNT": fx.BaseBlock = 7; fx.VulnerableStacks = 1; break; // block + vulnerable on enemy
            case "SNAKEBITE": fx.BaseDamage = 7; fx.PoisonStacks = 2; break; // 1-cost attack + poison
            case "PECK": fx.BaseDamage = 4; break; // low-cost attack
            case "FLICK_FLACK": fx.BaseDamage = 3; fx.BaseBlock = 3; break; // dual attack+block
            case "TREMBLE": fx.VulnerableStacks = 3; break;
            case "INFLAME": fx.GrantsStrength = true; fx.StrengthAmount = 2; break;
            case "DEMON_FORM": fx.GrantsStrength = true; fx.StrengthAmount = 2; break;
            case "FLEX": fx.GrantsStrength = true; fx.StrengthAmount = 2; break;
            case "SPOT_WEAKNESS": fx.GrantsStrength = true; fx.StrengthAmount = 3; break;
            case "BREAKTHROUGH": fx.BaseDamage = 9; fx.IsAoe = true; break;
            case "CLOTHESLINE": fx.BaseDamage = 12; fx.WeakStacks = 2; break;
            case "DROP_KICK": fx.BaseDamage = 5; break;
            case "CARNAGE": fx.BaseDamage = 20; break;
            case "IMMOLATE": fx.BaseDamage = 20; fx.IsAoe = true; break;
            case "RECKLESS_CHARGE": fx.BaseDamage = 7; fx.IsAoe = true; break;
            case "SEARING_BLOW": fx.BaseDamage = 12; break;
            case "HEAVY_BLADE": fx.BaseDamage = 14; break;
            case "SWORD_BOOMERANG": fx.BaseDamage = 9; break;
            case "PERFECTED_STRIKE": fx.BaseDamage = 10; break;
            case "FIEND_FIRE": fx.BaseDamage = 7; break;
            case "RAMPAGE": fx.BaseDamage = 9; break;
            case "SEVER_SOUL": fx.BaseDamage = 16; break;
            case "CLASH": fx.BaseDamage = 14; break;
            case "WILD_STRIKE": fx.BaseDamage = 12; break;
            case "SHOCKWAVE": fx.VulnerableStacks = 1; fx.WeakStacks = 1; break;
            case "POWER_THROUGH": fx.BaseBlock = 15; break;
            case "SECOND_WIND": fx.BaseBlock = 5; break;
            case "GHOSTLY_ARMOR": fx.BaseBlock = 10; break;
            case "ENTRENCH": fx.BaseBlock = 8; break;
            case "COLOSSUS": fx.BaseBlock = 5; break;
            case "EVIL_EYE": fx.BaseBlock = 8; break;
            case "LIMIT_BREAK": fx.GrantsStrength = true; fx.StrengthAmount = 0; break;
            // Energy/draw (Ironclad)
            case "OFFERING": fx.EnergyGain = 2; fx.HpCost = 6; break; // +2E, draw 3, lose 6 HP
            case "BLOODLETTING": fx.EnergyGain = 2; fx.HpCost = 3; break; // +2 energy, lose 3 HP
            case "BATTLE_TRANCE": fx.BaseBlock = 0; break;
            case "BURNING_PACT": fx.BaseBlock = 0; break;
            case "WARCRY": fx.BaseBlock = 0; break;
            case "CLEAVE": fx.BaseDamage = 8; fx.IsAoe = true; break;
            case "PUMMEL": fx.BaseDamage = 8; break; // 2x4
            case "HAVOC": fx.BaseDamage = 4; fx.BaseBlock = 0; break; // random top-deck play — expected value ~4 dmg
            case "SENTINEL": fx.BaseBlock = 5; break;
            case "EXHUME": fx.BaseBlock = 0; break;
            case "DISARM": fx.BaseBlock = 0; fx.WeakStacks = 2; break; // enemy str debuff
            case "PILLAGE": fx.BaseDamage = 7; break;
            case "SPITE": fx.BaseDamage = 6; break;
            case "STOKE": fx.BaseDamage = 10; break;
            case "POKE": fx.BaseDamage = 6; break; // 1-cost attack
            case "ASHEN_STRIKE": fx.BaseDamage = 12; break; // 2-cost big attack
            case "THE_QUEEN_CARD_UNFINISHED_CALAMITY": fx.BaseDamage = 16; fx.IsAoe = true; break;
            case "PROLONG": fx.BaseBlock = 7; break;
            case "EXPECT_A_FIGHT": fx.BaseBlock = 5; break; // draw card
            case "SLIMED": fx.BaseDamage = 3; break; // 1-cost status clear
            case "CINDER": fx.BaseDamage = 5; break; // 0-cost attack
            case "MOLTEN_FIST": fx.BaseDamage = 14; break; // 2-cost big attack
            case "POOR_SLEEP": fx.BaseDamage = 0; fx.BaseBlock = 0; break; // unplayable curse — clogs hand
            case "SPOILS_MAP": fx.BaseDamage = 0; fx.BaseBlock = 0; break; // utility card (draw/effect)
            case "DISCOVERY": fx.BaseDamage = 0; fx.BaseBlock = 0; break; // utility/exploration card
            case "JUGGLING": fx.BaseDamage = 6; break; // 1-cost attack
            case "STONE_ARMOR": fx.BaseBlock = 0; break; // power card (metallicize-like)
            case "RUPTURE": fx.GrantsStrength = true; fx.StrengthAmount = 0; break; // power: +1 Str when HP lost from cards
            case "FEEL_NO_PAIN": fx.BaseBlock = 0; break; // power: block when exhaust
            case "FIGHT_ME": fx.BaseBlock = 3; break; // skill/block
            case "CLUMSY": fx.BaseDamage = 0; fx.BaseBlock = 0; break; // curse, unplayable
            case "DISMANTLE": fx.BaseDamage = 5; break; // 1-cost attack
            case "FASTEN": fx.BaseBlock = 5; break; // skill — block
            case "DOUBT": fx.BaseDamage = 0; fx.BaseBlock = 0; break; // curse — unplayable
            case "GREED": fx.BaseDamage = 7; break; // attack — gains gold
            case "THRASH": fx.BaseDamage = 8; break; // 1-cost attack
            case "BULLY": fx.BaseDamage = 6; fx.WeakStacks = 1; break; // attack with weak debuff
            case "DARK_SHACKLES": fx.BaseBlock = 0; fx.WeakStacks = 2; break; // 0-cost: enemy str debuff, exhaust

            // ═══════════════════ NEW STS2 CARDS (discovered via reflection) ═══════════════════
            case "CASCADE": fx.BaseDamage = 8; fx.IsXCost = true; break; // X-cost attack — deals damage per energy, NOT block
            case "SETUP_STRIKE": fx.BaseDamage = 7; break; // 1-cost attack
            case "GUILTY": fx.BaseDamage = 7; fx.IsXCost = true; break; // X-cost attack (7 per energy)

            // ═══════════════════ SILENT ═══════════════════
            // Basics
            case "STRIKE_SILENT": fx.BaseDamage = 6; break;
            case "DEFEND_SILENT": fx.BaseBlock = 5; break;
            case "NEUTRALIZE": fx.BaseDamage = 3; fx.WeakStacks = 1; break;
            case "SURVIVOR": fx.BaseBlock = 8; break;
            // Poison
            case "DEADLY_POISON": fx.PoisonStacks = 5; break; // 5 poison
            case "BOUNCING_FLASK": fx.PoisonStacks = 3; break; // 3 poison × 3 bounces
            case "CORROSIVE_WAVE": fx.PoisonStacks = 4; fx.IsAoe = true; break; // 4 poison AOE
            case "CATALYST": fx.BaseDamage = 0; break; // doubles poison, no direct dmg
            // Attacks
            case "DAGGER_THROW": fx.BaseDamage = 10; break;
            case "SUCKER_PUNCH": fx.BaseDamage = 7; fx.WeakStacks = 1; break;
            case "BACKSTAB": fx.BaseDamage = 11; break;
            case "EVISCERATE": fx.BaseDamage = 18; break;
            case "FLEA": fx.BaseDamage = 4; break; // 0-cost
            case "DIE_DIE_DIE": fx.BaseDamage = 13; fx.IsAoe = true; break;
            case "STORM_OF_STEEL": fx.BaseDamage = 4; break; // creates shivs
            // Skills / Block
            case "BACKFLIP": fx.BaseBlock = 5; break;
            case "BLUR": fx.BaseBlock = 5; break;
            case "DODGE_AND_ROLL": fx.BaseBlock = 4; break;
            case "CLOAK_AND_DAGGER": fx.BaseBlock = 6; fx.BaseDamage = 4; break; // block + shiv
            case "DEFLECT": fx.BaseBlock = 4; break; // 0-cost
            case "LEG_SWEEP": fx.BaseBlock = 0; fx.WeakStacks = 2; break;
            case "MALAISE": fx.BaseBlock = 0; fx.WeakStacks = 1; break; // X-cost
            case "PIERCING_WAIL": fx.BaseBlock = 0; break; // AOE strength debuff
            // Powers
            case "AFTERIMAGE": fx.BaseBlock = 0; break;
            case "INFINITE_BLADES": fx.BaseBlock = 0; break;
            case "NOXIOUS_FUMES": fx.PoisonStacks = 2; fx.IsAoe = true; break; // 2 poison AOE per turn
            case "ENVENOM": fx.PoisonStacks = 1; break; // 1 poison per attack played
            case "TOOLS_OF_THE_TRADE": fx.BaseBlock = 0; break;
            case "WRAITH_FORM": fx.BaseBlock = 0; break;
            case "BURST": fx.BaseBlock = 0; break;
            case "NIGHTMARE": fx.BaseBlock = 0; break;
            case "WELL_LAID_PLANS": fx.BaseBlock = 0; break;
            case "THOUSAND_CUTS": fx.BaseBlock = 0; break;
            case "ACCURACY": fx.BaseBlock = 0; break;
            case "CALCULATED_GAMBLE": fx.BaseBlock = 0; break;
            // Energy
            case "ADRENALINE": fx.EnergyGain = 1; break; // +1 energy, draw 2
            case "EXPERTISE": fx.BaseBlock = 0; break;
            case "PREPARED": fx.BaseBlock = 0; break;
            case "ACROBATICS": fx.BaseBlock = 0; break;
            case "TACTICIAN": fx.EnergyGain = 1; break;
            case "REFLEX": fx.BaseBlock = 0; break;
            case "ESCAPE_PLAN": fx.BaseBlock = 3; break;
            case "FOOTWORK": fx.GrantsDexterity = true; fx.DexterityAmount = 2; break;
            // More Silent
            case "DASH": fx.BaseDamage = 10; fx.BaseBlock = 10; break;
            case "SKEWER": fx.BaseDamage = 7; fx.IsXCost = true; break; // X-cost, 7 per energy
            case "UNLOAD": fx.BaseDamage = 14; break;
            case "GLASS_KNIFE": fx.BaseDamage = 16; break;
            case "ENDLESS_AGONY": fx.BaseDamage = 4; break;
            case "HEEL_HOOK": fx.BaseDamage = 5; break;
            case "FLYING_KNEE": fx.BaseDamage = 8; break;
            case "QUICK_SLASH": fx.BaseDamage = 8; break;
            case "MASTERFUL_STAB": fx.BaseDamage = 12; break;
            case "PREDATOR": fx.BaseDamage = 15; break;
            case "SLICE": fx.BaseDamage = 5; break; // 0-cost
            case "FINISHER": fx.BaseDamage = 6; break;
            case "RIDDLE_WITH_HOLES": fx.BaseDamage = 15; break; // 3x5
            case "CORPSE_EXPLOSION": fx.BaseDamage = 6; fx.PoisonStacks = 6; break; // 6 poison + explode
            case "CRIPPLING_CLOUD": fx.BaseDamage = 4; fx.IsAoe = true; fx.WeakStacks = 2; fx.PoisonStacks = 2; break;
            case "GRAND_FINALE": fx.BaseDamage = 50; fx.IsAoe = true; break;
            case "BULLET_TIME": fx.BaseBlock = 0; break;
            case "CONCENTRATE": fx.BaseBlock = 0; break;
            case "OUTMANEUVER": fx.BaseBlock = 0; break;
            case "TERROR": fx.BaseBlock = 0; break; // applies 99 vulnerable
            case "PHANTASMAL_KILLER": fx.BaseBlock = 0; break;
            case "DOPPELGANGER": fx.BaseBlock = 0; break; // X-cost
            case "SETUP": fx.BaseBlock = 0; break;
            case "DISTRACTION": fx.BaseBlock = 0; break;
            case "CALTROPS": fx.BaseBlock = 0; break;

            // ═══════════════════ DEFECT ═══════════════════
            // Basics
            case "STRIKE_DEFECT": fx.BaseDamage = 6; break;
            case "DEFEND_DEFECT": fx.BaseBlock = 5; break;
            case "ZAP": fx.BaseDamage = 3; break; // channels lightning (passive ~3+focus/turn)
            case "DUALCAST": fx.BaseDamage = 16; fx.IsOrbEvoke = true; break; // evokes rightmost orb twice (~8×2 with Lightning)
            // Channeling
            case "BALL_LIGHTNING": fx.BaseDamage = 7; break;
            case "COLD_SNAP": fx.BaseDamage = 6; break;
            case "COOLHEADED": fx.BaseBlock = 5; break;
            case "GLACIER": fx.BaseBlock = 7; break;
            case "CHILL": fx.BaseBlock = 0; break; // channels frost for each enemy
            case "DARKNESS": fx.BaseDamage = 6; break;
            case "RAINBOW": fx.BaseBlock = 0; break;
            case "CHAOS": fx.BaseBlock = 0; break;
            // Attacks
            case "METEOR_STRIKE": fx.BaseDamage = 24; break;
            case "SUNDER": fx.BaseDamage = 24; break;
            case "HYPERBEAM": fx.BaseDamage = 26; break;
            case "ALL_FOR_ONE": fx.BaseDamage = 10; break;
            case "BARRAGE": fx.BaseDamage = 4; break;
            case "CLAW": fx.BaseDamage = 3; break;
            case "SWEEPING_BEAM": fx.BaseDamage = 6; fx.IsAoe = true; break;
            case "COMPILE_DRIVER": fx.BaseDamage = 7; break;
            case "OVERCLOCK": fx.BaseDamage = 0; break;
            case "REBOUND": fx.BaseDamage = 9; break;
            // Skills / Block
            case "CHARGE_BATTERY": fx.BaseBlock = 7; break;
            case "LEAP": fx.BaseBlock = 9; break;
            case "BOOT_SEQUENCE": fx.BaseBlock = 10; break;
            case "REINFORCED_BODY": fx.BaseBlock = 7; break; // X-cost
            case "GENETIC_ALGORITHM": fx.BaseBlock = 1; break;
            case "FORCE_FIELD": fx.BaseBlock = 12; break;
            case "HOLOGRAM": fx.BaseBlock = 3; break;
            case "SKIM": fx.BaseBlock = 0; break;
            // Powers
            case "ECHO_FORM": fx.BaseBlock = 0; break;
            case "CREATIVE_AI": fx.BaseBlock = 0; break;
            case "STORM": fx.BaseBlock = 0; break;
            case "STATIC_DISCHARGE": fx.BaseBlock = 0; break;
            case "HEATSINKS": fx.BaseBlock = 0; break;
            case "MACHINE_LEARNING": fx.BaseBlock = 0; break;
            case "BUFFER": fx.BaseBlock = 0; break;
            case "SELF_REPAIR": fx.BaseBlock = 0; break;
            case "LOOP": fx.BaseBlock = 0; break;
            case "CAPACITOR": fx.BaseBlock = 0; break;
            // Focus
            case "DEFRAGMENT": fx.BaseBlock = 0; break;
            case "BIASED_COGNITION": fx.BaseBlock = 0; break;
            case "CONSUME": fx.BaseBlock = 0; break;
            // Energy
            case "TURBO": fx.EnergyGain = 2; break; // +2 energy, add Void
            case "RECYCLE": fx.EnergyGain = 1; break; // exhaust for energy
            case "AGGREGATE": fx.EnergyGain = 1; break; // +1 energy per 4 deck
            case "DOUBLE_ENERGY": fx.EnergyGain = 0; break; // doubles energy (handled specially)
            // Missing Defect cards
            case "BEAM_CELL": fx.BaseDamage = 3; fx.VulnerableStacks = 1; break;
            case "DOOM_AND_GLOOM": fx.BaseDamage = 10; fx.IsAoe = true; break;
            case "STEAM_BARRIER": fx.BaseBlock = 6; break; // 0-cost, loses block per turn
            case "STREAMLINE": fx.BaseDamage = 15; break; // costs 2, gets cheaper
            case "EQUILIBRIUM": fx.BaseBlock = 13; break; // retain hand
            case "CORE_SURGE": fx.BaseDamage = 11; break; // gain 1 artifact
            case "GO_FOR_THE_EYES": fx.BaseDamage = 3; fx.WeakStacks = 1; break;
            case "MELTER": fx.BaseDamage = 10; break; // removes block
            case "FTL": fx.BaseDamage = 5; break; // draw if 0-cost
            case "SCRAPE": fx.BaseDamage = 7; break; // draw cards costing 0
            case "TEMPEST": fx.BaseDamage = 8; fx.IsXCost = true; break; // X lightning
            case "BLIZZARD": fx.BaseDamage = 6; fx.IsAoe = true; break; // +dmg per frost
            case "AUTO_SHIELDS": fx.BaseBlock = 15; break; // only if no block
            case "REPROGRAM": fx.GrantsStrength = true; fx.StrengthAmount = 1;
                               fx.GrantsDexterity = true; fx.DexterityAmount = 1; break;
            case "FUSION": fx.BaseBlock = 0; break; // channel plasma

            // ═══════════════════ NECROBINDER ═══════════════════
            case "THE_SCYTHE": fx.BaseDamage = 18; break;
            case "REAP": fx.BaseDamage = 14; fx.IsAoe = true; break;
            case "GRAVEBLAST": fx.BaseDamage = 12; break;
            case "BLIGHT_STRIKE": fx.BaseDamage = 8; break;
            case "ERADICATE": fx.BaseDamage = 20; break;
            case "SCULPTING_STRIKE": fx.BaseDamage = 9; break;
            case "SEVERANCE": fx.BaseDamage = 15; break;
            case "REAVE": fx.BaseDamage = 6; break;
            // Block
            case "BODYGUARD": fx.BaseBlock = 10; break;
            case "BONE_SHARDS": fx.BaseBlock = 7; fx.BaseDamage = 3; break;
            case "DEATHS_DOOR": fx.BaseBlock = 12; break;
            case "GRAVE_WARDEN": fx.BaseBlock = 8; break;
            case "PROTECTOR": fx.BaseBlock = 7; break;
            case "SHROUD": fx.BaseBlock = 6; break;
            case "SENTRY_MODE": fx.BaseBlock = 9; break;
            // Debuffs
            case "ENFEEBLING_TOUCH": fx.BaseDamage = 0; fx.WeakStacks = 2; break;
            case "CALCIFY": fx.BaseDamage = 5; fx.VulnerableStacks = 2; break;
            case "PUTREFY": fx.BaseDamage = 0; fx.WeakStacks = 1; break;
            case "DEBILITATE": fx.BaseDamage = 0; fx.VulnerableStacks = 1; fx.WeakStacks = 1; break;
            case "DEFILE": fx.BaseDamage = 0; break;
            // Powers
            case "REAPER_FORM": fx.BaseBlock = 0; break;
            case "NECRO_MASTERY": fx.BaseBlock = 0; break;
            case "DEATH_MARCH": fx.BaseBlock = 0; break;
            case "DEMESNE": fx.BaseBlock = 0; break;
            case "FORBIDDEN_GRIMOIRE": fx.BaseBlock = 0; break;
            case "EIDOLON": fx.BaseBlock = 0; break;
            case "END_OF_DAYS": fx.BaseBlock = 0; break;
            case "LEGION_OF_BONE": fx.BaseBlock = 0; break;
            case "SPIRIT_OF_ASH": fx.BaseBlock = 0; break;
            case "UNLEASH": fx.BaseBlock = 0; break;
            // Energy / Stars
            case "BORROWED_TIME": fx.BaseBlock = 0; break;
            case "DRAIN_POWER": fx.BaseBlock = 0; break;
            case "SOUL_STORM": fx.BaseBlock = 0; break;
            case "PAGESTORM": fx.BaseBlock = 0; break;
            // Draw
            case "DREDGE": fx.BaseDamage = 5; break;
            case "FETCH": fx.BaseBlock = 0; break;
            case "PARSE": fx.BaseBlock = 0; break;
            case "GLIMPSE_BEYOND": fx.BaseBlock = 0; break;

            // ═══════════════════ REGENT ═══════════════════
            case "STRIKE_REGENT": fx.BaseDamage = 6; break;
            case "DEFEND_REGENT": fx.BaseBlock = 5; break;
            // Regent attacks
            case "AWE": fx.BaseDamage = 10; break;
            case "BLESSING_OF_HUNTING": fx.BaseDamage = 8; fx.IsAoe = true; break;
            case "CHAMPIONS_BLOW": fx.BaseDamage = 15; break;
            case "CLEAVING_STRIKE": fx.BaseDamage = 8; fx.IsAoe = true; break;
            case "DIVINE_LANCE": fx.BaseDamage = 12; break;
            case "HOLY_BLADE": fx.BaseDamage = 9; break;
            case "OATH": fx.BaseDamage = 14; break;
            case "PURIFY": fx.BaseDamage = 7; break;
            case "RECLAMATION": fx.BaseDamage = 10; break;
            case "RETRIBUTION": fx.BaseDamage = 6; fx.IsAoe = true; break;
            case "SMITE": fx.BaseDamage = 11; break;
            case "ZEALOUS_STRIKE": fx.BaseDamage = 8; break;
            // Regent skills/block
            case "ABSOLVE": fx.BaseBlock = 8; break;
            case "BLESSED_SHIELD": fx.BaseBlock = 7; break;
            case "CONFESS": fx.BaseBlock = 10; break;
            case "DIVINE_PROTECTION": fx.BaseBlock = 12; break;
            case "GRACE": fx.BaseBlock = 6; break;
            case "HOLY_ARMOR": fx.BaseBlock = 9; break;
            case "PENANCE": fx.BaseBlock = 5; break;
            case "SANCTIFY": fx.BaseBlock = 6; break;
            // Regent powers
            case "APOTHEOSIS": fx.BaseBlock = 0; break;
            case "DIVINE_AEGIS": fx.BaseBlock = 0; break;
            case "HALLOWED_GROUND": fx.BaseBlock = 0; break;
            case "MARTYRDOM": fx.BaseBlock = 0; break;
            case "SACRED_OATH": fx.BaseBlock = 0; break;

            // ═══════════════════ COLORLESS / COMMON ═══════════════════
            case "STRIKE": fx.BaseDamage = 6; break;
            case "DEFEND": fx.BaseBlock = 5; break;

            default:
                if (card.Type == CardType.Attack) fx.BaseDamage = 6;
                if (card.Type == CardType.Skill) fx.BaseBlock = 5;
                break;
        }

        fx.DebugInfo = $"fallback: dmg={fx.BaseDamage} blk={fx.BaseBlock}";

        // Upgrade bonus (conservative 15% — many upgrades change mechanics, not raw damage/block)
        if (card.IsUpgraded)
        {
            fx.BaseDamage = (int)(fx.BaseDamage * 1.15);
            fx.BaseBlock = (int)(fx.BaseBlock * 1.15);
            if (fx.PoisonStacks > 0) fx.PoisonStacks += 1;
            if (fx.VulnerableStacks > 0) fx.VulnerableStacks += 1;
            if (fx.WeakStacks > 0) fx.WeakStacks += 1;
            if (fx.StrengthAmount > 0) fx.StrengthAmount += 1;
        }
    }
}
