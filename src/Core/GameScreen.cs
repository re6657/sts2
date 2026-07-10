namespace TokenSpire2.Core;

/// <summary>
/// All detectable game screens. Used by ScreenDetector to determine
/// the current game state and route to the appropriate handler.
///
/// Values are ordered so that overlays (checked first) have higher
/// ordinal values than base screens. This lets us check IsOverlay(screen)
/// by comparing against OVERLAY_CARD_REWARD.
/// </summary>
public enum GameScreen
{
    NONE = 0,

    // ── Base screens ────────────────────────────────────────────
    MAIN_MENU,
    COMBAT,
    MAP,
    EVENT,
    TREASURE,
    REST,
    SHOP,

    // ── Combat-adjacent ─────────────────────────────────────────
    COMBAT_VICTORY,     // proceed button visible after combat
    GAME_OVER,

    // ── Multiplayer screens ─────────────────────────────────────
    MULTIPLAYER_SUBMENU,
    MULTIPLAYER_HOST_SUBMENU,

    /// <summary>Steam friend list shown after clicking "Join Game" — broker mode
    /// must bypass this because there are no Steam friends to select.</summary>
    MULTIPLAYER_FRIEND_LIST,

    /// <summary>Multiplayer lobby — all players connected, waiting to start.</summary>
    LOBBY,

    CHARACTER_SELECT,
    CHARACTER_SELECT_MULTIPLAYER,

    // ── Overlays (stacked on top of other screens) ──────────────
    OVERLAY_CARD_REWARD,       // NCardRewardSelectionScreen
    OVERLAY_REWARDS,           // NRewardsScreen
    OVERLAY_CHOOSE_CARD,       // NChooseACardSelectionScreen
    OVERLAY_CHOOSE_BUNDLE,     // NChooseABundleSelectionScreen
    OVERLAY_CHOOSE_RELIC,      // NChooseARelicSelection
    OVERLAY_DECK_GRID,         // Upgrade/Transform/Enchant/Remove
    OVERLAY_SIMPLE_SELECT,     // NSimpleCardSelectScreen
    OVERLAY_CRYSTAL_SPHERE,    // NCrystalSphereScreen
}

/// <summary>Extension methods for GameScreen.</summary>
public static class GameScreenExtensions
{
    /// <summary>True if the screen is an overlay that appears on top of other content.</summary>
    public static bool IsOverlay(this GameScreen screen)
        => screen >= GameScreen.OVERLAY_CARD_REWARD;

    /// <summary>True if this screen needs a strategic decision (not just mechanical clicking).</summary>
    public static bool NeedsDecision(this GameScreen screen)
    {
        return screen switch
        {
            GameScreen.MAP => true,
            GameScreen.EVENT => true,
            GameScreen.REST => true,
            GameScreen.SHOP => true,
            GameScreen.OVERLAY_CARD_REWARD => true,
            GameScreen.OVERLAY_CHOOSE_CARD => true,
            GameScreen.OVERLAY_CHOOSE_BUNDLE => true,
            GameScreen.OVERLAY_CHOOSE_RELIC => true,
            GameScreen.OVERLAY_DECK_GRID => true,
            GameScreen.OVERLAY_SIMPLE_SELECT => true,
            GameScreen.OVERLAY_CRYSTAL_SPHERE => true,
            _ => false,
        };
    }
}
