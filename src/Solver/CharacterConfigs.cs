using System.Collections.Generic;

namespace TokenSpire2.Solver;

/// <summary>
/// Character-specific configuration for the combat solver.
/// Each character has different card priorities, evaluation weights, and
/// special mechanics that affect how the solver evaluates game states.
/// </summary>
public class CharacterConfig
{
    public string CharacterId = "";
    public string DisplayName = "";

    /// <summary>Card priority tiers — lower = explore first in DFS.</summary>
    public Dictionary<string, int> CardPriorities = new();

    /// <summary>Evaluation weight multipliers for state scoring.</summary>
    public double KillWeight = 1.0;
    public double DamageWeight = 1.0;
    public double BlockWeight = 1.0;
    public double HealthPenaltyWeight = 1.0;
    public double VulnerableWeight = 1.0;
    public double WeakWeight = 1.0;
    public double StrengthWeight = 1.0;
    public double PowerWeight = 1.0;
    public double OrbValueWeight = 0.0;       // Defect only
    public double PoisonDamageWeight = 0.0;   // Silent only
    public double StarConservationWeight = 0.0; // Necrobinder only
    public double DexterityWeight = 0.0;      // Silent only

    /// <summary>Class-specific special behavior flags.</summary>
    public bool UsesOrbs = false;       // Defect
    public bool UsesPoison = false;     // Silent
    public bool UsesStars = false;      // Necrobinder
    public bool HasDexterity = false;   // Silent

    /// <summary>Maximum copies of a card by ID. Default unlimited (no cap).</summary>
    public Dictionary<string, int> MaxCopies = new();

    /// <summary>Per-turn value estimates for powers (used in future-value scoring).</summary>
    public static readonly Dictionary<string, double> PowerPerTurnValues = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Ironclad ──────────────────────────────────────────────────
        ["DEMON_FORM"] = 30.0,      // +2 str/turn × 1.5 atk/turn × 10 dmg/pt
        ["DARK_EMBRACE"] = 27.0,    // ~1.5 draw/turn × 18 draw-pts
        ["RUPTURE"] = 20.0,         // +1 str/turn from self-damage
        ["CORRUPTION"] = 40.0,      // Makes all skills free — massive energy value
        ["FEEL_NO_PAIN"] = 18.0,    // Block from exhausts
        ["JUGGERNAUT"] = 15.0,      // Block per attack
        ["BARRICADE"] = 20.0,       // Block retention
        ["FIRE_BREATHING"] = 12.0,  // AOE from status draw
        ["EVOLVE"] = 15.0,          // Draw from statuses
        ["BERSERK"] = 22.0,         // +1 energy/turn
        ["METALLICIZE"] = 10.0,     // +3 block/turn
        ["COMBUST"] = 15.0,         // AOE + self-damage/turn
        ["BRUTALITY"] = 18.0,       // +1 draw/turn
        ["INFLAME"] = 14.0,         // +2(3) strength — immediate scaling
        ["SPOT_WEAKNESS"] = 16.0,   // +3(4) strength if enemy attacks
        ["RAGE"] = 12.0,            // +3 block per attack played
        ["STONE_ARMOR"] = 14.0,     // +4 block + metallicize effect
        ["CRIMSON_MANTLE"] = 16.0,  // Block + strength
        ["AGGRESSION"] = 14.0,      // Strength + vulnerable
        ["HELLRAISER"] = 18.0,      // Fire breathing + strength scaling
        ["STAMPEDE"] = 16.0,        // Multi-attack empowerment
        ["INFERNO"] = 17.0,         // Massive AOE scaling

        // ── Silent ────────────────────────────────────────────────────
        ["FOOTWORK"] = 21.0,        // +2 dex/turn × 1.5 blk/turn × 7 blk/pt
        ["NOXIOUS_FUMES"] = 22.0,   // +2 poison/turn × 11 dmg/pt avg
        ["AFTERIMAGE"] = 15.0,      // Block per card
        ["ACCURACY"] = 18.0,        // +4 shiv dmg × 3 shivs/turn
        ["THOUSAND_CUTS"] = 16.0,   // AOE per card
        ["INFINITE_BLADES"] = 14.0, // Free shiv/turn
        ["ENVENOM"] = 18.0,         // Poison per attack
        ["WELL_LAID_PLANS"] = 20.0, // Retain — better sequencing
        ["TOOLS_OF_THE_TRADE"] = 22.0, // Draw engine
        ["CALTROPS"] = 14.0,        // 3(5) thorns per hit
        ["ACCELERANT"] = 20.0,      // Poison no-decay + per-turn application
        ["WRAITH_FORM"] = 24.0,     // 2(3) turns intangible — game-winning
        ["PHANTASMAL_KILLER"] = 16.0, // Double attack damage next turn
        ["NIGHTMARE"] = 22.0,       // Duplicate any card — flexible value
        ["BURST"] = 15.0,           // Double next skill
        ["SETUP"] = 12.0,           // 0-cost next turn
        ["DOPPELGANGER"] = 18.0,    // X-cost, flexible clone

        // ── Defect ────────────────────────────────────────────────────
        ["DEFRAGMENT"] = 24.0,      // +1 focus × 4 orbs × 6 avg
        ["BIASED_COGNITION"] = 28.0, // +4 focus decaying
        ["CONSUME"] = 20.0,         // Focus + slot trade
        ["CAPACITOR"] = 18.0,       // Orb slots
        ["LOOP"] = 20.0,            // Double passive trigger
        ["CREATIVE_AI"] = 25.0,     // Random power/turn
        ["STORM"] = 16.0,           // Power → lightning
        ["MACHINE_LEARNING"] = 18.0, // +1 draw/turn
        ["HEATSINKS"] = 22.0,        // Power → draw
        ["ECHO_FORM"] = 35.0,        // Doubles every card
        ["ELECTRODYNAMICS"] = 24.0,  // Lightning AoE — transforms all
        ["BUFFER"] = 16.0,           // Negate 1(2) hits
        ["SELF_REPAIR"] = 12.0,      // 7(10) heal after combat
        ["STATIC_DISCHARGE"] = 14.0, // On-damage lightning channel
        ["REPROGRAM"] = 15.0,        // +1(2) Str + Dex — hybrid scaling
        ["TEMPEST"] = 18.0,          // X-cost lightning channel + evoke
        ["MULTI_CAST"] = 14.0,       // Orb activation
        ["FISSION"] = 16.0,          // Evoke all orbs for energy+draw
        ["RECYCLE"] = 12.0,          // Exhaust for energy — engine piece
        ["SUB_ROUTINE"] = 16.0,      // Power → energy engine
        ["MODDED"] = 14.0,           // Orb slot + draw

        // ── Necrobinder ───────────────────────────────────────────────
        ["NECRO_MASTERY"] = 28.0,   // Core Osty scaling engine
        ["SPIRIT_OF_ASH"] = 22.0,   // Ethereal → +4 block
        ["LETHALITY"] = 18.0,       // First attack bonus damage
        ["REAPER_FORM"] = 30.0,     // Damage doubler
        ["FRIENDSHIP"] = 26.0,      // Attack → +1 energy — S-tier engine
        ["INVOKE"] = 22.0,          // Grow Osty + energy
        ["CALCIFY"] = 20.0,         // Osty damage/block growth
        ["DEATH_MARCH"] = 18.0,     // Souls → damage engine
        ["DEMESNE"] = 14.0,         // Soul/curse synergy
        ["EIDOLON"] = 14.0,         // Summon-based value
        ["END_OF_DAYS"] = 16.0,     // Delayed massive value
        ["LEGION_OF_BONE"] = 16.0,  // Bone army scaling
        ["SOULBOUND"] = 12.0,       // Multiplayer synergy
        ["FORBIDDEN_GRIMOIRE"] = 16.0, // Curse-based scaling
        ["SOUL_STORM"] = 18.0,      // Soul generation engine
        ["PAGESTORM"] = 16.0,       // Draw engine
        ["BORROWED_TIME"] = 20.0,   // Extra turn energy

