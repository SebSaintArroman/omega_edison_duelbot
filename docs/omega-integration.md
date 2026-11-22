# YGO Omega Protocol & Integration

## 1. Omega Custom-AI REST API Overview

YGO Omega communicates with external custom bots via a local HTTP REST service hosted at:
```
http://127.0.0.1:19999
```

When an AI duel room is created in Omega with bot type **AI_CUSTOM**, Omega exposes endpoints allowing external processes to query bot slots, provide deck lists, poll game states, query field cards, and submit decision responses.

---

## 2. API Endpoints Reference

All API calls are centralized in [OmegaBotWrapper/OmegaApiClient.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/OmegaBotWrapper/OmegaApiClient.cs):

| Endpoint | Method | Description |
|---|---|---|
| `/bot-list` | `GET` | Retrieves the list of bot slots in the active duel room, including their `BotID`, status, and deck assignment. |
| `/get-bot-state-header/{botId}` | `GET` | High-frequency polling endpoint. Returns `BotStateHeader` containing current state code, turn number, phase, current player, and action counter. |
| `/get-bot-state/{botId}` | `GET` | Detailed prompt data for the active decision (e.g., `BattleCmdState`, `IdleCmdState`, `SelectCardState`). |
| `/set-bot-data/{botId}/{deckName}/{deckHashSafe}` | `GET` | Assigns a deck name and base64-encoded deck hash to the specified bot slot. Readies the bot for duel start. |
| `/set-bot-response/{botId}/{packetBase64Safe}` | `GET` | Submits the bot's binary decision packet (encoded as URL-safe base64). Returns HTTP 200 on acceptance. |
| `/bot-field-info/{botId}` | `GET` | Returns full field overview from the bot's perspective (monsters, backrow, GY, banished, hand, deck counts, LP). |
| `/field-info` | `GET` | Returns field overview from the human player's perspective. |
| `/bot-field-card-info/{botId}/{c}/{loc}/{seq}` | `GET` | Queries detailed dynamic stats for a specific card on the field (ATK, DEF, level, status flags, counters, equip targets). |
| `/field-card-info/{c}/{loc}/{seq}` | `GET` | Queries card stats from the human player's perspective. |
| `/current-deck` | `GET` | Returns current deck data of the player. |
| `/card-list[/{filters}]` | `GET` | Queries database card definitions matching given filter flags. |
| `/rematch-duel` | `GET` | Triggers a rematch request from the post-duel results screen. |

---

## 3. The Polling Lifecycle (`DuelBotWrapper.cs`)

The core execution loop is managed by [OmegaBotWrapper/DuelBotWrapper.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/OmegaBotWrapper/DuelBotWrapper.cs):

1. **Connection (`Connect`)**:
   - Polls `/bot-list`.
   - Locates an available slot where `DeckIndex == 0`, `CurrentStateCode == WaitingForDeck`, and `IsReady == false`.
   - Sends deck name and compressed deck hash via `/set-bot-data`.
2. **Heartbeat Loop (`Start` / `Heartbeat`)**:
   - Loops while duel is active, polling `/get-bot-state-header` every 50ms (`Const.HEARTBEAT_DELAY`).
   - Increments an internal `_fieldGeneration` counter on every heartbeat to invalidate cached field queries.
   - Detects turn transitions (`CurrentTurn != _lastTurnSeen`) and triggers `TurnStarted` event for state snapshots.
   - Dispatches decision prompts via `BotStateDispatcher.DispatchAsync`.
   - Encodes response byte array into URL-safe base64 and transmits via `/set-bot-response`.
3. **Field Caching**:
   - Querying the field and individual cards repeatedly within one decision would generate dozens of synchronous HTTP calls.
   - `DuelBotWrapper` caches `FieldInfo` and `DynamicCard` queries for the duration of the current `FieldGeneration`, ensuring fast heuristic evaluations.

---

## 4. State Dispatching & Response Validation

