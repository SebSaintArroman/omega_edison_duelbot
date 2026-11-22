# Omega Edison Duel Bot

A Yu-Gi-Oh! bot for [YGO Omega](https://omega.duelistsunite.org/) that plays the Edison format (Master Duel rules). You start a custom-AI duel in Omega, and the bot plays it.

Each of its decisions is checked by a search: it plays thousands of short lines with a copy of the real rules engine, scores the positions they reach, and picks the move whose lines come out best.

## Install (no build needed)

You need Windows 10 or 11 (64-bit) and [YGO Omega](https://omega.duelistsunite.org/).

1. Download **[DuelBot-Setup.exe](https://github.com/SebSaintArroman/omega_edison_duelbot/releases/latest/download/DuelBot-Setup.exe)** (the latest release).
2. Double-click it. The installer isn't signed, so Windows may warn "Windows protected your PC": click **More info**, then **Run anyway**.
3. The setup:
   - installs the Microsoft Visual C++ runtime if it's missing (Windows may ask for permission);
   - finds YGO Omega, or asks where `YGO Omega.exe` is;
   - asks for a name to put on your log exports;
   - puts two shortcuts on your desktop: **Duel Bot** and **Duel Bot - Export Logs**.

The bot installs to `%LOCALAPPDATA%\Programs\DuelBot`. No admin rights or .NET install are needed.

To play, double-click **Duel Bot** on your desktop, then follow steps 2 and 3 of [Play against it](#play-against-it).

### Updates

Every push to `main` builds a new release here on GitHub. When you start the **Duel Bot** shortcut, it checks for a newer release and offers to update. If you say yes, it downloads and installs the update (your settings, duel logs and comments are kept), then starts the bot. You can also download the latest `DuelBot-Setup.exe` from the link above and run it over your install.

### Sending your logs back

Double-click **Duel Bot - Export Logs**. It saves `DuelBot-logs-<your name>-<date>.zip` on your desktop, with your duel logs and comments.

## Build from source

You need:

- Windows 10 or 11 (64-bit)
- [YGO Omega](https://omega.duelistsunite.org/), installed in its default folder (`C:\Program Files (x86)\YGO Omega`)
- The [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

    git clone https://github.com/SebSaintArroman/omega_edison_duelbot.git
    cd omega_edison_duelbot
    powershell -ExecutionPolicy Bypass -File DuelSim\setup.ps1
    dotnet build

`DuelSim\setup.ps1` downloads the card database and card scripts that the search needs, once.

A plain `dotnet build` makes the Release build (the fast one), which is what the launcher runs.

## Play against it

1. Run the launcher, from the repo folder:

       powershell -ExecutionPolicy Bypass -File Launch-DuelBot.ps1

   It starts YGO Omega if it isn't already open, starts the bot, and opens the **AI Room** page in your browser (http://localhost:58966). If the bot is already running, it restarts it, so a new build is picked up. To have a one-click start, make a desktop shortcut to that command.
2. In the AI Room page, choose the bot's **Deck** (or a random one).
3. In YGO Omega, create a duel against the custom AI (**AI_CUSTOM**). The bot joins and plays.

While you play, the page shows what the bot did and why. Each decision expands to show the board and the bot's reasons. Tick **Thinking** to watch its search progress, or **Engine chatter** to see the low-level messages. **Pause auto-start** stops the bot from joining new duels on its own.

## Decks

All Edison format:

- Miracle Vayu
- Blackwing
- Blackwing Hybrid
- Gladiator Control
- Hero Frog Monarchs
- Machina Control
- Machina Jinzo

## Duel logs and comments

Every duel is written to `BotTest/duel-logs/`: a readable `.log`, and a `.jsonl` with the full detail. If the bot plays badly, use **Comment on this duel** on the page. The comment is saved to `BotTest/duel-logs/comments.jsonl`, together with the decision it's about, which makes it easy to find the moment in the log later.

## Tuning

Every number the bot weighs its decisions with lives in plain text files that you can edit:

- `BotTest/bot-tuning.txt`: shared values and the search's settings
- `BotTest/bot-tuning-<Deck>.txt`: each deck's own values and rules

Each line has a short explanation. Restart the bot after editing.

## DuelSim (bot against bot, offline)

`DuelSim/` runs whole duels between two of the bots without Omega. It's useful for testing changes. See [DuelSim/README.md](DuelSim/README.md).

## Making the installer yourself

- `Packaging/Build-Installer.ps1` builds `dist/DuelBot-Setup-<date>.exe`, the same one-click installer the releases have. A locally built installer never offers updates.
- `Packaging/Package-DuelBot.ps1` builds only the zip inside it: `dist/DuelBot-<date>.zip`, a self-contained copy with a setup script, a start script and a log-export script.
- `.github/workflows/release.yml` builds and publishes a release on every push to `main`.

## Project layout

- `OmegaBotWrapper/`: the connection to YGO Omega's custom-AI API, the duel logger, and the wrapper the decks build on
- `BotTest/`: the bot itself: the deck brains, the search, the AI Room page and its server
- `DuelSim/`: the offline simulator, and the rules engine, card database and card scripts the search uses
- `Packaging/`: the installer, the shareable package and its scripts
- `Launch-DuelBot.ps1`: the launcher

## Known limits of Omega's bot API

- Omega never asks the bot "attack directly?". A monster that can attack directly always does, even if there are monsters on the field.
- Effects that choose a zone to disable (Ojama King, Boompoline!!) aren't handled by the server and can end the duel.

## Credits

- Built on the Duel Bot Wrapper from the [Duelists Unite](https://duelistsunite.org/) community, by Kunogi ([@Kunogi](https://gitlab.com/Kunogi)) and BBeretta ([@BPBCoding](https://gitlab.com/BPBCoding)).
- Ideas and flow from [IceYGO/windbot](https://github.com/IceYGO/windbot).
- The rules engine is [edo9300/ygopro-core](https://github.com/edo9300/ygopro-core) (AGPL-3.0), built unmodified for 64-bit from commit 9cd0936 (`DuelSim/vendor/ocgcore-x64/ocgcore.dll`). Card scripts and the card database are from [Project Ignis](https://github.com/ProjectIgnis).
- YGO Omega is developed by the Duelists Unite community: [discord.gg/duelistsunite](https://discord.gg/duelistsunite).
