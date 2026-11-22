# DuelSim: bot vs bot, offline

Plays BotTest's deck bots against each other without Omega, so you can run many duels unattended and read the logs.

## How it works

```
 bot (P0)  ─┐                                    ┌─ EDOPro rules engine (ocgcore.dll)
            ├─ HTTP calls ─► OmegaEmulator ─► DuelHost ─┤  + ProjectIgnis card scripts (Lua)
 bot (P1)  ─┘   (in memory)                      └─ + BabelCDB card database
```

- **Real rules.** Duels run on EDOPro's engine (ocgcore), with real card scripts. Effects, chains and timing are handled by the engine, not re-implemented.
- **Bots unchanged.** They talk to `OmegaApiClient.Default` as usual. The simulator swaps that client's HTTP handler for an in-memory emulation of Omega's bot API (same endpoints, same JSON), so nothing in `BotTest`/`OmegaBotWrapper` is modified.
- **Seat routing.** Each bot runs with its seat in an `AsyncLocal`, so its calls, including the static "player" ones, go to the right side.
- **Message translation.** `DuelHost` converts the engine's select messages into Omega's state structs (`IdleCmdData`, `SelectCardData`, ...) and the bots' response packets back into engine responses.
- **Fallbacks.** When a bot gives an answer the engine rejects, the bot is asked once more. After that, a safe fallback is used and a warning goes into the transcript.
- **Hidden information.** Mirrors Omega: the opponent's hand, deck, and face-down cards report id 0.
- **Own build.** The bot sources are compiled into this project, so building DuelSim never touches BotTest's or OmegaBotWrapper's `bin`/`obj`. It always uses the current bot code.

## Setup

```powershell
.\DuelSim\setup.ps1          # downloads the engine, scripts and card DB into DuelSim\vendor (gitignored, ~170 MB)
dotnet build DuelSim\DuelSim.csproj
```

The simulator builds as a self-contained win-x64 app with the 64-bit engine DLL (vendor/ocgcore-x64, built from vendor/ygopro-core). No .NET install is needed.

## Run

From the repo root (`C:\Users\Seb\duelbotwrapper`), in PowerShell:

```powershell
# Rebuild after any bot change: the simulator compiles its own copy of the bot code,
# so rebuilding BotTest alone doesn't update it.
dotnet build DuelSim\DuelSim.csproj

# Run duels
DuelSim\bin\Debug\net9.0\win-x64\DuelSim.exe run Vayu Vayu --duels 15
DuelSim\bin\Debug\net9.0\win-x64\DuelSim.exe run Vayu MachinaControl --duels 20 --seed 42 --no-bot-logs
```

Options for `run <DeckA> <DeckB>`:

| Option | What it does |
|---|---|
| `--duels N` | Number of duels. The decks alternate going first. |
| `--seed N` | Shuffle seed. The same seed and the same code give the same duels, so use a new seed for fresh games. |
| `--no-bot-logs` | Skips the bots' own `.log`/`.jsonl` files and keeps only the transcript and summary. |
| `--timeout-min N` | Cancels a duel that runs longer than N minutes (default 20). |

Other commands:

```powershell
DuelSim\bin\Debug\net9.0\win-x64\DuelSim.exe list     # deck names you can use
DuelSim\bin\Debug\net9.0\win-x64\DuelSim.exe check    # engine, card data and decks load?
```

Ctrl+C stops a run. Finished duels keep their folders, but `summary.txt` is only written when the run completes.

Each duel takes about 10–35 s, depending on its length.

Output goes to `DuelSim/out/<time>_<A>-vs-<B>/`:
- `summary.txt`: wins per deck, going first vs second, errors and warnings.
- Per duel, in `duel-NNN/`:
  - `transcript.txt`: play-by-play from the engine: summons, chains, attacks, moves, LP and simulator warnings.
  - Each bot's usual duel log (`.log` and `.jsonl`), named `<Deck>-P0` / `<Deck>-P1`.
  - `bot-console.txt`: the bots' console output.

The decks alternate going first.

Bot logs are named `<time>_BOTvBOT_<Deck>-P<seat>_vs_<OppDeck>.log`. The BOTvBOT flag is also in each log's header line, so they can't be mixed up with logs from duels against you (those stay in `BotTest/duel-logs`).

**Progress:**
- **Console:** a live line shows the duel number, turn, turn player, LP and elapsed time.
- **`progress.txt`:** in the run folder, rewritten every turn, with the current duel's folder and the results so far. Open it to check on a long run.

**Loop guard:** a duel is cancelled, recorded as `stalled ... (probably a loop)` with error `loop`, and the run moves on when either:
- the same player activates the same effect of the same card 5 times in a row in one turn (two effects of one card
  alternating, like Indomitable Gladiator Beast from the hand and then its recycle from the graveyard, is not a loop), or
- a turn passes 400 decisions.

A duel is also cut off after 20 minutes (`--timeout-min`) or at turn 150.

