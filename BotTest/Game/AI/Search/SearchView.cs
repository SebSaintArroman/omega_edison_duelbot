using BotTest.Game.AI.Brains;

namespace BotTest.Game.AI.Search
{
    /// <summary>A card on the field as the bot sees it. Code 0 = face-down and unknown.</summary>
    internal sealed record ViewCard(int Code, int Sequence, uint Position, int Owner, bool Attacked,
        (int Controller, int Location, int Sequence)? EquipTarget,
        List<(int Controller, int Location, int Sequence)> Targets,
        List<(int Type, int Count)> Counters,
        bool Improper = false,
        /// <summary>Ours, this turn: a monster summoned, flipped or repositioned, or that attacked (it can't change position again);
        /// a spell/trap set this turn (a trap or quick-play spell can't be activated until next turn). Omega reports no card status.</summary>
        bool ThisTurn = false,
        /// <summary>Ours: can't attack this turn (Chaos Sorcerer after its banish: "cannot attack the turn you activate this effect").</summary>
        bool NoAttack = false,
        /// <summary>Its base ATK / DEF as Omega shows them (-1: unknown): a Gladiator Beast summoned by a Gladiator's effect has a
        /// flag the rebuild loses (Laquari 2100), restored when these differ from the rebuilt card's.</summary>
        int BaseAttack = -1, int BaseDefense = -1,
        /// <summary>Theirs: one of our monsters attacked it this turn (it battled: a Gladiator Beast tags out at the end of our Battle Phase).</summary>
        bool Battled = false,
        /// <summary>Its current ATK / DEF as Omega shows them (-1: unknown): changes made by resolved effects (Blackwing - Gale the
        /// Whirlwind's halving, a Card Trooper's mill boost) that the rebuild would otherwise lose.</summary>
        int Attack = -1, int Defense = -1,
        /// <summary>Ours but theirs by ownership, taken until the End Phase (Brain Control): rebuilt with its owner and a control effect
        /// that ends with the turn.</summary>
        bool Borrowed = false);

    /// <summary>
    /// Everything the bot knows at a decision (only what Omega shows it, plus its own deck list and the deck read), and
    /// how to deal the rest. Player 0 = the bot. Built once per decision; Determinize makes one full position from it.
    /// </summary>
    internal sealed class SearchView
    {
        /// <summary>A face-down monster when the deck read can't name one: a plain 1200/1500 body (Beaver Warrior).</summary>
        public const int UnknownSetMonster = 32452818;

        /// <summary>
        /// A card that can never do anything (Jar Robber: it only answers Pot of Greed, which no list plays), standing in for their
        /// unknown set spells/traps and their draws: the search doesn't guess at what they are.
        /// </summary>
        public const int InertCard = 33784505;

        /// <summary>Cards we know are in their hand (seen going back to it, revealed by Trap Dustshoot...): the search plays them.</summary>
        public List<int> EnemyKnownHand { get; } = new();

        public int[] LifePoints { get; } = new int[2];
        public List<int> OwnHand { get; } = new();
        public List<int> OwnDeck { get; } = new();
        public List<int> OwnGrave { get; } = new();
        public List<int> OwnBanished { get; } = new();
        public List<int> OwnExtra { get; } = new();
        public List<ViewCard> OwnMonsters { get; } = new();
        public List<ViewCard> OwnSpells { get; } = new();

        public int EnemyHandCount { get; set; }
        public int EnemyExtraCount { get; set; }
        public List<int> EnemyGrave { get; } = new();
        public List<int> EnemyBanished { get; } = new();
        /// <summary>Graveyard copies that weren't properly summoned (can't be revived), by id: ours, theirs.</summary>
        public Dictionary<int, int> OwnGraveImproper { get; } = new();
        public Dictionary<int, int> EnemyGraveImproper { get; } = new();
        public List<ViewCard> EnemyMonsters { get; } = new();
        public List<ViewCard> EnemySpells { get; } = new();
        /// <summary>Their unknown face-down monsters, by zone: what the deck read says each likely is (card, chance). Each line deals
        /// one by these odds (user, 2026-11-16: "weigh by likely sets only from deck read"); none (or no card the engine knows): the
        /// plain UnknownSetMonster body.</summary>
        public Dictionary<int, List<(int Id, double Chance)>> EnemySetMonsterOdds { get; } = new();

        public bool NormalSummonUsed { get; set; }
        public bool BattlePhaseDone { get; set; }
        /// <summary>The duel's first turn: no Battle Phase (the rebuilt turn mustn't allow one).</summary>
        public bool FirstTurnOfDuel { get; set; }

