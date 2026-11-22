# System Architecture

## 1. High-Level Overview

`omega_edison_duelbot` is an automated duel agent and simulation environment designed for Yu-Gi-Oh! Edison format play in [YGO Omega](https://omega.duelistsunite.org/). It combines:
1. A heuristic expert system (`DeckBotBrain`) encoding format-specific card rules, combos, and board evaluations.
2. An Information-Set Monte Carlo Tree Search (IS-MCTS) engine that validates or overrides heuristic decisions by simulating thousands of projected lines using an embedded C/C++ rules engine (`ocgcore.dll`).
3. An offline duel simulator (`DuelSim`) capable of running headless bot-vs-bot games, benchmark scenarios, and genetic parameter evolution without Omega.
4. An integrated ASP.NET Core control panel and web interface (`AI Room`) for real-time monitoring and deck configuration.

```
+-----------------------------------------------------------------------------------+
|                                   YGO Omega                                       |
|  (Game Client / Server running custom AI duel; exposes REST API on port 19999)     |
+-----------------------------------------+-----------------------------------------+
                                          | HTTP REST (JSON / Base64)
                                          v
+-----------------------------------------------------------------------------------+
|                               OmegaBotWrapper                                     |
|  - OmegaApiClient: REST communication with Omega                                  |
|  - DuelBotWrapper: Heartbeat polling loop (50ms), field caching, state management  |
|  - BotStateDispatcher: Routes state codes to handlers                             |
|  - ActivationIndexRepair: Fixes server action indexing defects                     |
|  - BotResponseValidator: Clamps and corrects bot answers to legal bounds          |
|  - BotResponseWriter: Serializes answers into little-endian binary packets         |
+-----------------------------------------+-----------------------------------------+
                                          |
                                          v
+-----------------------------------------------------------------------------------+
|                                     BotTest                                       |
|  - Program.cs / BotControlService: ASP.NET Core web server (port 58966)           |
|  - DeckManager: Deck catalog, base64 hashes, 3x Pot of Greed injection             |
|  - DeckBotBrain: Shared base for heuristic play, rule gates, hold penalties       |
|  - Deck Bots: Vayu, MachinaControl, HeroFrog, Blackwing, GladiatorControl, etc.   |
|  - Game/AI/Search: IS-MCTS engine, SearchView determinization, MctsEvaluator      |
+-----------------------------------------+-----------------------------------------+
                                          | Linked C# Sources & P/Invoke
                                          v
+-----------------------------------------------------------------------------------+
|                                     DuelSim                                       |
|  - Native/OcgApi: 64-bit P/Invoke bindings for ocgcore.dll (API 11.0)             |
|  - Engine/OcgDuel: Native duel instance wrapper, script/card loader callbacks      |
|  - Engine/DuelHost: Message translation, turn loop, puzzle script position loader |
|  - Engine/PuzzleEngine: Recyclable engine pool for high-throughput MCTS lines     |
|  - Bridge/OmegaEmulator: In-memory HTTP interception for headless bot-vs-bot      |
|  - Match/MatchRunner: Headless match orchestrator, transcripts, analysis metrics   |
|  - Tuning/Evolver: Genetic algorithm tuning parameters and rule probabilities      |
+-----------------------------------------------------------------------------------+
```

---

## 2. Major Components

### 2.1 OmegaBotWrapper (`OmegaBotWrapper/`)
The foundational networking, serialization, and protocol layer.
- **Purpose**: Interfaces with the YGO Omega Custom AI HTTP server (default port `19999`).
- **Key Types**:
  - `OmegaApiClient` ([OmegaApiClient.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/OmegaBotWrapper/OmegaApiClient.cs)): Handles typed HTTP requests to all `/bot-*` endpoints. Supports `AsyncLocal<OmegaApiClient?> Override` so `DuelSim` can intercept requests without changing bot code.
  - `DuelBotWrapper` ([DuelBotWrapper.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/OmegaBotWrapper/DuelBotWrapper.cs)): Encapsulates connection lifecycle (`Connect`), the heartbeat polling loop (`Start` / `Heartbeat`), per-decision field caches (`GetBotFieldInfo`, `GetBotFieldCardInfo`), and duel event callbacks.
  - `BotStateDispatcher` ([BotStateDispatcher.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/OmegaBotWrapper/BotStateDispatcher.cs)): Translates incoming `BotState` codes to registered handler delegates.
  - `ActivationIndexRepair` ([ActivationIndexRepair.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/OmegaBotWrapper/ActivationIndexRepair.cs)): Fixes an Omega bug where multi-activation card descriptions become misaligned across cards.
  - `BotResponseValidator` ([BotResponseValidator.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/OmegaBotWrapper/BotResponseValidator.cs)): Ensures bot responses conform to game constraints (e.g., selectable counts, valid phase transitions, attacker bounds) before transmission.
  - `BotResponseWriter` ([BotResponseWriter.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/OmegaBotWrapper/BotResponseWriter.cs)): Encodes high-level responses into exact little-endian byte buffers expected by the engine.

### 2.2 BotTest (`BotTest/`)
The main executable hosting the deck brains, the play search (MCTS), and the user-facing web server.
- **Purpose**: Runs the bot runtime against live Omega games.
- **Key Subsystems**:
  - **Web Host** (`Program.cs`, `Web/BotControlService.cs`, `Web/TerminalCapture.cs`, `wwwroot/`): ASP.NET Core minimal API running on `http://localhost:58966`. Houses the AI Room UI, captures terminal logs, manages deck selection, and executes the auto-join loop (`AutoLoopAsync`).
  - **Deck Catalog** (`Decks/DeckManager.cs`, `Web/DeckCatalog.cs`): Defines Edison format deck recipes (base64 compressed deck strings) and factories for deck-specific brains.
  - **Heuristic Brain Core** (`Game/AI/Brains/DeckBotBrain*.cs`): Comprehensive rule hierarchies for normal summons, special summons, battle phases, spell/trap chains, target prioritization, and resource commitments.
  - **Play Search & MCTS** (`Game/AI/Search/`): Rebuilds observed boards into native engine positions and uses multi-threaded IS-MCTS to evaluate moves over a 2-turn horizon.
  - **Linked Engine Sources**: `BotTest.csproj` links C# files directly from `../DuelSim/src/Native/*.cs`, `../DuelSim/src/Engine/*.cs`, and `../DuelSim/src/Data/*.cs`, embedding the simulation engine directly into the bot process.

### 2.3 DuelSim (`DuelSim/`)
The offline simulator and benchmarking suite.
- **Purpose**: Runs bot-vs-bot matches without YGO Omega, runs automated scenario tests, and tunes bot parameters using evolutionary search.
- **Key Subsystems**:
  - **Native P/Invoke Layer** (`src/Native/OcgApi.cs`): 64-bit C-ABI interop definitions for `ocgcore.dll` (API 11.0).
  - **Engine Wrapper** (`src/Engine/OcgDuel.cs`, `src/Engine/DuelHost*.cs`): Manages the unmanaged duel handle, native callbacks (CardReader, ScriptReader, LogHandler), query lists, message loops, and Omega prompt emulation.
  - **In-Memory HTTP Bridge** (`src/Bridge/OmegaEmulator.cs`): Replaces `HttpClientHandler` inside `OmegaApiClient.Default`, intercepting bot HTTP calls and routing them to the internal `DuelHost`.
  - **Evolution & Tuning** (`src/Tuning/Evolver.cs`, `src/Tuning/BrainParams.cs`): Mutates numeric parameters and rule gate probabilities across generations of headless duels.
  - **Compilation Design**: `DuelSim.csproj` compiles bot source files directly (`..\OmegaBotWrapper\**\*.cs`, `..\BotTest\**\*.cs`) with `EnableDefaultCompileItems=false`. This allows building and running `DuelSim` while `BotTest` is actively running, avoiding file lock conflicts.

### 2.4 Packaging (`Packaging/`)
Scripts and manifests for creating self-contained Windows releases.
- **Purpose**: Compiles a single-file, self-extracting installer (`DuelBot-Setup.exe`) using Windows IExpress. Includes all dependencies (.NET 9 runtime, SQLite database, 13,000+ Lua scripts, and `ocgcore.dll`).

---

## 3. Concurrency and Threading Architecture

The application employs specific threading strategies tailored to high-throughput search and low-latency networking:

1. **Heartbeat Polling Thread**:
   - `DuelBotWrapper.Start` runs on an asynchronous Task loop. In live Omega games, it queries the state header every 50ms (`HEARTBEAT_DELAY`).
   - In `DuelSim`, `HeartbeatDelayMs` is set to `0`, and `OmegaEmulator` implements long-polling (`WaitForPromptAsync`) up to 100ms, immediately waking the bot task when an engine prompt occurs.

2. **Dedicated MCTS Worker Threads**:
   - In `Mcts.Run`, worker threads are instantiated as dedicated `System.Threading.Thread` instances (named `mcts 0`, `mcts 1`, etc.), rather than dispatched to the .NET `ThreadPool`.
   - **Reason**: The .NET ThreadPool became saturated during high-frequency blocking HTTP calls and engine evaluations, resulting in worker thread starvation (lines/sec dropped by 90%+). Dedicated threads run at normal or below-normal priority with zero ThreadPool contention.

3. **Engine Duel Recycling (`PuzzleEngine`)**:
   - Creating a fresh native duel via `OCG_CreateDuel` and loading standard scripts (`constant.lua`, `utility.lua`) is computationally expensive (~75ms).
   - `PuzzleEngine` maintains an open engine duel and reloads new positions via `Debug.ReloadFieldBegin` / `Debug.ReloadFieldEnd` puzzle scripts (~7ms per reload).
   - Engines are recycled across up to 2,000 lines (`RebuildEvery`) before being safely reallocated to prevent native memory bloat.

4. **Multi-Thread Virtual Loss**:
   - When multiple MCTS threads traverse the shared `MctsTree`, the active selection applies a temporary virtual loss to the visited node.
   - This dissuades other worker threads from exploring the identical branch simultaneously, ensuring diverse search exploration.