## Tuning (evolve)

`evolve` tunes a deck bot's numbers automatically, with no Claude needed. Leave it running and check on it later.

```powershell
dotnet build DuelSim\DuelSim.csproj          # after any bot change
DuelSim\bin\Debug\net9.0\win-x64\DuelSim.exe evolve                  # Vayu, until Ctrl+C
DuelSim\bin\Debug\net9.0\win-x64\DuelSim.exe evolve --generations 20 --duels 100 --workers 6
DuelSim\bin\Debug\net9.0\win-x64\DuelSim.exe params vayu-test-1      # what the champion changes vs Vayu
DuelSim\bin\Debug\net9.0\win-x64\DuelSim.exe run vayu-test-1 Vayu --duels 50   # play a variant like any deck
```

**What gets tuned** (159 values for Vayu). The original deck (`Vayu`) always plays the values in the code, untouched.
- **Numbers:** the brain's public numeric properties, such as `PushHandFactorPerCard`, `SetCardIsMirrorForceChance`, `HoldWhenClockAtMost` and `HandDamagePerCard` (49).
- **Rule probabilities** (110): every rule of the Vayu brain and the shared deck brain has a named gate, listed in `VayuRules` / `DeckRules` in BotTest. In a variant file these appear as `rule:vayu.trooper-mill`, `rule:deck.crash-trades` and so on.
  - Each gate is the chance the rule fires when its conditions hold. The default of 1 means as written, and 0 switches the rule off.
  - When a rule is skipped by its roll, the duel log says so: "rule vayu.value-set skipped this time (fires 26% of the time)".
  - Follow-up steps of a play that's already been decided (e.g. the synchro Plaguespreader was summoned for) aren't gated, so plays don't break halfway.
- **New rules:** mutation can't write them. Add them by hand as candidates (a gate that defaults to 0 via `RuleGates.DefaultOverrides`), and evolve can switch them on if they win.

**How a generation works:**
1. `vayu-test-1` is the champion. It starts as a copy of Vayu's defaults.
2. `vayu-test-2`, the challenger, is the champion with random values changed (`--mutations 8`). Numbers are scaled by about ±30% (`--strength 0.3`), small integers move by ±1, and rule probabilities move by about ±0.15. Probabilities stay in 0–1.
3. They play `--duels` duels (default 100), split across `--workers` parallel DuelSim processes.
4. The challenger becomes the new champion only if it wins at least `--accept` of the decided duels (default 55%), so lucky noise rarely wins.
5. Every `--baseline-every` generations (default 5), the champion also plays the original Vayu (`--baseline-duels`), to catch overfitting to its own mirror.

**Files:**
- `tuning/vayu-test-1.json` / `-2.json`: the variants (base deck, a note on where they came from, every parameter).
- `tuning/vayu-test-history.csv`: one row per match (wins, draws, errors, deck-outs, average turns, accepted, changes). It opens in Excel.
- `tuning/vayu-test-evolve.log`: the readable log.
- `out/evolve/vayu-test/gen-NNNN-*/`: per-worker results and logs. The duel folders are deleted unless `--keep-duels`.

**Stopping and resuming:** Ctrl+C stops after the current match. Running `evolve` again carries on from the last generation with the saved champion. Start a separate line with `--name something-else`.

**Speed:** about 20 s per duel per worker, so 100 duels on 6–7 workers takes roughly 5 minutes per generation (plus a baseline every 5 generations).

**Using a result:** `params vayu-test-1` lists the changed values. To keep them, copy them into the brain's property defaults in the code.

## Rule win rates (analysis of many duels)

Every rule a bot applies (`DeckBotBrain.Rule`) is recorded with the **board genre** at that moment, the bot's deck, the
opponent's real deck and whether that bot won. Each run keeps its record in `rule-events.tsv`, appended as each duel
finishes (bot logs not needed: works with `--no-bot-logs`). After every run, `out/ANALYSIS.md` (and `out/ANALYSIS.csv`
with every number) is rebuilt from all current runs; superseded runs drop out with their folder. To rebuild by hand:

```
DuelSim\bin\Debug\net9.0\win-x64\DuelSim.exe analyze [run folders...] [--min 30] [--out file.md]
```

**The rule x board-genre matrix**: `out/ANALYSIS.html` (open in a browser) has, per deck, every rule (rows) in every
board genre (columns): the win rate of the duels where the rule fired in that genre, and the event count. Cells are
coloured by lift over the deck's win rate (green better, red worse) and faded below `--min` events. The same matrix as
a spreadsheet: `out/ANALYSIS-matrix.csv`.

`ANALYSIS.md` summarises, per deck: its win rate (overall and per matchup), and per rule its events, win rate, **lift** over
the deck's win rate, the 95% margin, the *skipped* comparison (probabilistic rules rolled not to fire: same situation,
the other choice), and its best and worst board genre. Correlation, not causation.

