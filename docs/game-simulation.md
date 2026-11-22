# Game Simulation & Rules Engine

## 1. Overview of the Rules Engine

Rather than attempting to reimplement the vast and complex rules of Yu-Gi-Oh!, this repository integrates the standard open-source C/C++ rules engine: **EDOPro's `ocgcore`** (commit `9cd0936`, built from `edo9300/ygopro-core`).
- **Binary**: [DuelSim/vendor/ocgcore-x64/ocgcore.dll](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/DuelSim/vendor/ocgcore-x64/ocgcore.dll) (64-bit Windows dynamic library).
- **Scripts**: ProjectIgnis official Lua scripts (~13,700 card scripts) located under [DuelSim/vendor/scripts/official/](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/DuelSim/vendor/scripts/official/).
- **Card Database**: BabelCDB SQLite database located at [DuelSim/vendor/cdb/cards.cdb](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/DuelSim/vendor/cdb/cards.cdb).

The engine executes card scripts inside an embedded Lua state, handling timing, chain links, damage calculations, continuous effects, and trigger order according to official Konami OCG/TCG rules.

---

## 2. P/Invoke Native Bindings (`OcgApi.cs`)

Low-level interaction with `ocgcore.dll` is defined in [DuelSim/src/Native/OcgApi.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/DuelSim/src/Native/OcgApi.cs).
Key unmanaged functions include:
- `OCG_CreateDuel(out IntPtr duel, ref DuelOptions options)`: Allocates an unmanaged duel instance with seeds, rule flags, and callback delegates.
- `OCG_DestroyDuel(IntPtr duel)`: Releases the unmanaged duel memory and associated Lua state.
- `OCG_DuelNewCard(IntPtr duel, ref NewCardInfo info)`: Injects card instances into starting locations (Deck, Hand, Extra, Grave, Field).
- `OCG_StartDuel(IntPtr duel)`: Commences the duel state machine.
- `OCG_DuelProcess(IntPtr duel)`: Steps the engine forward. Returns status codes:
  - `0` (`StatusEnd`): Duel has concluded.
  - `1` (`StatusAwaiting`): Engine is waiting for an external response/decision.
  - `2` (`StatusContinue`): Processing ongoing; more messages are buffered.
- `OCG_DuelGetMessage(IntPtr duel, out uint length)`: Extracts binary message buffers containing engine events and prompts.
- `OCG_DuelSetResponse(IntPtr duel, byte[] buffer, uint length)`: Supplies player response bytes to the engine.
- `OCG_LoadScript(IntPtr duel, byte[] buffer, uint length, string name)`: Loads supplementary Lua scripts into the engine's Lua state.
- `OCG_DuelQueryLocation`, `OCG_DuelQuery`: Queries field card attributes (ATK, DEF, position, equip targets, counters, overlays) directly from engine memory.

### Callback Function Pointers
`DuelOptions` registers four crucial C-callable delegate pointers:
1. `CardReader`: Invoked by `ocgcore` whenever card stats (code, type, attribute, race, attack, defense) are needed. Reads directly from the local SQLite `CardStore`.
2. `ScriptReader`: Invoked when the engine requires a card's Lua script (`c<code/alias>.lua`).
3. `LogHandler`: Captures engine diagnostic warnings and Lua syntax errors.
4. `CardReaderDone`: Cleans up allocated card memory when queries finish.

---

## 3. Edison Format & Rule Emulation

To accurately reflect Edison format (April 2010 banlist under Master Rule 1), the engine configuration applies specific flags defined in [DuelSim/src/Native/OcgApi.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/DuelSim/src/Native/OcgApi.cs) and [DuelSim/src/Engine/DuelHost.Search.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/DuelSim/src/Engine/DuelHost.Search.cs):

```csharp
public const ulong DuelModeEdison = 0x00000000; // DUEL_MODE_MR1 (Master Rule 1)
public const ulong Duel1stTurnDraw = 0x01;       // Player going first draws on turn 1
```

### Edison Rule Features Modeled:
1. **Ignition Priority**: Turn player monster ignition effects (Dark Armed Dragon, Dark Grepher, Lyla, Chaos Sorcerer, Machina Gearframe equip) can activate in response to their own summon before opponent spell-speed 2 traps can be triggered.
2. **Turn 1 Draw**: The player going first draws a card in their opening Draw Phase.
3. **Single Field Spell**: Only one active Field Spell is permitted across both fields.
4. **No Extra Monster Zone**: Extra Deck monsters summon directly into standard Main Monster Zones.
5. **No Errata Effects**: Cards operate with their original 2010 text and rulings.

---

## 4. Pre-Errata Cards & Omega ID Translation

