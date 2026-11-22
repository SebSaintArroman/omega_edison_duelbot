# Build, Packaging, Installation & Testing

## 1. Prerequisites & Environment

- **Operating System**: Windows 10 or 11 (64-bit).
- **Runtime / SDK**: [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).
- **Game Client**: [YGO Omega](https://omega.duelistsunite.org/) (default installation directory: `C:\Program Files (x86)\YGO Omega`).
- **C++ Runtime**: Microsoft Visual C++ 2015-2022 Redistributable (x64).

---

## 2. Setup & Compilation

### Step 1: Download Rules Engine Assets (`setup.ps1`)
The search engine requires the BabelCDB SQLite card database and official ProjectIgnis Lua scripts. Run the setup script once from the repo root:
```powershell
powershell -ExecutionPolicy Bypass -File DuelSim\setup.ps1
```
This downloads and populates:
- `DuelSim/vendor/cdb/cards.cdb` (~13,700 cards, SQLite format).
- `DuelSim/vendor/scripts/official/` (card scripts in Lua).
- `DuelSim/vendor/ocgcore-x64/ocgcore.dll` (64-bit engine binary).

### Step 2: Build the Solution
The repository includes `Directory.Build.props`, which configures **Release** as the default build configuration:
```powershell
dotnet build
```
To build individual projects:
```powershell
dotnet build BotTest\BotTest.csproj
dotnet build DuelSim\DuelSim.csproj
```

> [!NOTE]
> `BotTest` and `DuelSim` target `win-x64` and are configured as self-contained. `DuelSim.csproj` compiles bot source files directly to prevent assembly locking when testing against a running `BotTest` instance.

---

## 3. Running the Bot

### The Launcher (`Launch-DuelBot.ps1`)
To play against the bot in YGO Omega, run:
```powershell
powershell -ExecutionPolicy Bypass -File Launch-DuelBot.ps1
```

The launcher executes the following sequence:
1. Detects if YGO Omega is running; if not, launches `C:\Program Files (x86)\YGO Omega\YGO Omega.exe`.
2. Checks if an earlier instance of `BotTest` is running. If so, terminates it to ensure the latest build is loaded.
3. Launches `BotTest\bin\Release\net9.0\win-x64\BotTest.exe` in its own console window.
4. Probes `http://localhost:58966/api/state` until the bot server answers.
5. Opens the **AI Room** web interface in your default browser (`http://localhost:58966`).

### Playing in Omega:
1. In the AI Room browser page, select the bot's deck (e.g., *Miracle Vayu*, *Machina Control*, or *Random*).
2. In YGO Omega, host a duel against custom bot **AI_CUSTOM**.
3. The bot's auto-join loop (`AutoLoopAsync`) detects the waiting bot slot, loads the deck, and starts playing.

---

## 4. Packaging & Installer Creation

The repository includes a complete pipeline for packaging and deploying standalone Windows installers without requiring users to install .NET:

### 1. `Packaging\Package-DuelBot.ps1`
Publishes a self-contained release:
1. Runs `dotnet publish BotTest\BotTest.csproj -c Release -r win-x64 --self-contained true`.
2. Bundles the SQLite card database (`cards.cdb`), Lua scripts, and `ocgcore.dll`.
3. Copies starter scripts (`Start Duel Bot.bat`, `Setup.bat`, `Export Logs.bat`).
4. Injects version stamp and git commit info into `version.txt` and `tag.txt`.
5. Compresses everything into `dist\DuelBot-<timestamp>.zip`.

### 2. `Packaging\Build-Installer.ps1`
Wraps the zip package into a one-click self-extracting installer (`dist\DuelBot-Setup-<timestamp>.exe`):
- Uses Windows built-in `iexpress.exe` to generate the self-extracting CAB executable.
- At runtime on the user's PC, the installer extracts to `%LOCALAPPDATA%\Programs\DuelBot`, keeping existing settings, logs, and comments intact.
- Creates two desktop shortcuts: **Duel Bot** and **Duel Bot - Export Logs**.

### 3. Automatic Updates
When the user launches the installed bot via `Start-DuelBot.ps1`:
1. Reads `tag.txt` (e.g., `build-42`).
2. Queries the GitHub API (`/repos/SebSaintArroman/omega_edison_duelbot/releases/latest`).
3. If a higher build number is available, prompts the user to update.
4. If approved, downloads the latest `DuelBot-Setup.exe` to `%TEMP%` and executes it, seamlessly upgrading the installation while retaining user logs and tuning files.

### 4. CI/CD Workflow (`.github/workflows/release.yml`)
- Triggers on every push to `main`.
- Runs `DuelSim/setup.ps1` on a `windows-latest` runner.
- Builds the installer with build number tag (`build-${{ github.run_number }}`).
- Signs the installer with Authenticode using DigiCert timestamping.
- Publishes the release to GitHub Releases with `DuelBot-Setup.exe` and public certificate `OmegaEdison.cer`.

---

## 5. Testing & Verification Infrastructure

There is no xUnit/NUnit test project in the solution. Instead, testing is performed through the **DuelSim offline test harness** and historical scenario benchmarks.

### 1. Engine & Database Sanity Check
Verifies that `ocgcore.dll`, the SQLite database, core Lua scripts, and all 7 bot deck recipes decode properly:
```powershell
DuelSim\bin\Release\net9.0\win-x64\DuelSim.exe check
```

### 2. Headless Bot-vs-Bot Duels (`DuelSim run`)
Runs duels between any two bots offline with full play-by-play logging:
```powershell
# Run 10 duels between Vayu and MachinaControl
DuelSim\bin\Release\net9.0\win-x64\DuelSim.exe run Vayu MachinaControl --duels 10

# Fast run with no bot logs (summary and transcript only)
DuelSim\bin\Release\net9.0\win-x64\DuelSim.exe run Vayu Blackwing --duels 20 --no-bot-logs --seed 42
```
Output is saved to `DuelSim/out/<timestamp>_<DeckA>-vs-<DeckB>/`:
- `summary.txt`: Match results, win rates, first/second advantage, errors/warnings.
- `transcript.txt`: Complete engine-level log of summons, attacks, chains, and damage.
- `<Deck>-P0.log` / `<Deck>-P1.log`: Full decision logs for each bot.

### 3. Scenario Benchmarking (`DuelSim search-bench`)
Evaluates specific, recorded game positions stored in `DuelSim/scenarios/*.json`:
```powershell
DuelSim\bin\Release\net9.0\win-x64\DuelSim.exe search-bench --scenario DuelSim\scenarios\live-2026-11-10-brionac.json --lines 50
```
Outputs:
- Lines simulated per second across worker threads.
- Profiling breakdown (deal, duel creation, position reload, engine execution, evaluation).
- Top root actions with visit counts, confidence intervals, and projected follow-up plans.

### 4. Turn Replay Auditing (`ReplayAudit`)
During matches in `DuelSim`, `MatchRunner` can enable `ReplayAudit`:
- At the end of each turn, the turn is replayed from its opening snapshot across multiple lines.
- If a line fails to follow the real game (due to unexpected engine prompts or desyncs), a `REPLAY MISS` is recorded.
- Replay issues are flagged in `replay-issues.jsonl` for developer review.

### 5. Automated Parameter Evolution (`DuelSim evolve`)
Runs genetic optimization across bot tuning parameters:
```powershell
DuelSim\bin\Release\net9.0\win-x64\DuelSim.exe evolve --generations 20 --duels 100 --workers 6
```
- Champion vs. Challenger play matches with mutated weights and rule probabilities.
- Successful mutations (>55% win rate) become the new champion.
- Results and parameter diffs are inspected using `DuelSim params <variant>`.
