# TokenSpire2 — Architecture & Bug Analysis

> Auto-play mod for Slay the Spire 2 (STS2). Currently **solver-only mode** — all LLM/random play paths are disabled.

---

## 1. Project Structure (30 source files)

```
TokenSpire2/
├── MainFile.cs                          # [ModInitializer] entry point
├── TokenSpire2.csproj                   # .NET 9 + Godot 4.5.1 SDK
├── TokenSpire2.json                     # mod manifest
├── src/
│   ├── AutoSlayNode.cs                  # ★ CENTRAL BOT NODE (1623 lines) — main game loop
│   ├── AutoSlayCardSelector.cs          # ICardSelector for mid-combat card selects
│   ├── AutoSlayHelpers.cs              # Recursive child finders (FindFirst/FindAll)
│   ├── AutoSlayPatch.cs                # Harmony patch to attach bot to NGame._Ready()
│   ├── Handlers/
│   │   ├── CombatHandler.cs            # Random card play (NOT USED in solver mode)
│   │   ├── MapHandler.cs               # Random map node picker
│   │   ├── ShopHandler.cs              # Random shop buyer (async)
│   │   ├── RewardsHandler.cs           # Random reward clicker
│   │   ├── EventRoomHandler.cs         # Random event option selector
│   │   ├── RestSiteHandler.cs          # Random rest site action
│   │   ├── GameOverHandler.cs          # Continue/return-to-menu clicks
│   │   ├── CardRewardHandler.cs        # Random card reward pick
│   │   ├── CardGridHandler.cs          # 3-phase grid (upgrade/transform/enchant/remove)
│   │   ├── ChooseACardHandler.cs       # Random card selection
│   │   ├── ChooseABundleHandler.cs     # Random bundle selection
│   │   ├── ChooseARelicHandler.cs      # Random relic selection
│   │   ├── CrystalSphereHandler.cs     # Crystal Sphere event
│   │   ├── SimpleCardSelectHandler.cs  # Simple card select overlay
│   │   ├── TreasureRoomHandler.cs      # Chest opening + relic pickup
│   │   └── PotionHelper.cs             # Potion target resolution
│   ├── Llm/
│   │   ├── LlmClient.cs                # HTTP streaming LLM client (OpenAI/OpenRouter)
│   │   ├── LlmConfig.cs                # JSON config loader
│   │   ├── GameStateSerializer.cs      # Serializes game state → LLM prompt text
│   │   ├── PromptStrings.cs            # Bilingual prompt templates (EN/ZH)
│   │   └── RunSummaryLogger.cs         # Per-run stats + JSON log
│   └── Solver/
│       ├── IroncladSolver.cs           # ★ DFS state-space search solver
│       ├── CharacterConfigs.cs         # Per-character priorities + eval weights
│       ├── CardEffectReader.cs         # DynamicVar reflection for accurate card values
│       └── CardModelInspector.cs       # One-time runtime type discovery
```

---

## 2. Architecture Overview

### 2.1 Entry Point

`MainFile.Initialize()` (line 25) is called by the STS2 mod loader via `[ModInitializer]`. It:
1. **Allocates a Windows console** for debug output (`AllocConsole`)
2. **Patches the game** via Harmony (`Harmony.PatchAll()`)
3. **Attaches `AutoSlayNode` directly** to the scene tree root as a child node

`AutoSlayPatch.cs` is a *redundant* Harmony patch on `NGame._Ready()` that also tries to attach the node. In practice, the direct attach in `MainFile.cs` (line 48) runs first and is the one that works.

### 2.2 Main Loop (`AutoSlayNode._Process`)

`_Process(delta)` runs every frame (Godot tick). The method is a giant priority-ordered state machine:

```
1. F6 pause toggle
2. Wait for pending LLM call (_pendingLlm != null)
3. Handle mid-combat overlays (NOverlayStack screens)
4. Execute combat plan steps (_combatPlan != null)
5. ★ COMBAT: if in combat → draw-check → solver → plan execution
6. Out of combat: overlay dispatch → map → event → treasure → rest → shop → victory → main menu
```

