# TokenSpire2 LAN Multiplayer — Requirements Document

> **Status:** Draft — all previous LAN code deleted. This is a clean requirements doc for the next rewrite attempt.

---

## 1. Goal

Enable two instances of Slay the Spire 2 on the same LAN to play together:
- **Host (human):** Plays manually, makes all map/event/shop/rest decisions
- **Client (bot):** Runs TokenSpire2 auto-battle AI, follows the host through the run

---

## 2. What We Know About STS2 Networking

### 2.1 Architecture
- STS2 uses **Steam Matchmaking** (lobbies) + **ENet** (UDP) for network transport
- Combat is **deterministic lockstep**: both instances execute the same actions with the same RNG state
- `CombatStateSynchronizer` checks checksums — any divergence causes "Multiplayer data desync" freeze
- The game has built-in classes: `JoinFlow`, `Lobby`, `StartRunLobby`, `SteamNetGameService`

### 2.2 The Core Problem
Both instances run under the **same Steam account**, so:
- The Steam friends list is empty (can't see yourself)
- Steam Matchmaking may not let you join your own lobby via the normal UI
- The game's "Join Game" flow expects to pick from a friends list

### 2.3 What Failed Previously
1. **TCP Broker** (BrokerServer.exe on 127.0.0.1:9999) — relayed ENet packets between instances. Too many fragile Harmony patches. Client couldn't discover the host's lobby.
2. **Virtual friend injection** (Direction D) — Patching Steam Friends API to inject a fake friend. The game's join button doesn't use `SteamFriends` API to enumerate friends.
3. **Direct JoinFlow.Begin()** — Instance method, requires proper lobby context.
4. **Thread.Sleep on main thread** — Caused Windows green-screen (not responding). All delays must be frame-based.

---

## 3. Requirements for the Rewrite

### 3.1 Simplicity First
- **Minimize Harmony patches.** Each patch is a point of failure when the game updates.
- **No TCP Broker.** The broker added complexity (separate process, message relay, serialization) without solving the fundamental discovery problem.
- **No Steam API hacking.** Too brittle, too many unknowns about the game's internal Steam usage.

### 3.2 Recommended Approach: Couch Coop (Single Instance)
STS2 natively supports **Couch Coop** (multiple players on one machine). This is the simplest path:

1. **Host starts a Couch Coop run** from the main menu
2. **Host presses a key** to add a second player via `AddPlayerDebug()`
3. The bot takes control of Player 2, auto-battling in combat
4. Both players share the same screen — no networking needed

**Advantages:**
- Zero networking code
- Zero Harmony patches for networking
- No desync possible (shared game state)
- Already partially implemented in the codebase (`FindHumanPlayerCreature`, `MultiplayerCards`)

**Disadvantages:**
- Both players must be on the same PC (not true LAN)
- Shared screen (both see the same UI)

### 3.3 Alternative: Reverse-Engineer the Lobby Join
If true LAN (two PCs) is required, the cleanest approach is:

1. **Host creates a Steam lobby** normally via the game's Multiplayer menu
2. **Client needs a way to join that lobby** without the Steam friends list

Possible approaches to investigate:
- **Steam lobby ID:** If the host can log the lobby ID, the client might join via `SteamMatchmaking.JoinLobby()` directly (bypassing the friends list UI)
- **Game's internal join command:** Check if there's a console command or debug function to join by lobby ID
- **Lobby data key:** Host writes lobby ID to a shared file; client reads it and calls the join API

This path needs more reverse-engineering of the game's `JoinFlow` and `SteamNetGameService` classes.

---

## 4. Non-Negotiable Constraints

### 4.1 No `Thread.Sleep` on the Godot Main Thread
All delays must use frame counting (`_delta` accumulation). `Thread.Sleep` blocks the engine and causes green-screen freezes on Windows.

### 4.2 No `PlayerCmd.EndTurn` in Multiplayer
`PlayerCmd.EndTurn` is a local-only API. In multiplayer, ending the turn must go through the network action pipeline (UI button click → `PlayerInputCmd` → sync to all players).

### 4.3 No `TryManualPlay` for Network-Synced Card Play
`TryManualPlay` changes local state before network sync, causing RNG divergence. Cards must be played through the UI (click the card actor → game's input pipeline → deterministic execution).

### 4.4 Human Host Controls Non-Combat Decisions
The human host makes all map pathing, event, shop, and rest decisions. The bot only auto-plays combat.

---

## 5. What to Keep from the Current Codebase

These files/classes are useful for any multiplayer approach:
- `ScreenDetector` — detects which game screen is active
- `AppConfig` — centralized config (add coop settings when needed)
- `IroncladSolver` / `DecisionEngine` — combat AI
- `AutoSlayNode._Process` loop — frame-based main loop pattern
- `AutoSlayHelpers.ForceClick` — UI interaction utility

---

## 6. What Must Be Rewritten from Scratch

- All networking/Harmony patches for LAN
- Any lobby join flow
- Any broker/message relay
- Any Steam API interception

---

## 7. First Step

Before writing any code, do a focused reverse-engineering session:

1. **Explore Couch Coop path:** Can we make the bot control Player 2 in a single-instance Couch Coop session? This avoids networking entirely.
2. **If true LAN is required:** Find the minimum API calls needed for a client to join a host's lobby by ID (bypassing the friends list UI).

Start with Couch Coop — it's the simplest path and may satisfy the actual use case (one person running two instances for testing/optimization).
