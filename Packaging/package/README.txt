DUEL BOT
========

A bot that plays Yu-Gi-Oh! duels against you in YGO Omega.

You need: Windows 10 or 11, and YGO Omega installed.


FIRST TIME
----------
1. Unzip this folder somewhere permanent (for example Documents\DuelBot). Don't run it from inside the zip.
2. Double-click Setup.bat. It:
   - installs the Microsoft Visual C++ runtime if your PC doesn't have it (Windows may ask for permission),
   - finds YGO Omega, or asks you where YGO Omega.exe is,
   - asks for a name to put on your log exports,
   - puts two shortcuts on your desktop: "Duel Bot" and "Duel Bot - Export Logs".


PLAYING
-------
1. Double-click "Duel Bot" on your desktop. It starts YGO Omega (if it isn't open), the bot, and the AI Room page in
   your browser. A console window opens for the bot: leave it open while you play, close it to stop the bot.
2. In the AI Room page, pick the bot's deck (or Random).
3. In YGO Omega, make a room and add a bot to it. The bot joins by itself.

The comment box on the AI Room page is for feedback: if the bot makes a bad play, write what it should have done.
Each comment is tied to the moment in the duel log where you wrote it.


SENDING YOUR LOGS BACK
----------------------
Double-click "Duel Bot - Export Logs". It saves DuelBot-logs-<your name>-<date>.zip on your desktop.
Send that file back by whatever way you shared this folder. It holds only the duel logs and your comments.


TROUBLE?
--------
- The bot's console window shows what went wrong.
- If YGO Omega moved, run Setup.bat again.