Key state flags controlling flow:
| Flag | Purpose |
|------|---------|
| `_combatTurnRequested` | Already asked solver this turn; don't ask again |
| `_combatCardDelay` | Frame delay between card plays / before solver runs |
| `_drawJustFinished` | Hand was empty, now has cards — wait extra 0.3s |
| `_combatPlan` | List of CombatActions to execute (plays + potions) |
| `_combatPlanStep` | Current index into plan |
| `_combatPlanEndTurn` | Whether to end turn after plan complete |
| `_paused` | F6 toggle — halts all automation |

### 2.3 Draw Detection (THE CRITICAL PATH)

```
Turn flow:
  1. Enemy turn: PlayerActionsDisabled=true → reset _combatTurnRequested + _drawJustFinished
  2. Player turn starts: PlayerActionsDisabled=false, hand=0
  3. Draw check: handCount==0 → poll every 0.1s
  4. First card arrives: handCount>0 → set _drawJustFinished=true → wait 0.3s
  5. After 0.3s: solver runs
  6. Solver produces plan → _combatPlan filled, _combatTurnRequested=true
  7. ExecuteNextCombatStep() called each frame, plays one card per tick with 0.4s delay
  8. Plan complete → if END_TURN → PlayerCmd.EndTurn → enemy turn (go to 1)
```

**KEY STS2 PITFALL:** `PileType.Draw.GetPile(pl).Cards.Count` returns ALL undrawn cards in the deck, not just cards being drawn this turn. After the initial 5-card hand, the remaining deck cards stay in the draw pile. NEVER check `drawCount > 0` to determine if drawing is complete — it will ALWAYS be true as long as the deck has cards.

### 2.4 Solver (`IroncladSolver`)

The solver runs a DFS state-space search over playable cards in hand:

1. **Card filtering:** `CanPlayCard()` checks both cost affordability and `card.CanPlay()`
2. **Card prioritization:** Sorted by `CharacterConfig.CardPriorities`, then by BaseDamage+BaseBlock descending
3. **DFS search:** For each card → energy options (including X-cost) → valid targets → clone state → apply effects → recurse
4. **Limits:** MAX_SEARCH_STATES=2000, MAX_CARDS_PER_TURN=15
5. **Evaluation:** Multi-factor score — kills, damage dealt, block vs incoming, status effects, class-specific mechanics (orbs, poison, stars)
6. **Target selection:** Prioritizes vulnerable enemies, then lowest HP

**Supported characters:** IRONCLAD, SILENT, DEFECT, NECROBINDER, REGENT

### 2.5 Card Effect Reading (`CardEffectReader` + `CardModelInspector`)

These two files form a layered approach to getting accurate card stats:

1. **CardModelInspector.TestWithCard()**: One-time runtime reflection — discovers `DynamicVarSet` property, `DynamicVar` type, `CardPreviewMode` enum, and `UpdateDynamicVarPreview()` method
2. **CardEffectReader.ReadEffects()**: Calls `UpdateDynamicVarPreview()` on each card, then reads `PreviewValue` from DynamicVar fields
3. **Fallback:** If reflection fails, uses a hardcoded switch on `card.Id.Entry` for ~40 Ironclad cards + generic type-based defaults

### 2.6 LLM System (DISABLED — dead code paths)

The LLM system is fully implemented but **permanently disabled** (`_llm` is always `null` in `_Ready()`). The code remains in place:

- **LlmClient.cs**: Full SSE streaming HTTP client with OpenRouter thinking support, conversation history, memory persistence across runs
- **GameStateSerializer.cs**: Serializes combat/map/event/shop/rest/rewards into bilingual prompt text
- **PromptStrings.cs**: Complete bilingual prompt system (EN/ZH)
- **RunSummaryLogger.cs**: Per-run JSON stats and human-readable summaries
- **LlmConfig.cs**: JSON config loader (URL, API key, model, character, thinking settings)

All `if (_llm != null)` guards in `_Process()` now always fail, falling through to random handlers (for non-combat) or solver (for combat).

---