YGO Omega handles pre-errata cards by assigning them custom IDs computed as:
$$\text{Omega ID} = \text{Official ID} + 20$$
For example:
- **Brionac, Dragon of the Ice Barrier**: Official `50321796` $\rightarrow$ Omega `50321816`
- **Goyo Guardian**: Official `7391448` $\rightarrow$ Omega `7391468`
- **Ryko, Lightsworn Hunter**: Official `21502796` $\rightarrow$ Omega `21502816`
- **Future Fusion**: Official `77565204` $\rightarrow$ Omega `77565224`

### Implementation in the Simulator:
1. **Alias Mapping** ([DuelSim/src/Data/OmegaIds.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/DuelSim/src/Data/OmegaIds.cs)):
   - When the bot queries card data using Omega IDs, the database resolves them back to official cards while retaining the Omega ID identity.
2. **Custom Pre-Errata Lua Scripts** ([DuelSim/scripts/pre-errata/](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/DuelSim/scripts/pre-errata/)):
   - Hand-crafted Lua scripts exist for key Edison cards that have received modern erratas.
   - `c50321816.lua` (Brionac): Restores soft once-per-turn bouncing of cards on either field.
   - `c7391468.lua` (Goyo Guardian): Removes the modern "1 EARTH Tuner" requirement, allowing any Tuner.
   - `c21502816.lua` (Ryko): Restores targeted destruction upon activation followed by milling 3 cards.
   - `c77565224.lua` (Future Fusion): Sends fusion materials to the graveyard immediately on activation.

---

## 5. In-Engine Position Recreation (Puzzle Scripts)

During search (MCTS), the bot does not simulate entire duels from turn 1. Instead, it observes the current game board in Omega, constructs an equivalent position, and projects forward from that exact moment.

### Rebuilding via Puzzle Scripts
In [DuelSim/src/Engine/DuelHost.Search.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/DuelSim/src/Engine/DuelHost.Search.cs) and [BotTest/Game/AI/Search/SearchPosition.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/BotTest/Game/AI/Search/SearchPosition.cs), the game state is serialized into an in-memory Lua script utilizing the engine's built-in puzzle setup primitives:

```lua
Debug.ReloadFieldBegin(flags)
Debug.SetPlayerInfo(0, ourLP, 0, 0)
Debug.SetPlayerInfo(1, oppLP, 0, 0)

-- Populate Monsters, Spells, Hands, GY, Banished, Extra Deck
Debug.AddCard(code, controller, controller, location, sequence, position)
...
Debug.ReloadFieldEnd()
```

### Optimization: `PuzzleEngine` Epoch Re-arming
Native duels take significant CPU time to instantiate because all global utility scripts (`constant.lua`, `utility.lua`) and card scripts must be loaded and compiled.
- `PuzzleEngine` reuses the same native duel across thousands of lines.
- Calling `Debug.ReloadFieldBegin` clears active cards, zones, and continuous effects without wiping preloaded Lua scripts.
- To prevent once-per-duel card setup scripts (`aux.GlobalCheck`) from breaking across reloaded positions, `PuzzleEngine` injects a dynamic epoch counter:
  ```lua
  _st_epoch=(_st_epoch or 0)+1
  function Auxiliary.GlobalCheck(s,func)
      if s.global_check~=_st_epoch then
          s.global_check=_st_epoch
          func()
      end
  end
  ```
- This reduces position loading time from ~75ms to under 7ms per line.

---

## 6. Engine Message Loop & Prompt Translation

When `OCG_DuelProcess` returns `StatusAwaiting`, binary messages in `OCG_DuelGetMessage` are parsed by [DuelSim/src/Engine/MessageReader.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/DuelSim/src/Engine/MessageReader.cs) and converted into high-level prompt objects (`Prompt.cs`):
- `MSG_SELECT_IDLECMD`: Turn player's open Main Phase window $\rightarrow$ translated to `IdleCmdData`.
- `MSG_SELECT_BATTLECMD`: Battle Phase action prompt $\rightarrow$ translated to `BattleCmdData`.
- `MSG_SELECT_CARD` / `MSG_SELECT_TRIBUTE`: Card target or material selection prompt $\rightarrow$ translated to `SelectCardData` / `SelectTributeData`.
- `MSG_SELECT_CHAIN`: Chain response window $\rightarrow$ translated to `SelectChainData`.
- `MSG_SELECT_EFFECTYN` / `MSG_SELECT_YESNO`: Optional trigger confirmation $\rightarrow$ translated to `SelectEffectYnData` / `SelectYesNoData`.
- `MSG_SELECT_SUM`: Synchro or tribute material combination selection $\rightarrow$ translated to `SelectSumData`.
- `MSG_SELECT_POSITION`: Battle position selection $\rightarrow$ translated to `SelectPositionData`.

When the bot formulates its response packet, `DuelHost.TranslatePacket` converts the bot's binary format back into the raw integer/index buffers required by `OCG_DuelSetResponse`.