        // ── Regent ────────────────────────────────────────────────────
        ["CHILD_OF_THE_STARS"] = 24.0, // Star → block — defense core
        ["ARSENAL"] = 22.0,         // Created card → +1 Strength — scaling engine
        ["VOID_FORM"] = 30.0,       // Cost reduction — game-warping
        ["THE_SEALED_THRONE"] = 26.0, // Star cost reducer — S-tier
        ["GENESIS"] = 22.0,         // Star generation engine
        ["CHARGE"] = 18.0,          // Waste → power + draw
        ["HAMMER_TIME"] = 20.0,     // Forge engine
        ["FURNACE"] = 18.0,         // Auto-forge per turn
        ["DIVINE_AEGIS"] = 16.0,    // Block/scaling hybrid
        ["HALLOWED_GROUND"] = 14.0, // Holy zone — positional value
        ["MARTYRDOM"] = 14.0,       // Sacrifice for power
        ["SANCTIFY"] = 16.0,        // Blessing engine
        ["APOTHEOSIS"] = 18.0,      // Mass upgrade — one-time but massive
        ["SACRED_OATH"] = 14.0,     // Strength + block scaling
        ["ROYALTIES"] = 20.0,       // Pure future investment — delayed payoff
        ["BOMBARDMENT"] = 18.0,     // Delayed massive AoE
        ["MAKE_IT_SO"] = 16.0,      // Cumulative damage scaling
    };

    /// <summary>
    /// BEFORE/AFTER hard sequencing rules.
    /// BEFORE: cardA MUST be played before cardTypeB.
    /// AFTER:  cardA should be played AFTER cardTypeB.
    /// Applied in EvaluateState as bonus/penalty.
    /// </summary>
    public static readonly List<(string CardA, string CategoryB)> BeforeRules = new()
    {
        // === 0-cost energy: absolute first ===
        ("OFFERING", "*"),
        ("BLOODLETTING", "*"),
        ("ADRENALINE", "*"),
        ("TURBO", "*"),
        ("DOUBLE_ENERGY", "*"),
        ("AGGREGATE", "*"),
        ("RECYCLE", "*"),
        ("CONCENTRATE", "*"),
        ("TACTICIAN", "*"),
        ("FRIENDSHIP", "*"),         // Necrobinder: attack→energy — play early to benefit all attacks
        ("GENESIS", "*"),            // Regent: star generation — fuel everything
        ("SANCTIFY", "*"),           // Regent: energy engine

        // === Draw lock-in before everything ===
        ("BATTLE_TRANCE", "*"),
        ("EXPERTISE", "*"),
        ("CALCULATED_GAMBLE", "*"),

        // === Hand upgrade before all ===
        ("ARMAMENTS", "*"),
        ("APOTHEOSIS", "*"),         // Regent: mass upgrade

        // === Powers before their dependent card types ===
        ("DEMON_FORM", "ATTACK"),
        ("CORRUPTION", "SKILL"),
        ("DARK_EMBRACE", "SKILL"),   // exhaust skills
        ("FEEL_NO_PAIN", "SKILL"),   // exhaust skills
        ("FOOTWORK", "BLOCK"),
        ("INFLAME", "ATTACK"),
        ("ACCURACY", "ATTACK"),
        ("JUGGERNAUT", "ATTACK"),
        ("ENVENOM", "ATTACK"),       // Silent: attacks → poison
        ("THOUSAND_CUTS", "ATTACK"), // Silent: cards → AoE
        ("AFTERIMAGE", "SKILL"),     // Silent: skills → block
        ("DEFRAGMENT", "ORB"),       // Defect: focus before orbs
        ("BIASED_COGNITION", "ORB"), // Defect: focus before orbs
        ("CONSUME", "ORB"),          // Defect: focus before orbs
        ("LOOP", "ORB"),             // Defect: passive boost before channeling
        ("ELECTRODYNAMICS", "ORB"),  // Defect: lightning AoE before channeling
        ("STORM", "POWER"),          // Defect: power→lightning before other powers
        ("HEATSINKS", "POWER"),      // Defect: power→draw before other powers
        ("NECRO_MASTERY", "ATTACK"), // Necrobinder: Osty engine before attacks
        ("LETHALITY", "ATTACK"),     // Necrobinder: first-attack bonus
        ("SPIRIT_OF_ASH", "SKILL"),  // Necrobinder: ethereal→block
        ("CHILD_OF_THE_STARS", "SKILL"), // Regent: star→block
        ("ARSENAL", "ATTACK"),       // Regent: created card→strength

        // === Vulnerable/Weak before attacks ===
        ("BASH", "ATTACK"),
        ("UPPERCUT", "ATTACK"),
        ("THUNDERCLAP", "ATTACK"),
        ("SHOCKWAVE", "ATTACK"),
        ("TREMBLE", "ATTACK"),
        ("BEAM_CELL", "ATTACK"),
        ("TERROR", "ATTACK"),
        ("GO_FOR_THE_EYES", "ATTACK"),
        ("NEUTRALIZE", "ATTACK"),
        ("SUCKER_PUNCH", "ATTACK"),
        ("MALAISE", "ATTACK"),
        ("LEG_SWEEP", "ATTACK"),
        ("ENFEEBLING_TOUCH", "ATTACK"),
        ("PURIFY", "ATTACK"),
        ("INTIMIDATE", "ATTACK"),

        // === Doublers before doubled cards ===
        ("DOUBLE_TAP", "ATTACK"),
        ("BURST", "SKILL"),
        ("AMPLIFY", "POWER"),
        ("ECHO_FORM", "*"),          // Defect: doubles everything — play before all
        ("NIGHTMARE", "POWER"),      // Silent: copy a card
        ("PHANTASMAL_KILLER", "ATTACK"),
        ("REAPER_FORM", "ATTACK"),   // Necrobinder: damage doubler

        // === Tutors first (find best card) ===
        ("SEEK", "*"),
        ("HOLOGRAM", "*"),
        ("SECRET_TECHNIQUE", "*"),
        ("SECRET_WEAPON", "*"),

        // === Draw before attacks (may draw better attacks) ===
        ("POMMEL_STRIKE", "ATTACK"),
        ("SHRUG_IT_OFF", "ATTACK"),
        ("BACKFLIP", "ATTACK"),
        ("ACROBATICS", "ATTACK"),
        ("SKIM", "ATTACK"),
        ("COMPILE_DRIVER", "ATTACK"),
        ("DAGGER_THROW", "ATTACK"),
        ("ESCAPE_PLAN", "ATTACK"),
        ("OVERCLOCK", "ATTACK"),
        ("COOLHEADED", "ATTACK"),
        ("DREDGE", "ATTACK"),
        ("FETCH", "ATTACK"),
        ("PARSE", "ATTACK"),
        ("GRACE", "ATTACK"),
        ("CONFESS", "ATTACK"),

        // === Orb channeling before orb-consuming attacks ===
        ("GLACIER", "MULTI_CAST"),
        ("CHILL", "MULTI_CAST"),
        ("DARKNESS", "MULTI_CAST"),
        ("FUSION", "MULTI_CAST"),

        // === Poison before Catalyst ===
        ("DEADLY_POISON", "CATALYST"),
        ("BOUNCING_FLASK", "CATALYST"),
        ("NOXIOUS_FUMES", "CATALYST"),
        ("CORROSIVE_WAVE", "CATALYST"),
    };

    public static readonly List<(string CardA, string CategoryB)> AfterRules = new()
    {
        // ── Ironclad ──────────────────────────────────────────────────
        ("BODY_SLAM", "BLOCK"),      // After stacking block
        ("LIMIT_BREAK", "STRENGTH"), // After having strength
        ("FEED", "ATTACK"),          // After confirming lethal (approximate)
        ("REAPER", "ATTACK"),        // After damaging multiple targets
        ("ENTRENCH", "BLOCK"),       // After having block
        ("FLEX", "STRENGTH"),        // After strength is applied (before attacks)

        // ── Silent ────────────────────────────────────────────────────
        ("CATALYST", "POISON"),      // After poison is applied
        ("BURST", "SKILL"),          // After setting up skills to double
        ("NIGHTMARE", "POWER"),      // After key powers are played
        ("PHANTASMAL_KILLER", "ATTACK"), // After setting up attacks
        ("MALAISE", "ATTACK"),       // After enemy has strength (drain it)
        ("BLUR", "BLOCK"),           // After stacking some block first

        // ── Defect ────────────────────────────────────────────────────
        ("MULTI_CAST", "ORB"),       // After dark orb charged
        ("FISSION", "ORB"),          // After orbs are evoked for value
        ("TEMPEST", "ENERGY"),       // After energy generation
        ("REINFORCED_BODY", "ENERGY"), // After energy for max X-cost
        ("BLIZZARD", "FROST"),       // After frost orbs are channeled
        ("ALL_FOR_ONE", "ZERO_COST"), // After 0-cost cards are in discard

        // ── Necrobinder ───────────────────────────────────────────────
        ("THE_SCYTHE", "ATTACK"),    // After confirming lethal
        ("REAP", "ATTACK"),          // After weakening enemies
        ("ERADICATE", "ATTACK"),     // After debuffing target

        // ── Regent ────────────────────────────────────────────────────
        ("CHAMPIONS_BLOW", "BUFF"),  // After strength/buff setup
        ("BOMBARDMENT", "ATTACK"),   // Already delayed — play any time
    };

    /// <summary>Get max copies for a card. Returns int.MaxValue if no cap.</summary>
    public int GetMaxCopies(string cardId)
    {
        string upper = cardId.ToUpperInvariant();
        return MaxCopies.GetValueOrDefault(upper, int.MaxValue);
    }

    // ── Priority tiers (16 levels — extended, Optimization 4) ───────────
    // Lower number = explored FIRST in DFS search = played first in turn.
    // New philosophy: FREE_ENERGY(-3) → TUTOR(-2) → UPGRADE_HAND(-1) →
    //   DECK_THINNER(0) → SETUP(1) → ENERGY_DRAW(2) → POWER_S(3) →
    //   EXHAUST_DRAW(4) → STRENGTH_DEX(5) → VULNERABLE(6) → DOUBLER(7) →
    //   POWER(8) → DRAW_FILTER(9) → BUFF(10) → ATTACK(11) → BLOCK(12)
    //   → FLEX(13) → LAST(14)

    // ── Multiplayer card priorities — ABOVE everything else in coop mode ──
    // MP cards that directly help the human teammate MUST be played first.
    public const int PRIORITY_MULTIPLAYER_ALLY = -5;   // AnyAlly: target human player (BelieveInYou, Lift, etc.)
    public const int PRIORITY_MULTIPLAYER_TEAM = -4;   // AllAllies: buff entire team (Rally, EnergySurge, etc.)
    public const int PRIORITY_MULTIPLAYER_SELF = 0;    // Self: passive ally benefit (BeaconOfHope, Tank, etc.)
    // Enemy-debuff MP cards use PRIORITY_VULNERABLE (6) — same as other debuffs

    public const int PRIORITY_FREE_ENERGY = -3;    // 0-cost energy gain: Offering, Bloodletting, Adrenaline
    public const int PRIORITY_TUTOR = -2;           // Search/retrieve: Seek, Hologram, Secret Technique
    public const int PRIORITY_UPGRADE_HAND = -1;    // Hand upgrades: Armaments (upgraded)
    public const int PRIORITY_DECK_THINNER = 0;     // Deck thinning engines: Burning Pact, Second Wind, True Grit
    public const int PRIORITY_SETUP = 1;             // S-tier: MUST play first — Corruption, Wraith Form, Echo Form
    public const int PRIORITY_ENERGY_DRAW = 2;       // Energy + draw setup — Battle Trance, Turbo
    public const int PRIORITY_POWER_S = 3;           // S-tier powers — Demon Form, Dark Embrace, Footwork
    public const int PRIORITY_EXHAUST_DRAW = 4;      // Exhaust + draw/filter — Burning Pact, Well-Laid Plans
    public const int PRIORITY_STRENGTH_DEX = 5;      // Strength/dex scaling
    public const int PRIORITY_VULNERABLE = 6;        // Vulnerable/weak application (BEFORE attacks!)
    public const int PRIORITY_DOUBLER = 7;           // Doublers (Double Tap, Burst, Amplify)
    public const int PRIORITY_POWER = 8;             // Other powers — Feel No Pain, Barricade, etc.
    public const int PRIORITY_DRAW_FILTER = 9;       // Draw/filter — Pommel Strike, Shrug It Off
    public const int PRIORITY_BUFF = 10;             // Buff block — Armaments (un-upgraded), Rage
    public const int PRIORITY_ATTACK = 11;           // Attack cards
    public const int PRIORITY_BLOCK = 12;            // Block cards
    public const int PRIORITY_FLEX = 13;             // Flex/situational
    public const int PRIORITY_LAST = 14;             // Everything else

    // ── Factory methods ────────────────────────────────────────────────────

    public static CharacterConfig Create(string characterId) => (characterId ?? "").ToUpperInvariant() switch
    {
        "IRONCLAD" => CreateIronclad(),
        "SILENT" => CreateSilent(),
        "DEFECT" => CreateDefect(),
        "NECROBINDER" => CreateNecrobinder(),
        "REGENT" => CreateRegent(),
        _ => CreateIronclad(), // default fallback
    };

    // ═══════════════════════════════════════════════════════════════════════
    // IRONCLAD — Strength scaling, vulnerable, block, big attacks
    // ═══════════════════════════════════════════════════════════════════════

    static CharacterConfig CreateIronclad()
    {
        var c = new CharacterConfig
        {
            CharacterId = "IRONCLAD",
            DisplayName = "Ironclad",
            StrengthWeight = 1.5,
            VulnerableWeight = 1.3,
            PowerWeight = 1.2,
            KillWeight = 1.0,
            DamageWeight = 1.2,
            BlockWeight = 1.6,
            HealthPenaltyWeight = 2.5,
        };
        c.CardPriorities = new Dictionary<string, int>
        {
            // ── PRIORITY_FREE_ENERGY (-3): 0-cost energy gain — ABSOLUTE first ──
            ["OFFERING"] = PRIORITY_FREE_ENERGY,
            ["BLOODLETTING"] = PRIORITY_FREE_ENERGY,

            // ── PRIORITY_UPGRADE_HAND (-1): hand upgrades before everything ──
            ["ARMAMENTS"] = PRIORITY_UPGRADE_HAND,

            // ── PRIORITY_DECK_THINNER (0): deck thinning / free-play engines ──
            ["BURNING_PACT"] = PRIORITY_DECK_THINNER,
            ["SECOND_WIND"] = PRIORITY_DECK_THINNER,
            ["TRUE_GRIT"] = PRIORITY_DECK_THINNER,
            ["HAVOC"] = PRIORITY_DECK_THINNER, // Plays top cards for free → highest priority

            // ── PRIORITY_SETUP (1): "未来卡" S-tier — game-warping setup ──
            // These give no immediate damage/block but determine long-term combat strength
            ["CORRUPTION"] = PRIORITY_SETUP,
            ["UNMOVABLE"] = PRIORITY_SETUP,

            // ── PRIORITY_ENERGY_DRAW (2): Energy + draw setup ──
            ["BATTLE_TRANCE"] = PRIORITY_ENERGY_DRAW,
            ["FORGOTTEN_RITUAL"] = PRIORITY_ENERGY_DRAW,
            ["PYRE"] = PRIORITY_ENERGY_DRAW,
            ["EXPECT_A_FIGHT"] = PRIORITY_ENERGY_DRAW,
            ["DRUM_OF_BATTLE"] = PRIORITY_ENERGY_DRAW,

            // ── PRIORITY_POWER_S (3): S-tier powers — win conditions ──
            ["DEMON_FORM"] = PRIORITY_POWER_S,
            ["DARK_EMBRACE"] = PRIORITY_POWER_S,
            ["RUPTURE"] = PRIORITY_POWER_S,

            // ── PRIORITY_STRENGTH_DEX (5): Strength scaling ──
            ["INFLAME"] = PRIORITY_STRENGTH_DEX,
            ["SPOT_WEAKNESS"] = PRIORITY_STRENGTH_DEX,
            ["LIMIT_BREAK"] = PRIORITY_STRENGTH_DEX,
            ["FLEX"] = PRIORITY_STRENGTH_DEX,
            ["SETUP_STRIKE"] = PRIORITY_STRENGTH_DEX,
            ["BRAND"] = PRIORITY_STRENGTH_DEX,
            ["DOMINATE"] = PRIORITY_STRENGTH_DEX,
            ["FIGHT_ME"] = PRIORITY_STRENGTH_DEX,

            // ── PRIORITY_VULNERABLE (6): Vulnerable/weak BEFORE attacks ──
            ["BASH"] = PRIORITY_VULNERABLE,
            ["THUNDERCLAP"] = PRIORITY_VULNERABLE,
            ["TREMBLE"] = PRIORITY_VULNERABLE,
            ["UPPERCUT"] = PRIORITY_VULNERABLE,
            ["TAUNT"] = PRIORITY_VULNERABLE,
            ["SHOCKWAVE"] = PRIORITY_VULNERABLE,
            ["INTIMIDATE"] = PRIORITY_VULNERABLE,
            ["CLOTHESLINE"] = PRIORITY_VULNERABLE,

            // ── PRIORITY_DOUBLER (4): Doublers ──
            ["ONE_TWO_PUNCH"] = PRIORITY_DOUBLER,
            ["MOLTEN_FIST"] = PRIORITY_DOUBLER,
            ["DOUBLE_TAP"] = PRIORITY_DOUBLER,
            ["UNRELENTING"] = PRIORITY_DOUBLER,
            ["JUGGLING"] = PRIORITY_DOUBLER,

            // ── PRIORITY_POWER (5): Other powers — good but not S-tier ──
            ["FEEL_NO_PAIN"] = PRIORITY_POWER,
            ["JUGGERNAUT"] = PRIORITY_POWER,
            ["CRIMSON_MANTLE"] = PRIORITY_POWER,
            ["CRUELTY"] = PRIORITY_POWER,
            ["VICIOUS"] = PRIORITY_POWER,
            ["BARRICADE"] = PRIORITY_POWER,
            ["AGGRESSION"] = PRIORITY_POWER,
            ["HELLRAISER"] = PRIORITY_POWER,
            ["STAMPEDE"] = PRIORITY_POWER,
            ["INFERNO"] = PRIORITY_POWER,
            ["EVOLVE"] = PRIORITY_POWER,
            ["FIRE_BREATHING"] = PRIORITY_POWER,
            ["BERSERK"] = PRIORITY_POWER,
            ["METALLICIZE"] = PRIORITY_POWER,
            ["COMBUST"] = PRIORITY_POWER,
            ["STONE_ARMOR"] = PRIORITY_POWER,

            // ── PRIORITY_DRAW_FILTER (6): Draw/filter — after powers are online ──
            ["POMMEL_STRIKE"] = PRIORITY_DRAW_FILTER,
            ["SHRUG_IT_OFF"] = PRIORITY_DRAW_FILTER,
            ["PILLAGE"] = PRIORITY_DRAW_FILTER,
            ["SPITE"] = PRIORITY_LAST, // 最后打出：力量叠满后格挡收益最大
            ["STOKE"] = PRIORITY_DRAW_FILTER,
            ["WARCRY"] = PRIORITY_DRAW_FILTER,
            ["SPOILS_MAP"] = PRIORITY_DRAW_FILTER,
            ["HEADBUTT"] = PRIORITY_DRAW_FILTER,

            // ── PRIORITY_BUFF (10): Buff/block — Rage, etc. ──
            ["RAGE"] = PRIORITY_BUFF,
            ["COLOSSUS"] = PRIORITY_BUFF,
            ["FLAME_BARRIER"] = PRIORITY_BUFF,
            ["GHOSTLY_ARMOR"] = PRIORITY_BUFF,
            ["ENTRENCH"] = PRIORITY_BUFF,
            ["PROLONG"] = PRIORITY_BUFF,
            ["FLICK_FLACK"] = PRIORITY_BUFF,

            // ── PRIORITY_ATTACK (8): Attacks — last ──
            ["FASTEN"] = PRIORITY_BLOCK,
            ["GREED"] = PRIORITY_ATTACK,
            ["BLOOD_WALL"] = PRIORITY_BLOCK,
            ["POKE"] = PRIORITY_ATTACK,
            ["ASHEN_STRIKE"] = PRIORITY_ATTACK,
            ["THE_QUEEN_CARD_UNFINISHED_CALAMITY"] = PRIORITY_ATTACK,
            ["CINDER"] = PRIORITY_ATTACK,
            ["BREAKTHROUGH"] = PRIORITY_ATTACK,
            ["HEMOKINESIS"] = PRIORITY_ATTACK,
            ["DISMANTLE"] = PRIORITY_ATTACK,
            ["SNAKEBITE"] = PRIORITY_ATTACK,
            ["PECK"] = PRIORITY_ATTACK,
            ["THRASH"] = PRIORITY_ATTACK,
            ["BULLY"] = PRIORITY_ATTACK,

            // ── PRIORITY_FLEX (10): Situational utility ──
            ["PANIC_BUTTON"] = PRIORITY_FLEX, // 紧急按钮: 0-cost 30 block, exhausts, no block from cards for 2 turns
            ["DISARM"] = PRIORITY_FLEX,
            ["EXHUME"] = PRIORITY_FLEX,
            ["EXHUME"] = PRIORITY_FLEX,
            ["SENTINEL"] = PRIORITY_FLEX,
            ["EVIL_EYE"] = PRIORITY_FLEX,
            ["CLUMSY"] = PRIORITY_FLEX,
            ["DOUBT"] = PRIORITY_FLEX,
        };
        c.MaxCopies = new Dictionary<string, int>
        {
            // ── Limit 1: unique powers that don't stack ──
            ["DEMON_FORM"] = 1,         // One is enough — slow but game-winning
            ["BARRICADE"] = 1,          // Only need one block-retention engine
            ["CORRUPTION"] = 1,         // One exhausts everything
            ["DARK_EMBRACE"] = 1,       // One draw engine is enough
            ["JUGGERNAUT"] = 1,         // Stacking doesn't add much
            ["EVOLVE"] = 1,             // One handles all status cards
            ["FIRE_BREATHING"] = 1,     // One is enough
            ["BERSERK"] = 1,            // Vulnerable downside stacks poorly
            ["FEEL_NO_PAIN"] = 1,       // One is sufficient
            ["LIMIT_BREAK"] = 1,        // One doubles strength each use
            ["BRUTALITY"] = 1,          // One HP loss per turn is enough
            ["COMBUST"] = 1,            // One AOE damage per turn
            ["METALLICIZE"] = 1,        // Stacking is fine but diminishing returns
            ["RUPTURE"] = 1,            // One triggers all self-damage
            ["EXHUME"] = 1,             // Situational, one is enough

            // ── Limit 2: strong cards that work in pairs ──
            ["OFFERING"] = 2,           // First copy is great, second is fine
            ["INFLAME"] = 2,            // Two Inflames = +4 Strength, solid
            ["SPOT_WEAKNESS"] = 2,      // Two is decent for strength scaling
            ["BATTLE_TRANCE"] = 2,      // Two draws 6 cards, can be ok
            ["SHRUG_IT_OFF"] = 3,        // Can take 3 — cheap block + draw
            ["TRUE_GRIT"] = 2,          // Exhaust + block, two is fine
            ["IMPERVIOUS"] = 2,         // 30 block twice is still good
            ["FLAME_BARRIER"] = 2,      // Two is ok for block + thorns

            // ── Limit 3: solid commons you can stack ──
            ["POMMEL_STRIKE"] = 4,      // Draw + damage stacks well
            ["IRON_WAVE"] = 3,          // Attack + block is always useful
            ["HEADBUTT"] = 2,           // Retrieve key card, two max
            ["UPPERCUT"] = 2,           // Vulnerable + weak, two is fine
            ["CLOTHESLINE"] = 2,        // Weak application, two is fine
            ["SHOCKWAVE"] = 2,          // AOE vulnerable + weak
            ["ARMAMENTS"] = 1,          // One upgrade engine
            ["WHIRLWIND"] = 2,          // AOE strength scaling, two is ok
            ["HEAVY_BLADE"] = 2,        // Strength scaling, two is fine
            ["SWORD_BOOMERANG"] = 2,    // Multi-hit for strength
            ["TWIN_STRIKE"] = 2,        // Multi-hit for strength
            ["ANGER"] = 3,              // Generates more Angers, but don't go crazy
            ["FLEX"] = 2,               // Temporary strength, two is ok
            ["RAGE"] = 1,               // One Rage is usually enough
            ["SENTINEL"] = 1,           // One for exhaust synergy
            ["SECOND_WIND"] = 1,        // One is enough for status/exhaust synergy
            ["HAVOC"] = 1,              // Fun but unreliable, one
            ["INTIMIDATE"] = 2,         // AOE weak is good
            ["DISARM"] = 2,             // Strong vs multi-hit enemies
            ["GHOSTLY_ARMOR"] = 2,      // Ethereal block
            ["ENTRENCH"] = 2,           // Only with Barricade synergy
            ["BLOODLETTING"] = 2,       // Energy + self-damage
            ["HEMOKINESIS"] = 2,        // Cheap big damage
            ["CARNAGE"] = 2,            // Big AOE Act 1 carry
            ["IMMOLATE"] = 1,           // Big AOE, one is enough
            ["BLUDGEON"] = 2,           // Big single hit
            ["FEED"] = 1,               // HP scaling, one is enough
            ["REAPER"] = 1,             // Sustain, one is enough
            ["DOUBLE_TAP"] = 1,         // One doubler
            ["WARCRY"] = 2,             // Cheap draw manipulation
            ["BURNING_PACT"] = 2,       // Draw + exhaust
            ["BODY_SLAM"] = 2,          // Block-to-damage conversion
            ["BLOOD_WALL"] = 2,         // Big block
            ["POWER_THROUGH"] = 2,      // Big block + wounds
            ["RECKLESS_CHARGE"] = 2,    // AOE attack
            ["SEVER_SOUL"] = 1,         // Exhausts other cards, one
            ["FIEND_FIRE"] = 1,         // Exhausts hand, one
            ["DROPKICK"] = 2,           // Infinite enabler
            ["THUNDERCLAP"] = 1,        // AOE vulnerable, one is fine
            ["TREMBLE"] = 1,            // Big vulnerable
            ["COLOSSUS"] = 2,           // Block scaling
            ["EVIL_EYE"] = 1,           // Situation dependent
            ["PANIC_BUTTON"] = 1,       // 紧急按钮: 0-cost 30 block, exhausts — one emergency button is enough
            ["FEEL_NO_PAIN"] = 1,       // Duplicate entry for safety
        };
        AddMultiplayerPriorities(c);
        return c;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SILENT — Poison, shivs, dexterity, draw/discard synergy
    // ═══════════════════════════════════════════════════════════════════════

    static CharacterConfig CreateSilent()
    {
        var c = new CharacterConfig
        {
            CharacterId = "SILENT",
            DisplayName = "Silent",
            UsesPoison = true,
            HasDexterity = true,
            PoisonDamageWeight = 1.2,
            DexterityWeight = 1.0,
            WeakWeight = 1.5,
            PowerWeight = 1.0,
            DamageWeight = 0.8,
            BlockWeight = 1.0,
            KillWeight = 1.0,
            HealthPenaltyWeight = 2.0,
            VulnerableWeight = 1.2,
            StrengthWeight = 0.7,       // Silent doesn't scale strength much
        };
        c.CardPriorities = new Dictionary<string, int>
        {
            // ── PRIORITY_FREE_ENERGY (-3): 0-cost energy gain — ABSOLUTE first ──
            ["ADRENALINE"] = PRIORITY_FREE_ENERGY,

            // ── PRIORITY_SETUP (1): MUST play first — game-warping ──
            ["WRAITH_FORM"] = PRIORITY_SETUP,

            // ── PRIORITY_ENERGY_DRAW (2): Energy + draw setup ──
            ["TACTICIAN"] = PRIORITY_ENERGY_DRAW,       // Sly discard → energy engine
            ["CONCENTRATE"] = PRIORITY_ENERGY_DRAW,      // Energy + discard synergy
            ["OUTMANEUVER"] = PRIORITY_ENERGY_DRAW,      // Next-turn energy
            ["EXPERTISE"] = PRIORITY_ENERGY_DRAW,
            ["PREPARED"] = PRIORITY_ENERGY_DRAW,
            ["CALCULATED_GAMBLE"] = PRIORITY_ENERGY_DRAW,
            ["ACROBATICS"] = PRIORITY_ENERGY_DRAW,

            // ── PRIORITY_POWER_S (3): S-tier scaling — defense/poison foundation ──
            ["FOOTWORK"] = PRIORITY_POWER_S,             // +2(3) Dex — defense engine
            ["NOXIOUS_FUMES"] = PRIORITY_POWER_S,        // Passive poison AoE
            ["AFTERIMAGE"] = PRIORITY_POWER_S,           // Block per card played

            // ── PRIORITY_EXHAUST_DRAW (4): Engine enablers ──
            ["WELL_LAID_PLANS"] = PRIORITY_EXHAUST_DRAW, // Retain engine
            ["TOOLS_OF_THE_TRADE"] = PRIORITY_EXHAUST_DRAW, // Sly/discard engine
            ["ACCELERANT"] = PRIORITY_EXHAUST_DRAW,      // Poison no decay + per-turn

            // ── PRIORITY_STRENGTH_DEX (5): Damage scaling ──
            ["ACCURACY"] = PRIORITY_STRENGTH_DEX,        // Shiv +4(6) damage
            ["THOUSAND_CUTS"] = PRIORITY_STRENGTH_DEX,   // AoE per card played
            ["INFINITE_BLADES"] = PRIORITY_STRENGTH_DEX, // Free shiv per turn
            ["ENVENOM"] = PRIORITY_STRENGTH_DEX,         // Attack → poison

            // ── PRIORITY_VULNERABLE (6): Debuffs — poison + weak BEFORE attacks ──
            ["DEADLY_POISON"] = PRIORITY_VULNERABLE,
            ["BOUNCING_FLASK"] = PRIORITY_VULNERABLE,
            ["CORROSIVE_WAVE"] = PRIORITY_VULNERABLE,
            ["NEUTRALIZE"] = PRIORITY_VULNERABLE,
            ["SUCKER_PUNCH"] = PRIORITY_VULNERABLE,
            ["MALAISE"] = PRIORITY_VULNERABLE,
            ["LEG_SWEEP"] = PRIORITY_VULNERABLE,
            ["TERROR"] = PRIORITY_VULNERABLE,            // Vulnerable debuff
            ["CALTROPS"] = PRIORITY_VULNERABLE,           // Thorns

            // ── PRIORITY_DOUBLER (7): Multipliers ──
            ["BURST"] = PRIORITY_DOUBLER,
            ["NIGHTMARE"] = PRIORITY_DOUBLER,
            ["CATALYST"] = PRIORITY_DOUBLER,
            ["PHANTASMAL_KILLER"] = PRIORITY_DOUBLER,

            // ── PRIORITY_DRAW_FILTER (9): Draw/filter ──
            ["BACKFLIP"] = PRIORITY_DRAW_FILTER,
            ["DAGGER_THROW"] = PRIORITY_DRAW_FILTER,
            ["ESCAPE_PLAN"] = PRIORITY_DRAW_FILTER,
            ["REFLEX"] = PRIORITY_DRAW_FILTER,

            // ── PRIORITY_BUFF (10): Buff block ──
            ["BLUR"] = PRIORITY_BUFF,
            ["DODGE_AND_ROLL"] = PRIORITY_BUFF,
            ["CLOAK_AND_DAGGER"] = PRIORITY_BUFF,
            ["DEFLECT"] = PRIORITY_BUFF,
            ["SURVIVOR"] = PRIORITY_BUFF,

            // ── PRIORITY_FLEX (13): Situational ──
            ["DISTRACTION"] = PRIORITY_FLEX,
            ["SETUP"] = PRIORITY_FLEX,
            ["BULLET_TIME"] = PRIORITY_FLEX,
            ["DOPPELGANGER"] = PRIORITY_FLEX,
        };
        c.MaxCopies = new Dictionary<string, int>
        {
            // ── Limit 1: unique powers/game-warping effects ──
            ["WRAITH_FORM"] = 1,            // Intangible is powerful but costs dex each turn
            ["AFTERIMAGE"] = 1,             // One block-per-card engine is enough
            ["NOXIOUS_FUMES"] = 1,          // One passive poison AoE per turn
            ["THOUSAND_CUTS"] = 1,          // One AoE per card played
            ["ENVENOM"] = 1,                // One poison-on-attack engine
            ["INFINITE_BLADES"] = 1,        // One free shiv per turn
            ["WELL_LAID_PLANS"] = 1,        // One retain engine
            ["TOOLS_OF_THE_TRADE"] = 1,     // One sly/discard engine
            ["ACCELERANT"] = 1,             // Poison no-decay engine
            ["BURST"] = 1,                  // One skill doubler
            ["NIGHTMARE"] = 1,              // One card duplication engine
            ["PHANTASMAL_KILLER"] = 1,      // One attack doubler
            ["TERROR"] = 1,                 // One massive vulnerable debuff
            ["CALTROPS"] = 1,               // One thorns engine
            ["CORPSE_EXPLOSION"] = 1,       // One AoE poison explosion
            ["DIE_DIE_DIE"] = 1,            // One AoE spike
            ["CONCENTRATE"] = 1,            // One energy-from-discard engine
            ["BULLET_TIME"] = 1,            // One cost reduction engine

            // ── Limit 2: strong scaling/engine cards ──
            ["FOOTWORK"] = 2,               // Dex stacking is powerful
            ["ACCURACY"] = 2,               // Shiv damage scaling
            ["ADRENALINE"] = 2,             // Energy + draw, two is solid
            ["CATALYST"] = 2,               // Poison multiplier — two can end fights
            ["MALAISE"] = 2,                // Weak + strength drain
            ["TACTICIAN"] = 2,              // Energy from discard
            ["REFLEX"] = 2,                 // Draw from discard
            ["PREPARED"] = 2,               // Discard + draw engine
            ["CALCULATED_GAMBLE"] = 2,      // Mass discard + draw
            ["EXPERTISE"] = 2,              // Draw-to-fill engine
            ["DODGE_AND_ROLL"] = 2,         // Block-next-turn stacks
            ["BLUR"] = 2,                   // Block retention
            ["DEFLECT"] = 2,                // 0-cost block
            ["LEG_SWEEP"] = 2,              // Strong weak debuff
            ["DEADLY_POISON"] = 2,          // Reliable poison
            ["BOUNCING_FLASK"] = 2,         // Multi-target poison
            ["CORROSIVE_WAVE"] = 2,         // AoE poison + weak

            // ── Limit 3: solid commons that stack well ──
            ["BACKFLIP"] = 3,               // Block + draw stacks well
            ["ACROBATICS"] = 3,             // Draw + discard — engine fuel
            ["CLOAK_AND_DAGGER"] = 3,       // Shiv + block — always useful
            ["DAGGER_THROW"] = 3,           // Damage + draw + discard
            ["ESCAPE_PLAN"] = 3,            // 0-cost draw + conditional block
            ["SURVIVOR"] = 1,               // Basic discard — one is enough
            ["NEUTRALIZE"] = 1,             // Basic weak — one is enough
            ["SUCKER_PUNCH"] = 2,           // Weak + damage
            ["DISTRACTION"] = 1,            // Random skill — unreliable
            ["SETUP"] = 1,                  // Situational, one is enough
            ["DOPPELGANGER"] = 1,           // X-cost clone
            ["OUTMANEUVER"] = 2,            // Next-turn energy
        };
        AddMultiplayerPriorities(c);
        return c;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // DEFECT — Orbs (Lightning/Frost/Dark/Plasma), Focus scaling
    // ═══════════════════════════════════════════════════════════════════════

    static CharacterConfig CreateDefect()
    {
        var c = new CharacterConfig
        {
            CharacterId = "DEFECT",
            DisplayName = "Defect",
            UsesOrbs = true,
            OrbValueWeight = 1.5,
            PowerWeight = 1.3,
            BlockWeight = 0.7, // Frost orbs provide passive block
            DamageWeight = 0.7, // Lightning orbs provide passive damage
            KillWeight = 1.1,
            HealthPenaltyWeight = 2.2,
            VulnerableWeight = 1.1,
            WeakWeight = 1.3,           // Frost + weak = extreme mitigation
            StrengthWeight = 0.5,       // Defect rarely scales strength
        };
        c.CardPriorities = new Dictionary<string, int>
        {
            // ── PRIORITY_FREE_ENERGY (-3): 0-cost energy gain — ABSOLUTE first ──
            ["TURBO"] = PRIORITY_FREE_ENERGY,
            ["DOUBLE_ENERGY"] = PRIORITY_FREE_ENERGY,
            ["RECYCLE"] = PRIORITY_FREE_ENERGY,
            ["AGGREGATE"] = PRIORITY_FREE_ENERGY,

            // ── PRIORITY_TUTOR (-2): Search/retrieve — find best card ──
            ["SEEK"] = PRIORITY_TUTOR,
            ["HOLOGRAM"] = PRIORITY_TUTOR,

            // ── PRIORITY_SETUP (1): MUST play first — game-warping ──
            ["ECHO_FORM"] = PRIORITY_SETUP,              // 3-cost, doubles every card thereafter

            // ── PRIORITY_POWER_S (3): S-tier scaling — Focus is everything ──
            ["DEFRAGMENT"] = PRIORITY_POWER_S,           // +1(2) Focus — rarest scaling
            ["BIASED_COGNITION"] = PRIORITY_POWER_S,     // +4(5) Focus — ends fights fast
            ["CONSUME"] = PRIORITY_POWER_S,              // Focus at cost of orb slot

            // ── PRIORITY_EXHAUST_DRAW (4): Orb infrastructure ──
            ["CAPACITOR"] = PRIORITY_EXHAUST_DRAW,       // +2(3) orb slots — capacity = power ceiling
            ["LOOP"] = PRIORITY_EXHAUST_DRAW,            // Double passive orb trigger
            ["MODDED"] = PRIORITY_EXHAUST_DRAW,          // Orb slot + draw
            ["SUB_ROUTINE"] = PRIORITY_EXHAUST_DRAW,     // Power → energy engine

            // ── PRIORITY_STRENGTH_DEX (5): Secondary scaling ──
            ["REPROGRAM"] = PRIORITY_STRENGTH_DEX,       // Str+Dex scaling

            // ── PRIORITY_VULNERABLE (6): Orb channeling + debuffs ──
            ["GLACIER"] = PRIORITY_VULNERABLE,           // 2 frost orbs + block
            ["CHILL"] = PRIORITY_VULNERABLE,
            ["COLD_SNAP"] = PRIORITY_VULNERABLE,
            ["COOLHEADED"] = PRIORITY_VULNERABLE,        // Frost + draw
            ["BALL_LIGHTNING"] = PRIORITY_VULNERABLE,
            ["DARKNESS"] = PRIORITY_VULNERABLE,
            ["RAINBOW"] = PRIORITY_VULNERABLE,
            ["CHAOS"] = PRIORITY_VULNERABLE,
            ["ZAP"] = PRIORITY_VULNERABLE,
            ["BEAM_CELL"] = PRIORITY_VULNERABLE,         // Vulnerable debuff
            ["GO_FOR_THE_EYES"] = PRIORITY_VULNERABLE,   // Weak debuff
            ["FUSION"] = PRIORITY_VULNERABLE,            // Plasma orb
            ["RECURSION"] = PRIORITY_VULNERABLE,         // Evoke rightmost orb, re-channel it

            // ── PRIORITY_DOUBLER (7): Orb evoke multipliers ──
            ["DUALCAST"] = PRIORITY_DOUBLER,             // Evoke rightmost orb twice — burst double-evoke

            // ── PRIORITY_POWER (8): Other powers ──
            ["CREATIVE_AI"] = PRIORITY_POWER,            // Random power/turn — slow but infinite
            ["STORM"] = PRIORITY_POWER,                  // Power → lightning
            ["MACHINE_LEARNING"] = PRIORITY_POWER,       // +1 draw/turn
            ["HEATSINKS"] = PRIORITY_POWER,              // Power → draw
            ["BUFFER"] = PRIORITY_POWER,                 // Negate next hit
            ["SELF_REPAIR"] = PRIORITY_POWER,            // Sustain
            ["STATIC_DISCHARGE"] = PRIORITY_POWER,       // On-damage lightning

            // ── PRIORITY_DRAW_FILTER (9): Draw/filter ──
            ["SKIM"] = PRIORITY_DRAW_FILTER,
            ["COMPILE_DRIVER"] = PRIORITY_DRAW_FILTER,
            ["REBOUND"] = PRIORITY_DRAW_FILTER,
            ["OVERCLOCK"] = PRIORITY_DRAW_FILTER,
            ["FTL"] = PRIORITY_DRAW_FILTER,
            ["SCRAPE"] = PRIORITY_DRAW_FILTER,

            // ── PRIORITY_BUFF (10): Block ──
            ["CHARGE_BATTERY"] = PRIORITY_BUFF,
            ["LEAP"] = PRIORITY_BUFF,
            ["BOOT_SEQUENCE"] = PRIORITY_BUFF,
            ["REINFORCED_BODY"] = PRIORITY_BUFF,
            ["GENETIC_ALGORITHM"] = PRIORITY_BUFF,
            ["FORCE_FIELD"] = PRIORITY_BUFF,
            ["STEAM_BARRIER"] = PRIORITY_BUFF,
            ["EQUILIBRIUM"] = PRIORITY_BUFF,
            ["AUTO_SHIELDS"] = PRIORITY_BUFF,

            // ── PRIORITY_ATTACK (11): Attacks — last ──
            ["METEOR_STRIKE"] = PRIORITY_ATTACK,
            ["SUNDER"] = PRIORITY_ATTACK,
            ["HYPERBEAM"] = PRIORITY_ATTACK,
            ["ALL_FOR_ONE"] = PRIORITY_ATTACK,
            ["BARRAGE"] = PRIORITY_ATTACK,
            ["CLAW"] = PRIORITY_ATTACK,
            ["SWEEPING_BEAM"] = PRIORITY_ATTACK,
            ["DOOM_AND_GLOOM"] = PRIORITY_ATTACK,
            ["STREAMLINE"] = PRIORITY_ATTACK,
            ["CORE_SURGE"] = PRIORITY_ATTACK,
            ["MELTER"] = PRIORITY_ATTACK,
            ["BLIZZARD"] = PRIORITY_ATTACK,
            ["TEMPEREST"] = PRIORITY_ATTACK,

            // ── PRIORITY_FLEX (13): Situational ──
            ["MULTI_CAST"] = PRIORITY_FLEX,
            ["FISSION"] = PRIORITY_FLEX,
            ["WHITE_NOISE"] = PRIORITY_FLEX,
        };
        c.MaxCopies = new Dictionary<string, int>
        {
            // ── Limit 1: unique powers/game-warping effects ──
            ["ECHO_FORM"] = 1,              // One card doubler is enough
            ["CREATIVE_AI"] = 1,            // One random power engine
            ["STORM"] = 1,                  // One power→lightning engine
            ["MACHINE_LEARNING"] = 1,       // One draw-per-turn engine
            ["HEATSINKS"] = 1,              // One power→draw engine
            ["BUFFER"] = 1,                 // One hit-negation
            ["SELF_REPAIR"] = 1,            // One sustain
            ["STATIC_DISCHARGE"] = 1,       // One on-damage lightning
            ["ELECTRODYNAMICS"] = 1,        // Lightning AoE — one transforms all
            ["DARKNESS"] = 1,               // One dark orb scaling engine
            ["RAINBOW"] = 1,                // One all-orb channel
            ["CHAOS"] = 1,                  // One random orb engine
            ["FUSION"] = 1,                 // One plasma orb
            ["CONSUME"] = 1,                // Focus at orb slot cost — diminishing returns
            ["DOUBLE_ENERGY"] = 1,          // Energy doubler — one is enough
            ["AGGREGATE"] = 1,              // Energy from deck — one is enough
            ["RECYCLE"] = 1,                // Exhaust for energy — one is enough
            ["OVERCLOCK"] = 1,              // Draw + burn — one is enough
            ["REBOUND"] = 1,                // Play-next-turn — one is enough
            ["BOOT_SEQUENCE"] = 1,          // Innate block — one is enough
            ["FORCE_FIELD"] = 1,            // 0-cost with powers — one is enough
            ["AUTO_SHIELDS"] = 1,           // Conditional block — one is enough
            ["METEOR_STRIKE"] = 1,          // Big plasma — one is enough
            ["SUNDER"] = 1,                 // Execute — one is enough
            ["HYPERBEAM"] = 1,              // Big AoE — one is enough
            ["ALL_FOR_ONE"] = 1,            // 0-cost retrieval engine
            ["CORE_SURGE"] = 1,             // Artifact + damage — one is enough
            ["MELTER"] = 1,                 // Block removal — one is enough
            ["TEMPEST"] = 1,                // X-cost lightning channel — one is enough
            ["FISSION"] = 1,                // Orb explosion — one is enough
            ["WHITE_NOISE"] = 1,            // Random power — one is enough
            ["MULTI_CAST"] = 2,             // Orb activation — two can be strong
            ["REPROGRAM"] = 1,              // Str+Dex scaling — one is enough

            // ── Limit 2: strong scaling/engine cards ──
            ["DEFRAGMENT"] = 2,             // Focus is king, two is +2(4) Focus
            ["BIASED_COGNITION"] = 2,       // Focus burst, two can end fights
            ["CAPACITOR"] = 2,              // Orb slots — two is +4(6) slots
            ["LOOP"] = 2,                   // Double passive trigger
            ["GLACIER"] = 2,                // Frost + block — two is solid
            ["CHILL"] = 2,                  // Frost channel — two fills orb slots
            ["BALL_LIGHTNING"] = 2,         // Lightning + damage — two is solid
            ["TURBO"] = 2,                  // Energy + void — two is fine
            ["SEEK"] = 2,                   // Tutor — two finds key cards
            ["HOLOGRAM"] = 2,               // Retrieve — two is flexible
            ["SKIM"] = 2,                   // Mass draw
            ["COMPILE_DRIVER"] = 2,         // Draw + damage
            ["CHARGE_BATTERY"] = 2,         // Block + next-turn energy
            ["LEAP"] = 2,                   // Solid block
            ["REINFORCED_BODY"] = 2,        // X-cost block
            ["GENETIC_ALGORITHM"] = 2,      // Scaling block
            ["EQUILIBRIUM"] = 2,            // Block + retain
            ["STEAM_BARRIER"] = 1,          // Decaying block — one is enough
            ["SWEEPING_BEAM"] = 2,          // AoE + draw
            ["DOOM_AND_GLOOM"] = 1,         // Dark + AoE — one is enough
            ["STREAMLINE"] = 2,             // Cost reduction attack
            ["BLIZZARD"] = 2,              // Frost-scaling AoE

            // ── Limit 3+: commons that stack well ──
            ["COLD_SNAP"] = 3,             // Frost + damage — stacks well
            ["COOLHEADED"] = 3,            // Frost + draw — excellent common
            ["FTL"] = 3,                   // 0-cost attack + draw — strong common
            ["SCRAPE"] = 2,                // Draw 0-cost cards
            ["CLAW"] = 4,                  // Infinite scaling — stack aggressively
            ["GO_FOR_THE_EYES"] = 2,       // 0-cost weak
            ["BEAM_CELL"] = 1,             // Vulnerable — one is enough
            ["ZAP"] = 1,                   // Basic lightning — one is enough
            ["BARRAGE"] = 2,               // Multi-hit per orb slot
        };
        AddMultiplayerPriorities(c);
        return c;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // NECROBINDER — Star resource, curses, unique summon mechanics
    // ═══════════════════════════════════════════════════════════════════════

    static CharacterConfig CreateNecrobinder()
    {
        var c = new CharacterConfig
        {
            CharacterId = "NECROBINDER",
            DisplayName = "Necrobinder",
            UsesStars = true,
            StarConservationWeight = 0.5,
            PowerWeight = 1.2,
            DamageWeight = 1.0,
            BlockWeight = 0.9,
            KillWeight = 1.1,               // Osty rewards aggressive play
            HealthPenaltyWeight = 2.3,      // Fewer sustain options
            VulnerableWeight = 1.0,
            WeakWeight = 1.1,
            StrengthWeight = 0.8,
        };
        c.CardPriorities = new Dictionary<string, int>
        {
            // ── PRIORITY_SETUP (1): MUST play first — game-warping ──
            ["REAPER_FORM"] = PRIORITY_SETUP,            // 2-cost, damage doubler — zero immediate effect

            // ── PRIORITY_ENERGY_DRAW (2): Energy + star engine ──
            ["FRIENDSHIP"] = PRIORITY_ENERGY_DRAW,       // S-tier: attack → +1 energy per hit
            ["BORROWED_TIME"] = PRIORITY_ENERGY_DRAW,
            ["DRAIN_POWER"] = PRIORITY_ENERGY_DRAW,
            ["SOUL_STORM"] = PRIORITY_ENERGY_DRAW,
            ["PAGESTORM"] = PRIORITY_ENERGY_DRAW,

            // ── PRIORITY_POWER_S (3): S-tier scaling ──
            ["NECRO_MASTERY"] = PRIORITY_POWER_S,        // Osty atk+def — core engine
            ["SPIRIT_OF_ASH"] = PRIORITY_POWER_S,        // Ethereal → +4 block — A-tier
            ["LETHALITY"] = PRIORITY_POWER_S,            // First attack bonus damage

            // ── PRIORITY_EXHAUST_DRAW (4): Osty/soul engine — zero immediate, big payoff ──
            ["INVOKE"] = PRIORITY_EXHAUST_DRAW,          // Grow Osty + energy (Nat1Gaming: hard but worth)
            ["CALCIFY"] = PRIORITY_EXHAUST_DRAW,         // Osty damage growth — pure scaling
            ["DEATH_MARCH"] = PRIORITY_EXHAUST_DRAW,     // Souls → damage

            // ── PRIORITY_STRENGTH_DEX (5): Secondary scaling ──
            ["UNLEASH"] = PRIORITY_STRENGTH_DEX,
            ["FORBIDDEN_GRIMOIRE"] = PRIORITY_STRENGTH_DEX,

            // ── PRIORITY_VULNERABLE (6): Debuffs ──
            ["ENFEEBLING_TOUCH"] = PRIORITY_VULNERABLE,
            ["PUTREFY"] = PRIORITY_VULNERABLE,
            ["DEBILITATE"] = PRIORITY_VULNERABLE,
            ["DEFILE"] = PRIORITY_VULNERABLE,

            // ── PRIORITY_POWER (8): Other powers ──
            ["DEMESNE"] = PRIORITY_POWER,
            ["EIDOLON"] = PRIORITY_POWER,
            ["END_OF_DAYS"] = PRIORITY_POWER,
            ["LEGION_OF_BONE"] = PRIORITY_POWER,
            ["SOULBOUND"] = PRIORITY_POWER,              // Multiplayer — "energy on no effect is rough"

            // ── PRIORITY_DRAW_FILTER (9): Draw/filter ──
            ["DREDGE"] = PRIORITY_DRAW_FILTER,
            ["FETCH"] = PRIORITY_DRAW_FILTER,
            ["PARSE"] = PRIORITY_DRAW_FILTER,
            ["GLIMPSE_BEYOND"] = PRIORITY_DRAW_FILTER,

            // ── PRIORITY_BUFF (10): Block + Osty ──
            ["BODYGUARD"] = PRIORITY_BUFF,
            ["BONE_SHARDS"] = PRIORITY_BUFF,
            ["DEATHS_DOOR"] = PRIORITY_BUFF,
            ["GRAVE_WARDEN"] = PRIORITY_BUFF,
            ["PROTECTOR"] = PRIORITY_BUFF,
            ["SHROUD"] = PRIORITY_BUFF,
            ["SENTRY_MODE"] = PRIORITY_BUFF,             // Osty buff — "2-cost zero immediate effect"

            // ── PRIORITY_ATTACK (11): Attacks ──
            ["THE_SCYTHE"] = PRIORITY_ATTACK,
            ["REAP"] = PRIORITY_ATTACK,
            ["GRAVEBLAST"] = PRIORITY_ATTACK,
            ["BLIGHT_STRIKE"] = PRIORITY_ATTACK,
            ["ERADICATE"] = PRIORITY_ATTACK,
            ["SCULPTING_STRIKE"] = PRIORITY_ATTACK,
            ["SEVERANCE"] = PRIORITY_ATTACK,
            ["REAVE"] = PRIORITY_ATTACK,
        };
        c.MaxCopies = new Dictionary<string, int>
        {
            // ── Limit 1: unique powers/game-warping effects ──
            ["REAPER_FORM"] = 1,            // Damage doubler — one is enough
            ["NECRO_MASTERY"] = 1,          // Core Osty scaling engine
            ["SPIRIT_OF_ASH"] = 1,          // Ethereal→block engine
            ["LETHALITY"] = 1,              // First-attack bonus
            ["FRIENDSHIP"] = 1,             // Attack→energy engine — S-tier
            ["INVOKE"] = 1,                 // Grow Osty + energy
            ["CALCIFY"] = 1,                // Osty damage growth — pure scaling
            ["DEATH_MARCH"] = 1,            // Souls→damage engine
            ["DEMESNE"] = 1,                // Power — one is enough
            ["EIDOLON"] = 1,                // Power — one is enough
            ["END_OF_DAYS"] = 1,            // Power — one is enough
            ["LEGION_OF_BONE"] = 1,         // Power — one is enough
            ["SOULBOUND"] = 1,              // Multiplayer power — one is enough
            ["FORBIDDEN_GRIMOIRE"] = 1,     // Scaling — one is enough
            ["BORROWED_TIME"] = 1,          // Energy engine — one is enough
            ["DRAIN_POWER"] = 1,            // Energy drain — one is enough
            ["SOUL_STORM"] = 1,             // Soul engine — one is enough
            ["PAGESTORM"] = 1,              // Draw engine — one is enough
            ["SENTRY_MODE"] = 1,            // Osty buff — one is enough
            ["DEATHS_DOOR"] = 1,            // Emergency block
            ["THE_SCYTHE"] = 1,             // Big finisher attack
            ["ERADICATE"] = 1,              // Big removal attack
            ["SEVERANCE"] = 1,              // Finisher attack
            ["PUTREFY"] = 1,                // Strong debuff
            ["DEBILITATE"] = 1,             // Strong debuff
            ["DEFILE"] = 1,                 // AoE debuff
            ["SCULPTING_STRIKE"] = 2,       // Scaling attack

            // ── Limit 2: strong scaling/engine cards ──
            ["UNLEASH"] = 2,                // Scaling
            ["DREDGE"] = 2,                 // Draw
            ["FETCH"] = 2,                  // Draw
            ["PARSE"] = 2,                  // Draw
            ["GLIMPSE_BEYOND"] = 2,         // Draw
            ["BODYGUARD"] = 2,              // Block + Osty
            ["BONE_SHARDS"] = 2,            // Block
            ["GRAVE_WARDEN"] = 2,           // Block
            ["PROTECTOR"] = 2,              // Block
            ["SHROUD"] = 2,                 // Block
            ["REAP"] = 2,                   // Attack
            ["GRAVEBLAST"] = 2,             // Attack
            ["BLIGHT_STRIKE"] = 2,          // Attack
            ["REAVE"] = 2,                  // Attack
            ["ENFEEBLING_TOUCH"] = 2,       // Weak debuff
        };
        AddMultiplayerPriorities(c);
        return c;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // REGENT — Holy/divine themed, high damage, buff-based scaling
    // ═══════════════════════════════════════════════════════════════════════

    static CharacterConfig CreateRegent()
    {
        var c = new CharacterConfig
        {
            CharacterId = "REGENT",
            DisplayName = "Regent",
            DamageWeight = 1.2,
            BlockWeight = 0.9,
            PowerWeight = 1.0,
            VulnerableWeight = 1.1,
            WeakWeight = 0.9,
            StrengthWeight = 1.1,
            KillWeight = 1.1,
            HealthPenaltyWeight = 2.1,
        };
        c.CardPriorities = new Dictionary<string, int>
        {
            // ── PRIORITY_FREE_ENERGY (-3): 0-cost energy gain — ABSOLUTE first ──
            ["OFFERING"] = PRIORITY_FREE_ENERGY,         // Ironclad card in Regent pool

            // ── PRIORITY_SETUP (1): MUST play first — game-warping ──
            ["VOID_FORM"] = PRIORITY_SETUP,              // 3-cost, first 2 cards/turn cost drastically less
            ["THE_SEALED_THRONE"] = PRIORITY_SETUP,      // S-tier Act reward — makes high star cost nearly free

            // ── PRIORITY_ENERGY_DRAW (2): Energy + draw setup ──
            ["SANCTIFY"] = PRIORITY_ENERGY_DRAW,
            ["GENESIS"] = PRIORITY_ENERGY_DRAW,          // Star generation engine — fuel for everything

            // ── PRIORITY_POWER_S (3): S-tier scaling — defense foundation ──
            ["CHILD_OF_THE_STARS"] = PRIORITY_POWER_S,   // Per star produce/consume → block — defense core
            ["ARSENAL"] = PRIORITY_POWER_S,              // Per created card → +1 Strength — scaling engine

            // ── PRIORITY_EXHAUST_DRAW (4): Engine enablers — zero immediate effect ──
            ["CHARGE"] = PRIORITY_EXHAUST_DRAW,          // Convert waste → power cards + draw
            ["HAMMER_TIME"] = PRIORITY_EXHAUST_DRAW,     // Forge engine — 2-cost zero immediate effect
            ["FURNACE"] = PRIORITY_EXHAUST_DRAW,         // Auto-forge per turn — 2-cost zero immediate

            // ── PRIORITY_STRENGTH_DEX (5): Strength/buff scaling ──
            ["BLESSING_OF_HUNTING"] = PRIORITY_STRENGTH_DEX,
            ["APOTHEOSIS"] = PRIORITY_STRENGTH_DEX,
            ["SACRED_OATH"] = PRIORITY_STRENGTH_DEX,
            ["MAKE_IT_SO"] = PRIORITY_STRENGTH_DEX,      // Cumulative damage — "looks weak but stacks fast"

            // ── PRIORITY_VULNERABLE (6): Debuffs ──
            ["PURIFY"] = PRIORITY_VULNERABLE,
            ["OATH"] = PRIORITY_VULNERABLE,
            ["AWE"] = PRIORITY_VULNERABLE,

            // ── PRIORITY_DOUBLER (7): Multipliers ──
            ["DIVINE_LANCE"] = PRIORITY_DOUBLER,
            ["RECLAMATION"] = PRIORITY_DOUBLER,

            // ── PRIORITY_POWER (8): Other powers ──
            ["DIVINE_AEGIS"] = PRIORITY_POWER,
            ["HALLOWED_GROUND"] = PRIORITY_POWER,
            ["MARTYRDOM"] = PRIORITY_POWER,

            // ── PRIORITY_DRAW_FILTER (9): Draw/filter ──
            ["GRACE"] = PRIORITY_DRAW_FILTER,
            ["CONFESS"] = PRIORITY_DRAW_FILTER,

            // ── PRIORITY_BUFF (10): Pure setup — zero damage/block ──
            ["ROYALTIES"] = PRIORITY_BUFF,               // "Spend energy on a card that does NOTHING" — pure future investment
            ["BOMBARDMENT"] = PRIORITY_BUFF,             // 3-cost delayed bombardment

            // ── PRIORITY_ATTACK (11): Attacks ──
            ["CHAMPIONS_BLOW"] = PRIORITY_ATTACK,
            ["CLEAVING_STRIKE"] = PRIORITY_ATTACK,
            ["HOLY_BLADE"] = PRIORITY_ATTACK,
            ["RETRIBUTION"] = PRIORITY_ATTACK,
            ["SMITE"] = PRIORITY_ATTACK,
            ["ZEALOUS_STRIKE"] = PRIORITY_ATTACK,

            // ── PRIORITY_BLOCK (12): Block ──
            ["ABSOLVE"] = PRIORITY_BLOCK,
            ["BLESSED_SHIELD"] = PRIORITY_BLOCK,
            ["DIVINE_PROTECTION"] = PRIORITY_BLOCK,
            ["HOLY_ARMOR"] = PRIORITY_BLOCK,
            ["PENANCE"] = PRIORITY_BLOCK,
        };
        c.MaxCopies = new Dictionary<string, int>
        {
            // ── Limit 1: unique powers/game-warping effects ──
            ["VOID_FORM"] = 1,              // Cost reduction — one transforms the turn
            ["THE_SEALED_THRONE"] = 1,      // Star cost reducer — S-tier Act reward
            ["CHILD_OF_THE_STARS"] = 1,     // Star→block defense core
            ["ARSENAL"] = 1,                // Created card→Strength engine
            ["CHARGE"] = 1,                 // Waste→power engine
            ["HAMMER_TIME"] = 1,            // Forge engine
            ["FURNACE"] = 1,                // Auto-forge per turn
            ["GENESIS"] = 1,                // Star generation engine
            ["SANCTIFY"] = 1,               // Energy engine
            ["DIVINE_AEGIS"] = 1,           // Power — one is enough
            ["HALLOWED_GROUND"] = 1,        // Power — one is enough
            ["MARTYRDOM"] = 1,              // Power — one is enough
            ["APOTHEOSIS"] = 1,             // Mass upgrade — one is enough
            ["DIVINE_LANCE"] = 1,           // Doubler — one is enough
            ["RECLAMATION"] = 1,            // Doubler — one is enough
            ["ROYALTIES"] = 1,              // Pure future investment — one is enough
            ["BOMBARDMENT"] = 1,            // Delayed AoE — one is enough
            ["CHAMPIONS_BLOW"] = 1,         // Big finisher — one is enough

            // ── Limit 2: strong scaling/engine cards ──
            ["OFFERING"] = 2,               // Energy + draw — Ironclad card in pool
            ["BLESSING_OF_HUNTING"] = 2,    // Strength scaling
            ["SACRED_OATH"] = 2,            // Strength scaling
            ["MAKE_IT_SO"] = 2,             // Cumulative damage scaling
            ["GRACE"] = 2,                  // Draw
            ["CONFESS"] = 2,                // Draw
            ["PURIFY"] = 2,                 // Debuff
            ["OATH"] = 2,                   // Debuff
            ["AWE"] = 2,                    // Debuff
            ["CLEAVING_STRIKE"] = 2,        // Attack
            ["HOLY_BLADE"] = 2,             // Attack
            ["RETRIBUTION"] = 2,            // Attack
            ["SMITE"] = 2,                  // Attack
            ["ZEALOUS_STRIKE"] = 2,         // Attack
            ["ABSOLVE"] = 2,                // Block
            ["BLESSED_SHIELD"] = 2,         // Block
            ["DIVINE_PROTECTION"] = 2,      // Block
            ["HOLY_ARMOR"] = 2,             // Block
            ["PENANCE"] = 2,                // Block
        };
        AddMultiplayerPriorities(c);
        return c;
    }

    // ── Multiplayer card priorities — applied to ALL 5 characters ──────────
    // These 16 generic MP cards + character-specific ones get highest play priority.
    static void AddMultiplayerPriorities(CharacterConfig c)
    {
        // ── AnyAlly cards (PRIORITY_MULTIPLAYER_ALLY = -5): MUST target human ──
        c.CardPriorities["BELIEVE_IN_YOU"] = PRIORITY_MULTIPLAYER_ALLY;
        c.CardPriorities["COORDINATE"] = PRIORITY_MULTIPLAYER_ALLY;
        c.CardPriorities["INTERCEPT"] = PRIORITY_MULTIPLAYER_ALLY;
        c.CardPriorities["LARGESSE"] = PRIORITY_MULTIPLAYER_ALLY;
        c.CardPriorities["LIFT"] = PRIORITY_MULTIPLAYER_ALLY;
        c.CardPriorities["MIMIC"] = PRIORITY_MULTIPLAYER_ALLY;

        // ── AllAllies cards (PRIORITY_MULTIPLAYER_TEAM = -4): team-wide buff ──
        c.CardPriorities["ENERGY_SURGE"] = PRIORITY_MULTIPLAYER_TEAM;
        c.CardPriorities["HUDDLE_UP"] = PRIORITY_MULTIPLAYER_TEAM;
        c.CardPriorities["RALLY"] = PRIORITY_MULTIPLAYER_TEAM;

        // ── Self-buff MP cards (PRIORITY_MULTIPLAYER_SELF = 0): passive ally benefit ──
        c.CardPriorities["BEACON_OF_HOPE"] = PRIORITY_MULTIPLAYER_SELF;
        c.CardPriorities["HAMMER_TIME"] = PRIORITY_MULTIPLAYER_SELF;
        c.CardPriorities["TANK"] = PRIORITY_MULTIPLAYER_SELF;

        // ── Enemy-debuff MP cards: same priority as Vulnerable (BEFORE attacks) ──
        c.CardPriorities["FLANKING"] = PRIORITY_VULNERABLE;
        c.CardPriorities["GANG_UP"] = PRIORITY_ATTACK;
        c.CardPriorities["KNOCKDOWN"] = PRIORITY_ATTACK;
        c.CardPriorities["TAG_TEAM"] = PRIORITY_VULNERABLE;

        // ── Character-specific MP cards ──
        c.CardPriorities["DEMONIC_SHIELD"] = PRIORITY_MULTIPLAYER_ALLY;  // Ironclad: AnyAlly
        c.CardPriorities["GLIMPSE_BEYOND"] = PRIORITY_MULTIPLAYER_TEAM;  // Necrobinder: AllAllies
        c.CardPriorities["LEGION_OF_BONE"] = PRIORITY_MULTIPLAYER_TEAM;  // Necrobinder: AllAllies
        c.CardPriorities["IGNITION"] = PRIORITY_MULTIPLAYER_ALLY;        // Defect: AnyAlly
        c.CardPriorities["SNEAKY"] = PRIORITY_MULTIPLAYER_SELF;          // Silent: Self

        // ── Max copies: 1 each for rare MP powers, 2 for others ──
        c.MaxCopies["BEACON_OF_HOPE"] = 1;
        c.MaxCopies["TANK"] = 1;
        c.MaxCopies["SNEAKY"] = 1;
        c.MaxCopies["HAMMER_TIME"] = 1;
        c.MaxCopies["DEMONIC_SHIELD"] = 2;
        c.MaxCopies["IGNITION"] = 2;
    }
}
