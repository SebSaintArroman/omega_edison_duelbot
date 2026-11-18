# Omega Edison Duel Bot

A Yu-Gi-Oh! bot for [YGO Omega](https://omega.duelistsunite.org/) that plays the Edison format (Master Duel rules). You start a custom-AI duel in Omega, and the bot plays it.

Each of its decisions is checked by a search: it plays thousands of short lines with a copy of the real rules engine, scores the positions they reach, and picks the move whose lines come out best.

## What you need

- Windows 10 or 11
- [YGO Omega](https://omega.duelistsunite.org/), installed in its default folder (`C:\Program Files (x86)\YGO Omega`)
- The [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0), to build the bot

## Build

    git clone https://github.com/SebSaintArroman/omega_edison_duelbot.git
    cd omega_edison_duelbot
    dotnet build

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

## Sharing the bot without the SDK

`Packaging/Package-DuelBot.ps1` builds `dist/DuelBot-<date>.zip`: a self-contained copy (no .NET install needed) with a setup script, a start script, and a script that exports the duel logs to send back.

## Project layout

- `OmegaBotWrapper/`: the connection to YGO Omega's custom-AI API, the duel logger, and the wrapper the decks build on
- `BotTest/`: the bot itself: the deck brains, the search, the AI Room page and its server
- `DuelSim/`: the offline simulator, and the rules engine, card database and card scripts the search uses
- `Launch-DuelBot.ps1`: the launcher

## Known limits of Omega's bot API

- Omega always attacks with the bot's first attacker by zone, whichever one the bot names. The bot plans its attack order around this.
- Omega never asks the bot "attack directly?". A monster that can attack directly always does, even if there are monsters on the field.
- Effects that choose a zone to disable (Ojama King, Boompoline!!) aren't handled by the server and can end the duel.

## Credits

- Built on the Duel Bot Wrapper from the [Duelists Unite](https://duelistsunite.org/) community, by Kunogi ([@Kunogi](https://gitlab.com/Kunogi)) and BBeretta ([@BPBCoding](https://gitlab.com/BPBCoding)).
- Ideas and flow from [IceYGO/windbot](https://github.com/IceYGO/windbot).
- YGO Omega is developed by the Duelists Unite community: [discord.gg/duelistsunite](https://discord.gg/duelistsunite).
