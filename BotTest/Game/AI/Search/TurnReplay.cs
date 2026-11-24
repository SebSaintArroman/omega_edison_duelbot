using DuelSim.Engine;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Search
{
    /// <summary>One prompt of ours in the real turn (DeckBotBrain.TurnLog): its kind, the phase, the board as we saw it, the answer we sent.</summary>
    /// <param name="ChainCard">At a chain window: their card on the chain whose targets the real game reported (0: none reported).</param>
    /// <param name="ChainTargets">That link's targets in their graveyard (card codes): what D.D. Crow's rule reads.</param>
    internal sealed record ReplayCheckpoint(BotState State, int Phase, SearchView View, ReplayStep? Answer,
        IReadOnlyList<string>? ChainOptions = null, int ChainCard = 0, IReadOnlyList<int>? ChainTargets = null);

    /// <summary>How far one replayed line got: checkpoints reached in order, and where and why it left the real game (null: it reached all).</summary>
    internal sealed record ReplayRun(int Reached, string? Miss, int MissAt);

    /// <summary>
    /// The real turn played again from its start (plan "the search makes every decision", step 2; user, 2026-11-18: "it should rebuild from
    /// the start of my turn and play my turn through exactly as it was to the current point"). The turn-start snapshot is rebuilt with the
    /// turns before it skipped (SearchPosition.SkipTurns), so the turn begins with its own Draw Phase: no restarted turn, no second Normal
    /// Summon, nothing "this turn" lost. Then:
    ///  1. our prompts take the answers we really sent, each once the line's board is the board we saw at it (checkpoint);
    ///  2. their prompts take the move that brings the line's board to the next checkpoint's: what appears face-up is summoned or
    ///     activated, sets are set, monsters that attacked attack, the phase moves on when nothing else is left; their picks take
    ///     the cards that really moved;
    ///  3. prompts the real game didn't give us are declined (a few per checkpoint).
    /// A line whose board differs from a checkpoint is a miss, with the first difference: each one is something to fix.
    /// </summary>
    internal sealed class TurnReplay
    {
        private const int MaxPrompts = 3000;
        private const int MaxExtraPerGap = 8;

        public SearchView Start { get; }
        public bool Ours { get; }
        public IReadOnlyList<ReplayCheckpoint> Checkpoints { get; }
        /// <summary>The engine turn the real turn is played on.</summary>
        public int RebuiltTurn => 1 + Start.SkipTurns;

        private TurnReplay(SearchView start, bool ours, IReadOnlyList<ReplayCheckpoint> checkpoints)
        {
            Start = start;
            Ours = ours;
            Checkpoints = checkpoints;
        }

        private static int Canon(int id) => SearchHost.CanonicalId(id) is > 0 and var c ? c : id;

        private static Dictionary<int, int> Count(IEnumerable<int> ids)
        {
            var d = new Dictionary<int, int>();
            foreach (int id in ids)
                if (id > 0)
                    d[Canon(id)] = d.GetValueOrDefault(Canon(id)) + 1;
            return d;
        }

        private static bool FaceUp(uint position) => (position & 0x5) != 0;
        private static bool ExtraDeckMonster(int code) => (SearchHost.Type(code) & (0x40 | 0x2000 | 0x800000 | 0x4000000)) != 0;
        private static bool Token(int code) => (SearchHost.Type(code) & 0x4000) != 0;

        /// <summary>The card's text says it Special Summons itself (Swap Frog, Gorz): its activation is how it turns up face-up. Not D.D. Crow,
        /// whose hand effect only banishes (seen 2026-11-18: the replay activated it instead of the real Normal Summon).</summary>
        private static bool SummonsItself(int code) =>
            SearchHost.Available && SearchHost.Cards.Get(code)?.Text is { } text &&
            System.Text.RegularExpressions.Regex.IsMatch(text, @"Special Summon this card", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>The card's text Special Summons a monster from the graveyard (XX-Saber Faultroll: "target 1 ... in your Graveyard;
        /// Special Summon it").</summary>
        private static bool RevivesFromGrave(int code) =>
            SearchHost.Available && SearchHost.Cards.Get(code)?.Text is { } text &&
            System.Text.RegularExpressions.Regex.IsMatch(text, @"(Graveyard|GY)[^.]*Special Summon|Special Summon[^.]*from (your|either player's) (Graveyard|GY)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>The unseen cards using this card adds to its owner's hand: a search from the Deck (Machina Gearframe) 1, a draw its count
        /// (Pot of Greed 2). 0 for anything else.</summary>
        private static int HandGain(int code)
        {
            if (!SearchHost.Available || SearchHost.Cards.Get(code)?.Text is not { } text)
                return 0;
            var draw = System.Text.RegularExpressions.Regex.Match(text, @"\bdraw (\d+|one|two|three) cards?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (draw.Success)
                return draw.Groups[1].Value.ToLowerInvariant() switch { "one" => 1, "two" => 2, "three" => 3, var d => int.TryParse(d, out int n) ? n : 1 };
            return System.Text.RegularExpressions.Regex.IsMatch(text, @"from your Deck to your hand", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? 1 : 0;
        }

        /// <summary>
        /// The replay of a real turn from its snapshot (taken before the draw) and its checkpoints. Their cards we saw this turn are dealt
        /// where they came from: a set card of theirs from the start that turned face-up in its zone is that card; an Extra Deck monster
        /// goes to their Extra Deck; the rest to their hand (beyond the hand's size: on top of their deck, their draw or a search).
        /// Monsters they set that stayed unknown are plain set bodies in their hand. Our own draw is the card our hand really gained.
        /// </summary>
        public static TurnReplay Prepare(SearchView snapshot, bool ours, int realTurn, IReadOnlyList<ReplayCheckpoint> checkpoints)
        {
            var v = snapshot.AtTurnStart();
            v.RealTurn = realTurn;
            // Our turn: engine turn 3 (2 skipped, with its draw), or turn 1 for the duel's first turn (no attacks there). Theirs: engine turn 2.
            v.SkipTurns = !ours ? 1 : realTurn == 1 ? 0 : 2;
            v.FirstTurnOfDuel = ours && realTurn == 1;
            // Find the draw in the opening snapshots. Omega can hide it until Standby,
            // so look past passes while our hand still has no newly identified card.
            int drawn = 0;
            if (ours)
            {
                for (int i = 0; i < checkpoints.Count; i++)
                {
                    var cp = checkpoints[i];
                    if (i > 0 && cp.Phase is not ((int)DuelPhase.Draw or (int)DuelPhase.Standby or (int)DuelPhase.Main1))
                        break;
                    var handBefore = Count(snapshot.OwnHand);
                    drawn = cp.View.OwnHand.FirstOrDefault(id => handBefore.TryGetValue(Canon(id), out int n)
                        ? (handBefore[Canon(id)] = n - 1) < 0 : true);
                    if (drawn > 0 || !Declined(cp))
                        break;
                }
            }
            if (drawn > 0)
            {
                if (v.OwnDeck.Contains(drawn) && (v.KnownTop.Count == 0 || v.KnownTop[0] != drawn))
                    v.KnownTop.Insert(0, drawn);
                // Turn 1 draws for real (Omega's rules) but never in the engine's rebuilt turn 1: dealt into the hand as the line starts.
                if (realTurn == 1)
                    v.DrawsAtStart = 1;
            }
            // The rest of our deck's cards that left it this turn, checkpoint by checkpoint: those that went to the graveyard or banished (a mill:
            // Card Trooper, Ryko) next on top in that order; those that went to the hand (a draw effect) as forced draws (the engine moves each
            // to the top just before one of our draws). Searches are picked by what moved (OutcomePick), wherever they are in the deck.
            {
                static Dictionary<int, int> Raw(IEnumerable<int> ids) => ids.Where(id => id > 0).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
                var deck = Raw(snapshot.OwnDeck);
                var hand = Raw(snapshot.OwnHand);
                var gone = Raw(snapshot.OwnGrave.Concat(snapshot.OwnBanished));
                int turnDraw = v.KnownTop.Count > 0 && ours ? v.KnownTop[0] : 0; // the turn's draw, already on top
                var mills = new List<int>();
                ReplayStep? before = null; // our answer just before this checkpoint
                foreach (var cp in checkpoints)
                {
                    var deckNow = Raw(cp.View.OwnDeck);
                    var handNow = Raw(cp.View.OwnHand);
                    var goneNow = Raw(cp.View.OwnGrave.Concat(cp.View.OwnBanished));
                    // A copy our last answer played from the hand left it: one drawn in its place keeps the count level (seen 2026-11-18: Pot of
                    // Greed drew the second Pot of Greed, which was taken for a mill and the draws slid one card).
                    if (before is { Key: var played } && played.Contains("@hand") && SearchActions.KeyCardId(played) is > 0 and var playedId)
                        foreach (int id in hand.Keys.Where(id => Canon(id) == playedId).Take(1).ToList())
                            hand[id]--;
                    before = cp.Answer;
                    foreach (var (id, n) in deck)
                        for (int left = n - deckNow.GetValueOrDefault(id); left > 0; left--)
                        {
                            if (id == turnDraw)
                            {
                                turnDraw = 0;
                                continue;
                            }
                            if (handNow.GetValueOrDefault(id) > hand.GetValueOrDefault(id))
                                v.ForcedDraws.Add(id);
                            else if (goneNow.GetValueOrDefault(id) > gone.GetValueOrDefault(id))
                                mills.Add(id);
                        }
                    (deck, hand, gone) = (deckNow, handNow, goneNow);
                }
                if (mills.Count > 0)
                {
                    v.KnownTop.RemoveAll(mills.Contains);
                    v.KnownTop.InsertRange(ours && v.KnownTop.Count > 0 ? 1 : 0, mills);
                }
            }

            // Our hand cards that went to our graveyard / banished this turn (a discard, Thestalos's random one included): a random pick in the
            // line takes them first (SearchView.RandomPicks), so the replay's random outcomes come out as the real game's did.
            if (checkpoints.Count > 0)
            {
                var handLeft = Count(snapshot.OwnHand);
                var last = checkpoints[^1].View;
                foreach (int id in last.OwnHand)
                    if (handLeft.TryGetValue(Canon(id), out int n) && n > 0)
                        handLeft[Canon(id)] = n - 1;
                var goneBefore = Count(snapshot.OwnGrave.Concat(snapshot.OwnBanished));
                var goneNow = Count(last.OwnGrave.Concat(last.OwnBanished));
                foreach (var (code, n) in handLeft)
                    for (int i = 0; i < Math.Min(n, goneNow.GetValueOrDefault(code) - goneBefore.GetValueOrDefault(code)); i++)
                        v.RandomPicks.Add(code);
            }

            // Their cards seen this turn, beyond what the snapshot already showed, in the order they turned up.
            var known = Count(snapshot.EnemyMonsters.Select(m => m.Code).Concat(snapshot.EnemySpells.Select(s => s.Code))
                .Concat(snapshot.EnemyGrave).Concat(snapshot.EnemyBanished).Concat(snapshot.EnemyKnownHand));
            var setMonsterZones = snapshot.EnemyMonsters.Where(m => m.Code <= 0).Select(m => m.Sequence).ToHashSet();
            var setSpellZones = snapshot.EnemySpells.Where(s => s.Code <= 0).Select(s => s.Sequence).ToHashSet();
            int faceDownBefore = snapshot.EnemyMonsters.Count(m => m.Code <= 0 || !FaceUp(m.Position));
            int setSpellsBefore = snapshot.EnemySpells.Count(s => s.Code <= 0 || !FaceUp(s.Position));
            int theirHandBefore = snapshot.EnemyHandCount + (ours ? 0 : 1); // their turn: their draw
            var fromHand = new List<int>(); // their cards that came from the hand or a draw / search, in order
            var milled = new List<int>();   // their cards that went from the deck to the graveyard (Ryko's mill), in order
            foreach (var cp in checkpoints)
            {
                var view = cp.View;
                // Cards they set that we never saw: stand-ins (a plain set body, an inert set card) in their hand, at the point they were set.
                int faceDown = view.EnemyMonsters.Count(m => m.Code <= 0 || !FaceUp(m.Position));
                for (int i = faceDownBefore; i < faceDown; i++)
                    fromHand.Add(SearchView.UnknownSetMonster);
                faceDownBefore = faceDown;
                int setSpells = view.EnemySpells.Count(s => s.Code <= 0 || !FaceUp(s.Position));
                for (int i = setSpellsBefore; i < setSpells; i++)
                    fromHand.Add(SearchView.InertCard);
                setSpellsBefore = setSpells;
                // Cards that left their hand by this checkpoint: what turned up beyond that came from the deck (a mill).
                int handLeft = Math.Max(0, theirHandBefore - view.EnemyHandCount);
                theirHandBefore = view.EnemyHandCount;
                // (Their hand cards we know count too: seen 2026-11-18, our Trap Dustshoot showed their whole hand, Battle Fader went back to
                // their deck and was never on the field.)
                var seen = Count(view.EnemyMonsters.Select(m => m.Code).Concat(view.EnemySpells.Select(s => s.Code)).Concat(view.EnemyGrave)
                    .Concat(view.EnemyBanished).Concat(view.EnemyKnownHand));
                // Cards on the field first: they took the cards that left the hand, so what else turned up came from the deck (seen 2026-11-18:
                // Armageddon Knight from hand, Plaguespreader Zombie from the deck by its effect).
                var onField = view.EnemyMonsters.Concat(view.EnemySpells).Where(c => c.Code > 0).Select(c => Canon(c.Code)).ToHashSet();
                // A searcher or draw card that turned up added unseen cards to their hand, so more cards left it than the count shows (seen
                // 2026-11-18: Machina Gearframe searched, and Cyber Dragon, discarded for Machina Fortress, was taken for a mill; Pot of Greed x2
                // and Brain Control too).
                foreach (var (code, n) in seen)
                    if (n > known.GetValueOrDefault(code))
                        handLeft += (n - known.GetValueOrDefault(code)) * HandGain(code);
                foreach (var (code, n) in seen.OrderBy(kv => onField.Contains(kv.Key) ? 0 : 1))
                    for (int extra = n - known.GetValueOrDefault(code); extra > 0; extra--)
                    {
                        known[code] = known.GetValueOrDefault(code) + 1;
                        if (Token(code))
                            continue;
                        if (ExtraDeckMonster(code))
                        {
                            v.EnemyKnownExtra.Add(code);
                            continue;
                        }
                        var monster = view.EnemyMonsters.FirstOrDefault(m => m.Code > 0 && Canon(m.Code) == code && setMonsterZones.Contains(m.Sequence));
                        if (monster != null)
                        {
                            setMonsterZones.Remove(monster.Sequence);
                            int at = v.EnemyMonsters.FindIndex(m => m.Sequence == monster.Sequence);
                            if (at >= 0)
                                v.EnemyMonsters[at] = v.EnemyMonsters[at] with { Code = monster.Code };
                            continue;
                        }
                        var spell = view.EnemySpells.FirstOrDefault(s => s.Code > 0 && Canon(s.Code) == code && setSpellZones.Contains(s.Sequence));
                        if (spell != null)
                        {
                            setSpellZones.Remove(spell.Sequence);
                            int at = v.EnemySpells.FindIndex(s => s.Sequence == spell.Sequence);
                            if (at >= 0)
                                v.EnemySpells[at] = v.EnemySpells[at] with { Code = spell.Code };
                            continue;
                        }
                        // A spell/trap that turned up in their graveyard (or banished) while one of their set cards from the start left its zone:
                        // that set card (seen 2026-11-18: their set Trap Dustshoot, used in the Draw Phase, was an inert card in the replay).
                        int vanished = (SearchHost.Type(code) & 0x6) == 0 ? -1
                            : setSpellZones.Where(z => !view.EnemySpells.Any(s => s.Sequence == z && (s.Code <= 0 || !FaceUp(s.Position)))).DefaultIfEmpty(-1).First();
                        if (vanished >= 0 && !view.EnemySpells.Any(s => s.Code > 0 && Canon(s.Code) == code))
                        {
                            setSpellZones.Remove(vanished);
                            int at = v.EnemySpells.FindIndex(s => s.Sequence == vanished);
                            if (at >= 0)
                            {
                                v.EnemySpells[at] = v.EnemySpells[at] with { Code = code };
                                continue;
                            }
                        }
                        // In the graveyard or banished with their hand no smaller for it: from the deck (seen 2026-11-18: their Ryko milled Laquari,
                        // Equeste and Bottomless Trap Hole, which the replay dealt into their hand and set).
                        bool gone = view.EnemyGrave.Concat(view.EnemyBanished).Any(id => Canon(id) == code) &&
                                    !view.EnemyMonsters.Concat(view.EnemySpells).Any(c => c.Code > 0 && Canon(c.Code) == code) &&
                                    !view.EnemyKnownHand.Any(id => Canon(id) == code);
                        if (gone && handLeft == 0)
                        {
                            milled.Add(code);
                            continue;
                        }
                        if (handLeft > 0)
                            handLeft--;
                        fromHand.Add(code);
                    }
            }
            v.EnemyKnownHand.AddRange(fromHand);
            // More cards than their hand holds as the turn starts: the rest came later (their draw, a draw effect, a search), from their deck's
            // top; their turn's draw first (an unknown one when we never saw it), then what was milled, then the rest.
            var later = new List<int>();
            int over = v.EnemyKnownHand.Count - v.EnemyHandCount;
            if (over > 0)
            {
                later.AddRange(v.EnemyKnownHand.Skip(v.EnemyHandCount));
                v.EnemyKnownHand.RemoveRange(v.EnemyHandCount, over);
            }
            if (milled.Count > 0)
            {
                int afterDraw = ours ? 0 : 1;
                if (later.Count < afterDraw)
                    later.Add(SearchView.InertCard);
                later.InsertRange(afterDraw, milled);
            }
            v.EnemyDeckTop.InsertRange(0, later);
            // Our picks of their unseen hand cards (Trap Dustshoot's): the stand-ins for their unknown hand are inert spells, so the pick had
            // nothing to take and never came (seen 2026-11-18). A plain monster stands in for each card we picked.
            int handPicks = checkpoints.Count(cp => cp.Answer is { Key: var key } && key.StartsWith("pick:") && key[5..].Split('+').Contains("0@hand"));
            for (int i = 0; i < handPicks && v.EnemyKnownHand.Count < v.EnemyHandCount; i++)
                v.EnemyKnownHand.Add(SearchView.UnknownSetMonster);
            return new TurnReplay(v, ours, checkpoints);
        }

        /// <summary>One line: rebuild, replay to the last checkpoint. The engine duel is dropped after it (the line stops mid-turn).</summary>
        public ReplayRun Play(PuzzleEngine engine, Random rng)
        {
            var position = Start.Determinize(rng);
            var host = engine.Load(position.ToLua(Start.FirstTurnOfDuel ? DuelHost.PuzzleFlags & ~0x02UL : DuelHost.PuzzleFlags), out var error);
            if (host == null)
                return new ReplayRun(0, $"the engine rejected the position: {error}", 0);
            try
            {
                host.SetLifePoints(position.LifePoints[0], position.LifePoints[1]);
                return Run(host, rng);
            }
            catch (Exception ex)
            {
                return new ReplayRun(0, $"replay failed: {ex.Message}", 0);
            }
            finally
            {
                host.Dispose();
                engine.Discard();
            }
        }

        private static ReplayRun Miss(int at, string why) => new(at, why, at);

        private ReplayRun Run(DuelHost host, Random rng)
        {
            var cursor = new Cursor(this);
            int prompts = 0;
            Prompt? prompt;
            while ((prompt = host.NextPrompt(RebuiltTurn + 1)) != null)
            {
                if (++prompts > MaxPrompts)
                    return Miss(cursor.Next, "over 3000 prompts (a loop)");
                if (prompt.Attempts > 0)
                {
                    int fallback = prompt.Attempts - 1;
                    if (fallback >= prompt.Fallbacks.Count)
                        return Miss(cursor.Next, $"no valid answer to {prompt.State}");
                    host.Respond(prompt, prompt.Fallbacks[fallback]);
                    continue;
                }
                var (packet, _, miss) = cursor.Step(prompt, host, rng);
                if (miss != null)
                    return Miss(cursor.Next, miss);
                Mcts.Answer(host, prompt, packet!);
                if (cursor.Done)
                    return new ReplayRun(Checkpoints.Count, null, -1);
            }
            return cursor.TurnEnded(host) is { } why ? Miss(cursor.Next, why) : new ReplayRun(Checkpoints.Count, null, -1);
        }

        /// <summary>
        /// Where a replayed line is (which checkpoint comes next) and the answer to each prompt on the way: the audit's lines and the search's
        /// lines from the turn's start (Mcts.FromTurnStart) alike. With <see cref="IsRoot"/>, the last checkpoint is the decision being
        /// searched (no answer recorded yet): reaching it is the root.
        /// </summary>
        internal sealed class Cursor
        {
            private readonly TurnReplay _r;
            private int _next, _extra;
            private readonly MctsPolicy.LineState _line = new();
            private readonly Dictionary<int, int> _attacked = new();
            private SearchView.BattleAttack? _lastAttack;
            private SearchView.BattleCard? _declaredAttacker;
            private readonly List<string> _moves = new();

            public Cursor(TurnReplay replay) => _r = replay;

            /// <summary>The next checkpoint to reach (index).</summary>
            public int Next => _next;
            /// <summary>Every recorded answer replayed.</summary>
            public bool Done => _next >= _r.Checkpoints.Count;
            /// <summary>The searched decision (a prompt of ours whose board is the last checkpoint's): also its options must be the real ones.</summary>
            public Func<Prompt, DuelHost, bool>? IsRoot { get; init; }

            /// <summary>The line's turn ended: null when every checkpoint left only declined (the engine gave no window), else why it's a miss.</summary>
            public string? TurnEnded(DuelHost host) =>
                host.Result == null && IsRoot == null && _r.Checkpoints.Skip(_next).All(Declined) ? null
                    : (host.Result is { } r ? $"the duel ended ({r.Reason})" : $"turn {_r.RebuiltTurn} ended before checkpoint {_next + 1}") + _r.TheirTrail(host, _moves, _next);

            /// <summary>This prompt's answer (Packet), or the root reached (Root), or why the line left the real game (Miss).</summary>
            public (byte[]? Packet, bool Root, string? Miss) Step(Prompt prompt, DuelHost host, Random rng)
            {
                var checkpoints = _r.Checkpoints;
                if (host.Turn != _r.RebuiltTurn)
                    return (Mcts.Passive(prompt) ?? MctsPolicy.Answer(prompt, rng, _line, host), false, null);
                var activeAttack = CurrentAttack(host);
                if (activeAttack != null)
                    _lastAttack = activeAttack;
                if (prompt.Player == 0)
                {
                    if (prompt.Data is IdleCmdData)
                    {
                        while (_next < checkpoints.Count - 1 &&
                               checkpoints[_next] is { State: BotState.SelectChain, Answer.Key: "pass" } passed &&
                               passed.View.AttackInProgress == null &&
                               passed.Phase <= host.Phase && _r.SameCheckpointBoard(Key(BotState.SelectChain, host),
                                   Key(BotState.SelectChain, passed.View), passed.Phase))
                        {
                            _next++;
                            _extra = 0;
                        }
                    }
                    int k = _r.Match(prompt, host, _next, activeAttack, _lastAttack);
                    if (k >= 0)
                    {
                        var cp = checkpoints[k];
                        if (cp.Answer == null)
                        {
                            if (IsRoot != null && k == checkpoints.Count - 1 && IsRoot(prompt, host))
                            {
                                _next = k;
                                return (null, true, null);
                            }
                            if (IsRoot != null && Mcts.Passive(prompt) is { } notYet && _extra++ < MaxExtraPerGap)
                                return (notYet, false, null);
                            return (null, false, "no recorded answer");
                        }
                        if (ReplayKeys.Packet(prompt.Data, cp.Answer) is not { } packet)
                        {
                            // A pick Omega asked one card at a time (select/unselect) and the engine all at once (a sum: Machina Fortress's discard,
                            // fusion materials; seen 2026-11-18 live): the cards that really moved, and Omega's other prompts of that pick are passed.
                            if (Mcts.IsPick(prompt.State) && prompt.State != cp.State)
                            {
                                int after = k;
                                while (after < checkpoints.Count && checkpoints[after].State == cp.State)
                                    after++;
                                if (_r.OutcomePick(prompt.Data, host, Math.Min(after, checkpoints.Count - 1), 0) is { } whole)
                                {
                                    _next = after;
                                    _extra = 0;
                                    return (whole, false, null);
                                }
                            }
                            // The same board, but not the window the real game answered (seen 2026-11-18: Mirror Force not on offer yet, the window
                            // came before their attack): declined, and the checkpoint waits for the next one.
                            if (Mcts.Passive(prompt) is { } wait && _extra++ < MaxExtraPerGap)
                                return (wait, false, null);
                            return (null, false, $"our answer ({cp.Answer.Label}) not on offer at {prompt.State}");
                        }
                        _next = k + 1;
                        _extra = 0;
                        return (packet, false, null);
                    }
                    if (Mcts.Passive(prompt) is { } skip && _extra++ < MaxExtraPerGap)
                    {
                        _moves.Add($"(we declined a {prompt.State}: the line has {Difference(Key(prompt.State, host), Key(prompt.State, checkpoints[_next].View))})");
                        return (skip, false, null);
                    }
                    // A pick the real game made for us (a lone tribute, seen 2026-11-18: Thestalos over the Emissary token): what really moved.
                    if (Mcts.IsPick(prompt.State) && _extra++ < MaxExtraPerGap && _r.OutcomePick(prompt.Data, host, _next, 0) is { } picked)
                        return (picked, false, null);
                    // Prompts the real game answers for us without asking (a zone, an order: Omega and DuelSim never show them): any answer.
                    if (prompt.State is BotState.SelectPlace or BotState.SelectDisfield or BotState.SortCard)
                        return (MctsPolicy.Answer(prompt, rng, _line, host), false, null);
                    var want = checkpoints[_next];
                    return (null, false, $"at our {prompt.State} (phase 0x{host.Phase:x}) the line has {Difference(Key(prompt.State, host), Key(prompt.State, want.View))}; " +
                                         $"checkpoint {_next + 1} is {want.State} in phase 0x{want.Phase:x}{_r.TheirTrail(host, _moves, _next)}");
                }
                if (prompt.Data is IdleCmdData)
                {
                    // At an open Main Phase command, a recorded pass on this board may have had no
                    // equivalent window in the engine. Guide toward the next checkpoint instead of
                    // activating an unrelated ignition effect to manufacture that window.
                    string board = Key(BotState.SelectChain, host);
                    while (_next < checkpoints.Count - 1 &&
                           checkpoints[_next] is { State: BotState.SelectChain, Answer.Key: "pass", ChainCard: <= 0 } skipped &&
                           skipped.View.AttackInProgress == null &&
                           skipped.Phase == host.Phase && board == Key(BotState.SelectChain, skipped.View))
                    {
                        _next++;
                        _extra = 0;
                    }
                }
                // At the next open battle command, the previous attack's optional prompts can no longer occur.
                if (prompt.Data is BattleCmdData)
                {
                    while (_next < checkpoints.Count - 1 &&
                           checkpoints[_next] is { State: BotState.SelectChain, Answer.Key: "pass" } passed &&
                           passed.View.AttackInProgress != null && SameAttack(passed.View, _lastAttack))
                    {
                        _next++;
                        _extra = 0;
                    }
                }
                var (guided, stuck) = _r.Guide(prompt, host, _next, _attacked, _declaredAttacker);
                if (prompt.Data is BattleCmdData && stuck != null)
                {
                    string board = Key(BotState.SelectChain, host);
                    while (_next < checkpoints.Count - 1 &&
                           checkpoints[_next] is { State: BotState.SelectChain, Answer.Key: "pass" } passed &&
                           passed.View.AttackInProgress == null &&
                           IsBattle(passed.Phase) && board == Key(BotState.SelectChain, passed.View))
                    {
                        _next++;
                        _extra = 0;
                        (guided, stuck) = _r.Guide(prompt, host, _next, _attacked, _declaredAttacker);
                        if (stuck == null)
                            break;
                    }
                }
                if (stuck != null)
                    return (null, false, $"their {prompt.State} (phase 0x{host.Phase:x}): {stuck}{_r.TheirTrail(host, _moves, _next)}");
                if (prompt.Data is BattleCmdData battle)
                {
                    _declaredAttacker = null;
                    if (guided != null && BitConverter.ToUInt16(guided, 0) == (ushort)BattleAction.Attack)
                    {
                        var card = SearchActions.AttackerFor(battle, BitConverter.ToUInt16(guided, 2));
                        _declaredAttacker = new(card.ID, card.CurrentCoordinates.Sequence);
                    }
                }
                if (guided != null && prompt.Data is IdleCmdData or BattleCmdData)
                    _moves.Add(MoveLabel(prompt.Data, guided));
                else if (guided != null && prompt.Data is SelectChainData theirChain)
                    _moves.Add(BitConverter.ToInt32(guided, 0) is int c && c >= 0 && c < (theirChain.SelectableCards?.Length ?? 0)
                        ? $"chain {OmegaBotWrapper.Logging.CardNames.Get(theirChain.SelectableCards![c].ID)}" : "pass");
                else if (guided != null && prompt.Data is SelectEffectYnData)
                    _moves.Add(guided[0] == 1 ? "yes" : "no");
                return (guided ?? MctsPolicy.Answer(prompt, rng, _line, host), false, null);
            }
        }


        /// <summary>For a miss: their hand in the line and the guide's last main / battle moves for them.</summary>
        private string TheirTrail(DuelHost host, List<string> moves, int next) =>
            $" [their hand in the line: {string.Join(", ", host.Summary(1).HandCodes.Select(c => c == SearchView.InertCard ? "?" : OmegaBotWrapper.Logging.CardNames.Get(c)))}; " +
            $"their moves: {(moves.Count == 0 ? "none" : string.Join(" > ", moves.TakeLast(8)))}; " +
            $"next checkpoints: {string.Join(", ", Checkpoints.Skip(next).Take(3).Select((c, i) => $"{next + i + 1} {c.State} in 0x{c.Phase:x}: {c.Answer?.Label ?? "?"}"))}]";

        private static string MoveLabel(object data, byte[] packet)
        {
            string? label = data switch
            {
                IdleCmdData idle => SearchActions.Idle(idle).FirstOrDefault(c => SearchActions.Packet(c.Response).AsSpan().SequenceEqual(packet))?.Label,
                BattleCmdData battle => SearchActions.Battle(battle).FirstOrDefault(c => SearchActions.Packet(c.Response).AsSpan().SequenceEqual(packet))?.Label,
                _ => null,
            };
            return label ?? "?";
        }

        private static string Key(BotState state, DuelHost host) => DecisionKey.Of(state, host.Summary(0), host.Summary(1), Array.Empty<string>());
        private static string Key(BotState state, SearchView view) => DecisionKey.Of(state, view, Array.Empty<string>());

        /// <summary>A recorded answer that only declined (no chain, no effect): the line may not get that prompt at all.</summary>
        // (A zone prompt counts too: Omega asks where a monster goes, the engine often places it itself; seen 2026-11-18, Thestalos's zone.)
        private static bool Declined(ReplayCheckpoint cp) =>
            cp.State == BotState.SelectPlace ||
            cp.Answer is { } a && (a.Key == "pass" || cp.State is BotState.SelectEffectYn or BotState.SelectYesNo && a.Key == "raw:00");

        private static SearchView.BattleAttack? CurrentAttack(DuelHost host)
        {
            if (!host.Attacking)
                return null;
            var (ours, theirs) = host.BattlingCards(0);
            static SearchView.BattleCard? Card(DynamicCard? card) =>
                card is { } c ? new(c.ID, c.CurrentCoordinates.Sequence) : null;
            bool theirAttack = host.IsOpponentAttacking(0);
            var target = Card(theirAttack ? ours : theirs);
            return Card(theirAttack ? theirs : ours) is { } attacker && (target != null || host.IsDirectAttack)
                ? new(theirAttack ? 1 : 0, attacker, target) : null;
        }

        private static bool SameBattler(SearchView.BattleCard expected, SearchView.BattleCard actual, List<ViewCard> field) =>
            expected.Code <= 0 ? expected.Zone == actual.Zone :
            Canon(expected.Code) == Canon(actual.Code) &&
            (expected.Zone == actual.Zone || field.Count(c => Canon(c.Code) == Canon(expected.Code)) == 1);

        private static bool SameAttack(SearchView view, SearchView.BattleAttack? actual)
        {
            if (view.AttackInProgress is not { } expected)
                return actual == null;
            if (actual is not { } attack || attack.Player != expected.Player)
                return false;
            var attackers = attack.Player == 0 ? view.OwnMonsters : view.EnemyMonsters;
            var defenders = attack.Player == 0 ? view.EnemyMonsters : view.OwnMonsters;
            return SameBattler(expected.Attacker, attack.Attacker, attackers) &&
                (expected.Target is { } target
                    ? attack.Target is { } actualTarget && SameBattler(target, actualTarget, defenders)
                    : attack.Target == null);
        }

        /// <summary>The checkpoint this prompt of ours is (the next one, or a later one past checkpoints that only declined), or -1.</summary>
        private int Match(Prompt prompt, DuelHost host, int next, SearchView.BattleAttack? activeAttack, SearchView.BattleAttack? lastAttack)
        {
            string line = Key(prompt.State, host);
            List<string>? offered = null;
            for (int k = next; k < Checkpoints.Count; k++)
            {
                var cp = Checkpoints[k];
                if (Mcts.SameKind(prompt.State, cp.State) &&
                    (host.Phase == cp.Phase || IsBattle(host.Phase) && IsBattle(cp.Phase)) &&
                    (cp.State != BotState.SelectChain || !IsBattle(cp.Phase) || SameAttack(cp.View, activeAttack)) &&
                    SameCheckpointBoard(line, Key(prompt.State, cp.View), cp.Phase) &&
                    (cp.ChainOptions is not { Count: > 0 } || prompt.Data is SelectChainData chain &&
                        cp.ChainOptions.Any(key => SearchActions.MatchKey(key, offered ??= SearchActions.ChainOptionKeys(chain)) != null)) &&
                    SameChainLink(cp, host))
                    return k;
                if (!Declined(cp) || cp.View.AttackInProgress != null &&
                    !SameAttack(cp.View, activeAttack) && !SameAttack(cp.View, lastAttack))
                    break;
            }
            return -1;
        }

        /// <summary>
        /// A chain window whose real chain had their card on it (ChainCard): the line's window must have that card as its last link too (live
        /// 2026-11-24, Machina turn 8: the replay stopped at our window after XX-Saber Faultroll's special summon, same board and options as the
        /// real one, before its revive targeting X-Saber Airbellum; with no graveyard target in the lines, D.D. Crow's rule held it).
        /// </summary>
        private static bool SameChainLink(ReplayCheckpoint cp, DuelHost host) =>
            cp.State != BotState.SelectChain || cp.ChainCard <= 0 ||
            host.CurrentSearchChain(0) is { Player: 1 } link && Canon(link.CardId) == Canon(cp.ChainCard);

        internal bool SameCheckpointBoard(string line, string real, int phase)
        {
            if (line == real)
                return true;
            if (!Ours || phase != (int)DuelPhase.Draw)
                return false;
            // Omega's early draw windows can hide the drawn card. Require the rest
            // of the position and every known hand card to match.
            var actual = line.Split('|');
            var expected = real.Split('|');
            var hand = actual[3].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            var known = expected[3].Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (hand.Count != known.Length + 1 || known.Length != Start.OwnHand.Count ||
                !known.All(hand.Remove))
                return false;
            actual[3] = expected[3];
            return actual.SequenceEqual(expected);
        }

        private static readonly string[] KeyParts = { "prompt", "our LP", "their LP", "our hand", "our monsters", "our spells/traps", "our graveyard",
            "their monsters", "their spells/traps", "their graveyard", "options" };

        /// <summary>The first part of two decision keys that differs, named.</summary>
        private static string Difference(string line, string real)
        {
            var a = line.Split('|', '#');
            var b = real.Split('|', '#');
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i])
                    return $"{(i < KeyParts.Length ? KeyParts[i] : $"part {i}")} [{a[i]}], the real game had [{b[i]}]";
            return "the same board (a prompt the real game didn't give us)";
        }

        // ------------------------------------------------------------------ their moves

        /// <summary>What their side still has to do to look like the target board.</summary>
        private sealed class Needs
        {
            public Dictionary<int, int> UpMonsters = new(), UpSpells = new(), Grave = new(), Defense = new(), Attack = new();
            /// <summary>Their cards out of the hand by the checkpoint (on the field, in the graveyard or banished) beyond the line's: played
            /// on the way, even when they're gone again by then (Treeborn Frog set, then banished for Miracle Fusion).</summary>
            public Dictionary<int, int> Placed = new();
            public int SetMonsters, SetSpells;
            public bool Any(int code) => UpMonsters.GetValueOrDefault(code) > 0 || UpSpells.GetValueOrDefault(code) > 0 || Grave.GetValueOrDefault(code) > 0;
        }

        private static Needs NeedsFor(SideSummary line, SearchView target)
        {
            static Dictionary<int, int> Minus(Dictionary<int, int> a, Dictionary<int, int> b) =>
                a.Where(kv => kv.Value > b.GetValueOrDefault(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value - b.GetValueOrDefault(kv.Key));
            var t = target;
            var n = new Needs
            {
                UpMonsters = Minus(Count(t.EnemyMonsters.Where(m => m.Code > 0 && FaceUp(m.Position)).Select(m => m.Code)),
                                   Count(line.Monsters.Where(m => m.FaceUp).Select(m => m.Code))),
                UpSpells = Minus(Count(t.EnemySpells.Where(s => s.Code > 0 && FaceUp(s.Position)).Select(s => s.Code)),
                                 Count(line.SpellCodes.Where(s => s.FaceUp).Select(s => s.Code))),
                Grave = Minus(Count(t.EnemyGrave), Count(line.GraveCodes)),
                Defense = Minus(Count(t.EnemyMonsters.Where(m => m.Code > 0 && FaceUp(m.Position) && (m.Position & 0x3) == 0).Select(m => m.Code)),
                                Count(line.Monsters.Where(m => m.FaceUp && !m.AttackPosition).Select(m => m.Code))),
                Attack = Minus(Count(t.EnemyMonsters.Where(m => m.Code > 0 && FaceUp(m.Position) && (m.Position & 0x3) != 0).Select(m => m.Code)),
                               Count(line.Monsters.Where(m => m.FaceUp && m.AttackPosition).Select(m => m.Code))),
                SetMonsters = t.EnemyMonsters.Count(m => m.Code <= 0 || !FaceUp(m.Position)) - line.Monsters.Count(m => !m.FaceUp),
                SetSpells = t.EnemySpells.Count(s => s.Code <= 0 || !FaceUp(s.Position)) - line.SpellCodes.Count(s => !s.FaceUp),
                Placed = Minus(Count(t.EnemyMonsters.Select(m => m.Code).Concat(t.EnemySpells.Select(s => s.Code)).Concat(t.EnemyGrave).Concat(t.EnemyBanished)),
                               Count(line.Monsters.Select(m => m.Code).Concat(line.SpellCodes.Select(s => s.Code)).Concat(line.GraveCodes))),
            };
            return n;
        }

        private static bool IsBattle(int phase) => phase is >= (int)DuelPhase.BattleStart and <= (int)DuelPhase.Battle;

        /// <summary>
        /// Their answer toward the next checkpoint: (packet, null), or (null, null) to let the quick policy answer, or (null, why) when a
        /// main / battle prompt of theirs has no move that gets there (the line can't follow the real game).
        /// </summary>
        private (byte[]? Packet, string? Stuck) Guide(Prompt prompt, DuelHost host, int next, Dictionary<int, int> attacked,
            SearchView.BattleCard? declaredAttacker)
        {
            var cp = Checkpoints[next];
            var enemy = host.Summary(1);
            var needs = NeedsFor(enemy, cp.View);
            switch (prompt.Data)
            {
                case IdleCmdData idle:
                {
                    var options = SearchActions.Idle(idle);
                    byte[]? Pick(MainAction action, Func<int, bool> card)
                    {
                        string prefix = $"{(int)action}:";
                        var o = options.FirstOrDefault(c => c.Key.StartsWith(prefix) && card(Canon(SearchActions.KeyCardId(c.Key))));
                        return o == null ? null : SearchActions.Packet(o.Response);
                    }
                    // A card that can summon itself is special summoned (keeping the Normal Summon, as a player would: seen 2026-11-18, the replay
                    // normal summoned Infernal Prodigy and then had no Normal Summon left for the real Raiza).
                    var found = Pick(MainAction.SpSummon, c => needs.UpMonsters.GetValueOrDefault(c) > 0)
                                ?? Pick(MainAction.Activate, c => needs.UpMonsters.GetValueOrDefault(c) > 0 && SummonsItself(c)) // an effect that summons itself
                                ?? Pick(MainAction.Summon, c => needs.UpMonsters.GetValueOrDefault(c) > 0)
                                ?? Pick(MainAction.Repos, c => needs.UpMonsters.GetValueOrDefault(c) > 0) // a flip summon
                                // An Extra Deck monster to appear that nothing summons directly: a graveyard effect that brings it out (Vayu's, seen
                                // 2026-11-18: Blackwing Armed Wing).
                                ?? (needs.UpMonsters.Keys.Any(ExtraDeckMonster) && options.FirstOrDefault(o => o.Key.StartsWith($"{(int)MainAction.Activate}:") && o.Key.Contains("@0.16.")) is { } fromGrave
                                    ? SearchActions.Packet(fromGrave.Response) : null)
                                // A monster to appear that sits in their graveyard: a face-up monster's effect that revives from the graveyard
                                // (seen 2026-11-24: XX-Saber Faultroll revived XX-Saber Emmersblade; the replay summoned Faultroll and went to battle).
                                ?? (needs.UpMonsters.Keys.Any(c => enemy.GraveCodes.Any(g => Canon(g) == c)) &&
                                    options.FirstOrDefault(o => o.Key.StartsWith($"{(int)MainAction.Activate}:") && o.Key.Contains("@0.4.") &&
                                                                RevivesFromGrave(Canon(SearchActions.KeyCardId(o.Key)))) is { } revive
                                    ? SearchActions.Packet(revive.Response) : null)
                                ?? Pick(MainAction.Activate, c => needs.UpSpells.GetValueOrDefault(c) > 0 || needs.Grave.GetValueOrDefault(c) > 0)
                                ?? (needs.SetMonsters > 0 ? Pick(MainAction.SetMonster, c => !needs.Any(c)) ?? Pick(MainAction.SetMonster, _ => true) : null)
                                ?? (needs.SetSpells > 0 ? Pick(MainAction.SetSpell, c => c == SearchView.InertCard) ?? Pick(MainAction.SetSpell, c => !needs.Any(c)) : null)
                                ?? WrongPosition();
                    // A face-up monster in the wrong position: only the copy that needs it (seen 2026-11-18: with two Machina Gearframes, the
                    // replay turned the one already in attack position to defence).
                    byte[]? WrongPosition()
                    {
                        foreach (var card in idle.ReposableCards ?? Array.Empty<DynamicCard>())
                        {
                            if (!FaceUp(card.Position))
                                continue;
                            int c = Canon(card.ID);
                            if (!((card.Position & 0x3) != 0 ? needs.Defense.GetValueOrDefault(c) > 0 : needs.Attack.GetValueOrDefault(c) > 0))
                                continue;
                            string at = $"{(int)MainAction.Repos}:{SearchActions.CardKey(card)}";
                            if (options.FirstOrDefault(o => o.Key == at) is { } o)
                                return SearchActions.Packet(o.Response);
                        }
                        return null;
                    }
                    // Nothing visible left to do, but a known card of theirs left the hand by the checkpoint: it was played on the way (a material
                    // gone again by then). Set first: a summoned one would have shown face-up at a window.
                    bool Played(int c) => needs.Placed.GetValueOrDefault(c) > 0 && c != SearchView.InertCard && c != SearchView.UnknownSetMonster;
                    found ??= Pick(MainAction.SetMonster, Played) ?? Pick(MainAction.Summon, Played) ?? Pick(MainAction.SpSummon, Played)
                              ?? Pick(MainAction.Activate, Played) ?? Pick(MainAction.SetSpell, Played);
                    if (found != null)
                        return (found, null);
                    // (The next checkpoint's phase; a later one's only once the boards match: the line mustn't jump past windows of the real Main Phase.)
                    bool same = Key(BotState.SelectChain, host) == Key(BotState.SelectChain, cp.View);
                    int phase = same ? PhaseAhead(next, host.Phase) : cp.Phase;
                    var phaseKey = (DuelPhase)phase switch
                    {
                        _ when IsBattle(phase) => "battle",
                        DuelPhase.Main2 => options.Any(o => o.Key == "battle") ? "battle" : "end",
                        DuelPhase.End => "end",
                        _ => null,
                    };
                    // The line's board is the checkpoint's already and nothing is left to do: the real game's next prompt of ours came as the turn
                    // moved on (seen 2026-11-18: they set Shield Warrior, and our next prompt came in their End Phase).
                    // ...unless that prompt is a chain window in this phase: they used an effect whose result isn't on the board yet (an ignition
                    // effect of a card on their field): its activation.
                    // The card the real chain had on it first (ChainCard: Faultroll's revive, not another monster's effect).
                    bool FieldEffect(SearchActions.Choice<IdleCmdResponse> o) =>
                        o.Key.StartsWith($"{(int)MainAction.Activate}:") && (o.Key.Contains("@0.4.") || o.Key.Contains("@0.8."));
                    if (same && cp.State == BotState.SelectChain && (IsBattle(cp.Phase) ? IsBattle(host.Phase) : cp.Phase == host.Phase) &&
                        (options.FirstOrDefault(o => cp.ChainCard > 0 && FieldEffect(o) && Canon(SearchActions.KeyCardId(o.Key)) == Canon(cp.ChainCard))
                         ?? options.FirstOrDefault(FieldEffect)) is { } effect)
                        return (SearchActions.Packet(effect.Response), null);
                    if (phaseKey == null && same)
                        phaseKey = "end";
                    if (phaseKey != null && options.FirstOrDefault(o => o.Key == phaseKey) is { } move)
                        return (SearchActions.Packet(move.Response), null);
                    return (null, $"no move toward checkpoint {next + 1} ({cp.State} in phase 0x{cp.Phase:x}): {Describe(needs)}");
                }
                case BattleCmdData battle:
                {
                    var options = SearchActions.Battle(battle);
                    bool matched = Key(BotState.SelectChain, host) == Key(BotState.SelectChain, cp.View);
                    if (matched && cp.View.AttackInProgress is { Player: 1 } observed)
                    {
                        var attackMove = options.FirstOrDefault(o => o.Key.StartsWith("attack:") &&
                            SearchActions.AttackerFor(battle, o.Response.Index) is var card &&
                            SameBattler(observed.Attacker, new(card.ID, card.CurrentCoordinates.Sequence), cp.View.EnemyMonsters));
                        if (attackMove == null)
                            return (null, $"recorded attacker {observed.Attacker.Code} is unavailable");
                        int code = Canon(observed.Attacker.Code);
                        attacked[code] = attacked.GetValueOrDefault(code) + 1;
                        return (SearchActions.Packet(attackMove.Response), null);
                    }
                    var done = Count(cp.View.EnemyMonsters.Where(m => m.Attacked && m.Code > 0).Select(m => m.Code));
                    // A damage-triggered window such as Gorz can be the first prompt
                    // after a direct attack, with the attacker unmarked by Omega.
                    int directDamage = 0;
                    if (cp.State == BotState.SelectChain && IsBattle(cp.Phase) && cp.View.OwnMonsters.Count == 0)
                    {
                        var own = host.Summary(0);
                        if (own.Monsters.Count == 0 && enemy.LifePoints == cp.View.LifePoints[1])
                            directDamage = own.LifePoints - cp.View.LifePoints[0];
                    }
                    var attack = options.FirstOrDefault(o => o.Key.StartsWith("attack:") &&
                        done.GetValueOrDefault(Canon(SearchActions.KeyCardId(o.Key))) > attacked.GetValueOrDefault(Canon(SearchActions.KeyCardId(o.Key))));
                    if (attack == null && directDamage > 0)
                    {
                        attack = options.FirstOrDefault(o => o.Key.StartsWith("attack:") &&
                            enemy.Monsters.Any(m => m.FaceUp && m.AttackPosition && m.Attack == directDamage &&
                                Canon(m.Code) == Canon(SearchActions.KeyCardId(o.Key))));
                    }
                    if (attack != null)
                    {
                        int code = Canon(SearchActions.KeyCardId(attack.Key));
                        attacked[code] = attacked.GetValueOrDefault(code) + 1;
                        return (SearchActions.Packet(attack.Response), null);
                    }
                    var activate = options.FirstOrDefault(o => o.Key.StartsWith("bact:") && needs.Any(Canon(SearchActions.KeyCardId(o.Key))));
                    if (activate != null)
                        return (SearchActions.Packet(activate.Response), null);
                    // Our window in their Battle Phase with nothing on the board to show for it: their attack declaration (live Omega never
                    // marks their attackers; seen 2026-11-18: Mirror Force offered as Blue Thunder T-45 attacked, the replay never attacked).
                    // With no attacker recorded, try an eligible attacker. OutcomePick reconstructs its target.
                    if (matched && cp.State == BotState.SelectChain && IsBattle(cp.Phase) &&
                        options.FirstOrDefault(o => o.Key.StartsWith("attack:") &&
                            attacked.GetValueOrDefault(Canon(SearchActions.KeyCardId(o.Key))) == 0) is { } declared)
                    {
                        int code = Canon(SearchActions.KeyCardId(declared.Key));
                        attacked[code] = attacked.GetValueOrDefault(code) + 1;
                        return (SearchActions.Packet(declared.Response), null);
                    }
                    string? phaseKey =(DuelPhase)(matched ? PhaseAhead(next, host.Phase) : cp.Phase) switch
                    {
                        DuelPhase.Main2 => "main2",
                        DuelPhase.End => options.Any(o => o.Key == "end") ? "end" : "main2",
                        _ => null,
                    };
                    if (phaseKey != null && options.FirstOrDefault(o => o.Key == phaseKey) is { } move)
                        return (SearchActions.Packet(move.Response), null);
                    return (null, $"no attack or move toward checkpoint {next + 1} ({cp.State} in phase 0x{cp.Phase:x}): {Describe(needs)}");
                }
                case SelectCardData pick when pick.SelectionHint == (int)DHint.ATTACKTARGET &&
                    cp.View.AttackInProgress is { Player: 1 } observed && declaredAttacker is { } declared &&
                    SameBattler(observed.Attacker, declared, cp.View.EnemyMonsters):
                {
                    var cards = pick.SelectableCards ?? Array.Empty<DynamicCard>();
                    int index = observed.Target is { } target ? Array.FindIndex(cards, card =>
                        SameBattler(target, new(card.ID, card.CurrentCoordinates.Sequence), cp.View.OwnMonsters)) : -1;
                    return index >= 0 ? (new[] { (byte)1, (byte)index }, null)
                        : (null, $"recorded attack target {observed.Target?.Code.ToString() ?? "direct"} is unavailable");
                }
                // The target of their effect the real chain reported (ChainTargets: Faultroll's revive targeting X-Saber Airbellum in their
                // graveyard), not what the quick policy would pick: the window's target is what D.D. Crow answers.
                case SelectCardData targetPick when ChainTargetIndex(targetPick, cp) is int target and >= 0 &&
                                                    Key(BotState.SelectChain, host) == Key(BotState.SelectChain, cp.View):
                    return (new[] { (byte)1, (byte)target }, null);
                case SelectChainData chain:
                {
                    var cards = chain.SelectableCards ?? Array.Empty<DynamicCard>();
                    bool fieldNeeds = needs.UpMonsters.Count > 0 || needs.UpSpells.Count > 0 || needs.SetMonsters > 0 || needs.SetSpells > 0;
                    for (int i = 0; i < cards.Length; i++)
                        if (needs.Any(Canon(cards[i].ID)) && !DestroyedByUs(cards[i], needs, next) && !SummonedInstead(cards[i], needs, host) &&
                            !AlreadyUp(cards[i], needs) && !(fieldNeeds && host.Phase < cp.Phase && SetForGraveOnly(cards[i], needs)))
                            return (BitConverter.GetBytes(i), null);
                    // Their trigger that changes only our side (Raiza's bounce): a face-up card of theirs on the field, not a card from their hand
                    // or a set one (seen 2026-11-18: their Mystical Space Typhoon chained from hand to their own Pot of Greed in the replay).
                    if (TheirEffectOnUs(host, cp, needs))
                        for (int i = 0; i < cards.Length; i++)
                            if (cards[i].CurrentCoordinates.Location == (byte)CardLocation.MonsterZone && (cards[i].Position & 0x5) != 0)
                                return (BitConverter.GetBytes(i), null);
                    return (chain.Forced ? BitConverter.GetBytes(0) : BitConverter.GetBytes(-1), null);
                }
                case SelectEffectYnData:
                    return (new[] { (byte)(TheirEffectOnUs(host, cp, needs) || OwnTrigger(host, cp) ? 1 : 0) }, null);
                case SelectPositionData position:
                {
                    int code = Canon(position.CardID);
                    byte want = needs.Defense.GetValueOrDefault(code) > 0 ? (byte)CardPosition.FaceUpDefence
                        : needs.Attack.GetValueOrDefault(code) > 0 ? (byte)CardPosition.FaceUpAttack
                        : needs.SetMonsters > 0 ? (byte)CardPosition.FaceDownDefence : (byte)0;
                    return want != 0 && (position.Positions ?? Array.Empty<byte>()).Contains(want) ? (new[] { want }, null) : (null, null);
                }
            }
            return (OutcomePick(prompt.Data, host, next, 1), null);
        }

        /// <summary>Their one-card pick of a card in their own graveyard the checkpoint's chain link targets (ChainTargets), or -1.
        /// (The pick is theirs: controller 0 is their side.)</summary>
        private static int ChainTargetIndex(SelectCardData pick, ReplayCheckpoint cp)
        {
            if (cp.ChainTargets is not { Count: > 0 } targets || pick.Minimum > 1)
                return -1;
            var cards = pick.SelectableCards ?? Array.Empty<DynamicCard>();
            return Array.FindIndex(cards, card => card.CurrentCoordinates is { Controller: 0, Location: (byte)CardLocation.Grave } &&
                targets.Any(t => Canon(t) == Canon(card.ID)));
        }

        /// <summary>A face-up monster of theirs whose only need is another face-up copy: it's already there, so its effect makes nothing
        /// the checkpoint wants (seen 2026-11-18: the replay chained the on-field Machina Gearframe's equip for the second Gearframe).</summary>
        private static bool AlreadyUp(DynamicCard card, Needs needs)
        {
            int code = Canon(card.ID);
            return card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone && FaceUp(card.Position) &&
                   needs.Grave.GetValueOrDefault(code) <= 0 && needs.UpSpells.GetValueOrDefault(code) <= 0;
        }

        /// <summary>A set spell/trap of theirs the checkpoint only has in the graveyard: before an earlier phase's summons and sets are made, it
        /// waits (seen 2026-11-18: the replay fired their Trap Dustshoot in the Draw Phase; the real game used it as the Battle Phase began,
        /// after Machina Fortress and Machina Gearframe were summoned).</summary>
        private static bool SetForGraveOnly(DynamicCard card, Needs needs)
        {
            int code = Canon(card.ID);
            return card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone && !FaceUp(card.Position) &&
                   needs.Grave.GetValueOrDefault(code) > 0 && needs.UpSpells.GetValueOrDefault(code) <= 0;
        }

        /// <summary>
        /// Their effect on offer (a trigger) is the one the real game used: in their own turn, in the checkpoint's phase, with their side as the
        /// checkpoint has it but ours not (seen 2026-11-18: Raiza's return of our set Solemn Judgment, which changes only our side).
        /// </summary>
        private static bool TheirEffectOnUs(DuelHost host, ReplayCheckpoint cp, Needs needs) =>
            host.TurnPlayer == 1 && host.Phase == cp.Phase &&
            // (Their graveyard may still differ: a trigger can fill it, seen 2026-11-18: Ryko's mill.)
            needs.UpMonsters.Count == 0 && needs.UpSpells.Count == 0 && needs.SetMonsters <= 0 && needs.SetSpells <= 0 &&
            Key(BotState.SelectChain, host) != Key(BotState.SelectChain, cp.View);

        /// <summary>
        /// A monster in their hand the checkpoint has face-up (and nowhere else new), at a chain window in their own Main Phase: they summon it
        /// at their main prompt instead (seen 2026-11-18: the replay chained D.D. Crow's discard effect, in their Main and Draw Phases, the real game normal summoned it).
        /// Outside their Main Phase a hand monster that turns up face-up summoned itself (Gorz, Battle Fader).
        /// </summary>
        private static bool SummonedInstead(DynamicCard card, Needs needs, DuelHost host)
        {
            int code = Canon(card.ID);
            return card.CurrentCoordinates.Location == (byte)CardLocation.Hand && host.TurnPlayer == 1 &&
                   !IsBattle(host.Phase) &&
                   needs.UpMonsters.GetValueOrDefault(code) > 0 && needs.Grave.GetValueOrDefault(code) == 0;
        }

        /// <summary>Their own turn, the checkpoint in the line's phase, and the board not yet the checkpoint's: their triggers are used.</summary>
        private static bool OwnTrigger(DuelHost host, ReplayCheckpoint cp) =>
            host.TurnPlayer == 1 && (IsBattle(cp.Phase) ? IsBattle(host.Phase) : cp.Phase == host.Phase) &&
            Key(BotState.SelectChain, host) != Key(BotState.SelectChain, cp.View);

        /// <summary>
        /// A set card of theirs the checkpoint has in their graveyard, which our own answers on the way there destroy: one of our recorded
        /// picks up to that checkpoint takes a face-down card (seen 2026-11-18: our Ryko destroyed their set Torrential Tribute; the replay had
        /// them activate it to get it into the graveyard). Not activated then.
        /// </summary>
        private bool DestroyedByUs(DynamicCard card, Needs needs, int next)
        {
            int code = Canon(card.ID);
            if (card.CurrentCoordinates.Location != (byte)CardLocation.SpellZone || (card.Position & 0x5) != 0 ||
                needs.Grave.GetValueOrDefault(code) <= 0 || needs.UpSpells.GetValueOrDefault(code) > 0)
                return false;
            for (int k = next; k < Checkpoints.Count; k++)
            {
                if (Checkpoints[k].Answer is { } a && a.Key.StartsWith("pick:") && a.Key.Split(':', '+').Any(part => part.StartsWith("0@")))
                    return true;
                if (Count(Checkpoints[k].View.EnemyGrave).GetValueOrDefault(code) > 0 && k > next)
                    break;
            }
            return false;
        }

        /// <summary>
        /// The phase their main / battle moves head for: the next checkpoint's, or when it's still in the line's phase (nothing left to do
        /// there: Omega's header can show the phase a step late), the first later checkpoint's that is in another phase.
        /// </summary>
        private int PhaseAhead(int next, int linePhase)
        {
            static int Stage(int p) => IsBattle(p) ? (int)DuelPhase.BattleStart : p;
            for (int k = next; k < Checkpoints.Count; k++)
                if (Stage(Checkpoints[k].Phase) != Stage(linePhase))
                    return Checkpoints[k].Phase;
            return Checkpoints[next].Phase;
        }

        /// <summary>What the rebuild dealt them (for a miss): their known hand, deck top, Extra Deck and named set cards.</summary>
        public string DescribeStart()
        {
            static string Names(IEnumerable<int> ids) => string.Join(", ", ids.Select(OmegaBotWrapper.Logging.CardNames.Get));
            return $"dealt them: hand [{Names(Start.EnemyKnownHand)}] + {Math.Max(0, Start.EnemyHandCount - Start.EnemyKnownHand.Count)} unknown; " +
                   $"deck top [{Names(Start.EnemyDeckTop)}]; Extra Deck [{Names(Start.EnemyKnownExtra)}]; named sets [{Names(Start.EnemyMonsters.Concat(Start.EnemySpells).Where(c => c.Code > 0 && !FaceUp(c.Position)).Select(c => c.Code))}]; " +
                   $"our deck top [{Names(Start.KnownTop.Take(5))}], forced draws [{Names(Start.ForcedDraws)}]";
        }

        private static string Describe(Needs n)
        {
            var parts = new List<string>();
            static string Names(Dictionary<int, int> d) => string.Join(", ", d.Select(kv => $"{OmegaBotWrapper.Logging.CardNames.Get(kv.Key)}{(kv.Value > 1 ? $" x{kv.Value}" : "")}"));
            if (n.UpMonsters.Count > 0) parts.Add($"face-up monsters {Names(n.UpMonsters)}");
            if (n.UpSpells.Count > 0) parts.Add($"face-up spells/traps {Names(n.UpSpells)}");
            if (n.Grave.Count > 0) parts.Add($"graveyard {Names(n.Grave)}");
            if (n.SetMonsters > 0) parts.Add($"{n.SetMonsters} more set monster(s)");
            if (n.SetSpells > 0) parts.Add($"{n.SetSpells} more set spell/trap(s)");
            return parts.Count == 0 ? "their side already matches; the real game's next step isn't visible on their side" : "still missing " + string.Join("; ", parts);
        }

        /// <summary>
        /// A card pick, as the real game came out (their picks, and ours the real game never asked us: a lone tribute it took for us): the
        /// picking player's cards that moved by the next checkpoint (a hand card that turned up on the field or in the graveyard, a field card
        /// that left, a graveyard card that left, a known card from the deck), the other player's cards that are gone there. Looks further
        /// ahead when nothing fits the next checkpoint (an attack target that only goes at the damage). Null: the quick policy picks.
        /// </summary>
        private byte[]? OutcomePick(object data, DuelHost host, int next, int player)
        {
            var (cards, min, max) = data switch
            {
                SelectCardData d => (d.SelectableCards, d.Minimum, d.Maximum),
                SelectTributeData d => (d.SelectableCards, d.Minimum, d.Maximum),
                SelectUnselectCardData d => (d.SelectableCards, Math.Max(1, d.Minimum), d.Maximum),
                SelectSumData d => (d.SelectableCards, Math.Max(1, d.Minimum), Math.Max(d.Maximum, d.SelectableCards?.Length ?? 0)),
                _ => (null, 0, 0),
            };
            if (cards == null || cards.Length == 0)
                return null;
            var lines = new[] { host.Summary(0), host.Summary(1) };
            for (int k = next; k < Checkpoints.Count; k++)
            {
                var t = Checkpoints[k].View;
                // Per engine side (0 = us, 1 = them): the checkpoint's cards, and the line's.
                var tField = new[] { t.OwnMonsters.Concat(t.OwnSpells), t.EnemyMonsters.Concat(t.EnemySpells) };
                var tPlaced = new[] { Count(t.OwnMonsters.Select(m => m.Code).Concat(t.OwnSpells.Select(s => s.Code)).Concat(t.OwnGrave).Concat(t.OwnBanished)),
                                      Count(t.EnemyMonsters.Select(m => m.Code).Concat(t.EnemySpells.Select(s => s.Code)).Concat(t.EnemyGrave).Concat(t.EnemyBanished)) };
                var tGrave = new[] { Count(t.OwnGrave), Count(t.EnemyGrave) };
                var lPlaced = lines.Select(l => Count(l.Monsters.Select(m => m.Code).Concat(l.SpellCodes.Select(s => s.Code)).Concat(l.GraveCodes))).ToArray();
                var lGrave = lines.Select(l => Count(l.GraveCodes)).ToArray();
                var tOurHand = Count(t.OwnHand);
                var lOurHand = Count(lines[0].HandCodes);
                var chosen = new List<byte>();
                for (int i = 0; i < cards.Length && chosen.Count < Math.Max(1, max); i++)
                {
                    var at = cards[i].CurrentCoordinates;
                    int side = at.Controller == 0 ? player : 1 - player; // the prompt sees the board from the picking player's side
                    // Our hand card in their pick is hidden from them (Trap Dustshoot's pick, seen 2026-11-18: the line sent back a random card):
                    // the engine knows it, from our side.
                    int id = cards[i].ID > 0 || side != 0 ? cards[i].ID
                        : host.CardAt(0, 0, at.Location, at.Sequence)?.ID ?? 0;
                    int code = Canon(id);
                    bool StillThere(bool monsters) => (side == 0 ? (monsters ? t.OwnMonsters : t.OwnSpells) : (monsters ? t.EnemyMonsters : t.EnemySpells))
                        .Any(c => c.Sequence == at.Sequence && (c.Code <= 0 || Canon(c.Code) == code));
                    bool moved = at.Location switch
                    {
                        (byte)CardLocation.Hand => side == 0 ? tOurHand.GetValueOrDefault(code) < lOurHand.GetValueOrDefault(code)
                                                             : tPlaced[1].GetValueOrDefault(code) > lPlaced[1].GetValueOrDefault(code),
                        (byte)CardLocation.MonsterZone => !StillThere(true),
                        (byte)CardLocation.SpellZone => !StillThere(false),
                        (byte)CardLocation.Grave => tGrave[side].GetValueOrDefault(code) < lGrave[side].GetValueOrDefault(code),
                        (byte)CardLocation.Deck => side == 0 ? tOurHand.GetValueOrDefault(code) > lOurHand.GetValueOrDefault(code) ||
                                                               tPlaced[0].GetValueOrDefault(code) > lPlaced[0].GetValueOrDefault(code)
                                                             : code != SearchView.InertCard,
                        // From the Extra Deck: the monster that turned up (a synchro by Vayu, a fusion).
                        (byte)CardLocation.Extra => tPlaced[side].GetValueOrDefault(code) > lPlaced[side].GetValueOrDefault(code),
                        _ => false,
                    };
                    if (!moved)
                        continue;
                    chosen.Add((byte)i);
                    // One copy per card that really moved.
                    switch (at.Location)
                    {
                        case (byte)CardLocation.Hand when side == 0: lOurHand[code] = lOurHand.GetValueOrDefault(code) - 1; break;
                        case (byte)CardLocation.Hand: lPlaced[1][code] = lPlaced[1].GetValueOrDefault(code) + 1; break;
                        case (byte)CardLocation.Grave: lGrave[side][code] = lGrave[side].GetValueOrDefault(code) - 1; break;
                        case (byte)CardLocation.Deck when side == 0: lOurHand[code] = lOurHand.GetValueOrDefault(code) + 1; break;
                    }
                }
                if (chosen.Count >= Math.Max(1, min))
                    return new[] { (byte)chosen.Count }.Concat(chosen).ToArray();
            }
            return null;
        }
    }
}
