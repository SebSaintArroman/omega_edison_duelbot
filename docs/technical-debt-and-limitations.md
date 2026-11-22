# Limitations, Workarounds & Technical Debt

## 1. YGO Omega API Limitations & Workarounds

Interfacing with YGO Omega's custom bot API requires working around several architectural constraints and quirks in the server:

### 1.1 Unconditional Direct Attacks
- **Observed Behavior**: Omega never prompts the bot with "Attack directly?". If a monster possesses an effect allowing direct attacks (or when the opponent has no monsters), Omega automatically executes a direct attack.
- **Bot Workaround**: The bot cannot choose to attack a monster if that monster could be bypassed for a direct attack under certain card conditions.

### 1.2 Zone-Disabling Server Hangs
- **Observed Behavior**: Effects requiring a player to select and disable a field zone (e.g., *Ojama King*, *Boompoline!!*) are not supported by the custom-AI server protocol. Receiving such prompts can freeze or terminate the duel.
- **Status**: Known external limitation; unhandled by design.

### 1.3 Activation Index Desynchronization (`ActivationIndexRepair.cs`)
- **Observed Behavior**: In prompts with multiple activable cards, Omega occasionally sends an `ActionActivateIndex` array on one card that contains indices belonging to another card (e.g., Vayu receiving Heavy Storm's activation slot, causing the bot to unintentionally activate Heavy Storm).
- **Bot Workaround**: [OmegaBotWrapper/ActivationIndexRepair.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/OmegaBotWrapper/ActivationIndexRepair.cs) reconstructs activation indices from the prompt's positional order before passing prompt data to the brain.

---

## 2. Simulation & Replay Desynchronization (`REPLAY MISS`)

The MCTS re-evaluates decisions mid-turn by reconstructing the turn from an initial pre-draw snapshot and replaying observed actions up to the current prompt.

### Common Causes of Replay Misses:
1. **Prompt Granularity Discrepancies**:
   - Omega asks for certain discards or material selections one card at a time via `SelectUnselectCard` (e.g., discarding 8 stars for Machina Fortress).
   - The native EDOPro rules engine (`ocgcore`) presents this prompt as a single `SelectSum` prompt.
   - Handled by custom translation logic in [BotTest/Game/AI/Brains/DeckBotBrain.TurnLog.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/BotTest/Game/AI/Brains/DeckBotBrain.TurnLog.cs) (`ReplayKeys.SumPacket`), but edge cases can still cause replay failures.
2. **Snapshot Timing**:
   - If the turn-start snapshot is captured *after* the opening draw occurs, the drawn card's identity cannot be separated from the initial hand, causing the replay to fail.
3. **Hidden Information Shifts**:
   - If the opponent activates an effect that reveals previously unknown cards in their hand, lines simulated prior to the reveal become invalid.
   - When a replay miss occurs, the search aborts and the heuristic brain's proposed move is executed directly, preventing corrupted searches from making bad plays.

---

## 3. Concurrency & Performance Trade-Offs

### 3.1 Dedicated Search Threads vs. .NET ThreadPool
- **Issue**: .NET ThreadPool threads were originally used for MCTS search workers. Because the bot runs an active ASP.NET Core web server and executes blocking HTTP calls to Omega, ThreadPool worker threads suffered starvation, reducing search throughput from ~1,000 lines/sec to under 10 lines/sec.
- **Resolution**: `Mcts.Run` explicitly creates dedicated `System.Threading.Thread` instances.

### 3.2 Native Memory vs. Position Reload Speed (`PuzzleEngine`)
- **Issue**: Calling `OCG_CreateDuel` takes ~75ms; reloading a puzzle script on an existing duel takes ~7ms. However, calling `Debug.ReloadFieldBegin` repeatedly on a single native duel handle causes gradual memory fragmentation and state leakage in Lua.
- **Resolution**: `PuzzleEngine` reloads on the same handle for up to 2,000 lines (`RebuildEvery = 2000`), after which the native duel is destroyed and cleanly reallocated.

### 3.3 Abandonment of Opponent Dead-Time Search
- **Background**: Earlier versions attempted to run MCTS during the opponent's turn to pre-compute our subsequent turn.
- **Finding**: In real games, opponent actions (summons, backrow sets, removals) modified the field so drastically that the projected tree almost never matched the actual board when our turn arrived (a 0% tree reuse rate).
- **Resolution**: Dead-time planning was disabled by default (`DeadTimePlanning = false`). Instead, the first decision of each turn receives an additional 2,000ms budget (`DefaultTurnStartMs = 2000`) to compute a high-quality opening plan.

---

## 4. Architectural Technical Debt

1. **Source File Duplication in `DuelSim.csproj`**:
   - `DuelSim.csproj` includes `..\BotTest\**\*.cs` and `..\OmegaBotWrapper\**\*.cs` via `<Compile>` globbing rather than referencing `BotTest.csproj` as a `<ProjectReference>`.
   - **Reason**: Allows developer to run `dotnet build DuelSim\DuelSim.csproj` while `BotTest.exe` is actively running, without triggering file lock errors on `BotTest.dll`.
   - **Trade-off**: Both projects compile duplicate copies of the bot source files, increasing compilation times.

2. **Artificial 3x Pot of Greed Injection**:
   - Every Master Duel bot deck in `DeckManager.cs` includes 3 copies of *Pot of Greed* (`55144522`).
   - The bot is hardcoded to immediately activate Pot of Greed whenever drawn before planning main lines.
   - While advantageous for testing, this renders the decks illegal under standard Edison tournament lists.

3. **Absence of Unit Tests**:
   - There is no automated xUnit/NUnit test suite covering individual helper methods, state parsers, or evaluators.
   - Regression testing relies on running offline duels in `DuelSim` or running scenario benchmarks.