        /// <summary>The real turn number at the decision (the rebuilt turn is turn 1; our brain playing the line sees the real one).</summary>
        public int RealTurn { get; set; } = 1;

        /// <summary>The decision is in the Battle Phase: the rebuilt turn goes straight to it.</summary>
        public bool InBattlePhase { get; set; }

        /// <summary>Cards drawn from our deck into the hand when a line starts (a projected next turn whose draw we don't know).</summary>
        public int DrawsAtStart { get; set; }

        /// <summary>What our real turn has done so far, for the line penalties (see TurnRecord).</summary>
        public TurnRecord Record { get; set; } = TurnRecord.Empty;

        /// <summary>Our deck's top cards when we know them (first = next draw), dealt in that order.</summary>
        public List<int> KnownTop { get; } = new();

        /// <summary>Cards our draw effects drew since the replay's start, in order (SearchPosition.ForcedDraws).</summary>
        public List<int> ForcedDraws { get; } = new();

        /// <summary>Graveyard / banished cards that got there this turn, by id (ours, theirs): scripts' "except the turn it was sent"
        /// conditions (aux.exccon) read the turn id, which a rebuilt card doesn't have (SearchPosition.ThisTurn).</summary>
        public Dictionary<int, int> ArrivedThisTurn { get; } = new();
        public Dictionary<int, int> EnemyArrivedThisTurn { get; } = new();


        /// <summary>A copy (the deck read is shared): for the turn planner's projections.</summary>
        public SearchView Clone()
        {
            var v = new SearchView
            {
                EnemyHandCount = EnemyHandCount, EnemyExtraCount = EnemyExtraCount, NormalSummonUsed = NormalSummonUsed,
                BattlePhaseDone = BattlePhaseDone, FirstTurnOfDuel = FirstTurnOfDuel, RealTurn = RealTurn, InBattlePhase = InBattlePhase,
                DrawsAtStart = DrawsAtStart, Record = Record,
            };
            v.KnownTop.AddRange(KnownTop);
            v.ForcedDraws.AddRange(ForcedDraws);
            v.LifePoints[0] = LifePoints[0];
            v.LifePoints[1] = LifePoints[1];
            v.OwnHand.AddRange(OwnHand); v.OwnDeck.AddRange(OwnDeck); v.OwnGrave.AddRange(OwnGrave);
            v.OwnBanished.AddRange(OwnBanished); v.OwnExtra.AddRange(OwnExtra);
            v.OwnMonsters.AddRange(OwnMonsters); v.OwnSpells.AddRange(OwnSpells);
            v.EnemyGrave.AddRange(EnemyGrave); v.EnemyBanished.AddRange(EnemyBanished); v.EnemyKnownHand.AddRange(EnemyKnownHand);
            v.EnemyMonsters.AddRange(EnemyMonsters); v.EnemySpells.AddRange(EnemySpells);
            foreach (var kv in OwnGraveImproper) v.OwnGraveImproper[kv.Key] = kv.Value;
            foreach (var kv in EnemyGraveImproper) v.EnemyGraveImproper[kv.Key] = kv.Value;
            foreach (var kv in ArrivedThisTurn) v.ArrivedThisTurn[kv.Key] = kv.Value;
            foreach (var kv in EnemyArrivedThisTurn) v.EnemyArrivedThisTurn[kv.Key] = kv.Value;
            foreach (var kv in EnemySetMonsterOdds) v.EnemySetMonsterOdds[kv.Key] = kv.Value;
            return v;
        }

        /// <summary>
        /// Our next turn's Main Phase as it would start from this board (seen during their turn) if we draw this card: a
        /// fresh turn (Normal Summon unused, nobody has attacked). Their side as it stands now.
        /// </summary>
        /// <summary>Moves a card of ours from the graveyard to a free monster zone (a Standby Phase revive, for projections).</summary>
        public bool ReviveFromGrave(int code, uint position)
        {
            if (!OwnGrave.Contains(code))
                return false;
            var used = OwnMonsters.Select(m => m.Sequence).ToHashSet();
            int zone = Enumerable.Range(0, 5).FirstOrDefault(z => !used.Contains(z), -1);
            if (zone < 0)
                return false;
            OwnGrave.Remove(code);
            OwnMonsters.Add(new ViewCard(code, zone, position, 0, false, null, new(), new()));
            return true;
        }