## 3. Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                      MainFile.Initialize()                       │
│  AllocConsole → Harmony.PatchAll → Root.AddChild(AutoSlayNode)  │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│                    AutoSlayNode._Process(delta)                  │
│                                                                  │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │  COMBAT BRANCH                                              ││
│  │  CombatManager.IsInProgress + !PlayerActionsDisabled         ││
│  │                                                              ││
│  │  Draw Check → wait for hand>0 → 0.3s delay                  ││
│  │       │                                                      ││
│  │       ▼                                                      ││
│  │  State Gathering: hand, enemies, energy, block, HP,          ││
│  │    powers (Str/Dex/Vuln/Weak/Frail/Poison)                   ││
│  │    orbs (Lightning/Frost/Dark/Plasma/Focus), stars           ││
│  │       │                                                      ││
│  │       ▼                                                      ││
│  │  CharacterConfig.Create(character)                           ││
│  │       │                                                      ││
│  │       ▼                                                      ││
│  │  IroncladSolver.Solve(hand, enemies, energy, ...)            ││
│  │    ├─ CardFilter: CanPlayCard() + cost check                 ││
│  │    ├─ CardEntry: CardEffectReader.ReadEffects()              ││
│  │    ├─ DFS search (max 2000 states, 15 cards/turn)            ││
│  │    └─ Best plan: List<SolveAction> + END_TURN                ││
│  │       │                                                      ││
│  │       ▼                                                      ││
│  │  _combatPlan = actions → ExecuteNextCombatStep() per frame   ││
│  │    ├─ card.TryManualPlay(target)                             ││
│  │    ├─ potion.EnqueueManualUse(target)                        ││
│  │    └─ PlayerCmd.EndTurn(player)                              ││
│  └─────────────────────────────────────────────────────────────┘│
│                                                                  │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │  NON-COMBAT BRANCHES (random handlers, _rng)                ││
│  │  Map → Event → Treasure → Rest → Shop → Victory → Menu      ││
│  └─────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
```

---

## 4. Bug Analysis — Known & Potential Issues

### 4.1 ⚠️ CRITICAL — Solved but fragile

#### B1. Draw check infinite wait (FIXED)
**File:** `AutoSlayNode.cs:320`  
**Old:** `if (drawCount > 0)` — `drawCount` never reaches 0 in STS2 (all undrawn cards stay in draw pile)  
**Fix:** Changed to `if (handCount == 0)` + `_drawJustFinished` flag with 0.3s extra delay  
**Risk:** If a relic/effect makes the player start with 0 cards in hand (e.g., Pandora's Box + no card draw), the solver will run with `hand=0` and crash or produce empty plan. The current code handles empty plan by ending the turn.

#### B2. Partial hand detection (FIXED)
**Problem:** Solver running mid-draw with only 2 cards in hand (turn 2+)  
**Fix:** `_drawJustFinished` flag — waits 0.3s after first card appears  
**Risk:** 0.3s may be too short or too long depending on animation speed/CPU. If too short → partial hand. If too long → wasted time.

### 4.2 ⚠️ Logic Issues

#### B3. Solver crash → immediate end turn
**File:** `AutoSlayNode.cs:459-476`  
If the solver throws any exception, the plan is nulled and the turn is ended immediately with no cards played. This means:
- A single buggy card (e.g., one that crashes `CardEffectReader`) silently skips the entire turn
- The `DebugInfo` for the crashed card won't appear because `CardEntry` already caught the exception
- Lost turns accumulate and can cause a winnable fight to become a loss

**Recommendation:** Add a fallback: if solver crashes, log the crashing card IDs and try a minimal plan (play highest-priority non-crashing cards).

#### B4. `CanPlayCard` discrepancy between filter and execution
**File:** `IroncladSolver.cs:604-609`  
In `Solve()`, cards are filtered by `CanPlayCard(card, energy)` before entering the search. But `CanPlayCard()` simply checks `card.CanPlay(out _, out _)` + cost. It does NOT call `CardEffectReader.ReadEffects()` first — so cards that would be playable based on their actual stats might not make it through.

More importantly, `ExecuteNextCombatStep()` (line 1161-1186) re-checks `action.Card.CanPlay(out var cantPlayReason, out _)` before playing. If the solver thought a card could target enemy A, but the game engine disagrees (e.g., because of an intangible debuff on that enemy), the card is silently skipped.

#### B5. No re-evaluation after skipped cards
**File:** `AutoSlayNode.cs:1162-1170`  
When a card is skipped during execution (not in hand, not playable, manual play fails), the plan moves to the next step. There's no re-evaluation of remaining hand cards. If cards 1-2 fail and card 3 succeeds, leftover energy from 1-2 is wasted.

#### B6. `_attemptedCards` leak in random play path
**File:** `CombatHandler.cs:18,84`  
`_attemptedCards` tracks tried cards to avoid infinite loops. It's cleared in `OnCombatEnded()` and `OnNonPlayPhase()`. But this handler is **only used in random mode** (which is currently disabled). In solver mode, this code is dead — no concern.

#### B7. `_chestOpened` static state leak
**File:** `TreasureRoomHandler.cs:12`  
`_chestOpened` is a static bool that's set to `true` when the chest is opened and `false` after clicking proceed. If the bot dies mid-treasure-room (e.g., from a relic that damages on pickup), the static persists and the next run would skip chest opening. The `Reset()` method exists but is never called from `AutoSlayNode`.

### 4.3 ⚠️ Solver-Specific Issues

#### B8. X-cost card energy handling
**File:** `IroncladSolver.cs:359-372`  
`GetEnergyOptions()` for X-cost cards generates: `[max(1, energy-1), energy-1+1, ..., energy]`. For energy=3, it generates `[2, 3]`. For energy=1, it generates `[1]`. This means at least 1 energy is always spent for X-cost cards. But some X-cost cards might be worth playing at 0 energy (e.g., if they have an enchant that grants block even at 0). However, since the DFS starts with `baseCost = entry.GetEnergyCost(0)` (which returns 0 for X-cost), and the cost check is `if (baseCost > state.Energy)` → 0 ≤ energy always true → X-cost cards always enter the search, but `GetEnergyOptions(energy=0)` returns an empty list → search skips to next card. This is correct behavior — X-cost cards at 0 energy produce no action.

#### B9. `BODY_SLAM` damage = current block
**File:** `IroncladSolver.cs:170`  
`dmg = state.Block` is set before Strength and Weak modifiers. This means `BODY_SLAM` damage = Block + Strength, then ×0.75 if Weak. But in the actual game, Body Slam damage is Block value (not affected by Strength). This overestimates Body Slam damage by Strength amount.

#### B10. No card draw simulation
The solver does NOT simulate card draw effects (e.g., Pommel Strike, Battle Trance). If a card draws cards, the solver doesn't add new cards to the search state's hand. The `ExecuteNextCombatStep()` at line 1128-1134 handles this by setting `_combatTurnRequested = false` when the plan completes without END_TURN — the solver runs again with the new hand. This is correct but the initial plan may be suboptimal because it doesn't know what will be drawn.

#### B11. Orb passive effects not simulated between cards
**File:** `IroncladSolver.cs:400-464`  
`ApplyCardEffects()` applies direct card effects but does NOT simulate orb passives between cards (Lightning dealing 3+Focus, Frost gaining 2+Focus block). This means multi-card plans involving orbs may be inaccurate — the solver doesn't know that a Frost orb will generate block before the enemy attacks.

#### B12. Enemy Weak/Vulnerable decay not simulated per-card
**File:** `IroncladSolver.cs:468-485`  
`ApplyDamageModifiers()` decrements Vulnerable and Weak stacks on each hit, which is correct for STS1. But in STS2, some statuses may work differently (e.g., Vulnerable might decay per turn, not per hit). This is unclear without STS2 documentation.

#### B13. Fallback card estimates are Ironclad-only
**File:** `CardEffectReader.cs:251-323`  
`FallbackEstimate()` contains hardcoded values for ~40 Ironclad cards. For Silent/Defect/Necrobinder/Regent cards, it falls through to the generic type-based defaults (Attack=6, Skill=5). This means if reflection fails, non-Ironclad characters get very inaccurate card values.

#### B14. Upgrade bonus is crude
**File:** `CardEffectReader.cs:316-323`  
Upgraded cards get +30% to all values, which is a rough approximation. Real upgrades vary widely — some double damage, some just reduce cost (which isn't reflected in this system).

### 4.4 ⚠️ Race Conditions / Timing

#### B15. `_combatCardDelay` accumulation
**File:** `AutoSlayNode.cs:141,304`  
`_combatCardDelay` is decremented by `delta` at line 141, but there are multiple return paths that set it and then `return` without the decrement happening. On the next frame, it's decremented. This means actual delays are `floor(delay / frame_time)` frames — inconsistent across different hardware/load conditions.

#### B16. `_drawJustFinished` not reset on combat end
**File:** `AutoSlayNode.cs`  
`_drawJustFinished` is reset at line 489 (enemy turn start). But if combat ends while `_drawJustFinished = true` (e.g., poison kills last enemy mid-draw), the flag stays true. At line 493-494, `_combatTurnRequested` is reset but `_drawJustFinished` is NOT. If a new combat starts, it could skip the draw delay.

### 4.5 ⚠️ Non-Combat Handler Issues

#### B17. Card reward skip: no "skip" detection
**File:** `CardRewardHandler.cs:9-17`  
Random handler always picks a random card holder — never skips. `AutoSlayCardSelector.GetSelectedCardReward()` also always picks a random card. There's no mechanism to skip card rewards even when all options are bad.

#### B18. Shop handler: infinite loop guard
**File:** `ShopHandler.cs:19-38`  
`HandleAsync()` has a `while (attempts++ < 50)` guard, which is good. But if the shop has exactly 0 purchasable items (all unaffordable), it immediately proceeds. If the shop has items but `OnTryPurchaseWrapper` fails silently (throws exception caught by `TaskHelper.RunSafely`), `attempts` still increments and you can hit 50 attempts buying nothing, wasting ~15 seconds.

#### B19. Event handler: Ancient event dialogue
**File:** `EventRoomHandler.cs:38-44`  
Hardcoded path `"%DialogueHitbox"` for the Ancient event. If this node name changes in a game update, the Ancient event becomes stuck (no options to click, no dialogue to advance).

#### B20. Main menu: character select assumes specific hierarchy
**File:** `AutoSlayNode.cs:775-851`  
`HandleMainMenu()` hardcodes paths like `"Submenus/SingleplayerSubmenu/StandardButton"`. These could change with game updates. Also, the method always selects `_character` (default IRONCLAD) — there's no UI to change this without editing config.

### 4.6 ⚠️ STS2 API Uncertainties

#### B21. `PlayerCmd.EndTurn` with `canBackOut: false`
**File:** `AutoSlayNode.cs:474`  
Setting `canBackOut: false` means the player cannot cancel the turn end. If the solver erroneously calls EndTurn when the player still has playable cards and energy, those are wasted. This is by design (no undo in auto-play mode) but makes solver bugs more punishing.

#### B22. `DebugOnlyGetState()` may return stale data
Multiple places call `RunManager.Instance?.DebugOnlyGetState()`. This method name suggests it's for debugging — it might not reflect the latest frame's state. If there's any lag between state updates and `_Process` ticks, the bot could act on stale information.

#### B23. `NOverlayStack.Instance?.ScreenCount` race
**File:** `AutoSlayNode.cs:255`  
Overlay check at line 255 is gated by `CombatManager.Instance?.IsInProgress == true`. But overlays can appear and disappear between frames. If an overlay appears and then disappears within one frame, the bot misses it entirely. This is unlikely but possible.

---

## 5. Summary of Most Impactful Bugs

| Level | Bug | Impact |
|-------|-----|--------|
| 🔴 HIGH | B16: `_drawJustFinished` not reset on combat end | Could skip draw delay in next combat → solver runs with empty hand |
| 🔴 HIGH | B3: Solver crash → silent turn skip | Buggy card wastes entire turn |
| 🟡 MEDIUM | B9: Body Slam overestimates damage | Suboptimal play |
| 🟡 MEDIUM | B11: Orb passives not between cards | Suboptimal multi-card Defect plans |
| 🟡 MEDIUM | B10: No card draw simulation | Solver plans may waste energy on draw cards |
| 🟡 MEDIUM | B13: Non-Ironclad fallback values inaccurate | Other characters get bad estimates if reflection fails |
| 🟢 LOW | B7: `_chestOpened` static state leak | Treasure room broken on next run after death |
| 🟢 LOW | B20: Hardcoded UI paths | Breaks on game updates |
