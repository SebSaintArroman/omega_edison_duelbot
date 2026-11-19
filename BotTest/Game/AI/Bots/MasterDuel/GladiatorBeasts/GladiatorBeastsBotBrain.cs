using BotTest.Game.AI.Brains;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Bots.MasterDuel.GladiatorBeasts
{
    /// <summary>
    /// Gladiator Beasts brain, for the Gladiator Control deck (Gladiator Beasts + three Raigeki Breaks + a Lightsworn package) deck brain. The deck's engine:
    ///  - A Gladiator Beast that battled tags out at the end of the Battle Phase: it shuffles into the deck and another one
    ///    comes from the deck with its effect (Bestiari pops a Spell/Trap, Murmillo a face-up monster, Equeste returns a
    ///    Gladiator Beast card from the GY to the hand, Darius revives one, Laquari is 2100, Retiari banishes from their GY).
    ///    So Gladiator Beasts attack readily, and the tag-in is picked for the board.
    ///  - Contact Fusions from the field: Gyzarus (Bestiari + 1) destroys up to 2 cards on summon and tags out into 2 more;
    ///    Heraklinos (Laquari + 2) negates Spell/Traps for a discard.
    ///  - Raigeki Break / Heraklinos discards: Shield Warrior first (it works from the GY), Indomitable Gladiator Beast
    ///    (it comes back from the GY), then the least useful card.
    /// Staples (Raigeki Break, Mirror Force, Torrential, D-Prison, Bottomless, MST, Heavy Storm, Ryko, Lyla, Card Trooper,
    /// Charge) are the shared brain's. Every rule writes a "glads:" note into the duel log.
    /// </summary>
    public sealed class GladiatorBeastsBotBrain : DeckBotBrain
    {
        // ------------------------------------------------------------------ tunable numbers (bot-tuning-GladiatorBeasts.txt, user 2026-11-16)

        [Tunable("A Gladiator board's worth for the tag-out weighing: per monster, this + GladBodyPerThousand x ATK/1000.")]
        public static double GladBodyBase = 0.6;
        [Tunable("A Gladiator board's worth: card units per 1000 ATK of each monster.")]
        public static double GladBodyPerThousand = 0.8;
        [Tunable("Gladiator Proving Ground / recycled Indomitable: the cost of each one recycled.")]
        public static double RecycledIndomitableCost = 0.1;
        [Tunable("A face-down monster of theirs with at least this Ryko chance counts as a flip effect coming (War Chariot worth more).")]
        public static double FlipThreatChance = 0.25;
        [Tunable("Tag-out value: Murmillo's kill counts this share when Raigeki Break in hand could do it too.")]
        public static double TagMurmilloOtherAnswerShare = 0.5;
        [Tunable("Tag-out value: Bestiari destroying a spell/trap of theirs (card units).")]
        public static double TagBestiariValue = 1;
        [Tunable("Tag-out value: Bestiari's extra when they have 2+ spells/traps to choose from.")]
        public static double TagBestiariChoiceBonus = 0.3;
        [Tunable("Tag-out value: Equeste returning War Chariot.")]
        public static double TagEquesteChariotValue = 1;
        [Tunable("Tag-out value: Equeste's extra for War Chariot when a monster effect of theirs is likely next turn.")]
        public static double TagEquesteChariotNeededBonus = 0.5;
        [Tunable("Tag-out value: Equeste returning a Gladiator Beast.")]
        public static double TagEquesteGladValue = 1;
        [Tunable("Tag-out value: Equeste returning a Gladiator Beast with one already in hand.")]
        public static double TagEquesteGladWithOneInHand = 0.8;
        [Tunable("Tag-out value: Retiari banishing an important card from their graveyard.")]
        public static double TagRetiariStrongValue = 1.2;
        [Tunable("Tag-out value: Retiari banishing a minor card from their graveyard.")]
        public static double TagRetiariMinorValue = 0.3;
        [Tunable("Tag-out value: Darius reviving a Gladiator Beast for Gyzarus / Heraklinos.")]
        public static double TagDariusUsefulValue = 1.2;
        [Tunable("Tag-out value: Darius when its revive sets nothing up.")]
        public static double TagDariusIdleValue = 0.1;
        [Tunable("Tag-out value: the share of the tagged-in monster's body (ATK table) that counts.")]
        public static double TagBodyShare = 0.2;
        [Tunable("Tag-out value: extra when the tagged-in monster is bigger than their best attacker.")]
        public static double TagBodySafeBonus = 0.3;

        /// <summary>
        /// Indomitable Gladiator Beast may go off in a Main Phase while it can come back for another use: its GY effect returns it
        /// to the hand for a Gladiator Beast card in the GY (user, 2026-11-15: "indomitable might be necessary to use earlier than
        /// the damage step (mp1) so that you can use it multiple times ... although the last one of those should still be damage
        /// step"). The last use (no Gladiator Beast card left in the GY to pay for the return) waits for the damage step.
        /// </summary>
        protected override async Task<bool> StatBoostMayGoEarlyAsync(DynamicCard card, DuelPhase phase)
        {
            if (!card.IsCode(GladCardId.IndomitableGladiatorBeast) || phase is not (DuelPhase.Main1 or DuelPhase.Main2))
                return false;
            var grave = (await CurrentField.GetBotField()).OwnSide.Grave ?? Array.Empty<int>();
            return grave.Any(id => GladCardId.GladCards.Contains(id) || GladCardId.GladCards.Contains(Search.SearchHost.CanonicalId(id)));
        }

        public GladiatorBeastsBotBrain(
            ICardEvaluator? cardEvaluator = null,
            IBoardEvaluator? boardEvaluator = null,
            IActionScorer? actionScorer = null)
            : base(cardEvaluator, boardEvaluator, actionScorer)
        {
        }

        // Version history: 1 = 2026-11-10 first build (tag-out picks, contact fusions, on-summon targets, War Chariot,
        // Heraklinos negates, discards).
        public override int BrainVersion => 6; // v6: attacks made safe by Shield Warrior / Waboku for a tag-out (shared). v5: Retiari tagged in for their graveyard; Gyzarus fodder tagged in for its effect. v4: Raigeki Break only on big threats; boosts equally costed. v3: attack boosts stacked over a wall; Indomitable recycle counts War Chariot. v2: Shield Warrior a value set

        private static void Note<T>(DecisionContext<T> context, string message) => context.Bot.LogNote($"glads: {message}");

        private static bool IsGlad(DynamicCard card) => card.ID > 0 && card.IsCode(GladCardId.GladMonsters);

        // Shield Warrior works from the graveyard (banish it: our monsters survive a battle): set, it bluffs a Ryko and gets
        // there by being destroyed (user, 2026-11-11). Ryko too (the shared default).
        // The MCTS's card values for this deck (user, 2026-11-11): graveyard effects. Shield Warrior saves our monsters in
        // battle, Skill Successor gives +800, Indomitable comes back to the hand for two Gladiator Beast cards.
        // The Gladiator Beast Darius revives is a combo piece into the Contact Fusions, not a card that stays: it goes back
        // to the deck when Darius leaves (and its effects are negated meanwhile), so a line's end board doesn't count it
        // (user, 2026-11-11: "don't count the glad that darius brings back as a plus"). Told apart as a negated main-deck
        // Gladiator Beast next to a face-up Darius.
        // A Gladiator Beast that battles tags out at the end of the Battle Phase (and its tag-in gets an effect): battling pays
        // even when it would lose, if Shield Warrior / Waboku keep it alive (user, 2026-11-11). Not a negated one (Darius'
        // revive): its tag-out is negated too. Heraklinos has no tag-out.
        protected override string? BattleGain(DynamicCard attacker) =>
            SummonLock(attacker) == null && attacker.Negator.Location == 0 && (attacker.IsCode(GladCardId.Glads) || attacker.IsCode(GladCardId.Gyzarus))
                ? "it tags out after battling (a tag-in with its effect)" : null;

        // The cards that stop the tag-out's Special Summon (user, 2026-11-11: "might be simpler to list them"). Royal Oppression
        // negates it (for 800 LP), so it counts too.
        private static readonly int[] SummonLockMonsters =
        {
            59509952, // Archlord Kristya
            47084486, // Vanity's Fiend
            72634965, // Vanity's Ruler
            42009836, // Fossil Dyna Pachycephalo
            10963799, 19740112, 46145256, 47961808, 73356503, 84478195, // the Barrier Statues
        };
        private const int RoyalOppression = 93016201;

        // Royal Oppression face-up on their side (see OnFieldRefreshed).
        private bool _oppression;

        /// <summary>
        /// What stops this attacker's tag-out, or null: Royal Oppression, or lock monsters of theirs face-up that our attackers can't
        /// run over this Battle Phase (one it beats goes before the tag-out; user, 2026-11-11: "the monster ones can be ignored
        /// if they can be attacked over").
        /// </summary>
        /// <summary>
        /// A Special Summon lock monster of theirs that this attacker runs over, while a Gladiator Beast of ours is set to tag
        /// out (one that battled, or this one): it goes first, so the lock is gone by the end of the Battle Phase (user,
        /// 2026-11-11: the other attackers should "actually hit the lock monsters first"). The strongest one it beats.
        /// </summary>
        protected override async Task<(DynamicCard Target, string Why)?> PriorityAttackTargetAsync(DynamicCard attacker)
        {
            if (_oppression)
                return null;
            var theirs = await GetEnemyMonsterCardsAsync();
            static int Power(DynamicCard m) => m.IsAttack() ? m.Attack : m.Defense;
            var target = theirs.Where(m => m.IsFaceup() && m.IsCode(SummonLockMonsters) && attacker.Attack > Power(m))
                .OrderByDescending(Power).FirstOrDefault();
            if (target.ID <= 0)
                return null;
            bool TagsOut(DynamicCard m) => m.ID > 0 && m.IsFaceup() && m.Negator.Location == 0 && (m.IsCode(GladCardId.Glads) || m.IsCode(GladCardId.Gyzarus));
            var ours = await GetBotMonsterCardsAsync();
            if (!TagsOut(attacker) && !ours.Any(m => TagsOut(m) && (m.Attacked || m.IsAttack())))
                return null;
            return (target, $"{CardNames.Get(target.ID)} stops Special Summons, so our Gladiator Beasts can't tag out while it's there");
        }

        /// <summary>
        /// A Gladiator Beast isn't expendable or a cheap attacker (round-robin 29b: Darius sent into a likely Ryko "as a cheap
        /// monster"): its value is the tag-out after battle, which Ryko's flip takes away. A negated one (Darius' revive) still is.
        /// </summary>
        protected override bool IsExpendable(DynamicCard card) =>
            card.IsCode(GladCardId.ShieldWarrior) || // CSV M6: Shield Warrior is expendable (it works from the graveyard)
            !(IsGlad(card) && card.Negator.Location == 0) && base.IsExpendable(card);

        private static readonly System.Text.RegularExpressions.Regex GainsAtkText = new(@"gains (\d+) ATK",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// CSV M7: in a battle of ours or theirs, our Gladiator Beast would be destroyed (or only tie): the smallest of our ATK boosts on
        /// offer that makes it win (Skill Successor, Indomitable, Rush Recklessly...: "gains N ATK") is chained, so it survives the
        /// battle and tags out. Not when another of their attack-position monsters would still beat it this turn, boost included
        /// (their turn: one yet to attack). Null: nothing to do.
        /// </summary>
        private async Task<int?> BoostToWinBattleAsync(DecisionContext<SelectChainData> context, DynamicCard[] options)
        {
            var ours = await CurrentField.GetBotBattlingMonster(isOwnSide: true);
            var theirs = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            if (ours.ID <= 0 || theirs.ID <= 0 || !IsGlad(ours) || !ours.IsAttack() ||
                !(await GetBotMonsterCardsAsync()).Any(m => m.ID == ours.ID && m.CurrentCoordinates.Sequence == ours.CurrentCoordinates.Sequence))
                return null;
            int theirPower = theirs.IsAttack() ? theirs.Attack : theirs.Defense;
            if (ours.Attack > theirPower)
                return null; // it wins already
            // Our own card on the chain already removes their battling monster (user, 2026-11-18: Raigeki Break on the attacking Gearframe,
            // then Skill Successor on the same battle: "using two cards when one would have done"): no battle left to win.
            foreach (var link in await GetPlayerCardsInZoneAsync(CardLocation.SpellZone))
                if (link.IsFaceup() && (link.CurrentChainTargets ?? Array.Empty<CardCoordinate>()).Any(t =>
                        t.Controller == 1 && t.Location == (byte)CardLocation.MonsterZone && t.Sequence == theirs.CurrentCoordinates.Sequence))
                {
                    Note(context, $"no boost: our {CardNames.Get(link.ID)} on the chain already removes {CardNames.Get(theirs.ID)}");
                    return null;
                }
            var boosts = new List<(int Index, int Gain)>();
            for (int i = 0; i < options.Length; i++)
                if (options[i].CurrentCoordinates.Controller == 0 && await CardDatabase.GetAsync(options[i].ID) is DBCardInfo info &&
                    GainsAtkText.Matches(info.CardText ?? "") is { Count: > 0 } gains)
                {
                    // Skill Successor from the GY gives the second number (800), set it gives the first (400).
                    int gain = options[i].CurrentCoordinates.Location == (byte)CardLocation.Grave && gains.Count > 1
                        ? int.Parse(gains[1].Groups[1].Value) : int.Parse(gains[0].Groups[1].Value);
                    if (ours.Attack + gain > theirPower)
                        boosts.Add((i, gain));
                }
            if (boosts.Count == 0)
                return null;
            var best = boosts.OrderBy(b => b.Gain).First();
            int boosted = ours.Attack + best.Gain;
            if (!IsOwnTurn(context.Header) && OpponentMonsters.Any(m => m.IsFaceup() && m.IsAttack() && !m.Attacked && m.Attack >= boosted &&
                                                                         m.CurrentCoordinates.Sequence != theirs.CurrentCoordinates.Sequence))
                return null; // another attack of theirs would still destroy it this turn
            Note(context, $"{CardNames.Get(options[best.Index].ID)} on {CardNames.Get(ours.ID)}: {ours.Attack} -> {boosted} beats {CardNames.Get(theirs.ID)} ({theirPower}), " +
                          "so it survives the battle and tags out");
            return best.Index;
        }

        /// <summary>
        /// CSV M6: Shield Warrior stays in hand while Raigeki Break is there (it's the discard), and a Gladiator Beast summoned with no
        /// use for its tag-out is held: it can't attack any monster of theirs without dying (all of them bigger, in attack position), and
        /// their biggest attacker would run it over on their turn.
        /// </summary>
        protected override async Task DeckHoldsAsync(DecisionContext<IdleCmdData> context)
        {
            var data = context.StateData;
            var hand = await GetBotHandCardsAsync();
            // Raigeki Break's discards are kept in hand: Shield Warrior, Indomitable Gladiator Beast and Skill Successor (all work from the
            // GY), in that order, but no more of them than we have Raigeki Breaks (in hand or set): one discard each (user, 2026-11-17).
            int raigekis = hand.Count(c => c.IsCode(GladCardId.RaigekiBreak)) +
                           (await CurrentField.GetCardsInSpellZone()).Count(c => c.IsCode(GladCardId.RaigekiBreak));
            var discards = hand.Where(c => c.IsCode(GladCardId.ShieldWarrior, GladCardId.IndomitableGladiatorBeast, GladCardId.SkillSuccessor))
                .OrderBy(c => c.IsCode(GladCardId.ShieldWarrior) ? 0 : c.IsCode(GladCardId.IndomitableGladiatorBeast) ? 1 : 2)
                .Take(raigekis).Select(c => c.ID).ToList();
            foreach (int keep in discards.Distinct())
                foreach (var (cards, action) in new[] { (data.SummonableCards, MainAction.Summon), (data.MonsterSetableCards, MainAction.SetMonster), (data.SpellSetableCards, MainAction.SetSpell) })
                    if ((cards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(keep)))
                        Hold(keep, action, GladRules.ShieldWarriorForRaigeki);
            var theirs = OpponentMonsters.Where(m => m.ID > 0 && m.IsFaceup()).ToList();
            if (theirs.Count == 0)
                return;
            foreach (var (cards, action) in new[] { (data.SummonableCards, MainAction.Summon), (data.SpecialSummonableCards, MainAction.SpSummon) })
                foreach (var card in cards ?? Array.Empty<DynamicCard>())
                {
                    if (!IsGlad(card))
                        continue;
                    bool attacksSafely = theirs.Any(m => m.IsAttack() ? m.Attack < card.Attack : m.Defense < card.Attack);
                    bool survives = !theirs.Any(m => m.IsAttack() && m.Attack > card.Attack);
                    if (!attacksSafely && !survives)
                        Hold(card.ID, action, GladRules.TagOutUnusable);
                }
        }

        private string? SummonLock(DynamicCard attacker)
        {
            if (_oppression)
                return "Royal Oppression";
            var locks = OpponentMonsters.Where(m => m.IsFaceup() && m.IsCode(SummonLockMonsters)).ToList();
            if (locks.Count == 0)
                return null;
            // Every lock monster run over this Battle Phase, each by a different attacker of ours (the weakest that beats it):
            // then the lock is gone before the tag-out (user, 2026-11-11: "unless you can attack them all?").
            static int Power(DynamicCard m) => m.IsAttack() ? m.Attack : m.Defense;
            var attackers = OwnMonsters.Where(m => m.IsFaceup() && m.IsAttack() && !m.Attacked)
                .Append(attacker).GroupBy(m => (m.CurrentCoordinates.Location, m.CurrentCoordinates.Sequence)).Select(g => g.First())
                .Select(m => m.Attack).OrderBy(atk => atk).ToList();
            foreach (var lockMonster in locks.OrderByDescending(Power))
            {
                int index = attackers.FindIndex(atk => atk > Power(lockMonster));
                if (index < 0)
                    return CardNames.Get(lockMonster.ID);
                attackers.RemoveAt(index);
            }
            return null;
        }

        internal override double SearchSetupValue(DuelSim.Engine.SideSummary own)
        {
            if (!own.Monsters.Any(m => m.FaceUp && Search.SearchHost.Is(m.Code, GladCardId.Darius)))
                return 0;
            return -own.Monsters
                .Where(m => m.FaceUp && m.Negated && GladCardId.Glads.Any(id => Search.SearchHost.Is(m.Code, id)))
                .Sum(m => GladBodyBase + GladBodyPerThousand * (m.AttackPosition ? m.Attack : m.Defense * Search.MctsEvaluator.DefenceShare) / 1000);
        }

        // (No deck graveyard values: Shield Warrior, Skill Successor and Indomitable have their own graveyard readings; user, 2026-11-17.)

        protected override int[] ValueSetIds => GladValueSets;

        private static readonly int[] GladValueSets = { GladCardId.Ryko, GladCardId.ShieldWarrior };

        // Gladiator Beasts first as Normal Summons (they tag out after battling): Laquari / Darius / Equeste carry the
        // engine; Bestiari next (its own effect needs a tag-in, but it's Gyzarus material); the small ones last.
        protected override int SafeSummonPriority(DynamicCard card, List<DynamicCard> hand) =>
            card.IsCode(GladCardId.Laquari) ? 0
            : card.IsCode(GladCardId.Darius) ? 1
            : card.IsCode(GladCardId.Equeste) ? 2
            : card.IsCode(GladCardId.Bestiari) ? 3
            : card.IsCode(GladCardId.CardTrooper, GladCardId.Lyla) ? 4
            : card.IsCode(GladCardId.Retiari) ? 5
            : card.IsCode(GladCardId.Murmillo) ? 6
            : 7;

        // ------------------------------------------------------------------ main phase

        public override async Task<IdleCmdResponse> OnIdleCmdAsync(DecisionContext<IdleCmdData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            var state = context.StateData;
            bool main2 = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main2;
            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            var enemySpells = await GetEnemySpellCardsAsync();
            var botMonsters = await GetBotMonsterCardsAsync();
            var hand = await GetBotHandCardsAsync();
            var grave = await GetBotGraveyardCardsAsync();
            int theirCards = enemyMonsters.Count + enemySpells.Count;

            // Raigeki Break on a big threat first (user, 2026-11-11: on Archlord Kristya, which stops every tag-out, it was "by
            // far the best play"); attack boosts are for monsters that aren't worth it.
            if (await TryRaigekiThreatAsync(context, state, enemyMonsters) is IdleCmdResponse raigekiThreat)
                return raigekiThreat;

            // A likely Ryko set: battle something else to tag out into Bestiari, then Gyzarus destroys the Ryko unflipped.
            if (await TryGyzarusForRykoAsync(context, state, botMonsters, enemyMonsters, enemySpells, hand, grave) is IdleCmdResponse gyzarusRoutine)
                return gyzarusRoutine;

            // Attack boosts stacked on one Glad to get it over their monster (fewest needed).
            if (await TryBoostOverWallAsync(context, state, botMonsters, enemyMonsters, grave) is IdleCmdResponse boost)
                return boost;

            // Held, for the search: Raigeki Break on nothing worth it (a small monster, a set card), and attack boosts outside a
            // plan that gets a Glad over a wall (a boost spent for no battle is a card thrown away).
            if (TryGetActivable(state, GladCardId.RaigekiBreak, out _))
                Hold(GladCardId.RaigekiBreak, MainAction.Activate, GladRules.RaigekiThreat);
            foreach (var card in (state.ActivableCards ?? Array.Empty<DynamicCard>())
                         .Where(card => card.IsCode(GladCardId.SkillSuccessor) ||
                                        card.IsCode(GladCardId.IndomitableGladiatorBeast) && card.CurrentCoordinates.Location != (byte)CardLocation.Grave))
                Hold(card.ID, MainAction.Activate, GladRules.BoostOverWall);

            // Indomitable Gladiator Beast from the GY: 2 Gladiator Beast cards back to the deck (more tag-ins) for a card in hand.
            // Any "Gladiator Beast" card pays (War Chariot too; seen 2026-11-11: Laquari + War Chariot went uncounted).
            if (TryGetActivable(state, GladCardId.IndomitableGladiatorBeast, out var indomitable) &&
                indomitable.CurrentCoordinates.Location == (byte)CardLocation.Grave &&
                grave.Count(card => card.IsCode(GladCardId.GladCards)) >= 2 && Rule(GladRules.IndomitableRecycle))
            {
                Note(context, "Indomitable Gladiator Beast back to the hand: 2 Gladiator Beasts from the GY go back to the deck to tag in again");
                return indomitable.MainActivateFromIndex();
            }

            // Gyzarus: destroys up to 2 of their cards on summon (and tags out into 2 Gladiator Beasts after battling).
            if (TryGetSpecialSummon(state, GladCardId.Gyzarus, out var gyzarus) && theirCards > 0 && Rule(GladRules.Gyzarus))
            {
                Note(context, $"contact fuse Gyzarus (Bestiari + 1 Gladiator Beast): it destroys up to 2 of their {theirCards} card(s)");
                return gyzarus.SpecialSummon();
            }

            // Bestiari as the Normal Summon next to another Gladiator Beast: Gyzarus straight after.
            var otherGlad = botMonsters.FirstOrDefault(card => card.IsFaceup() && IsGlad(card) && !card.IsCode(GladCardId.Bestiari));
            if (TryGetSummonable(state, GladCardId.Bestiari, out var bestiari) && otherGlad.ID > 0 && theirCards > 0 &&
                botMonsters.Count < 5 && Rule(GladRules.BestiariForGyzarus))
            {
                Note(context, $"normal summon Bestiari next to {CardNames.Get(otherGlad.ID)}: Gyzarus next, destroying up to 2 of their cards");
                return bestiari.NormalSummon();
            }

            // Heraklinos after the battle (or with no battle to come): 3000, and it negates their Spell/Traps for a discard.
            if (TryGetSpecialSummon(state, GladCardId.Heraklinos, out var heraklinos) && (main2 || !state.CanBattlePhase) &&
                hand.Count >= 1 && (enemySpells.Count > 0 || await OppHandAsync() >= 3) && Rule(GladRules.Heraklinos))
            {
                Note(context, "contact fuse Heraklinos (Laquari + 2 Gladiator Beasts): it negates their Spell/Traps, and we have a card to discard");
                return heraklinos.SpecialSummon();
            }

            // Staples first (Heavy Storm / MST / Smashing Ground / Raigeki Break before we commit, Ryko, Lyla...).
            if (await TryStapleIdleAsync(context) is IdleCmdResponse staple)
                return staple;

            // The Normal Summon or Set (shared choice, Gladiator Beasts first: SafeSummonPriority).
            int turn = context.Header.CurrentTurn;
            var summonOrSet = await ChooseNormalSummonOrSetAsync(context, state, enemyMonsters, enemySpells, botMonsters, hand);
            if (summonOrSet is IdleCmdResponse summon)
            {
                return summon;
            }

            // Traps (shared: after attacking, no redundant sets, not into a likely Heavy Storm). Seen 2026-11-10: without this
            // the turn ended with Mirror Force in hand.
            var (trapSet, trapBattleAhead, gorzLive) = await ChooseTrapSetAsync(context, state, enemyMonsters, botMonsters, hand);
            if (trapSet is IdleCmdResponse setTrap)
                return setTrap;

            // The rest is the generic brain's (repositions, the Battle Phase), without summons it didn't choose above, and not
            // Gyzarus / Heraklinos for nothing (the search may still).
            var rest = state;
            rest.SummonableCards = Array.Empty<DynamicCard>();
            rest.MonsterSetableCards = Array.Empty<DynamicCard>();
            rest.SpecialSummonableCards = (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>())
                .Where(card => !card.IsCode(GladCardId.Gyzarus, GladCardId.Heraklinos)).ToArray();
            if (trapBattleAhead || gorzLive)
                rest.SpellSetableCards = Array.Empty<DynamicCard>();
            return await base.OnIdleCmdAsync(context with { StateData = rest }, ct);
        }

        /// <summary>
        /// Attack boosts on one Glad to get it over their monster (user, 2026-11-11: Skill Successor and Indomitable from the
        /// GY would have put Equeste over Archlord Kristya). Main Phase 1 with a Battle Phase ahead. The attacker is our first
        /// attacker by zone (the server attacks with it); the wall is their strongest face-up monster it doesn't already beat.
        /// Boosts: Skill Successor from the GY (+800, our turn), Indomitable from the hand (+500, Gladiator Beasts only), its
        /// recycle from the GY (2 Gladiator Beast cards back to the deck: another +500, repeatable while there are cards to
        /// pay), a set Skill Successor (+400). The cheapest combination that gets over is used, nothing more ("dont use more
        /// than you need"); it's recomputed at every step, so each boost only goes on while it's still needed.
        /// </summary>
        private async Task<IdleCmdResponse?> TryBoostOverWallAsync(DecisionContext<IdleCmdData> context, IdleCmdData state,
            List<DynamicCard> botMonsters, List<DynamicCard> enemyMonsters, List<DynamicCard> grave)
        {
            int turn = context.Header.CurrentTurn;
            if ((DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !state.CanBattlePhase || turn <= 1)
                return null;
            var attacker = botMonsters.Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack() && !card.Attacked)
                .OrderBy(card => card.CurrentCoordinates.Sequence).FirstOrDefault();
            if (attacker.ID <= 0)
                return null;
            int atk = attacker.Attack;
            static int Power(DynamicCard card) => card.IsAttack() ? card.Attack : card.Defense;
            var wall = enemyMonsters.Where(card => card.ID > 0 && card.IsFaceup() && Power(card) >= atk)
                .OrderByDescending(Power).FirstOrDefault();
            if (wall.ID <= 0)
                return null;
            int need = Power(wall) - atk + 1;

            var activable = state.ActivableCards ?? Array.Empty<DynamicCard>();
            bool Is(DynamicCard card, int id, CardLocation where) => card.IsCode(id) && card.CurrentCoordinates.Location == (byte)where;
            int skillGrave = activable.Count(card => Is(card, GladCardId.SkillSuccessor, CardLocation.Grave));
            int skillSet = activable.Count(card => Is(card, GladCardId.SkillSuccessor, CardLocation.SpellZone));
            bool glad = IsGlad(attacker);
            bool indHand = glad && activable.Any(card => Is(card, GladCardId.IndomitableGladiatorBeast, CardLocation.Hand));
            bool indGrave = glad && activable.Any(card => Is(card, GladCardId.IndomitableGladiatorBeast, CardLocation.Grave));
            int recycles = indHand || indGrave ? grave.Count(card => card.IsCode(GladCardId.GladCards)) / 2 : 0;
            int indUses = (indHand ? 1 : 0) + recycles;

            // Fewest boosts over the wall (each costs about the same; see below).
            (int Grave, int Ind, int Set, double Cost)? best = null;
            for (int g = 0; g <= skillGrave; g++)
                for (int i = 0; i <= indUses; i++)
                    for (int s = 0; s <= skillSet; s++)
                    {
                        if (800 * g + 500 * i + 400 * s < need)
                            continue;
                        int recycled = Math.Max(0, i - (indHand ? 1 : 0));
                        // Every boost costs about the same (user, 2026-11-11: a set Skill Successor comes back as the GY +800, so
                        // use it freely): the fewest boosts win; a recycle a little more (2 Gladiator Beast cards leave the GY).
                        double cost = g + i + s + RecycledIndomitableCost * recycled;
                        if (best == null || cost < best.Value.Cost)
                            best = (g, i, s, cost);
                    }
            if (best is not (int useGrave, int useInd, int useSet, _) || !Rule(GladRules.BoostOverWall))
            {
                // No boosts get over it: Raigeki Break it instead, if it's worth two cards (Raigeki + the discard).
                var raigeki = activable.FirstOrDefault(card => Is(card, GladCardId.RaigekiBreak, CardLocation.SpellZone));
                if (raigeki.ID > 0 && (await GetBotHandCardsAsync()).Count > 0 && await RaigekiWorthyAsync(wall) is string worth &&
                    Rule(GladRules.RaigekiThreat))
                {
                    PlanTarget(turn, wall, theirs: true);
                    Note(context, $"Raigeki Break {CardNames.Get(wall.ID)} ({Power(wall)}), in the way of {CardNames.Get(attacker.ID)} ({atk}): {worth}");
                    return raigeki.MainActivateFromIndex();
                }
                return null;
            }
            string plan = string.Join(" + ", new[]
            {
                useGrave > 0 ? $"Skill Successor from the GY x{useGrave} (+{800 * useGrave})" : null,
                useInd > 0 ? $"Indomitable x{useInd} (+{500 * useInd}{(useInd > (indHand ? 1 : 0) ? ", recycled from the GY" : "")})" : null,
                useSet > 0 ? $"set Skill Successor x{useSet} (+{400 * useSet})" : null,
            }.Where(part => part != null));
            string why = $"{CardNames.Get(attacker.ID)} ({atk}) over {CardNames.Get(wall.ID)} ({Power(wall)}): {plan}";

            // The whole boost sequence handed over at once and followed (DeckBotBrain.TurnPlan, user 2026-11-12), then the attack
            // into the wall, committed: Skill Successor from the GY, Indomitable from the hand, its recycle and Indomitable
            // again, the set Skill Successor, the Battle Phase. Each boost aims at our attacker as it's taken.
            string attackerName = CardNames.Get(attacker.ID);
            void Aim() => PlanTarget(turn, attacker, theirs: false);
            var steps = new List<RoutineStep>();
            for (int g = 0; g < useGrave; g++)
                steps.Add(new RoutineStep(MainAction.Activate, GladCardId.SkillSuccessor, $"Skill Successor from the GY on {attackerName}", Aim, CardLocation.Grave));
            int indFromHand = useInd > 0 && indHand ? 1 : 0;
            if (indFromHand == 1)
                steps.Add(new RoutineStep(MainAction.Activate, GladCardId.IndomitableGladiatorBeast, $"Indomitable Gladiator Beast on {attackerName}", Aim, CardLocation.Hand));
            for (int r = 0; r < useInd - indFromHand; r++)
            {
                steps.Add(new RoutineStep(MainAction.Activate, GladCardId.IndomitableGladiatorBeast, "Indomitable back to the hand", null, CardLocation.Grave));
                steps.Add(new RoutineStep(MainAction.Activate, GladCardId.IndomitableGladiatorBeast, $"Indomitable Gladiator Beast on {attackerName}", Aim, CardLocation.Hand));
            }
            for (int s = 0; s < useSet; s++)
                steps.Add(new RoutineStep(MainAction.Activate, GladCardId.SkillSuccessor, $"set Skill Successor on {attackerName}", Aim, CardLocation.SpellZone));
            steps.Add(RoutineStep.Battle);
            if (await StartRoutineAsync(context, "boost over a wall", why, steps) is not IdleCmdResponse first)
                return null;
            CommitAttack(turn, attacker.ID, wall, $"{attackerName} was boosted over {CardNames.Get(wall.ID)}");
            return first;
        }

        /// <summary>Our main phase: a set Raigeki Break on their biggest threat (worth two cards), with a card to discard.</summary>
        /// <summary>
        /// Routine "Gyzarus for a set Ryko" (user, 2026-11-13, round-robin 29b): with a likely Ryko set, the first attacker (a
        /// Gladiator Beast that tags out) battles a face-up monster it survives, even one it can't kill, just to tag out; it tags
        /// into Bestiari, and in Main Phase 2 Bestiari + our second Gladiator Beast make Gyzarus, which destroys the Ryko without
        /// flipping it (and one more card of theirs). Needs a second Glad on the field, Bestiari in the deck and Gyzarus in the
        /// Extra Deck. Weighed by its outcome in cards: Gyzarus's Ryko (+1) and another card of theirs (+1 if there is one);
        /// Bestiari's tag-in destroy: +1 on their spell/trap, -1 when only ours are there (it must hit one), 0 with none on
        /// either side. Made when that's at least 2 and the battle damage leaves us above DangerLifePoints.
        /// </summary>
        private async Task<IdleCmdResponse?> TryGyzarusForRykoAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> botMonsters,
            List<DynamicCard> enemyMonsters, List<DynamicCard> enemySpells, List<DynamicCard> hand, List<DynamicCard> grave)
        {
            int turn = context.Header.CurrentTurn;
            if ((DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !state.CanBattlePhase || turn <= 1 || _oppression)
                return null;
            var ryko = enemyMonsters.FirstOrDefault(m => !m.IsFaceup() && LikelyRyko(m));
            if (ryko.CurrentCoordinates.Location == 0)
                return null;
            var attacker = botMonsters.Where(m => m.ID > 0 && m.IsFaceup() && m.IsAttack() && !m.Attacked)
                .OrderBy(m => m.CurrentCoordinates.Sequence).FirstOrDefault();
            if (attacker.ID <= 0 || !IsGlad(attacker) || BattleGain(attacker) == null || attacker.IsCode(GladCardId.Bestiari))
                return null;
            var second = botMonsters.FirstOrDefault(m => m.ID > 0 && m.IsFaceup() && IsGlad(m) && m.CurrentCoordinates.Sequence != attacker.CurrentCoordinates.Sequence);
            if (second.ID <= 0)
                return null;
            bool bestiariInDeck = !hand.Concat(grave).Concat(botMonsters).Any(c => c.IsCode(GladCardId.Bestiari)) &&
                                  !((await CurrentField.GetBotField()).OwnSide.Banish ?? Array.Empty<int>()).Contains(GladCardId.Bestiari);
            bool gyzarusInExtra = (await CurrentField.GetCardsInLocation(CardLocation.Extra)).Any(c => c.IsCode(GladCardId.Gyzarus));
            if (!bestiariInDeck || !gyzarusInExtra)
                return null;
            int lp = await CurrentField.GetBotLifePoints();
            // The battle: a face-up monster it survives (defence; or attack position it beats), the cheapest damage.
            var wall = enemyMonsters.Where(m => m.IsFaceup() && m.ID > 0 && !BouncesAttacker(m) &&
                                                (!m.IsAttack() || attacker.Attack > m.Attack) &&
                                                lp - Math.Max(0, (m.IsAttack() ? 0 : m.Defense) - attacker.Attack) > DangerLifePoints)
                .OrderBy(m => Math.Max(0, (m.IsAttack() ? 0 : m.Defense) - attacker.Attack)).FirstOrDefault();
            if (wall.ID <= 0)
                return null;
            int ourSpells = (await CurrentField.GetCardsInSpellZone()).Count(c => c.ID > 0);
            int bestiari = enemySpells.Count > 0 ? 1 : ourSpells > 0 ? -1 : 0;
            int theirOthers = enemyMonsters.Count + enemySpells.Count - 1 - (enemySpells.Count > 0 ? 1 : 0); // after Ryko and Bestiari's pick
            int value = 1 + (theirOthers > 0 ? 1 : 0) + bestiari;
            string weigh = $"Gyzarus takes the Ryko{(theirOthers > 0 ? " and another card" : "")}, Bestiari's destroy " +
                           (bestiari > 0 ? "hits their spell/trap" : bestiari < 0 ? "has to hit one of ours" : "has nothing to hit") + $": {value:+0;-0} card(s)";
            if (value < 2 || !Rule(GladRules.GyzarusForRyko))
            {
                Note(context, $"no Gyzarus routine for their likely Ryko: {weigh}");
                return null;
            }
            var bot = context.Bot;
            var steps = new List<RoutineStep>
            {
                RoutineStep.Battle,
                RoutineStep.AttackWith(attacker.ID, $"attack {CardNames.Get(wall.ID)} with {CardNames.Get(attacker.ID)} to tag out into Bestiari", () =>
                {
                    AimAt(bot, wall, AimKind.Committed, $"{CardNames.Get(attacker.ID)} battles {CardNames.Get(wall.ID)} to tag out into Bestiari (Gyzarus for their Ryko)");
                    _tagBestiariTurn = turn;
                }),
                new RoutineStep(MainAction.SpSummon, GladCardId.Gyzarus, $"contact fuse Gyzarus (Bestiari + {CardNames.Get(second.ID)}): destroy the Ryko"),
            };
            return await StartRoutineAsync(context, "Gyzarus for a set Ryko",
                $"their face-down is Ryko {FaceDownRykoChance(ryko):P0}: {weigh}", steps);
        }

        // The turn a routine wants the next tag-out to bring in Bestiari (Gyzarus material).
        private int _tagBestiariTurn = -1;

        private async Task<IdleCmdResponse?> TryRaigekiThreatAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> enemyMonsters)
        {
            var raigeki = (state.ActivableCards ?? Array.Empty<DynamicCard>())
                .FirstOrDefault(card => card.IsCode(GladCardId.RaigekiBreak) && card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone);
            if (raigeki.ID <= 0 || (await GetBotHandCardsAsync()).Count == 0)
                return null;
            foreach (var threat in enemyMonsters.Where(card => card.ID > 0 && card.IsFaceup()).OrderByDescending(card => card.GetRealPower()))
                if (await RaigekiWorthyAsync(threat) is string worth && Rule(GladRules.RaigekiThreat))
                {
                    PlanTarget(context.Header.CurrentTurn, threat, theirs: true);
                    Note(context, $"Raigeki Break {CardNames.Get(threat.ID)}: {worth}");
                    return raigeki.MainActivateFromIndex();
                }
            return null;
        }

        /// <summary>
        /// Whether their monster is worth Raigeki Break (two cards: Raigeki + the discard), or null: a big threat (2400+ ATK,
        /// an Extra Deck monster, an attacker-bouncer, beats our monsters, a strong effect), and not one that pays them for
        /// being destroyed (Sangan...). User, 2026-11-11: "dimensional alchemist is nowhere near strong enough to raigeki break".
        /// </summary>
        /// <summary>Their monster has an effect when it's destroyed by battle (Machina Fortress): better removed another way than beaten.</summary>
        private static async Task<bool> PaysOnBattleDestructionAsync(DynamicCard monster) =>
            monster.ID > 0 && await CardDatabase.GetAsync(monster.ID) is DBCardInfo info &&
            System.Text.RegularExpressions.Regex.IsMatch(info.CardText ?? "", @"destroyed by battle", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private async Task<string?> RaigekiWorthyAsync(DynamicCard monster) =>
            monster.ID <= 0 || !monster.IsFaceup() || RewardsDestruction(monster) || !await IsBigThreatAsync(monster)
                ? null
                : $"{CardNames.Get(monster.ID)} ({monster.GetRealPower()}) is a big threat, worth two cards";

        // A planner line spending Raigeki Break on a monster of theirs that isn't worth two cards pays the suggestion cost:
        // under 1900 ATK/DEF, Level 4 or lower, not from the Extra Deck (seen 2026-11-11: Dimensional Alchemist 1300, with
        // Mystical Space Typhoon discarded for it).
        internal override double PlanSuggestions(IReadOnlyList<Search.PlanStep> steps, int index, IReadOnlyList<string> offered, DuelSim.Engine.SideSummary theirs)
        {
            double cost = base.PlanSuggestions(steps, index, offered, theirs);
            string key = steps[index].Key;
            if (!key.StartsWith("card:") || key.IndexOf('@') is not (int at and > 0) || !key.Substring(at + 1).StartsWith("1.4."))
                return cost;
            int previous = index - 1;
            while (previous >= 0 && steps[previous].Key.StartsWith("card:"))
                previous--;
            if (previous < 0 || KeyCardId(steps[previous].Key) != GladCardId.RaigekiBreak)
                return cost;
            int code = KeyCardId(key);
            var monster = theirs.Monsters.FirstOrDefault(m => m.Code == code || Search.SearchHost.CanonicalId(m.Code) == code);
            const int extraDeck = 0x40 | 0x2000 | 0x800000 | 0x4000000; // fusion, synchro, xyz, link
            bool worthy = monster.Code != 0 && monster.FaceUp &&
                          (Math.Max(monster.Attack, monster.Defense) >= 1900 || monster.Level >= 5 || (monster.Type & extraDeck) != 0);
            return worthy ? cost : cost + SuggestionCost;
        }


        private async Task<int> OppHandAsync() => (await CurrentField.GetBotField()).EnemySide.Hand?.Length ?? 0;

        // ------------------------------------------------------------------ chains

        public override async Task<int> OnSelectChainAsync(DecisionContext<SelectChainData> context, CancellationToken ct = default)
        {
            var state = context.StateData;
            var options = state.SelectableCards ?? Array.Empty<DynamicCard>();
            if (state.Forced || options.Length == 0)
                return await base.OnSelectChainAsync(context, ct);
            await PrepareContextAsync(context, ct);
            bool theirLink = context.Header.LastChainPlayer == 1;
            int Find(int id) => Array.FindIndex(options, card => card.IsCode(id) && card.CurrentCoordinates.Controller == 0);

            // War Chariot: their Effect Monster's effect, negated and destroyed (only offered while we have a Gladiator Beast).
            if (theirLink && Find(GladCardId.WarChariot) is int chariot and >= 0 && Rule(GladRules.WarChariot))
            {
                Note(context, "Gladiator Beast War Chariot: negate their monster's effect and destroy it");
                return chariot;
            }
            // Heraklinos: discard 1 to negate their Spell/Trap.
            if (theirLink && Find(GladCardId.Heraklinos) is int herak and >= 0 && (await GetBotHandCardsAsync()).Count > 0 &&
                Rule(GladRules.HeraklinosNegate))
            {
                Note(context, "Heraklinos: discard 1 to negate their Spell/Trap");
                return herak;
            }
            // A boost that wins the battle comes before Raigeki Break (user, 2026-11-18: "if the boost would win the battle the boost is
            // preferable. gladiator beasts should prefer winning battles when possible"): it costs one card, not two, and our Gladiator
            // Beast survives and tags out. Not against a monster that pays them for being destroyed by battle (Machina Fortress).
            var battling = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            if (!await PaysOnBattleDestructionAsync(battling) && await BoostToWinBattleAsync(context, options) is int earlyBoost)
                return earlyBoost;
            // Raigeki Break on their attacker when it's worth two cards (a big threat) and it would win the battle or hit us.
            if (!IsOwnTurn(context.Header) && Find(GladCardId.RaigekiBreak) is int raigeki and >= 0 && (await GetBotHandCardsAsync()).Count > 0)
            {
                var attacker = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
                var defender = await CurrentField.GetBotBattlingMonster(isOwnSide: true);
                bool hurts = attacker.ID > 0 && (defender.ID <= 0 || attacker.Attack > defender.GetRealPower());
                if (hurts && await RaigekiWorthyAsync(attacker) is string worth && Rule(GladRules.RaigekiThreat))
                {
                    PlanTarget(context.Header.CurrentTurn, attacker, theirs: true);
                    Note(context, $"Raigeki Break their attacking {CardNames.Get(attacker.ID)}: {worth}");
                    return raigeki;
                }
            }
            // CSV M7: our Gladiator Beast in a battle it would lose (or only tie): a stat card on offer that wins it, so it survives
            // and tags out - unless another battle this turn would still destroy it (the boost lasts the turn, so it counts there).
            if (await BoostToWinBattleAsync(context, options) is int boost)
                return boost;
            // Our Gladiator Beasts' own triggers: the tag-outs at the end of the Battle Phase, Gyzarus' destruction on summon,
            // Darius' revive.
            int own = Array.FindIndex(options, card => IsGlad(card) && card.CurrentCoordinates.Controller == 0 &&
                                                      !card.IsCode(GladCardId.Heraklinos));
            if (!theirLink && own >= 0 && Rule(GladRules.TagOut))
            {
                Note(context, $"use {CardNames.Get(options[own].ID)}'s effect");
                return own;
            }
            return await base.OnSelectChainAsync(context, ct);
        }

        // ------------------------------------------------------------------ card selections

        public override async Task<IList<byte>?> OnSelectCardAsync(DecisionContext<SelectCardData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            if (StapleTargets(context) is IList<byte> stapleTarget)
                return stapleTarget;
            var state = context.StateData;
            var cards = state.SelectableCards ?? Array.Empty<DynamicCard>();
            var hint = (DHint)state.SelectionHint;
            if (cards.Length == 0)
                return await base.OnSelectCardAsync(context, ct);
            var indexed = cards.Select((card, i) => (Card: card, Index: i)).ToList();
            bool In(CardLocation location) => indexed.All(x => x.Card.CurrentCoordinates.Location == (byte)location);
            bool allOurs = indexed.All(x => x.Card.CurrentCoordinates.Controller == 0);
            int count = Math.Max(1, state.Minimum);
            IList<byte> Pick(IEnumerable<(DynamicCard Card, int Index)> ordered, int n, string what)
            {
                var picks = ordered.Take(n).ToList();
                Note(context, $"{what}: {string.Join(", ", picks.Select(x => CardNames.Get(x.Card.ID)))}");
                return picks.Select(x => (byte)x.Index).ToList();
            }

            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            var enemySpells = await GetEnemySpellCardsAsync();
            var grave = await GetBotGraveyardCardsAsync();

            // Contact Fusion materials (our Gladiator Beasts on the field): Bestiari / Laquari as required, then the least useful.
            if (allOurs && In(CardLocation.MonsterZone) && indexed.All(x => IsGlad(x.Card)) && LastMainMove is { Action: MainAction.SpSummon } fusion &&
                fusion.Turn == context.Header.CurrentTurn && (fusion.CardId == GladCardId.Gyzarus || fusion.CardId == GladCardId.Heraklinos ||
                                                              Search.SearchHost.CanonicalId(fusion.CardId) is GladCardId.Gyzarus or GladCardId.Heraklinos))
            {
                int key = fusion.CardId == GladCardId.Gyzarus || Search.SearchHost.CanonicalId(fusion.CardId) == GladCardId.Gyzarus
                    ? GladCardId.Bestiari : GladCardId.Laquari;
                return Pick(indexed.OrderBy(x => x.Card.IsCode(key) ? 0 : 1).ThenBy(x => x.Card.IsCode(GladCardId.Laquari) ? 1 : 0)
                    .ThenBy(x => x.Card.GetRealPower()), count, "contact fusion materials");
            }

            // The tag-in from the deck (a tag-out, or Gyzarus' two).
            if (hint == DHint.SPSUMMON && allOurs && In(CardLocation.Deck) && indexed.All(x => x.Card.IsCode(GladCardId.Glads)) && Rule(GladRules.TagPick))
            {
                var retiari = await RetiariTargetAsync();
                if (retiari.Strong && indexed.Any(x => x.Card.IsCode(GladCardId.Retiari)))
                    Note(context, $"Retiari can banish {retiari.Why} from their graveyard");
                // A Bestiari staying on our field: Gyzarus in Main Phase 2 shuffles this tag-in back into the deck, so only its
                // tag-in effect counts, not its body (user, 2026-11-11: "it may as well get retiari (or equeste or something)").
                var ours = await GetBotMonsterCardsAsync();
                bool dariusWorth = await DariusRevivesWorthAsync(ours, grave, (DuelPhase)context.Header.CurrentPhase);
                var battlePhase = (DuelPhase)context.Header.CurrentPhase;
                bool fodder = ours.Any(m => m.IsFaceup() && m.IsCode(GladCardId.Bestiari) && !CanTagOutNow(m, battlePhase)) &&
                              (DuelPhase)context.Header.CurrentPhase is DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Damage or DuelPhase.DamageCal or DuelPhase.Battle;
                if (fodder)
                    Note(context, "tag in for its effect: Bestiari stays, so Gyzarus takes this one back to the deck in Main Phase 2");
                // CSV M3: each tag-in scored by what its effect does on this board (all known information), not a fixed ranking.
                var handNow = await GetBotHandCardsAsync();
                var valued = new List<((DynamicCard Card, int Index) X, double Value, string Why)>();
                foreach (var x in indexed)
                {
                    var (value, why) = TagValue(x.Card, enemyMonsters, enemySpells, grave, handNow, retiari.Strong, retiari.Any, fodder, dariusWorth);
                    if (_tagBestiariTurn == context.Header.CurrentTurn && x.Card.IsCode(GladCardId.Bestiari))
                        (value, why) = (value + 100, "the Bestiari planned for Gyzarus");
                    valued.Add((x, value, why));
                }
                valued = valued.OrderByDescending(v => v.Value).ToList();
                var ordered = valued.Select(v => v.X).ToList();
                if (valued.Count > 0)
                    Note(context, $"tag in {CardNames.Get(valued[0].X.Card.ID)}: {valued[0].Why} ({valued[0].Value:0.0})" +
                                  (valued.Count > 1 ? $"; next: {string.Join(", ", valued.Skip(1).Take(3).Select(v => $"{CardNames.Get(v.X.Card.ID)} {v.Value:0.0} ({v.Why})"))}" : ""));
                // Two at once (Gyzarus): different ones.
                var picks = new List<(DynamicCard Card, int Index)>();
                foreach (var x in ordered)
                    if (picks.Count < count && !picks.Any(p => p.Card.ID == x.Card.ID))
                        picks.Add(x);
                foreach (var x in ordered)
                    if (picks.Count < count && !picks.Contains(x))
                        picks.Add(x);
                return Pick(picks, count, "tag in");
            }

            // Darius' revive from the GY: Bestiari (Gyzarus material), Laquari, then the rest.
            if (hint == DHint.SPSUMMON && allOurs && In(CardLocation.Grave) && indexed.All(x => x.Card.IsCode(GladCardId.Glads)))
                return Pick(indexed.OrderBy(x => ReviveRank(x.Card)), count, "Darius revives");

            // Equeste: a Gladiator Beast card from the GY to the hand.
            // (Our cards only: with a card of theirs among the options the handler used to give up and the default picked; CSV M4.)
            if (hint == DHint.ATOHAND && In(CardLocation.Grave) && indexed.Any(x => x.Card.CurrentCoordinates.Controller == 0))
            {
                var oursOnly = indexed.Where(x => x.Card.CurrentCoordinates.Controller == 0).ToList();
                bool monsterEffectNext = enemyMonsters.Any(m => !m.IsFaceup() && FaceDownRykoChance(m) >= FlipThreatChance);
                lock (OpponentDeck)
                    monsterEffectNext |= OpponentDeck.LikelyCards().Any(id => MonarchIds.Contains(id));
                bool gyzarusNext = (await GetBotMonsterCardsAsync()).Any(m => m.IsFaceup() && IsGlad(m) && !m.IsCode(GladCardId.Bestiari));
                double Worth(DynamicCard c) =>
                    c.IsCode(GladCardId.WarChariot) ? (monsterEffectNext ? 5000 : 0)
                    : c.IsCode(GladCardId.Bestiari) && gyzarusNext ? 4000
                    : c.IsCode(GladCardId.Laquari) ? 2100
                    : c.HasType(CardType.Monster) ? c.Attack : -1;
                return Pick(oursOnly.OrderByDescending(x => Worth(x.Card)), count,
                    $"Equeste returns (War Chariot when a monster effect of theirs is likely, Bestiari when Gyzarus follows, else the biggest ATK)");
            }

            // Retiari: banish a card from their GY: a graveyard effect first, then their biggest monster.
            // Against a Fairy deck (two or more Fairies in their graveyard: Kristya, Honest revive and search them), their Fairies come
            // next (CSV M2), then the rest by ATK.
            if (hint == DHint.REMOVE && indexed.All(x => x.Card.CurrentCoordinates.Controller == 1) && In(CardLocation.Grave))
            {
                const int RaceFairy = 0x4;
                bool fairyDeck = indexed.Count(x => x.Card.HasType(CardType.Monster) && (x.Card.Race & RaceFairy) != 0) >= 2;
                return Pick(indexed.OrderBy(x => HasGraveyardEffect(x.Card) ? 0 : fairyDeck && x.Card.HasType(CardType.Monster) && (x.Card.Race & RaceFairy) != 0 ? 1 : 2)
                        .ThenByDescending(x => x.Card.HasType(CardType.Monster) ? x.Card.Attack : -1),
                    count, fairyDeck ? "Retiari banishes (Fairies before the rest: a Fairy deck)" : "Retiari banishes");
            }

            // Destruction on summon (Bestiari, Murmillo, Gyzarus' up to 2): their cards only, set Spell/Traps and threats first.
            if (hint == DHint.DESTROY && indexed.Any(x => x.Card.CurrentCoordinates.Controller == 1))
            {
                var theirs = indexed.Where(x => x.Card.CurrentCoordinates.Controller == 1)
                    .OrderByDescending(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone ? TargetThreat(x.Card) : 0)
                    .ThenBy(x => x.Card.IsFaceup() ? 1 : 0)
                    .ToList();
                int n = Math.Min(Math.Max(count, state.Maximum), theirs.Count);
                if (n >= count)
                    return Pick(theirs, n, "destroy");
            }

            // Discards (Raigeki Break, Heraklinos): Shield Warrior and Indomitable first (they work from the GY), then the least useful.
            if (hint == DHint.DISCARD && allOurs && In(CardLocation.Hand) && Rule(GladRules.Discard))
                return Pick(indexed.OrderBy(x => DiscardRank(x.Card)), count, "discard");

            return await base.OnSelectCardAsync(context, ct);
        }

        /// <summary>
        /// The tag-in for this board (lower first): Murmillo when they have a face-up monster we'd rather not fight, Bestiari
        /// when they have a Spell/Trap (and never when only ours could be hit), Equeste with a good card in the GY, Darius with
        /// a Gladiator Beast in the GY, Laquari (2100) otherwise. Murmillo never without a face-up monster of theirs: its
        /// destruction is mandatory.
        /// </summary>
        // Monarchs: their summon effects are what War Chariot answers.
        private static readonly int[] MonarchIds = { 9748752, 73125233, 26205777, 4929256, 51945556, 60229110 };

        /// <summary>
        /// CSV M3 (user, 2026-11-15: "score the tag-in choice dynamically depending on the board state and *all* known information"): what
        /// this Gladiator Beast's tag-in is worth on this board, in card units, and why. Effects by what they do now; bodies by whether
        /// they last (only effects when it's Gyzarus fodder: Bestiari stays and Gyzarus shuffles this one back).
        /// </summary>
        private (double Value, string Why) TagValue(DynamicCard card, List<DynamicCard> enemyMonsters, List<DynamicCard> enemySpells,
            List<DynamicCard> grave, List<DynamicCard> hand, bool retiariStrong, bool retiariAny, bool gyzarusFodder, bool dariusWorth)
        {
            var parts = new List<string>();
            double value = 0;
            var faceUp = enemyMonsters.Where(m => m.IsFaceup()).ToList();
            int theirBest = faceUp.Where(m => m.IsAttack()).Select(m => m.Attack).DefaultIfEmpty(0).Max();
            if (card.IsCode(GladCardId.Murmillo))
            {
                var target = faceUp.OrderByDescending(m => m.GetRealPower()).FirstOrDefault();
                if (target.ID > 0)
                {
                    double kill = 1 + Search.MctsEvaluator.AtkValue(target.GetRealPower());
                    bool otherAnswer = hand.Any(c => c.IsCode(GladCardId.RaigekiBreak));
                    if (otherAnswer)
                        kill *= TagMurmilloOtherAnswerShare;
                    value += kill;
                    parts.Add($"destroys {CardNames.Get(target.ID)}{(otherAnswer ? " (Raigeki Break in hand could too)" : "")}");
                }
                else
                    parts.Add("no face-up monster of theirs to destroy");
            }
            else if (card.IsCode(GladCardId.Bestiari))
            {
                if (enemySpells.Count > 0)
                {
                    value += TagBestiariValue + (enemySpells.Count >= 2 ? TagBestiariChoiceBonus : 0);
                    parts.Add($"destroys one of their {enemySpells.Count} spell/trap(s)");
                }
                else
                    parts.Add("no spell/trap of theirs to destroy");
            }
            else if (card.IsCode(GladCardId.Equeste))
            {
                bool chariot = grave.Any(g => g.IsCode(GladCardId.WarChariot));
                bool gladThere = grave.Any(g => g.IsCode(GladCardId.Glads));
                bool monsterEffectNext = enemyMonsters.Any(m => !m.IsFaceup() && FaceDownRykoChance(m) >= FlipThreatChance);
                lock (OpponentDeck)
                    monsterEffectNext |= OpponentDeck.LikelyCards().Any(id => MonarchIds.Contains(id));
                if (chariot)
                {
                    value += TagEquesteChariotValue + (monsterEffectNext ? TagEquesteChariotNeededBonus : 0);
                    parts.Add($"returns War Chariot{(monsterEffectNext ? " (a monster effect of theirs likely next turn)" : "")}");
                }
                else if (gladThere)
                {
                    bool gladInHand = hand.Any(c => IsGlad(c));
                    value += gladInHand ? TagEquesteGladWithOneInHand : TagEquesteGladValue;
                    parts.Add($"returns a Gladiator Beast (+1 card{(gladInHand ? "; one already in hand" : "")})");
                }
                else
                    parts.Add("nothing in our graveyard to return");
            }
            else if (card.IsCode(GladCardId.Retiari))
            {
                value += retiariStrong ? TagRetiariStrongValue : retiariAny ? TagRetiariMinorValue : 0;
                parts.Add(retiariStrong ? "banishes an important card from their graveyard" : retiariAny ? "banishes a minor card from their graveyard" : "their graveyard is empty");
            }
            else if (card.IsCode(GladCardId.Darius))
            {
                value += dariusWorth ? TagDariusUsefulValue : TagDariusIdleValue;
                parts.Add(dariusWorth ? "revives a Gladiator Beast for Gyzarus/Heraklinos" : "its revive sets nothing up");
            }
            if (!gyzarusFodder)
            {
                // The body: what it's worth on the field, more when nothing of theirs runs it over next turn.
                int atk = card.IsCode(GladCardId.Laquari) ? 2100 : card.Attack;
                double body = TagBodyShare * Search.MctsEvaluator.AtkValue(atk) + (atk > theirBest ? TagBodySafeBonus : 0);
                value += body;
                if (card.IsCode(GladCardId.Laquari))
                    parts.Add(atk > theirBest ? "2100 ATK: survives their turn" : "2100 ATK");
            }
            return (value, parts.Count == 0 ? "body only" : string.Join("; ", parts));
        }

        private int TagRank(DynamicCard card, List<DynamicCard> enemyMonsters, List<DynamicCard> enemySpells, List<DynamicCard> grave,
            bool retiariStrong = false, bool retiariAny = false, bool gyzarusFodder = false, bool dariusWorth = false)
        {
            var faceUpThreat = enemyMonsters.Where(m => m.IsFaceup()).OrderByDescending(m => m.GetRealPower()).FirstOrDefault();
            bool theirSpellTrap = enemySpells.Count > 0;
            bool anySpellTrapOfOurs = OwnSpellTrapCount > 0;
            bool gladInGrave = grave.Any(g => g.IsCode(GladCardId.Glads));
            bool goodInGrave = grave.Any(g => g.IsCode(GladCardId.Bestiari, GladCardId.Laquari, GladCardId.WarChariot));
            // Only the tag-in effect matters (Gyzarus fodder): Murmillo on a threat, Retiari, Equeste, Bestiari's destroy, Darius;
            // Laquari (only a body) last.
            if (gyzarusFodder)
                return card.IsCode(GladCardId.Murmillo) ? (faceUpThreat.ID > 0 ? 0 : 99)
                    : card.IsCode(GladCardId.Retiari) ? (retiariAny ? 1 : 98)
                    : card.IsCode(GladCardId.Equeste) ? (goodInGrave || gladInGrave ? 2 : 6)
                    : card.IsCode(GladCardId.Bestiari) ? (theirSpellTrap ? 3 : 7)
                    : card.IsCode(GladCardId.Darius) ? (dariusWorth ? 4 : 12)
                    : 9;
            if (card.IsCode(GladCardId.Murmillo))
                return faceUpThreat.ID > 0 && faceUpThreat.GetRealPower() >= 1800 ? 0 : faceUpThreat.ID > 0 ? 5 : 99;
            if (card.IsCode(GladCardId.Bestiari))
                return theirSpellTrap ? 1 : anySpellTrapOfOurs ? 98 : 6;
            if (card.IsCode(GladCardId.Equeste))
                return goodInGrave ? 2 : 7;
            if (card.IsCode(GladCardId.Darius))
                return dariusWorth ? 3 : 12; // only worth it as Contact Fusion setup (see DariusRevivesWorthAsync)
            // Retiari banishes a card from their graveyard (user, 2026-11-11: "removing things from my grave is strong"): right
            // after Murmillo's big-threat kill when a graveyard effect, a revive target or a DAD-count DARK is there; ahead of
            // Laquari whenever their graveyard holds anything; last only when it's empty (the tag-in would do nothing).
            if (card.IsCode(GladCardId.Retiari))
                return retiariStrong ? 1 : retiariAny ? 4 : 9;
            if (card.IsCode(GladCardId.Laquari))
                return 5;
            return 10;
        }

        /// <summary>
        /// What Retiari's tag-in could banish from their graveyard: Strong when there's a card worth taking (one that works
        /// from the graveyard, a monster of 1900+ ATK or from the Extra Deck to revive, a DARK for Dark Armed Dragon's count),
        /// Any when their graveyard holds anything at all.
        /// </summary>
        private async Task<(bool Strong, bool Any, string Why)> RetiariTargetAsync()
        {
            var theirGrave = ((await CurrentField.GetBotField()).EnemySide.Grave ?? Array.Empty<int>()).Where(id => id > 0).ToList();
            if (theirGrave.Count == 0)
                return (false, false, "");
            foreach (int id in theirGrave)
            {
                if (!Search.SearchHost.Available || Search.SearchHost.Cards.Get(id) is not { } c)
                    continue;
                bool monster = (c.Type & 0x1) != 0;
                string? why = EffectEvaluator.HasGraveyardEffect(c.Text ?? "") ? "a card that works from the graveyard"
                    : monster && (c.IsExtraDeckMonster || c.Attack >= 1900) ? "a monster worth reviving"
                    : monster && c.Attribute == 0x20 ? "a DARK (Dark Armed Dragon's count)"
                    : null;
                if (why != null)
                    return (true, true, $"{CardNames.Get(id)} ({why})");
            }
            return (false, true, "");
        }

        /// <summary>
        /// Whether Darius' tag-in revive sets up a Contact Fusion (user, 2026-11-11: "darius is really just for reviving
        /// bestiari or laquari (or anything if you have either bestiari or laquari on field / another glad that you're going
        /// to tag out for bestiari or laquari)"): Bestiari or Laquari in our graveyard; or any Gladiator Beast there while
        /// Bestiari / Laquari stays on our field (not tagging out itself), or another of our Gladiator Beasts tags out this Battle
        /// Phase (CanTagOutNow) and can bring one (one left in the deck).
        /// </summary>
        /// <summary>
        /// Whether this monster of ours tags out at the end of this Battle Phase: a face-up Gladiator Beast with a tag-out
        /// (the main-deck ones; Gyzarus, which brings two), not negated (Darius' revive, a negation), that battled this turn,
        /// while the Battle Phase is still on. Heraklinos has no tag-out.
        /// </summary>
        private static bool CanTagOutNow(DynamicCard card, DuelPhase phase) =>
            card.ID > 0 && card.IsFaceup() && card.Attacked && card.Negator.Location == 0 &&
            (card.IsCode(GladCardId.Glads) || card.IsCode(GladCardId.Gyzarus)) &&
            phase is DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Damage or DuelPhase.DamageCal or DuelPhase.Battle;

        private async Task<bool> DariusRevivesWorthAsync(List<DynamicCard> ours, List<DynamicCard> grave, DuelPhase phase)
        {
            if (grave.Any(g => g.IsCode(GladCardId.Bestiari, GladCardId.Laquari)))
                return true;
            if (!grave.Any(g => g.IsCode(GladCardId.Glads)))
                return false;
            if (ours.Any(m => m.IsFaceup() && m.IsCode(GladCardId.Bestiari, GladCardId.Laquari) && !CanTagOutNow(m, phase)))
                return true;
            if (!ours.Any(m => CanTagOutNow(m, phase)))
                return false;
            // One of them still in the deck for that tag-out: copies run minus those seen (hand, graveyard, field, banished).
            var seen = (await GetBotHandCardsAsync()).Concat(grave).Concat(ours)
                .Concat(await CurrentField.GetCardsInLocation(CardLocation.Removed)).Where(c => c.CurrentCoordinates.Controller == 0 || c.CurrentCoordinates.Location != (byte)CardLocation.Removed).ToList();
            return seen.Count(c => c.IsCode(GladCardId.Bestiari)) < 1 || seen.Count(c => c.IsCode(GladCardId.Laquari)) < 3;
        }

        private static int ReviveRank(DynamicCard card) =>
            card.IsCode(GladCardId.Bestiari) ? 0 : card.IsCode(GladCardId.Laquari) ? 1 : card.IsCode(GladCardId.Equeste) ? 2
            : card.IsCode(GladCardId.Darius) ? 3 : card.IsCode(GladCardId.Retiari) ? 4 : 5;

        private static int EquesteRank(DynamicCard card) =>
            card.IsCode(GladCardId.Bestiari) ? 0 : card.IsCode(GladCardId.Laquari) ? 1 : card.IsCode(GladCardId.WarChariot) ? 2
            : card.IsCode(GladCardId.Darius) ? 3 : card.IsCode(GladCardId.Equeste) ? 4 : card.IsCode(GladCardId.IndomitableGladiatorBeast) ? 5 : 6;

        // What we lose least by discarding (Raigeki Break's cost, Heraklinos'): GY-effect cards first.
        private static int DiscardRank(DynamicCard card) =>
            card.IsCode(GladCardId.ShieldWarrior, GladCardId.SkillSuccessor) ? 0 // GY effects; Skill Successor: +800 from the GY (user, 2026-11-11: a good discard)
            : card.IsCode(GladCardId.IndomitableGladiatorBeast) ? 1
            : card.IsCode(GladCardId.Retiari) ? 3
            : card.IsCode(GladCardId.Murmillo) ? 4
            : card.IsCode(GladCardId.UpstartGoblin) ? 5
            : card.IsCode(GladCardId.TrapStun, GladCardId.Waboku) ? 6
            : card.IsCode(GladCardId.Glads) ? 8
            : 7;

        // Our Spell/Trap zone at this decision (Bestiari's tag-in would hit our own card when it holds the only ones).
        private int OwnSpellTrapCount => _ownSpellTraps;
        private int _ownSpellTraps;

        // A Gladiator Beast of ours on the field: a set Skill Successor has a target, so it's bait (user, 2026-11-16: "skill sucessor can be
        // used as a bait card as soon as you have a glad on field": chained when their removal hits it).
        private bool _gladOnField;

        protected override IReadOnlyCollection<int> BaitCardIds =>
            _gladOnField ? DefaultBaitCards.Append(GladCardId.SkillSuccessor).ToArray() : DefaultBaitCards;

        protected override void OnFieldRefreshed(FieldInfo field)
        {
            _gladOnField = (field.OwnSide.MonsterZones ?? Array.Empty<CardArray>()).Any(zone => (zone.Cards ?? Array.Empty<int>()).FirstOrDefault() is int id && id > 0 &&
                (GladCardId.GladMonsters.Contains(id) || GladCardId.GladMonsters.Contains(Search.SearchHost.CanonicalId(id))));
            _ownSpellTraps = (field.OwnSide.SpellZones ?? Array.Empty<CardArray>()).Count(zone => zone.Cards?.Any(id => id != 0) == true);
            _oppression = (field.EnemySide.SpellZones ?? Array.Empty<CardArray>())
                .Any(zone => (zone.Cards ?? Array.Empty<int>()).FirstOrDefault() is int id && (id == RoyalOppression || id == RoyalOppression + 20));
        }
    }
}
