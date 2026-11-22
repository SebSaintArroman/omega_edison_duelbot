# Legal Play Generation & MCTS Implementation

## 1. Overview: Information-Set MCTS

Yu-Gi-Oh! is an imperfect-information, sequential game with a massive state space and thousands of unique card interactions. In `omega_edison_duelbot`, decisions are verified and refined using **Information-Set Monte Carlo Tree Search (IS-MCTS)**.

The MCTS engine does not run in isolation. Instead:
1. The heuristic brain (`DeckBotBrain`) proposes an initial candidate move.
2. The search evaluates candidate branches over a **2-turn horizon** (the remainder of our turn plus the opponent's entire following turn, and in some contexts into our subsequent turn).
3. The search replaces the heuristic proposal only when an alternative move achieves a statistically superior score that clears a risk margin (`SearchOverrideMargin` + `OverrideNoiseFactor * StdErr`).
4. When an override occurs, follow-up picks along the winning line (targets, discards, materials) are adopted as a **committed plan** (`DeckBotBrain.FollowPlan`).

```
                    +---------------------------+
                    | Current Board State (API) |
                    +-------------+-------------+
                                  |
                                  v
                    +---------------------------+
                    |  SearchView (Observed)    |
                    +-------------+-------------+
                                  |
               +------------------+------------------+
               | Determinization (per iteration)     |
               v                                     v
       [World Instance 1]                    [World Instance 2]
     (Sampled Deck Order /                 (Sampled Deck Order /
      Sampled Opponent Hand)                Sampled Opponent Hand)
               |                                     |
               v                                     v
+-----------------------------+       +-----------------------------+
| ocgcore Engine Simulation   |       | ocgcore Engine Simulation   |
| (1. UCB Selection           |       | (1. UCB Selection           |
|  2. Expansion               |       |  2. Expansion               |
|  3. Rollout Playout         |       |  3. Rollout Playout         |
|  4. MctsEvaluator Scoring)  |       |  4. MctsEvaluator Scoring)  |
+--------------+--------------+       +--------------+--------------+
               \                                     /
                +-----------------+-----------------+
                                  | Backpropagation
                                  v
                      +-----------------------+
                      | Shared MctsTree Graph |
                      +-----------------------+
```

---

## 2. Information-Set Determinization (`SearchView.cs`)

Omega conceals private game information from the bot:
- Opponent hand cards have ID `0`.
- Opponent face-down set monsters and backrow have ID `0`.
- Both players' deck draw order is unknown.

Before each search line begins, [BotTest/Game/AI/Search/SearchView.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/BotTest/Game/AI/Search/SearchView.cs) constructs a concrete **determinization** (`SearchView.Determinize`):
1. **Our Deck**: Shuffled randomly from the known remaining cards in our deck list.
2. **Opponent Hand & Sets**: Sampled from real tournament decklists stored in `edison-decks.json` (Format Library top 10 placing lists for 22 Edison archetypes).
   - If the bot has observed specific opponent cards (via search effects, bounces, or reveals), those cards are guaranteed in their hand/set pool.
   - Remaining hidden slots are sampled proportionally according to archetype likelihoods and card weights (e.g., flip monsters and recruiters are weighted heavily for face-down monster sets).

---

## 3. Action Representation & Stable Keying (`SearchActions.cs`)

Because native engine positions are rebuilt dynamically, card sequences in memory may shift. [BotTest/Game/AI/Search/SearchActions.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/BotTest/Game/AI/Search/SearchActions.cs) establishes stable action keys across engine instances:

### `CardKey` Formats:
- Hand: `{id}@hand` (copies in hand are fungible).
- Deck / Extra Deck: `{id}@{controller}.deck` or `{id}@{controller}.extra`.
- Field: `{id}@{controller}.{location}.{sequence}`.

### Action Key Formats:
- Main Phase Actions: `{(int)MainAction}:{CardKey}` (e.g., `1:42940404@0.hand` for Normal Summon).
- Effect Activations: `3:{CardKey}:{description}`.
- Battle Phase Attacks: `attack:{CardKey}`.
- Card Selections: `card:{CardKey}` or `cards:{Key1}+{Key2}`.
- Phase Transitions: `battle`, `main2`, `end`, `pass`.

### Main Phase Windows and Hard Card Rules

`Mcts.OptionsFor` applies the search's Main Phase windows before it creates tree options. Face-up repositioning is offered after other
Main Phase 1 plays have been exhausted and before leaving that phase. Flip Summons remain available during Main Phase 1. Setting is
offered after other Main Phase plays and after face-up repositioning: in Main Phase 2 when a Battle Phase is available, or in Main
Phase 1 when it is not. Set cards are still individual engine responses; their orders converge through the existing position
transpositions, while the search chooses whether to set each card or finish the phase. Choosing a set in Main Phase 1 commits that
line to finishing without entering the Battle Phase; otherwise, sets can be made after battle in Main Phase 2.

Generic keyword restrictions live in `BotTest/Game/AI/Brains/TagRules.cs`; per-card tag assignments and exceptions live in
`BotTest/Game/AI/Brains/CardRules.cs`. They filter actions before expansion, and rollout proposals are checked against the same
filtered options. A filtered action therefore cannot enter a searched line or be reintroduced by the rollout brain.
`BotStrategicState` currently defines `Base` and `Threatened`; callers may provide the state, while state transitions remain outside
this change. The generic rules apply across decks and are queried only for offered actions using a tagged card.

---

## 4. MCTS Node Selection

During tree traversal, node selection balances exploitation of high-value lines with exploration of unvisited options using the **Upper Confidence Bound (UCB / ISMCTS)** formula:

$$\text{Score} = \mu_i + c_{\text{explore}} \sqrt{\frac{\ln A_i}{N_i}} + \frac{c_{\text{prior}}}{\sqrt{1 + N_i}}$$

Where:
- $\mu_i$: Node value (see Section 7 on `LineValue`).
- $A_i$ (`Available`): The number of times this action was legally available when the parent was visited (Information-Set MCTS availability).
- $N_i$ (`Visits`): The number of times this specific action was traversed.
- $c_{\text{explore}}$ (`Exploration` = `0.5`): UCB exploration constant.
- $c_{\text{prior}}$ (`PriorBonus` = `0.1`): Progressive bias bonus awarded to our heuristic brain's proposed candidate move (`PriorKey`), which naturally decays as visits accumulate.

### Progressive Widening
To prevent branching explosions when many choices exist, nodes restrict active children using progressive widening:
- **Card selections (targeting, tributes)**: Allowed children $\le 1 + \text{Widening} \times \sqrt{N}$ (default `Widening = 2`).
- **Main / Battle decisions**: Allowed children $\le 1 + \text{MainWidening} \times \sqrt{N}$ (default `MainWidening = 3`).
- **Ordering of Expansion**: The heuristic brain's prior proposal is expanded first; moves unpenalized by heuristic rules ("free moves") are expanded next; moves flagged as rule violations ("held moves") are expanded last.

### Virtual Loss
To prevent multi-threaded workers from searching identical branches simultaneously:
- When a thread enters a child node, it immediately increments `Visits` and adds a virtual loss (0 for us, 1 for opponent).
- When the simulation concludes and scores the position, the virtual loss is deducted and replaced with the actual score.

---

## 5. Node Expansion & Playout (Rollout)

When traversal reaches an unexpanded node or progressive widening opens a new slot:
1. A new `MctsNode` is instantiated.
2. The engine executes the selected action.
3. The search transitions from tree traversal to **rollout playout**:
   - **Our Side (Player 0)**: Controlled by a twin instance of our heuristic deck brain (`SearchBrain`), mirroring realistic bot play.
   - **Opponent Side (Player 1)**: Controlled by a lightweight, fast heuristic policy (`MctsPolicy.cs`) that attacks with beaters, chains obvious negations, and activates standard removal.
4. Playout continues until the simulation reaches the search horizon (`StopTurn`).

---

## 6. Position Evaluation (`MctsEvaluator.cs`)

When a line terminates, [BotTest/Game/AI/Search/MctsEvaluator.cs](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/BotTest/Game/AI/Search/MctsEvaluator.cs) scores the resulting game state in **Card Units** (CU), where $1.0\text{ CU} \approx \text{worth of a generic card in hand}$:

### Evaluation Components:
1. **Decisive Outcome**: Immediate Win = `1.0`; Immediate Loss = `0.0`.
2. **Board Value**:
   - Monster Bodies: Weighted by effective attack and defense.
   - Effect Points: Computed by `EffectEvaluator` and tapered logarithmically:
     $$\text{Taper}(x) = \begin{cases} x & x \le 2 \\ 2 + \ln(1 + x - 2) & x > 2 \end{cases}$$
   - Graveyard Value: Cards with graveyard effects (Vayu, Plaguespreader, Necro Gardna) retain significant value; regular monsters contribute $2.5\%$.
3. **Card Advantage Bonus**:
   - Gaining a card without losing one (e.g., Pot of Greed, Black Whirlwind) or taking an opponent card for free yields $+1.0\text{ CU}$ (`PlusOneBonus`).
4. **Overexposure Penalties**:
   - Lines that overcommit into opponent mass-removal traps are penalized based on peak exposure:
     - Torrential Tribute exposure at summons.
     - Mirror Force exposure at attack declarations.
     - Heavy Storm exposure on set backrow at turn end.
     - Icarus Attack / Spot removal exposure.
5. **Life Point Delta**:
   - Damage dealt to opponent adds up to $+0.12\text{ CU}$ per 1,000 LP.
   - Damage taken by our bot subtracts up to $-0.10\text{ CU}$ per 1,000 LP.
6. **Rule Violations**:
   - Any heuristic brain rules violated during our turn apply penalty deductions (see [Rules & Heuristics](rules-and-heuristics.md)).

### Logistic Normalization
The net difference between our side and the opponent side ($\Delta\text{CU}$), centered around the starting position difference, is compressed into $[0, 1]$ via a logistic sigmoid:

$$\text{Value} = \frac{1}{1 + e^{-\Delta\text{CU} / \text{Scale}}}$$

(Default `Scale = 3`).

---

## 7. Backpropagation & Recursive Line Valuation (`LineValue`)

### Why Raw Averages Fail in Yu-Gi-Oh!
In traditional MCTS, a node's value is the simple average: $\frac{\sum \text{Scores}}{\text{Visits}}$.
In Yu-Gi-Oh!, this causes severe pathological behavior:
- If our bot chooses "End Turn", there are no subsequent decisions; the line scores cleanly.
- If our bot chooses "Normal Summon Bora", the game opens dozens of possible follow-up attacks, synchro plays, and backrow activations. Some of those downstream branches will be sub-optimal during random sampling.
- Under simple averaging, having multiple choices artificially depresses the parent move's score, causing the bot to favor ending the turn rather than summoning powerful monsters.

### The `LineValue` Solution ([Mcts.cs#L158-L234](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/BotTest/Game/AI/Search/Mcts.cs#L158-L234)):
At our decision nodes, instead of taking the raw average, `LineValue(minVisits)` recursively selects the **maximum value among well-explored children** ($N \ge 30$ visits):

$$\text{LineValue}(v) = \max_{c \in \text{ExploredChildren}(v)} \text{LineValue}(c)$$

For children that have not yet reached `minVisits`, `UnexploredBest` estimates value using the best follow-up with at least 10 visits minus one standard error.

---

## 8. Transposition Detection & Graph Merging

Different move orders often reach identical board states (e.g., Summoning Monster A then Monster B vs. Summoning Monster B then Monster A).

1. When a Main Phase or Battle decision node is reached, a strict state fingerprint is generated ([DuelHost.Search.cs#L209](file:///c:/Users/Seb/Documents/GitHub/omega_edison_duelbot/DuelSim/src/Engine/DuelHost.Search.cs#L209)):
   - Includes all monster/spell codes, battle positions, stat modifications, equip targets, counters, overlays, graveyard/banished contents, and life points.
   - Excludes exact zone sequence numbers (column independence).
2. If an existing node in `MctsTree._positions` matches the strict fingerprint at the **exact same search depth**, the current node is merged into the existing node.
3. This transforms the MCTS tree into a Directed Acyclic Graph (DAG), consolidating visit counts and dramatically improving search depth for commutative plays.

---

## 9. Turn Replay (`TurnReplay.cs`)

When searching mid-turn in a live game, the bot must ensure the simulated position matches the exact engine state of the active turn:
1. At the beginning of each turn (before the opening draw), `DuelBotWrapper` captures a `TurnStartSnapshot`.
2. Every subsequent prompt and player response is logged as a `TurnCheckpoint`.
3. To evaluate a decision mid-turn, each MCTS line rebuilds the board from the turn's start snapshot, replays all recorded checkpoints up to the current decision (the search root), and branches into MCTS exploration from there.
4. If a simulated line deviates from the recorded checkpoints (due to engine discrepancies or non-deterministic effects), it is flagged as a `REPLAY MISS` and recorded to `replay-issues.jsonl`.