**Board genres** (`BotTest/Game/AI/Brains/BoardGenre.cs`): every board gets exactly one situation (the first that
applies): we're in lethal range / they're in lethal range / both boards empty / their board empty with a big (4+) or
small hand / our board empty vs a strong monster (2400+) or not / they have a monster we can't beat / they outnumber us /
we outnumber them / even boards; plus their backrow (none, 1-2, 3+) and whose turn: 58 genres at most.

## Opponent deck data (Edison lists)

The bots read the opponent's deck from real Edison deck lists: `BotTest/Game/AI/Brains/Data/edison-decks.json`
(embedded in BotTest and DuelSim). It holds the 10 best-placing lists of every Edison deck type with 10+ lists on
[Format Library](https://formatlibrary.com) (220 lists, 22 types), with each card's kind and name.

To refresh it:

1. Download the lists with `scripts\fetch-edison-decks.ps1 path\to\decks.json`. It pages
   `https://formatlibrary.com/api/decks?filter=formatName:eq:Edison&limit=100&page=N`, then fetches each sampled deck's
   YDK from `https://formatlibrary.com/api/decks/<id>`, into a `decks.json` (`[{id, type, placement, main[], extra[], side[]}]`).
2. Convert it for the bot (official ids, card kinds, names):

```
DuelSim\bin\Debug\net9.0\win-x64\DuelSim.exe edison-data path\to\decks.json ..\BotTest\Game\AI\Brains\Data\edison-decks.json
```

To add the bot's own lists (every Master Duel deck in the catalog, ids 900001+, under their Edison deck type) and keep
every list already there, rebuild from the data file itself. Rerun it after adding or changing a bot deck; the bot lists
are rebuilt each time and the downloaded lists are kept:

```
DuelSim\bin\Debug\net9.0\win-x64\DuelSim.exe edison-data ..\BotTest\Game\AI\Brains\Data\edison-decks.json ..\BotTest\Game\AI\Brains\Data\edison-decks.json --with-bots
```

`DuelSim cards <deck code>` prints any deck code's cards with their text (handy when writing a new bot).

## Matching Omega

The simulator is meant to play exactly like Omega. What it does to match:

- **Rules.** Omega's Edison-era rules, as EDOPro's `DUEL_MODE_MR1` (`Ocg.DuelModeEdison`), in full duels and in the search's rebuilt positions:
  - **priority:** after the turn player's summon, or a chain ending in the Main Phase, their face-up monsters' ignition effects can go in a chain window (Dark Armed Dragon, Dark Grepher, Lyla, a union's equip);
  - a draw on the first turn;
  - one face-up Field Spell;
  - no Extra Monster Zone;
  - the old once-per-turn Special Summon, return-to-deck trigger and summon-oath rulings.
- **Attack order.** The simulator executes the declared attack index. The search can compare all eligible attackers and choose their order.
- **Engine.** EDOPro's engine and scripts, not Omega's own. Rulings follow EDOPro's scripts, which can differ from Omega's on individual cards.
- **Pre-errata cards.** Omega has its own ids for pre-errata cards: official id + 20, reported with the official id as Alias.
  - EDOPro doesn't have these ids. The simulator gives each one the official card's data under the Omega id, so the bots still see Omega's ids.
  - Each plays with its hand-written script from `scripts/pre-errata/` when one exists:

    | Card | Omega id | Pre-errata behaviour |
    |---|---|---|
    | Lyla | 22624393 | No custom script: plays as the current card, which matches the old rulings (targets; the position change is part of the effect, so if she leaves the field nothing happens). |
    | Ryko | 21502816 | Targets on activation (optional), then mills 3 and destroys the target. |
    | Goyo Guardian | 7391468 | 1 Tuner of any Attribute + 1 or more non-Tuners (no EARTH Tuner requirement). |
    | Brionac | 50321816 | Soft once per turn. Can return cards on either field. |

  - Otherwise the card plays as the current card, and `run` says which cards that applies to. To add one, drop a `c<omega id>.lua` into `scripts/pre-errata/`. See `src/Data/OmegaIds.cs`.
- **Harness only (no effect on play).**
  - Prompts Omega never shows a bot (chain prompts with nothing to chain, trigger ordering, rock-paper-scissors) are answered automatically.
  - Turn order alternates between duels instead of a coin toss.
  - The bots' heartbeat and the search set the pace.

## Layout

| Path | What |
|---|---|
| `src/Native/OcgApi.cs` | P/Invoke bindings and struct layouts (32- or 64-bit) for ocgcore API 11.0 |
| `src/Data/` | card database (SQLite), script lookup, Omega deck codes, card filters, Omega-id aliases |
| `src/Engine/DuelHost*.cs` | duel loop and state tracking; Omega-shaped views; prompt building and response translation |
| `src/Bridge/OmegaEmulator.cs` | the in-memory Omega bot API |
| `src/Match/MatchRunner.cs` | duel series, output files, summary |