        /// <summary>This board with one more face-up attack-position monster of theirs (from their hand): "if they summon X".</summary>
        public SearchView WithEnemyMonster(int code)
        {
            var used = EnemyMonsters.Select(m => m.Sequence).ToHashSet();
            int zone = Enumerable.Range(0, 5).FirstOrDefault(z => !used.Contains(z), -1);
            var v = Clone();
            if (zone < 0)
                return v;
            v.EnemyMonsters.Add(new ViewCard(code, zone, SearchPosition.PosFaceUpAttack, 1, false, null, new(), new()));
            v.EnemyHandCount = Math.Max(0, v.EnemyHandCount - 1);
            return v;
        }

        /// <summary>
        /// Our next turn's Main Phase as it would start from this board, the draw dealt in each line from our deck (the next
        /// card when we know it, see KnownTop): a fresh turn, their side as it stands now.
        /// </summary>
        public SearchView ProjectNextTurn()
        {
            var v = Clone();
            for (int i = 0; i < v.OwnMonsters.Count; i++)
                v.OwnMonsters[i] = v.OwnMonsters[i] with { Attacked = false, ThisTurn = false };
            for (int i = 0; i < v.OwnSpells.Count; i++)
                v.OwnSpells[i] = v.OwnSpells[i] with { ThisTurn = false };
            v.NormalSummonUsed = v.BattlePhaseDone = v.InBattlePhase = v.FirstTurnOfDuel = false;
            v.Record = TurnRecord.Empty;
            v.RealTurn = RealTurn + 1;
            v.ReturnBorrowed();
            v.DrawsAtStart = v.OwnDeck.Count > 0 ? 1 : 0;
            v.ArrivedThisTurn.Clear();
            v.EnemyArrivedThisTurn.Clear();
            return v;
        }

        public SearchView ProjectNextTurn(int draw)
        {
            var v = Clone();
            v.OwnHand.Add(draw);
            v.OwnDeck.Remove(draw);
            v.ArrivedThisTurn.Clear();
            v.EnemyArrivedThisTurn.Clear();
            for (int i = 0; i < v.OwnMonsters.Count; i++)
                v.OwnMonsters[i] = v.OwnMonsters[i] with { Attacked = false, ThisTurn = false };
            for (int i = 0; i < v.OwnSpells.Count; i++)
                v.OwnSpells[i] = v.OwnSpells[i] with { ThisTurn = false };
            v.NormalSummonUsed = v.BattlePhaseDone = v.InBattlePhase = v.FirstTurnOfDuel = false;
            v.Record = TurnRecord.Empty;
            v.RealTurn = RealTurn + 1;
            v.ReturnBorrowed();
            return v;
        }

        /// <summary>Borrowed monsters (Brain Control) go back to them at the End Phase: for a projected next turn.</summary>
        private void ReturnBorrowed()
        {
            foreach (var card in OwnMonsters.Where(c => c.Borrowed).ToList())
            {
                OwnMonsters.Remove(card);
                var used = EnemyMonsters.Select(m => m.Sequence).ToHashSet();
                int zone = Enumerable.Range(0, 5).FirstOrDefault(z => !used.Contains(z), -1);
                if (zone >= 0)
                    EnemyMonsters.Add(card with { Sequence = zone, Owner = 1, Borrowed = false, Attacked = false, ThisTurn = false });
            }
        }

        /// <summary>
        /// The board as the turn planner's cache key: our side in full (hand, field, graveyard, banished, life points,
        /// Battle Phase done), their monsters by what's face-up and how many are face-down.
        /// </summary>
        public string PlanKey()
        {
            // Cards and positions, not zones: a card revived in the Standby Phase lands wherever it lands (combo steps then
            // match by card, see DeckBotBrain.ComboKey).
            static string Field(IEnumerable<ViewCard> cards) =>
                string.Join(",", cards.Select(c => $"{c.Code}:{c.Position}").OrderBy(x => x, StringComparer.Ordinal));
            static string Ids(IEnumerable<int> ids) => string.Join(",", ids.OrderBy(x => x));
            static string Theirs(IEnumerable<ViewCard> cards) =>
                Ids(cards.Where(c => c.Code > 0 && (c.Position & 0x5) != 0).Select(c => c.Code)) + $"+{cards.Count(c => c.Code <= 0 || (c.Position & 0x5) == 0)}";
            // (Not the Normal Summon flag: at the first decision of a turn it reads "used" when there's nothing to summon,
            // which changes nothing about the board. Not their spells/traps or life points: they set cards in their End
            // Phase after our last prompt (seen 2026-11-10), and our combos are played out against a passing opponent anyway;
            // the play search still weighs the combo's first move against the real board.)
            return $"{Ids(OwnHand)}|{Field(OwnMonsters)}|{Field(OwnSpells)}|{Ids(OwnGrave)}|{Ids(OwnBanished)}|{LifePoints[0]}|{(BattlePhaseDone ? 1 : 0)}" +
                   $"#{Theirs(EnemyMonsters)}";
        }

