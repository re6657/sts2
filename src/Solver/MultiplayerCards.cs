using System;
using System.Collections.Generic;

namespace TokenSpire2.Solver;

/// <summary>
/// Multiplayer-only card database and helpers.
/// Source: sts2.dll v0.107.1 — 21 cards with CardMultiplayerConstraint.MultiplayerOnly.
///
/// Target type categories:
///   AnyAlly   — must target the human player (BelieveInYou, Coordinate, DemonicShield,
///               Ignition, Intercept, Largesse, Lift, Mimic)
///   AllAllies — auto-targets all allies (EnergySurge, GlimpseBeyond, HuddleUp,
///               LegionOfBone, Rally)
///   Self      — self-target, benefits allies passively (BeaconOfHope, HammerTime,
///               Sneaky, Tank)
///   AnyEnemy  — normal enemy targeting (Flanking, GangUp, Knockdown, TagTeam)
/// </summary>
public static class MultiplayerCards
{
    /// <summary>All 21 multiplayer-only card IDs (UPPERCASE).</summary>
    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── MULTIPLAYER_GENERIC (16 cards) ──
        "BEACON_OF_HOPE",   // Self — allies gain Block when you do
        "BELIEVE_IN_YOU",   // AnyAlly — grant 2 Energy to ally
        "COORDINATE",        // AnyAlly — grant 5 Strength to ally
        "ENERGY_SURGE",      // AllAllies — grant 2 Energy to all allies
        "FLANKING",          // AnyEnemy — apply Flanking debuff
        "GANG_UP",           // AnyEnemy — damage + bonus per ally hit
        "HAMMER_TIME",       // Self — upgrade cards during ally turns
        "HUDDLE_UP",         // AllAllies — all allies draw 2
        "INTERCEPT",         // AnyAlly — gain Block, grant Covered to ally
        "KNOCKDOWN",         // AnyEnemy — high damage + reduce enemy output
        "LARGESSE",          // AnyAlly — generate random card for ally
        "LIFT",              // AnyAlly — grant 11 Block to ally
        "MIMIC",             // AnyAlly — gain Block equal to ally's Block
        "RALLY",             // AllAllies — grant 12 Block to all allies
        "TAG_TEAM",          // AnyEnemy — damage + mark enemy
        "TANK",              // Self — redirect ally damage to yourself

        // ── IRONCLAD (1 card) ──
        "DEMONIC_SHIELD",    // AnyAlly — lose HP, grant 2x Block to ally

        // ── NECROBINDER (2 cards) ──
        "GLIMPSE_BEYOND",    // AllAllies — all allies add Soul cards
        "LEGION_OF_BONE",    // AllAllies — summon skeleton for all allies

        // ── DEFECT (1 card) ──
        "IGNITION",          // AnyAlly — channel Plasma orb for ally

        // ── SILENT (1 card) ──
        "SNEAKY",            // Self — gain Sly (Block when any ally attacked)
    };

    /// <summary>MP cards that target a specific ally — must target the human player.</summary>
    public static readonly HashSet<string> AnyAllyTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "BELIEVE_IN_YOU", "COORDINATE", "DEMONIC_SHIELD", "IGNITION",
        "INTERCEPT", "LARGESSE", "LIFT", "MIMIC",
    };

    /// <summary>MP cards that target all allies — no manual targeting needed.</summary>
    public static readonly HashSet<string> AllAlliesTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "ENERGY_SURGE", "GLIMPSE_BEYOND", "HUDDLE_UP", "LEGION_OF_BONE", "RALLY",
    };

    /// <summary>MP cards that target self — passive ally benefits.</summary>
    public static readonly HashSet<string> SelfTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "BEACON_OF_HOPE", "HAMMER_TIME", "SNEAKY", "TANK",
    };

    /// <summary>MP cards that target enemies — normal combat targeting.</summary>
    public static readonly HashSet<string> AnyEnemyTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "FLANKING", "GANG_UP", "KNOCKDOWN", "TAG_TEAM",
    };

    // ── Helpers ────────────────────────────────────────────────────────────

    public static bool IsMultiplayerCard(string? cardId) =>
        cardId != null && All.Contains(cardId);

    public static bool NeedsAllyTarget(string? cardId) =>
        cardId != null && AnyAllyTargets.Contains(cardId);

    public static bool IsAllAllies(string? cardId) =>
        cardId != null && AllAlliesTargets.Contains(cardId);

    public static bool IsSelfTargetMP(string? cardId) =>
        cardId != null && SelfTargets.Contains(cardId);

    /// <summary>
    /// Score bonus for picking MP cards in card rewards.
    /// High enough to virtually guarantee they're chosen over non-MP cards.
    /// </summary>
    public const double PickBonus = 300.0;

    /// <summary>
    /// Score bonus for playing MP cards in combat.
    /// Added to the solver's state evaluation when an MP card is played.
    /// </summary>
    public const double PlayBonus = 200.0;
}
