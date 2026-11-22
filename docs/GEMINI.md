# GEMINI.md - Developer Guide for Omega Edison DuelBot

## 1. Project Overview

`omega_edison_duelbot` is a Yu-Gi-Oh! AI bot designed to play the **Edison format** (April 2010 banlist under Master Rule 1 rules) against humans in [YGO Omega](https://omega.duelistsunite.org/). 

The bot pairs a hand-crafted expert rules system (`DeckBotBrain`) with a native **Information-Set Monte Carlo Tree Search (IS-MCTS)** engine. When facing a decision, the bot runs thousands of simulated lines using an embedded copy of EDOPro's C/C++ rules engine (`ocgcore.dll`, 64-bit API 11.0), scoring the resulting board states and overriding the heuristic rules whenever a superior tactical line is discovered.

The repository also includes `DuelSim`, a headless offline simulator for running bot-vs-bot games, benchmark scenarios, and genetic parameter tuning without launching YGO Omega.

---

## 2. Key Architectural Concepts

### 2.1 The Hybrid Decision Pipeline
1. **Heuristic Proposal**: At each decision prompt, `DeckBotBrain` evaluates the board and proposes a move based on domain-specific rules.
2. **Search Prior**: The proposed move is given a progressive bias bonus (`PriorBonus`) in the MCTS tree.
3. **Soft Constraint (`Hold`)**: Heuristic rules that reject moves register them as **Holds**. If an MCTS line takes a held move anyway, the line pays a penalty cost in card units.
4. **MCTS Override**: MCTS simulates lines across a 2-turn horizon. If another action achieves a higher score clearing the safety margin (`SearchOverrideMargin` + `OverrideNoiseFactor * StdErr`), the search overrides the brain.
5. **Plan Execution**: Subsequent choices along the winning line (targets, discards, materials) become an adopted plan followed on subsequent prompts.

### 2.2 In-Engine Simulation via Puzzle Scripts
- MCTS does not replay entire duels from turn 1. It takes a snapshot of the observed board in Omega, determinizes hidden cards (opponent hand/sets sampled from Format Library Edison decklists in `edison-decks.json`), and serializes the board into an in-memory Lua puzzle script (`Debug.ReloadFieldBegin` / `Debug.ReloadFieldEnd`).
- `PuzzleEngine` reuses the same native `ocgcore` instance across lines with dynamic epoch re-arming, reloading positions in ~7ms instead of ~75ms.

### 2.3 Information-Set MCTS Features
- **Selection**: Upper Confidence Bound (UCB) with progressive widening on card picks and main moves.
- **Rollout**: Our side played by a twin instance of `DeckBotBrain`; opponent side played by `MctsPolicy`.
- **Recursive Line Valuation (`LineValue`)**: Evaluates moves using the maximum score of well-explored follow-up choices rather than raw averages, preventing moves with many follow-ups from being penalized by bad exploratory branches.
- **Transposition Merging**: Merges identical board states at the same search depth into a Directed Acyclic Graph (DAG).
- **Virtual Loss**: Directs concurrent worker threads to explore diverse branches without thread collisions.

---

## 3. Important Files and Their Purposes

### Core Wrapper (`OmegaBotWrapper/`)
- `OmegaApiClient.cs`: Typed HTTP client communicating with Omega on port `19999`. Supports `AsyncLocal` override for in-memory simulation.
- `DuelBotWrapper.cs`: Main loop polling `/get-bot-state-header` every 50ms, managing field caches and state dispatching.
- `BotStateDispatcher.cs`: Maps incoming `BotState` codes to strongly-typed state handlers.
- `ActivationIndexRepair.cs`: Rebuilds action activation indices to fix Omega server index misalignment defects.
- `BotResponseValidator.cs`: Validates and clamps responses (actions, selection counts, phases) to prevent illegal packet crashes.
- `BotResponseWriter.cs`: Serializes bot responses into little-endian binary buffers.

### Bot Runtime & Search (`BotTest/`)
- `Program.cs`: ASP.NET Core minimal API host (port `58966`) serving the AI Room web interface and terminal logs.
- `Web/BotControlService.cs`: Manages UI state, deck selection, comment storage, and the auto-join loop (`AutoLoopAsync`).
- `Decks/DeckManager.cs`: Registry of Master Duel Edison decks (Vayu, Machina Control, Hero Frog, Blackwing, Gladiator Control, Machina Jinzo, Blackwing Hybrid).
- `Game/AI/Brains/DeckBotBrain*.cs`: Base class for heuristic brains, staple card handlers, rule gates, hold penalties, and search coordination.
- `Game/AI/Search/Mcts.cs`: The core IS-MCTS engine (node selection, expansion, rollouts, line values, transpositions).
- `Game/AI/Search/SearchView.cs`: Determinizes hidden game state (shuffling our deck, sampling opponent hands from `edison-decks.json`).
- `Game/AI/Search/SearchActions.cs`: Generates legal choices and stable action keys (`CardKey`).
- `Game/AI/Search/MctsEvaluator.cs`: Evaluates terminal positions in card units (body ATK, tapered effect points, card advantage, exposure risks, LP delta).

### Simulator & Tools (`DuelSim/`)
- `src/Native/OcgApi.cs`: 64-bit P/Invoke bindings for `ocgcore.dll` (API 11.0).
- `src/Engine/OcgDuel.cs` & `src/Engine/DuelHost*.cs`: Wraps native engine instances, Lua script loading, message parsing, and prompt generation.
- `src/Bridge/OmegaEmulator.cs`: In-memory HTTP message handler routing `OmegaApiClient` calls directly to `DuelHost`.
- `src/Match/MatchRunner.cs`: Orchestrates headless bot-vs-bot duel matches.
- `src/Tuning/Evolver.cs`: Genetic algorithm mutating parameters and rule probabilities to maximize win rate.
- `setup.ps1`: Setup script that downloads `ocgcore.dll`, BabelCDB SQLite database, and ProjectIgnis Lua scripts.

### Packaging & Automation
- `Launch-DuelBot.ps1`: Launcher script starting Omega, the bot server, and opening the browser UI.
- `Packaging/Package-DuelBot.ps1`: Builds self-contained release zip.
- `Packaging/Build-Installer.ps1`: Uses Windows IExpress to build a single-file executable setup installer (`DuelBot-Setup.exe`).
- `.github/workflows/release.yml`: GitHub Actions workflow building, signing, and publishing releases on push to `main`.

---

## 4. Development Conventions

1. **Language & Framework**: C# 13, .NET 9.0 SDK, Windows x64.
2. **Release Configuration Default**: `Directory.Build.props` sets `Configuration` to `Release` by default. Always compile in Release mode for benchmarking or running the bot, as MCTS runs significantly faster.
3. **No ProjectReference in DuelSim**: `DuelSim.csproj` compiles bot source files directly from `..\BotTest\**\*.cs` and `..\OmegaBotWrapper\**\*.cs` using `<Compile>` items. **Do not convert this to a `<ProjectReference>`**, or building DuelSim will lock `BotTest.dll` while `BotTest` is running.
4. **Card Units Metric**: All board evaluations, rule costs, and advantages are calibrated in **Card Units** (CU), where $1.0\text{ CU} \approx \text{value of a generic card in hand}$.
5. **Pre-Errata Card IDs**: Pre-errata cards use Omega ID offset: $\text{Omega ID} = \text{Official ID} + 20$. Hand-written pre-errata Lua scripts live in `DuelSim/scripts/pre-errata/`.
6. **Rule Gate Pattern**: Heuristic rules must be gated using `Rule(RuleName)` with a named rule constant, allowing tuning and genetic mutation.
7. **Hold Pattern**: When a rule declines an action, call `Hold(cardId, action, ruleName)` so MCTS understands the heuristic penalty for overriding that decision.

---

## 5. Build and Test Commands

### Initial Environment Setup
```powershell
# Run once to download ocgcore.dll, SQLite database, and Lua scripts
powershell -ExecutionPolicy Bypass -File DuelSim\setup.ps1
```

### Build
```powershell
# Build entire solution (defaults to Release)
dotnet build

# Build individual projects
dotnet build BotTest\BotTest.csproj --no-restore
dotnet build DuelSim\DuelSim.csproj --no-restore
```

### Verification & Testing
```powershell
# 1. Sanity check (verifies engine API, card database, scripts, and deck decodes)
DuelSim\bin\Release\net9.0\win-x64\DuelSim.exe check

# 2. Run benchmark scenario from DuelSim/scenarios/
DuelSim\bin\Release\net9.0\win-x64\DuelSim.exe search-bench --scenario DuelSim\scenarios\live-2026-11-10-brionac.json --lines 20

# 3. Run headless bot-vs-bot matches
DuelSim\bin\Release\net9.0\win-x64\DuelSim.exe run Vayu MachinaControl --duels 5 --no-bot-logs

# 4. Run parameter evolution
DuelSim\bin\Release\net9.0\win-x64\DuelSim.exe evolve --generations 5 --duels 20 --workers 4
```

### Launch Against Omega
```powershell
powershell -ExecutionPolicy Bypass -File Launch-DuelBot.ps1
```

---

## 6. Important Constraints and Things That Must Not Be Broken

> [!CAUTION]
> Breaking any of these invariants will cause engine desyncs, crashes, or severe bot blunders:

1. **Battle command indices**:
   - Every eligible attacker is a search option. Use its advertised attack action index in the battle response.
2. **Dedicated Threads for MCTS**:
   - Never use `Task.Run` or ThreadPool threads for MCTS worker loops in `Mcts.cs`. MCTS threads must remain dedicated `System.Threading.Thread` instances to prevent thread pool starvation against ASP.NET and HTTP polling.
3. **Activation Index Repair**:
   - Never remove `ActivationIndexRepair.Repair` from `BotStateDispatcher`. Omega frequently scrambles activation indices when multiple activable cards are present.
4. **Response Validation**:
   - All bot responses must pass through `BotResponseValidator` before serialization. Sending an illegal choice (e.g. invalid attack action, out-of-range card index) can crash the Omega duel session.
5. **No Direct Attack Prompts**:
   - Omega never asks "attack directly?". Direct attacks are declared automatically by the server if conditions permit.
6. **PuzzleEngine Epoch Re-arming**:
   - When recycling native engine duels in `PuzzleEngine`, `aux.GlobalCheck` must be re-armed with an incremented epoch counter (`_st_epoch`). Disabling this causes once-per-duel card effects to permanently fail to register in subsequent lines.
7. **3x Pot of Greed in Decks**:
   - Master Duel bot deck recipes in `DeckManager.cs` intentionally include 3x *Pot of Greed* (`55144522`). Do not remove these unless explicitly reconfiguring tournament deck definitions.