### 4.1 State Dispatching (`BotStateDispatcher.cs`)
Maps engine state codes to strongly typed handlers:
- `SelectBattleCmd` $\rightarrow$ `HandleBattleCmdAsync`
- `SelectIdleCmd` $\rightarrow$ `HandleIdleCmdAsync`
- `SelectCard` $\rightarrow$ `HandleSelectCardAsync`
- `SelectChain` $\rightarrow$ `HandleSelectChainAsync`
- `SelectPosition` $\rightarrow$ `HandleSelectPositionAsync`
- `SelectYesNo` / `SelectEffectYn` $\rightarrow$ `HandleSelectEffectYnAsync`
- `SelectSum` / `SelectTribute` $\rightarrow$ `HandleSelectSumAsync` / `HandleSelectTributeAsync`

### 4.2 Omega Index Repair (`ActivationIndexRepair.cs`)
A known defect in Omega's API causes `ActivableCards` lists to occasionally contain cross-contaminated activation indices from other cards (e.g., Vayu receiving Heavy Storm's activation index). `ActivationIndexRepair.Repair` scans the prompt array and rebuilds each card's `ActionActivateIndex` to match its true positional offset.

### 4.3 Response Validation (`BotResponseValidator.cs`)
Before sending any response packet to Omega, the validator inspects the payload:
- **BattleCmd**: Clamps action codes (0-3). If `Attack` is chosen but `AttackableCards` is empty, or `ToMainPhaseTwo` is chosen when disallowed, falls back safely to `ToEndPhase`.
- **IdleCmd**: If `ToBattlePhase` is chosen when `CanBattlePhase == false`, falls back to `ToEndPhase`.
- **Card Selection**: Clamps selection counts to $[ \text{Minimum}, \text{Maximum} ]$. Filters out out-of-bounds indices and pads missing selections with valid sequential indices.
- **SelectPosition**: Ensures declared battle position exists in `SelectablePositions`.

### 4.4 Binary Serialization (`BotResponseWriter.cs`)
Serializes validated answers into exact little-endian binary buffers:
- **BattleCmd / IdleCmd**: `ushort Action`, followed by `ushort Index` (or `int`).
- **Card Selection**: `byte Count`, followed by `byte[] Indices`.
- **SelectChain**: `int ChainIndex` (-1 for pass).
- **Boolean / YesNo**: `byte 1` (Yes) or `byte 0` (No).
- **Position**: `byte PositionCode`.

---

## 5. Known Server-Side API Limitations

The README and source code document two critical limitations imposed by Omega's custom bot API:

1. **Missing Direct Attack Prompts**:
   - **Issue**: Omega never sends a "Declare direct attack?" prompt. Any monster capable of attacking directly will always attack directly, even if opponent monsters are present on the field.
2. **Field Zone Disabling Crash**:
   - **Issue**: Prompts requiring zone selection for column/zone disabling (e.g., Ojama King, Boompoline!!) are not handled by Omega's custom-AI server and cause the duel session to hang or abort.

---

## 6. Web Control Service (`BotTest/Web/`)

`BotTest` embeds an ASP.NET Core minimal web API running on port `58966`:
- **AI Room Web UI** (`wwwroot/index.html`, `app.js`, `styles.css`): Visual dashboard showing duel status, current turn, board breakdown, and search thinking lines.
- **Auto-Join Loop (`BotControlService.AutoLoopAsync`)**:
  - Automatically polls Omega every 3 seconds (`AutoPollMs`).
  - When an unassigned bot slot appears, automatically starts a bot with the user-selected deck (or draws a random deck).
- **Terminal Capture (`TerminalCapture.cs`)**:
  - Intercepts all `Console.Out` writes from the bot process into a memory ring buffer, streaming logs live to the browser UI.
- **User Comments (`comments.jsonl`)**:
  - Users can click "Comment on this duel" in the UI to submit feedback on misplays, which is saved alongside the exact decision number and duel log path.
