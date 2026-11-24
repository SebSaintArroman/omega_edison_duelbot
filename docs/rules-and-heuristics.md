# Strategic Rules & Evaluation System

## 1. The Dual-System Philosophy

A central design pillar of `omega_edison_duelbot` is the synergy between **heuristic expert rules** and **Monte Carlo tree search**:
- **Pure heuristics** can execute immediate tactical plays and recognize archetypal synergy, but struggle with long multi-step combos, lookahead calculation, and unforeseen opponent responses.
- **Pure MCTS** excels at tactical calculation and combo discovery, but suffers from massive branching factors, blundering into known format traps or wasting resources on aimless summons during shallow rollouts.

The bot solves this by using heuristic rules in three distinct capacities:
1. **As the default player**: Provides sensible base proposals for all Main Phase, Battle Phase, and Chain decisions.
2. **As search priors**: Guides MCTS node selection via progressive bias (`PriorBonus`), ensuring high-priority heuristic lines are investigated first.
3. **As soft constraints (`Holds`)**: When a heuristic rule decides not to take a move, that rejected move is registered as a **Hold**. If an MCTS line decides to take that move anyway, the line pays a penalty cost in card units.

---

## 2. Heuristic Brain Hierarchy

The heuristic brain classes form an object-oriented hierarchy:

```
                  +--------------------------------+
                  |  IBotBrain / DefaultBotBrain   |
                  +---------------+----------------+
                                  |
                                  v
                  +--------------------------------+
                  |         DeckBotBrain           |
                  |  (Shared Staples, Chains,      |
                  |   Synchros, Holds, Search API) |
                  +---------------+----------------+
                                  |
       +--------------------------+--------------------------+
       |                          |                          |
       v                          v                          v
+---------------+          +---------------+          +---------------+
| VayuBotBrain  |          |MachinaBotBrain|          |HeroFrogBotBrain|
+---------------+          +---------------+          +---------------+
       |                          |
       v                          v
+------------------+       +----------------------+
|BlackwingBotBrain |       |GladiatorBeastsBotBrain|
+------------------+       +----------------------+
```

### Key Shared Modules in `DeckBotBrain`:
- `Brains/Staples/Staples.cs`: Definition of staple cards, removal priorities, and threat scoring.
- `DeckBotBrain.StapleIdle.cs`: Main Phase staple logic (Heavy Storm, Trunade, MST, Pot of Greed, Allure of Darkness, Future Fusion).
- `DeckBotBrain.StapleChains.cs`: Chain response logic for spell-speed 2/3 traps (Solemn Judgment, Royal Oppression, Mirror Force, Torrential Tribute, Dimensional Prison, Compulsory, Bottomless).
- `DeckBotBrain.MonsterStaples.cs`: Logic for staple removal spells (Smashing Ground, Brain Control).
- `DeckBotBrain.Synchros.cs`: Synchro summon line detection and material combination calculations (Brionac, Goyo Guardian, Stardust Dragon, Black Rose Dragon, Armory Arm, Colossal Fighter).
- `DeckBotBrain.BattleProtection.cs`: Battle-phase defense (Gorz the Emissary of Darkness, Battle Fader, Necro Gardna).
- `DeckBotBrain.Targeting.cs`: Intelligent target selection avoiding indestructible cards, floating triggers, or useless targets.

---

## 3. Rule Gates (`RuleGates.cs`)

Every rule in the brain is named and routed through a probabilistic gate:
```csharp
if (Rule(DeckRules.HeavyStorm) && ...)
{
    // execute play
}
```

### Characteristics of Rule Gates:
1. **Configurable Probabilities**: Each gate defaults to `1.0` (active as written). Tuning files or genetic evolution can set any probability between `0.0` and `1.0`.
2. **Non-Breaking Rollouts**: Follow-up actions of an already decided play (e.g., synchro material selection for a summoned Tuner) are never gated, preventing half-executed plays.
3. **Event Logging**: Every rule roll is logged to the duel record:
   `rule-event|<rule_name>|<1_or_0>|<probability>|<turn>`