        public IEnumerable<int> AllKnownIds() =>
            OwnHand.Concat(OwnDeck).Concat(OwnGrave).Concat(OwnBanished).Concat(OwnExtra)
                .Concat(OwnMonsters.Select(c => c.Code)).Concat(OwnSpells.Select(c => c.Code))
                .Concat(EnemyGrave).Concat(EnemyBanished)
                .Concat(EnemyMonsters.Select(c => c.Code)).Concat(EnemySpells.Select(c => c.Code))
                .Concat(EnemyKnownHand).Concat(EnemySetMonsterOdds.Values.SelectMany(odds => odds.Select(o => o.Id)))
                .Append(UnknownSetMonster).Append(InertCard);

        /// <summary>One complete position: our deck shuffled, their hidden cards dealt from the deck read.</summary>
        public SearchPosition Determinize(Random rng)
        {
            bool Known(int id) => SearchHost.Knows(id);

            var position = new SearchPosition();
            position.LifePoints[0] = LifePoints[0];
            position.LifePoints[1] = LifePoints[1];
            position.NormalSummonUsed = NormalSummonUsed;
            position.BattlePhaseDone = BattlePhaseDone;
            position.ForcedDraws.AddRange(ForcedDraws);

            var placed = new Dictionary<(int, int, int), int>();
            var links = new List<(int Index, ViewCard Card, int Controller)>();
            void Field(ViewCard card, int controller, uint location, int code)
            {
                if (!Known(code))
                    return;
                // A monster whose owner isn't its controller (taken by Goyo Guardian, Brain Control...) is placed with its real owner
                // and held by a control effect, so a line that destroys it sends it to the right graveyard (seen 2026-11-16: our Shura
                // under their Goyo went to their graveyard in the lines and to ours for real, a DESYNC). Spells/traps stay with their
                // controller (an equip taken with the monster is rare).
                bool taken = location == SearchPosition.LocMZone && card.Owner is 0 or 1 && card.Owner != controller;
                bool borrowed = card.Borrowed && controller == 0 && location == SearchPosition.LocMZone;
                var p = position.Add(code, controller, location, card.Sequence, card.Position, owner: taken || borrowed ? 1 - controller : controller);
                p.TakenControl = taken || borrowed;
                p.BorrowedUntilEnd = borrowed;
                // Owner = controller: the puzzle places a card on its owner's side, so a stolen monster (Goyo Guardian's, Brain
                // Control's) would move back home (seen 2026-11-10). Lost: a stolen card destroyed in a line goes to the wrong GY.
                p.Attacked = card.Attacked && controller == 0;
                p.ThisTurn = card.ThisTurn && controller == 0;
                p.NoAttack = card.NoAttack && controller == 0;
                (p.BaseAttack, p.BaseDefense) = (card.BaseAttack, card.BaseDefense);
                (p.Attack, p.Defense) = (card.Attack, card.Defense);
                p.Battled = card.Battled && controller == 1;
                p.Proper = !card.Improper;
                p.Counters.AddRange(card.Counters);
                placed[(controller, (int)location, card.Sequence)] = position.Cards.Count - 1;
                links.Add((position.Cards.Count - 1, card, controller));
            }

            foreach (var card in OwnMonsters)
                Field(card, 0, SearchPosition.LocMZone, card.Code);
            foreach (var card in OwnSpells)
                Field(card, 0, SearchPosition.LocSZone, card.Code);
            foreach (var card in EnemyMonsters)
            {
                int code = card.Code;
                if (code <= 0)
                {
                    // Unknown: one of the deck read's likely sets for this zone, dealt by its chance (cards the engine knows only);
                    // without a read, a plain body holds the zone.
                    code = UnknownSetMonster;
                    if (EnemySetMonsterOdds.TryGetValue(card.Sequence, out var odds))
                    {
                        var usable = odds.Where(o => o.Chance > 0 && Known(o.Id)).ToList();
                        double roll = rng.NextDouble() * usable.Sum(o => o.Chance);
                        foreach (var (id, chance) in usable)
                            if ((roll -= chance) <= 0) { code = id; break; }
                    }
                }
                Field(card, 1, SearchPosition.LocMZone, code);
            }
            foreach (var card in EnemySpells)
            {
                int code = card.Code;
                if (code <= 0)
                {
                    // Unknown: an inert card holds the zone (removal still has it to hit; it never does anything).
                    code = InertCard;
                }
                Field(card, 1, SearchPosition.LocSZone, code);
            }

            // Equips and targets: coordinates are relative to the bot, which is player 0 here too.
            foreach (var (index, card, controller) in links)
            {
                if (card.EquipTarget is { } equip && placed.TryGetValue(equip, out int target))
                    position.Cards[index].EquippedTo = target;
                foreach (var t in card.Targets)
                    if (placed.TryGetValue(t, out int targeted))
                        position.Cards[index].Targets.Add(targeted);
            }

            void Pile(IEnumerable<int> ids, int controller, uint location, uint pos, Dictionary<int, int>? improper = null, Dictionary<int, int>? thisTurn = null)
            {
                int seq = 0;
                var left = improper == null ? null : new Dictionary<int, int>(improper);
                var fresh = thisTurn == null ? null : new Dictionary<int, int>(thisTurn);
                foreach (int id in ids)
                    if (Known(id))
                    {
                        var p = position.Add(id, controller, location, seq++, pos);
                        if (fresh != null && fresh.TryGetValue(id, out int t) && t > 0)
                        {
                            p.ThisTurn = true;
                            fresh[id] = t - 1;
                        }
                        if (left != null && left.TryGetValue(id, out int n) && n > 0)
                        {
                            p.Proper = false;
                            left[id] = n - 1;
                        }
                    }
            }
            // Our deck: the cards we know are on top in that order (KnownTop), the rest shuffled; the turn's draw (DrawsAtStart)
            // comes off the top into the hand, so a projected next turn deals its draw afresh in every line.
            var rest = new List<int>(OwnDeck);
            var top = new List<int>();
            foreach (int id in KnownTop)
                if (rest.Remove(id))
                    top.Add(id);
            var deck = top.Concat(rest.OrderBy(_ => rng.Next())).ToList();
            int draws = Math.Min(DrawsAtStart, deck.Count);
            Pile(OwnHand.Concat(deck.Take(draws)), 0, SearchPosition.LocHand, SearchPosition.PosFaceDown);
            Pile(OwnGrave, 0, SearchPosition.LocGrave, SearchPosition.PosFaceUp, OwnGraveImproper, ArrivedThisTurn);
            Pile(OwnBanished, 0, SearchPosition.LocRemoved, SearchPosition.PosFaceUp, thisTurn: ArrivedThisTurn);
            Pile(OwnExtra, 0, SearchPosition.LocExtra, SearchPosition.PosFaceDown);
            // The deck in order, bottom first, each at sequence 0: the engine puts a sequence-0 card on top (push_back) and
            // shuffles the deck for any sequence of 2 or more, so the old 0,1,2... numbering lost the known top cards. A replay
            // to a chain window needs them (DeckBotBrain.MidChain: Card Trooper's cost mills the three cards it really milled).
            foreach (int id in deck.Skip(draws).Reverse())
                if (Known(id))
                    position.Add(id, 0, SearchPosition.LocDeck, 0, SearchPosition.PosFaceDown);
            Pile(EnemyGrave, 1, SearchPosition.LocGrave, SearchPosition.PosFaceUp, EnemyGraveImproper, EnemyArrivedThisTurn);
            Pile(EnemyBanished, 1, SearchPosition.LocRemoved, SearchPosition.PosFaceUp, thisTurn: EnemyArrivedThisTurn);
            // No guessing at their unknown cards (user, 2026-11-11): only the cards we know are in their hand; their draws
            // are inert (Jar Robber, which only answers Pot of Greed), so their turn plays out with what we know. What
            // their unknown cards might do is the evaluator's threat term instead (MctsEvaluator.Threat).
            var knownHand = EnemyKnownHand.Where(Known).ToList();
            Pile(knownHand, 1, SearchPosition.LocHand, SearchPosition.PosFaceDown);
            // Their other hand cards as inert stand-ins (still no guessing at what they are): effects that need cards in their hand see
            // the right count (seen 2026-11-15: XX-Saber Gottoms' discard was never on offer in the lines, their hand being empty there).
            Pile(Enumerable.Repeat(InertCard, Math.Max(0, EnemyHandCount - knownHand.Count)), 1, SearchPosition.LocHand, SearchPosition.PosFaceDown);
            Pile(Enumerable.Repeat(InertCard, 20), 1, SearchPosition.LocDeck, SearchPosition.PosFaceDown);
            return position;
        }
    }
}
