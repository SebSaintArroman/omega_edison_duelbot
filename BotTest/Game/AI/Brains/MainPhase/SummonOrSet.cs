using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Which monster to Normal Summon or set, and whether to summon at all (ChooseSafeSummonOrSet,
    /// ChooseNormalSummonOrSetAsync), with the deck hooks for it.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // A summon trigger a Normal Summon sets off: "When this card is (Normal) Summoned", "... Normal or Special Summoned"; not
        // one only a Special Summon starts (Gladiator Beast Murmillo's, seen 2026-11-13 summoned "for its effect").
        private static readonly System.Text.RegularExpressions.Regex NormalSummonTriggerText = new(
            @"\b(when|if) this card is (normal |tribute )?summoned|\b(when|if) this card is normal (or special )?summoned",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>From this many LP of ours, Solemn Judgment is set after our other traps (early game). Tune from logs.</summary>
        [Tunable("From this many LP of ours, Solemn Judgment is set after our other traps (early game). Tune from logs.")]
        public int SolemnEarlyGameLifePoints { get; set; } = 6000;

        /// <summary>What an unused Normal Summon is worth (card units): a monster in hand worth less than this gets set rather than
        /// waste it. Tune from logs.</summary>
        [Tunable("What an unused Normal Summon is worth (card units): a monster in hand worth less than this gets set rather than waste it. Tune from logs.")]
        public double UnusedNormalSummonValue { get; set; } = 1.0;

        // ------------------------------------------------------------------ deck hooks for the summon / set choice

        /// <summary>Safe Normal Summon order among the candidates (lower first; ties go to the bigger ATK). Default: no preference.</summary>
        protected virtual int SafeSummonPriority(DynamicCard card, List<DynamicCard> hand) => 0;

        /// <summary>Monsters worth Setting because losing them isn't a straight -1 (Ryko flips, Necro works from the GY), first = preferred.</summary>
        protected virtual int[] ValueSetIds => DefaultValueSets;

        private static readonly int[] DefaultValueSets = { RykoLightswornHunter };

        /// <summary>A value-set monster below this ATK is never the "safe" face-up Normal Summon: it's set instead.</summary>
        [Tunable("A value-set monster below this ATK is never the \"safe\" face-up Normal Summon: it's set instead.")]
        public int ValueSetSummonAttack { get; set; } = 1500;

        /// <summary>
        /// Chance the opponent kills the bot next turn, without and with one extra blocker.
        /// Their face-up board (minus attacks our monsters soak up) is certain damage; the rest must come from their hand:
        /// chance = 1 - e^(-hand * HandDamagePerCard / damage still needed). 100% if the board alone is lethal, 0% with no hand.
        /// A blocker absorbs the biggest unblocked attack.
        /// </summary>
        protected async Task<(double LethalChance, double LethalChanceWithBlocker, string Why)> OpponentLethalRiskAsync(List<DynamicCard> enemyMonsters)
        {
            int botLp = await CurrentField.GetBotLifePoints();
            int oppHand = (await CurrentField.GetBotField()).EnemySide.Hand?.Length ?? 0;
            var botMonsters = await GetBotMonsterCardsAsync();

            var attacks = enemyMonsters.Where(card => card.IsFaceup()).Select(card => card.Attack).OrderByDescending(atk => atk).ToList();
            // Each of our monsters soaks up one attack (the biggest ones first).
            var unblocked = attacks.Skip(botMonsters.Count).ToList();
            int board = unblocked.Sum();
            int boardWithBlocker = unblocked.Skip(1).Sum();

            double Chance(int boardDamage)
            {
                int needed = botLp - boardDamage;
                if (needed <= 0)
                    return 1;
                if (oppHand == 0)
                    return 0;
                return 1 - Math.Exp(-(double)oppHand * HandDamagePerCard / needed);
            }

            string why = $"opponent board {board} (+{oppHand} cards in hand) vs our LP {botLp}";
            return (Chance(board), Chance(boardWithBlocker), why);
        }

        private int _enemySpellCountNow;
        private readonly HashSet<int> _summonRemovalIds = new();

        // A summon effect that puts a card on top of the deck (Raiza).
        private static readonly System.Text.RegularExpressions.Regex PlaceOnDeckText = new(
            @"when this card is tribute summoned[^.]*(place|return)[^.]*top of[^.]*deck",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Their lethal chance next turn from which a last-resort blocker is set (round-robin 5; user: "50% sounds a little high", to tune).</summary>
        [Tunable("Their lethal chance next turn from which a last-resort blocker is set (round-robin 5; user: \"50% sounds a little high\", to tune).")]
        public double LastResortLethalChance { get; set; } = 0.5;
        /// <summary>How much a last-resort blocker must cut their lethal chance to be set.</summary>
        [Tunable("How much a last-resort blocker must cut their lethal chance to be set.")]
        public double LastResortBlockerDrop { get; set; } = 0.25;

        /// <summary>Typical extra damage one card in the opponent's hand can add next turn. Tune from the logged rolls.</summary>
        [Tunable("Typical extra damage one card in the opponent's hand can add next turn. Tune from the logged rolls.")]
        public int HandDamagePerCard { get; set; } = 1000;

        /// <summary>What one summon-or-set decision sees: the prompt, both boards, and the monsters we could summon / set.</summary>
        private sealed class SummonSituation
        {
            public required DecisionContext<IdleCmdData> Context { get; init; }
            public required IdleCmdData State { get; init; }
            public required List<DynamicCard> EnemyMonsters { get; init; }
            public required List<DynamicCard> BotMonsters { get; init; }
            /// <summary>Monsters we may Normal Summon (rules below take some away).</summary>
            public required List<DynamicCard> Summonable { get; set; }
            /// <summary>Monsters we may set (Level 4 or lower, not Card Trooper).</summary>
            public required List<DynamicCard> Settable { get; init; }
            /// <summary>Their strongest face-up attack-position monster's ATK.</summary>
            public int StrongestEnemy { get; init; }
            /// <summary>A weak monster: alone it's an Almost None clock or none (Plaguespreader at 400): face-up it's just exposed.</summary>
            public required Func<DynamicCard, bool> Weak { get; init; }
            /// <summary>The deck's summon order (SafeSummonPriority).</summary>
            public required Func<DynamicCard, int> SafePriority { get; init; }
            // For the battle-this-turn rules (SummonToKill, SummonToTrade).
            public bool BattleNow { get; set; }
            public int TheirSpellTraps { get; set; }
            public List<int> OurAttackersNow { get; set; } = new();
        }

        /// <summary>
        /// Which monster to Normal Summon or set this Main Phase, if any. The steps, in order (each rule in MainPhase/Rules):
        ///  1. the options: what may be summoned (tribute summons off cheap tributes: TributeExpendable) and set;
        ///  2. options taken away: LowLpNoExposure, PlagueSummonForSynchro;
        ///  3. summons: SafeSummon (then Plaguespreader set in Main Phase 2), SummonToKill, SummonToTrade, SummonForEffect;
        ///  4. holding: HoldMonsterSetsForBattle, KeepGorzLive;
        ///  5. sets: ValueSet (not into a bouncer: NoSetIntoBouncer, nor a stealer: NoSetIntoStealer);
        ///  6. DevelopWhenBleeding, LastResortBlocker, UseNormalSummon;
        ///  7. otherwise nothing: NoBlockers.
        /// </summary>
        protected async Task<IdleCmdResponse?> ChooseSafeSummonOrSet(
            DecisionContext<IdleCmdData> context,
            IdleCmdData state,
            List<DynamicCard> enemyMonsters,
            List<DynamicCard> botMonsters)
        {
            // 1.
            var s = await SummonOptionsAsync(context, state, enemyMonsters, botMonsters);
            if (s == null)
                return null;

            // 2.
            NoSummonIntoLowLpExposure(s, await LowLpExposureLimitAsync());
            var plagueWanted = PlagueOnlySummonedForSynchro(s);

            // 3.
            if (SafeSummon(s) is IdleCmdResponse safe)
                return safe;
            if (PlagueSetInMainPhase2(s, plagueWanted) is IdleCmdResponse plagueSet)
                return plagueSet;
            s.BattleNow = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase && context.Header.CurrentTurn > 1;
            s.TheirSpellTraps = (await GetEnemySpellCardsAsync()).Count;
            s.OurAttackersNow = botMonsters.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).Select(card => card.Attack).ToList();
            if (SummonToKill(s) is IdleCmdResponse kill)
                return kill;
            if (SummonToTrade(s) is IdleCmdResponse trade)
                return trade;
            if (await SummonForEffectAsync(s) is IdleCmdResponse forEffect)
                return forEffect;

            // 4.
            if (await HoldMonsterSetsForBattleAsync(s) || await KeepMonstersInHandForGorzAsync(s))
                return null;

            // 5.
            if (ValueSet(s) is IdleCmdResponse valueSet)
                return valueSet;

            // 6.
            if (DevelopWhenBleeding(s) is IdleCmdResponse develop)
                return develop;
            if (await LastResortBlockerAsync(s) is IdleCmdResponse blocker)
                return blocker;
            if (await UseNormalSummonAsync(s) is IdleCmdResponse use)
                return use;

            // 7.
            NoBlockers(s);
            return null;
        }

        /// <summary>
        /// The monsters we may summon and set. Flip monsters (Ryko) are never summoned face-up (they only do anything set and
        /// flipped). Level 5+ only without tributes (Sirocco on an empty field), never tributing our monsters away, except: a
        /// Lyla we'd rather use up (spent after her effect, or her mill would deck us out) for a Level 5-6; and (RULE
        /// deck.tribute-expendable, round-robin 50, user 2026-11-13: "yes absolutely") any cheap tribute (TributeCost: Treeborn,
        /// a graveyard-effect monster like Vayu, an expendable monster, a small token, a stolen one) when the new monster is
        /// stronger - or any weaker monster when the new one deals with something we otherwise can't ("sometimes it will be
        /// correct to tribute a non-expendable monster if the monarch's effect (or atk) gets over something that you otherwise
        /// can't deal with"). The tributes are planned here. Null: nothing to summon or set.
        /// </summary>
        private async Task<SummonSituation?> SummonOptionsAsync(DecisionContext<IdleCmdData> context, IdleCmdData state,
            List<DynamicCard> enemyMonsters, List<DynamicCard> botMonsters)
        {
            // For the tribute-summon check: their spell/trap count, and our Level 5-6 monsters whose summon effect removes a card.
            _enemySpellCountNow = (await GetEnemySpellCardsAsync()).Count;
            int ourBestNow = botMonsters.Where(m => m.ID > 0 && m.IsFaceup()).Select(EffectiveAttack).DefaultIfEmpty(0).Max();
            _summonRemovalIds.Clear();
            foreach (var card in (state.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => card.Level is 5 or 6))
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && OnSummonTriggerText.IsMatch(info.CardText ?? "") &&
                    CardRemovalText.IsMatch(info.CardText ?? "") || card.ID > 0 && await TextMatchesAsync(card.ID, PlaceOnDeckText))
                    _summonRemovalIds.Add(card.ID);
            var summonable = (state.SummonableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.ID > 0)
                .Where(card => !card.HasType(CardType.Flip))
                .Where(card => card.Level <= 4 || (card.Level == 5 && botMonsters.Count == 0) ||
                               card.Level is 5 or 6 && botMonsters.Any(LylaToUseUp) ||
                               card.Level is 5 or 6 && CheapTribute(card).ID > 0 && Rule(DeckRules.TributeExpendable) ||
                               card.Level >= 7 && CheapTributes(card, 2).Count == 2 && Rule(DeckRules.TributeExpendable))
                .ToList();
            bool Answers(DynamicCard summon) =>
                enemyMonsters.Any(m => m.IsFaceup() && m.GetRealPower() < summon.Attack && m.GetRealPower() >= ourBestNow) ||
                _summonRemovalIds.Contains(summon.ID) && enemyMonsters.Count + _enemySpellCountNow > 0;
            DynamicCard CheapTribute(DynamicCard summon) => botMonsters
                .Where(m => m.ID > 0 && EffectiveAttack(m) < summon.Attack && (TributeCost(m) <= ExpendableTributeCost || Answers(summon)))
                .OrderBy(TributeCost).FirstOrDefault();
            // Two tributes for a Level 7+: the cheapest pair, each weaker than the new monster (cheap, or it answers something).
            List<DynamicCard> CheapTributes(DynamicCard summon, int count) => botMonsters
                .Where(m => m.ID > 0 && EffectiveAttack(m) < summon.Attack && (TributeCost(m) <= ExpendableTributeCost || Answers(summon)))
                .OrderBy(TributeCost).Take(count).ToList();
            if (summonable.Any(card => card.Level is 5 or 6) && botMonsters.FirstOrDefault(LylaToUseUp) is { ID: > 0 } lylaTribute)
                PlanTributes(context.Header.CurrentTurn, new[] { lylaTribute },
                    $"a Level 5-6 off Lyla ({(DeckTooSmallForLyla ? "her mill would deck us out" : "spent after her effect")})");
            else if (summonable.FirstOrDefault(card => card.Level is 5 or 6) is { ID: > 0 } tributeSummon && CheapTribute(tributeSummon) is { ID: > 0 } cheapTribute)
                PlanTributes(context.Header.CurrentTurn, new[] { cheapTribute },
                    $"{CardNames.Get(tributeSummon.ID)} off {CardNames.Get(cheapTribute.ID)} (cost {TributeCost(cheapTribute):0.#}: expendable)");
            else if (summonable.FirstOrDefault(card => card.Level >= 7) is { ID: > 0 } bigSummon && CheapTributes(bigSummon, 2) is { Count: 2 } pair)
                PlanTributes(context.Header.CurrentTurn, pair,
                    $"{CardNames.Get(bigSummon.ID)} off {string.Join(" + ", pair.Select(t => CardNames.Get(t.ID)))} (two cheap tributes)");
            var settable = (state.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.ID > 0 && card.Level <= 4)
                .Where(card => !card.IsCode(CardTrooper))
                .ToList();

            if (summonable.Count == 0 && settable.Count == 0)
                return null;

            int strongestEnemy = enemyMonsters
                .Where(card => card.IsFaceup() && card.IsAttack())
                .Select(card => card.Attack)
                .DefaultIfEmpty(0)
                .Max();
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            var safeHand = await GetBotHandCardsAsync();
            return new SummonSituation
            {
                Context = context, State = state, EnemyMonsters = enemyMonsters, BotMonsters = botMonsters,
                Summonable = summonable, Settable = settable, StrongestEnemy = strongestEnemy,
                Weak = card => BoardAssessment.SpeedOf(BoardAssessment.TurnsToWin(
                    new[] { EffectiveAttack(card) }, Array.Empty<BoardAssessment.Defender>(), oppLp)) is ClockSpeed.AlmostNone or ClockSpeed.None,
                SafePriority = card => SafeSummonPriority(card, safeHand),
            };
        }

        /// <summary>
        /// Whether to set Ryko now (it destroys a card when an attack flips it), or null with the reason logged:
        ///  - not into their set monster (most likely their own Ryko, which flips first and counters ours),
        ///  - not into Grand Mole (it attacks the set Ryko and returns it to our hand without flipping it),
        ///  - not when it would switch off Gorz (an empty field keeps Gorz live).
        /// </summary>
        protected async Task<string?> RykoSetWhyAsync(DecisionContext<IdleCmdData> context, List<DynamicCard> enemyMonsters, bool opponentHasFaceDown)
        {
            if (opponentHasFaceDown && Rule(DeckRules.NoRykoIntoFaceDown))
            {
                StapleNote(context, "don't set Ryko first: they have a set monster that's likely their own Ryko (it flips first and counters ours)");
                Hold(RykoLightswornHunter, MainAction.SetMonster, DeckRules.NoRykoIntoFaceDown);
                return null;
            }
            if (SetIntoBouncerHold(context) is string bouncerHold && Rule(DeckRules.NoSetIntoBouncer))
            {
                StapleNote(context, $"don't set Ryko first: {bouncerHold}");
                Hold(RykoLightswornHunter, MainAction.SetMonster, DeckRules.NoSetIntoBouncer);
                return null;
            }
            if (await KeepGorzLiveAsync(context, enemyMonsters))
                return null;
            return "whatever attacks it flips it, and it destroys a card";
        }

        /// <summary>
        /// The Normal Summon / Set choice for staple monsters, with the deck's own summons in the middle (DeckSummonAsync):
        /// Card Trooper into a face-down (likely Ryko), a set Ryko on our first turn, Lyla into their backrow, an on-summon
        /// monster against Grand Mole, a set Ryko later, then the deck's summons, Card Trooper, and finally the safe
        /// summon / value set / blocker choice.
        /// </summary>
        protected async Task<IdleCmdResponse?> ChooseNormalSummonOrSetAsync(
            DecisionContext<IdleCmdData> context,
            IdleCmdData state,
            List<DynamicCard> enemyMonsters,
            List<DynamicCard> enemySpells,
            List<DynamicCard> botMonsters,
            List<DynamicCard> hand)
        {
            int botLp = await CurrentField.GetBotLifePoints();
            bool danger = botLp < DangerLifePoints;

            // Never summoned: D.D. Crow (a hand effect). Never face-up next to our Cyber Dragon: a Machine (their
            // Chimeratech Fortress Dragon would take both), unless it's a union that can equip itself away.
            state = WithoutNeverSummons(state);
            // Lyla mills 3 every End Phase: not with the deck nearly gone.
            if (DeckTooSmallForLyla && (state.SummonableCards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(LylaLightswornSorceress)))
            {
                StapleNote(context, "don't summon Lyla: our deck is too small for her End Phase mill");
                state.SummonableCards = state.SummonableCards!.Where(card => !card.IsCode(LylaLightswornSorceress)).ToArray();
            }
            // (A union is fine only if it can equip itself away: some Machine of ours with no union on it yet; a monster
            // holds one union at a time.)
            var exposing = await ChimeratechExposingNormalSummonsAsync(state.SummonableCards ?? Array.Empty<DynamicCard>(), botMonsters);
            if (exposing.Count > 0 && await OpponentMayHaveChimeratechAsync(context) && Rule(DeckRules.NoChimeratechExposure))
            {
                StapleNote(context, $"don't summon {string.Join(", ", exposing.Select(card => CardNames.Get(card.ID)))} face-up: {SummonExposesChimeratech(exposing[0], botMonsters)}");
                state.SummonableCards = state.SummonableCards!.Where(card => !exposing.Any(e => e.ID == card.ID)).ToArray();
            }

            var developers = (state.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => card.ID > 0).ToList();

            // Card Trooper is especially good into a face-down monster (likely Ryko): if it's destroyed, we draw a card.
            // A face-down of theirs that's likely a Ryko (from the deck lists their cards fit; a deck without Ryko makes it not).
            bool opponentHasFaceDown = enemyMonsters.Any(card => !card.IsFaceup() && LikelyRyko(card));
            if (opponentHasFaceDown && developers.TryGetByID(CardTrooper, out var trooperFirst) && Rule(DeckRules.TrooperIntoFaceDown))
            {
                StapleNote(context, "normal summon Card Trooper: mill 3 to become 1900 ATK, and it draws a card if the face-down (Ryko?) destroys it");
                return trooperFirst.NormalSummon();
            }

            // A set Ryko is usually the best first-turn play: whatever attacks it flips it, and it destroys a card.
            var ryko = (state.MonsterSetableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card => card.IsCode(RykoLightswornHunter));
            string? rykoWhy = ryko.ID > 0 ? await RykoSetWhyAsync(context, enemyMonsters, opponentHasFaceDown) : null;
            bool ourFirstTurn = context.Header.CurrentTurn <= 2;
            if (rykoWhy != null && ourFirstTurn && Rule(DeckRules.SetRykoFirstTurn))
            {
                if (await SetsWaitForMainPhase2Async(context))
                {
                    StapleNote(context, "set Ryko in Main Phase 2 (the Normal Summon waits for it)");
                    return null;
                }
                StapleNote(context, $"set Ryko, Lightsworn Hunter: our first turn, {rykoWhy}");
                return ryko.SetMonster();
            }

            // Board development: summon face-up for the effect, even into a bigger monster, unless life points are dangerously low.
            if (enemySpells.Count > 0 && !danger &&
                developers.TryGetByID(LylaLightswornSorceress, out var lyla) && Rule(DeckRules.SummonLyla))
            {
                StapleNote(context, $"normal summon Lyla to destroy one of the opponent's {enemySpells.Count} S/T (LP {botLp})");
                return lyla.NormalSummon();
            }

            // Grand Mole up: an on-summon effect monster first (Armageddon Knight), as long as the effect does something.
            // If Mole bounces it, we summon it and use the effect again.
            var onSummon = await PreferOnSummonAgainstBouncerAsync(developers.Where(card => !card.HasType(CardType.Flip)));
            if (onSummon.ID > 0 && !await OnSummonEffectUselessAsync(onSummon, hand) && Rule(DeckRules.OnSummonIntoBouncer))
            {
                StapleNote(context, $"normal summon {CardNames.Get(onSummon.ID)}: their Grand Mole-type monster is up, and if it bounces this we get its summon effect again (LP {botLp})");
                return onSummon.NormalSummon();
            }

            // Later turns: a set Ryko still comes before Armageddon Knight / Dark Grepher.
            if (rykoWhy != null && Rule(DeckRules.SetRyko))
            {
                if (await SetsWaitForMainPhase2Async(context))
                {
                    StapleNote(context, "set Ryko in Main Phase 2 (the Normal Summon waits for it)");
                    return null;
                }
                StapleNote(context, $"set Ryko, Lightsworn Hunter: {rykoWhy}");
                return ryko.SetMonster();
            }

            // The deck's own summons (e.g. Armageddon Knight / Dark Grepher for Vayu).
            if (await DeckSummonAsync(context, state, developers, hand, danger, botLp) is IdleCmdResponse deckSummon)
                return deckSummon;

            // Card Trooper is never Set: its effect needs it face-up, and it replaces itself when destroyed (any LP).
            if (developers.TryGetByID(CardTrooper, out var trooper) && Rule(DeckRules.SummonTrooper))
            {
                StapleNote(context, $"normal summon Card Trooper: mill 3 to become 1900 ATK, draws a card when destroyed (LP {botLp})");
                return trooper.NormalSummon();
            }

            return await ChooseSafeSummonOrSet(context, state, enemyMonsters, botMonsters);
        }

        /// <summary>Below this the bot plays defensively; above it, life points matter little and board development comes first.</summary>
        [Tunable("Below this the bot plays defensively; above it, life points matter little and board development comes first.")]
        protected static readonly Tuned<int> DangerLifePoints = new(1500);

        /// <summary>The deck's own Normal Summons, between the staple ones and the safe-summon fallback. Default: none.</summary>
        protected virtual Task<IdleCmdResponse?> DeckSummonAsync(DecisionContext<IdleCmdData> context, IdleCmdData state,
            List<DynamicCard> developers, List<DynamicCard> hand, bool danger, int botLp) => Task.FromResult<IdleCmdResponse?>(null);

        /// <summary>Whether this on-summon monster's effect would do nothing now (e.g. Armageddon Knight with nothing left to send).</summary>
        protected virtual Task<bool> OnSummonEffectUselessAsync(DynamicCard card, List<DynamicCard> hand) => Task.FromResult(false);
    }
}