4. **Offline Win Rate Attribution**: `DuelSim` analyzes these logged events across thousands of duels to generate a **Rule $\times$ Board Genre Matrix** (`ANALYSIS.md` / `ANALYSIS.html`), showing whether each rule provides positive or negative lift to the bot's win rate in specific tactical situations.

---

## 4. The `Hold` Mechanism: Rules as Search Penalties

In [BotTest/Game/AI/Brains/Planning/SearchAtDecisions.cs](../BotTest/Game/AI/Brains/Planning/SearchAtDecisions.cs), when a rule decides to decline an action, it records a **Hold**:

```csharp
Hold(cardId, action, ruleName);
```

### Penalty Costs:
Each rule is assigned a penalty tier in Card Units:
- **Light Penalty** (`0.5 CU`): Minor sequence preferences (e.g., setting backrow after battle rather than before).
- **Standard Penalty** (`1.5 CU`): Significant tactical guidelines (e.g., holding Smashing Ground for larger threats).
- **Strong Penalty** (`2.5 CU`): Major strategic principles (e.g., avoiding summoning blockers that give opponent free attacks, or wasting Gorz).

### Impact on MCTS:
During search simulations:
- If a line chooses an action that is currently held, `penalty += holdCost`.
- The line is not forbidden from making the move, but it must gain enough board advantage, card advantage, or damage to compensate for the penalty.
- This allows the MCTS to override heuristic rules when a game-winning play or lethal combo is discovered.

### Hard Card Rules

Rules that must never be overridden are declared in `BotTest/Game/AI/Brains/TagRules.cs` for generic keyword tags and
`BotTest/Game/AI/Brains/CardRules.cs` for card-specific tag assignments and exceptions; they do not use `Hold`. The search removes
these actions before expansion and checks rollout-brain proposals against the same option list. The initial `SetOnly` tag covers
Ryko and Super-Nimble Mega Hamster: it forbids normal summoning, with the shared lethal and threatened-tuner exceptions, plus
Hamster's no-Ryko-in-deck exception. `BotStrategicState` exposes `Base` and `Threatened`; the caller has not yet been given a
board-based state transition policy.

---

## 5. Sequence Penalties (`LineTally.cs`)

Certain bad play patterns span multiple actions rather than single decisions. [BotTest/Game/AI/Search/LineTally.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/BotTest/Game/AI/Search/LineTally.cs) tracks sequence penalties throughout a simulated turn:

1. **Summoning Before Backrow Removal**:
   - Summoning a key monster before activating Heavy Storm or Giant Trunade is heavily penalized.
2. **Setting Spells/Traps Before Attacking**:
   - Setting non-Quick-Play spells or setting traps before attacking into potential Gorz / Tragoedia exposes cards unnecessarily to Heavy Storm.
3. **Aimless Summons**:
   - Summoning a monster in Main Phase 1 that does not attack, does not use an activated effect, and is not used as tribute/material.
4. **Premature Battle Phase Exits**:
   - Entering Battle Phase with viable attackers but making no attacks.

---

## 6. Overexposure Threat Modeling

The evaluation engine calculates potential blowout risks from opponent staples that could be lurking face-down:

1. **Torrential Tribute Exposure**:
   - Evaluated at each Normal / Special Summon. Measures the total value of our monsters destroyed if the opponent responds with Torrential Tribute.
2. **Mirror Force Exposure**:
   - Evaluated whenever an attack is declared. Measures the total value of all our face-up attack-position monsters that would be destroyed by Mirror Force.
3. **Heavy Storm Exposure**:
   - Evaluated at the end of our turn based on our set spell/trap cards. If our set backrow exceeds 1 card, exposure increases quadratically.
4. **Single-Target Removal (Dimensional Prison, Compulsory, Bottomless)**:
   - Evaluated against our strongest attacking or summoned threats.
5. **Sitting Duck Exposure**:
   - Penalizes leaving low-ATK monsters in face-up attack position where opponent beaters can destroy them for damage next turn.

All exposures are discounted by the opponent's probability of holding the card (derived from the Edison archetype read) and scaled by `RiskDiscount` (which decreases when our bot needs to push aggressively).
