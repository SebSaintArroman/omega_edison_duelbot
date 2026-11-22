# Documentation Index

This directory contains technical documentation for the `omega_edison_duelbot` repository, an AI agent and duel simulation environment designed to play the Edison format in [YGO Omega](https://omega.duelistsunite.org/).

## Table of Contents

1. [Architecture Overview](architecture.md)
   - Component responsibilities (`OmegaBotWrapper`, `BotTest`, `DuelSim`, `Packaging`)
   - Solution structure and inter-component dependencies
   - Runtime lifecycles and concurrency model

2. [Game Simulation & Rules Engine](game-simulation.md)
   - EDOPro native rules engine (`ocgcore.dll`, P/Invoke, API 11.0)
   - Card database (`cards.cdb`) and ProjectIgnis Lua card scripts
   - Pre-errata cards and Omega ID translation (+20 offset)
   - In-engine position recreation via puzzle scripts (`Debug.ReloadFieldBegin` / `Debug.ReloadFieldEnd`)
   - Edison Master Duel rule flags (`DUEL_MODE_MR1`, priority, opening draw)

3. [Legal Play Generation & MCTS Implementation](search-and-mcts.md)
   - Information-Set MCTS and determinization (`SearchView.Determinize`)
   - Action representation and stable keying (`SearchActions`, `CardKey`)
   - MCTS lifecycle: Selection (UCB + progressive widening + progressive bias), Expansion, Simulation/Rollout, Evaluation, Backpropagation
   - Virtual loss across dedicated search threads
   - Transposition detection and graph merging
   - Recursive line valuation (`LineValue`) vs raw average penalty

4. [Strategic Rules & Evaluation System](rules-and-heuristics.md)
   - Heuristic bot brains (`DeckBotBrain`) and rule gates (`RuleGates`)
   - Soft constraints: The `Hold` mechanism and search penalties
   - Positional scoring: `MctsEvaluator`, effect points (`EffectEvaluator`, `Taper`), card advantage (+1 bonus)
   - Overexposure penalties: Torrential Tribute, Mirror Force, Heavy Storm, Icarus Attack, spot removal, sitting ducks
   - Sequence tallies (`LineTally`) and turn replay audit

5. [YGO Omega Protocol & Integration](omega-integration.md)
   - Omega custom-AI REST API endpoints (`http://127.0.0.1:19999`)
   - Decision polling loop, state dispatching, and response validation (`BotResponseValidator`)
   - Little-endian binary packet serialization (`BotResponseWriter`)
   - Known server-side API limitations (zone attack lock, no direct-attack prompts)
   - Embedded ASP.NET control server and web interface (`http://localhost:58966`)

6. [Build, Packaging & Testing](build-packaging-testing.md)
   - Prerequisites and build commands (.NET 9, win-x64, Release configuration)
   - Asset setup script (`DuelSim/setup.ps1`)
   - Packaging pipeline: self-contained publishing and IExpress single-file installer
   - Release workflow and update check mechanism
   - Test harness: `DuelSim` offline runner, benchmark scenarios (`DuelSim/scenarios/`), evolutionary parameter tuning (`DuelSim evolve`)

7. [Limitations, Workarounds & Technical Debt](technical-debt-and-limitations.md)
   - Known Omega server bugs and bot workarounds
   - Replay misses (`REPLAY MISS`) and desynchronization
   - Concurrency constraints (dedicated threads vs ThreadPool)
   - Single-assembly compilation design in `DuelSim.csproj`
