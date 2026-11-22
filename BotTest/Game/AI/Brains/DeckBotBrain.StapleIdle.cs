using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Shared main-phase rules for staple cards (moved from the Vayu brain): free card advantage (Upstart Goblin, Charge
    /// of the Light Brigade, Reinforcement of the Army), backrow removal (Heavy Storm, Lyla, MST), Card Trooper, Jar of
    /// Greed / Legacy when there's no play, and the safe Normal Summon / Set / blocker choice (setting Ryko, value sets,
    /// not setting into Grand Mole or Goyo, keeping Gorz live). Deck brains call these in their own order.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        private int _stapleActivationTurn = -1;
        private readonly Dictionary<int, int> _stapleActivations = new();

        /// <summary>
        /// Activates this card if it's on offer and its rule fires (rolled only once it's on offer). At most 3 times per
        /// turn per card, as a guard against an effect the server keeps offering.
        /// </summary>
        protected bool TryActivateStaple(DecisionContext<IdleCmdData> context, IdleCmdData state, int cardId, string reason, out IdleCmdResponse response, string? rule = null)
        {
            response = default;
            if (!TryGetActivable(state, cardId, out var card))
                return false;
            if (rule != null && !Rule(rule))
                return false;
            if (_stapleActivationTurn != context.Header.CurrentTurn)
            {
                _stapleActivationTurn = context.Header.CurrentTurn;
                _stapleActivations.Clear();
            }
            _stapleActivations.TryGetValue(cardId, out int used);
            if (used >= 3)
                return false;
            _stapleActivations[cardId] = used + 1;
            StapleNote(context, $"activate {CardNames.Get(cardId)}: {reason}");
            _lastStapleActivated = cardId;
            response = card.MainActivateFromIndex();
            return true;
        }

        /// <summary>Records a staple activated outside TryActivateStaple (e.g. chained in the priority window).</summary>
        protected void MarkStapleActivated(int cardId) => _lastStapleActivated = cardId;

        /// <summary>Free card advantage, first thing: Upstart Goblin, Charge of the Light Brigade, Reinforcement of the Army.</summary>
        protected async Task<IdleCmdResponse?> TryFreeCardAdvantageAsync(DecisionContext<IdleCmdData> context, IdleCmdData state)
        {
            IdleCmdResponse response;
            // Upstart Goblin gives them 1000 LP: when this turn's damage could be lethal, it waits for Main Phase 2 (user,
            // 2026-11-13, round-robin 38: "great rule. don't hold it indefinitely, but hold it for mp2 if you think you can kill
            // them that turn"; seen: Upstart first, then Swap Frog's hit left them at exactly 1000).
            if (TryGetActivable(state, UpstartGoblinId, out _) && await UpstartWaitsForLethalAsync(context) is string upstartWait &&
                Rule(DeckRules.UpstartWaitsForLethal))
            {
                StapleNote(context, $"hold Upstart Goblin for Main Phase 2: {upstartWait}");
                Hold(UpstartGoblinId, MainAction.Activate, DeckRules.UpstartWaitsForLethal);
            }
            else if (TryActivateStaple(context, state, UpstartGoblinId, "draw a card", out response, DeckRules.UpstartGoblin)) return response;
            // Round-robin 31 (user, 2026-11-13: "yes, but only as a last resort"): when we lose next turn anyway, the gamble draws
            // go: a set Jar of Greed now, and Allure of Darkness with no DARK once nothing else is left in hand.
            if (await WeLoseNextTurnAsync())
            {
                if ((state.ActivableCards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(JarOfGreedId)) && Rule(DeckRules.GambleWhenLosing) &&
                    TryActivateStaple(context, state, JarOfGreedId, "draw now: we lose next turn otherwise", out response))
                    return response;
                bool allureAlone = _ownHandIds.All(id => id == AllureOfDarknessId);
                if (_ownHandIds.Count(IsDarkMonsterId) == 0 && allureAlone && Rule(DeckRules.GambleWhenLosing) &&
                    TryActivateStaple(context, state, AllureOfDarknessId, "draw 2 with no DARK to banish (it takes the hand, which is empty): we lose next turn otherwise", out response))
                    return response;
            }
            if (TryActivateStaple(context, state, ChargeOfTheLightBrigadeId, "mill 3 and search a Lightsworn", out response, DeckRules.ChargeOfTheLightBrigade)) return response;
            if (TryActivateStaple(context, state, ReinforcementOfTheArmyId, "search a Warrior", out response, DeckRules.ReinforcementOfTheArmy)) return response;
            // Allure of Darkness: draw 2, then banish a DARK from the hand (the whole hand without one): only with a DARK we
            // can spare already in hand. Shared since 2026-11-10 (the Machina brain held it all game). A brain with its own
            // Allure rule (Blackwing) reaches it here first; its banish pick stays its own.
            int darks = _ownHandIds.Count(IsDarkMonsterId);
            if (darks > 0 && TryActivateStaple(context, state, AllureOfDarknessId,
                    $"draw 2, then banish a spare DARK ({darks} in hand)", out response, DeckRules.AllureOfDarkness))
                return response;
            // No DARK to banish: it would take the whole hand. Held (a line that plays it pays for it).
            if (darks == 0 && TryGetActivable(state, AllureOfDarknessId, out _))
                Hold(AllureOfDarknessId, MainAction.Activate, DeckRules.AllureOfDarkness);
            return null;
        }

        /// <summary>
        /// Why Upstart Goblin waits for Main Phase 2: Main Phase 1 with a Battle Phase ahead, and our attack this turn (the
        /// board plus the best monster we can still Normal Summon) reaches their LP but not with Upstart's 1000 added. Null to use it.
        /// </summary>
        private async Task<string?> UpstartWaitsForLethalAsync(DecisionContext<IdleCmdData> context)
        {
            if ((DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !context.StateData.CanBattlePhase || context.Header.CurrentTurn <= 1)
                return null;
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            var defenders = BoardAssessment.Defenders(await GetEnemyMonsterCardsAsync(), OpponentMemory);
            var attacks = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).Select(EffectiveAttack).ToList();
            int summon = (context.StateData.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => card.Level <= 4)
                .Select(card => card.Attack).DefaultIfEmpty(0).Max();
            if (summon > 0)
                attacks.Add(summon);
            if (attacks.Count == 0 || BoardAssessment.TurnsToWin(attacks, defenders, oppLp) != 1)
                return null;
            return BoardAssessment.TurnsToWin(attacks, defenders, oppLp + 1000) == 1
                ? null
                : $"our attack this turn ({string.Join(" + ", attacks)}) reaches their {oppLp} LP, but not {oppLp + 1000}";
        }

        // A summon trigger a Normal Summon sets off: "When this card is (Normal) Summoned", "... Normal or Special Summoned"; not
        // one only a Special Summon starts (Gladiator Beast Murmillo's, seen 2026-11-13 summoned "for its effect").
        private static readonly System.Text.RegularExpressions.Regex NormalSummonTriggerText = new(
            @"\b(when|if) this card is (normal |tribute )?summoned|\b(when|if) this card is normal (or special )?summoned",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        protected const int AllureOfDarknessId = 1475311;

        // Our hand, from the last field refresh (ids).
        private int[] _ownHandIds = Array.Empty<int>();

        private static bool IsDarkMonsterId(int id) =>
            (Search.SearchHost.Type(id) & 0x1) != 0 && Search.SearchHost.Attribute(id) == 0x20;

        /// <summary>What Allure's banish pick looks at: our hand, field and graveyard.</summary>
        protected sealed record AllureView(List<DynamicCard> Hand, List<DynamicCard> OurMonsters, List<int> GraveIds);

        /// <summary>
        /// Allure of Darkness' banish from the hand, lower = banished first: the DARK least useful with the cards we have now,
        /// not a fixed order (user, 2026-11-13: "it should depend on which is most useful with the other current cards you
        /// have"; Allure is a staple, so this is shared):
        ///  - a spare copy first;
        ///  - Gorz needs an empty field: cheap with monsters out;
        ///  - Dark Armed Dragon: the right banish when the graveyard has too many DARKs to ever trim back to 3 (DadStuck), a
        ///    bad one at or near 3;
        ///  - Kalut (a boost) needs a Blackwing to boost; Blizzard (a revive) needs a Blackwing in the graveyard;
        ///  - otherwise by ATK. Decks override for their own engines (Machina's Jinzo pieces).
        /// </summary>
        protected virtual double AllureBanishRank(DynamicCard card, AllureView v)
        {
            bool Named(DynamicCard c, string name) => CardNames.Get(c.ID).Contains(name, StringComparison.OrdinalIgnoreCase);
            if (v.Hand.Count(other => other.IsCode(card.ID)) > 1)
                return 0;
            if (card.IsCode(GorzId))
                return v.OurMonsters.Count > 0 ? AllureRankGorzWithMonsters : AllureRankGorz;
            if (card.IsCode(DarkArmedDragonId))
            {
                int darks = DadTrimRoom(v.GraveIds).Darks;
                return DadStuck(v.GraveIds) ? AllureRankDadStuck : Math.Abs(darks - 3) <= 1 ? AllureRankDadNearLive : darks == 1 ? AllureRankDadOneDark : AllureRankDadOther;
            }
            if (Named(card, "Kalut"))
                return v.OurMonsters.Any(c => c.IsFaceup() && Named(c, "Blackwing")) ||
                       v.Hand.Any(c => Named(c, "Blackwing") && !Named(c, "Kalut") && c.Level <= 4) ? AllureRankKalutUseful : AllureRankKalutIdle;
            if (Named(card, "Blizzard"))
                return v.GraveIds.Any(id => CardNames.Get(id).Contains("Blackwing") && (Search.SearchHost.Available && Search.SearchHost.Cards.Get(id) is { } revive ? revive.Level : 4) <= 4) ? AllureRankBlizzardUseful : AllureRankBlizzardIdle;
            if (Named(card, "Vayu"))
                return AllureRankVayu;
            return AllureRankOther - card.Attack / 10000.0;
        }

        protected const int DarkArmedDragonId = 65192027;

        /// <summary>
        /// DARK monsters in our graveyard, and how many our graveyard can trim off (a Vayu with a partner banishes 2,
        /// Plaguespreader leaving it 1, Necro Gardna banishing itself 1, a Blizzard revive target 1). Dark Armed Dragon is
        /// stuck when the DARKs minus the trims are still above 3 (user, 2026-11-13: then banishing DAD to Allure is right;
        /// at or near 3 it's a much worse choice).
        /// </summary>
        protected static (int Darks, int Trims) DadTrimRoom(IEnumerable<int> graveIds)
        {
            var ids = graveIds.ToList();
            bool Dark(int id) => (Search.SearchHost.Type(id) & 0x1) != 0 && Search.SearchHost.Attribute(id) == 0x20;
            bool Named(int id, string name) => CardNames.Get(id).Contains(name, StringComparison.OrdinalIgnoreCase);
            int darks = ids.Count(Dark);
            int vayus = ids.Count(id => Named(id, "Vayu"));
            int partners = ids.Count(id => Dark(id) && (Search.SearchHost.Type(id) & 0x1000) == 0 && !Named(id, "Vayu"));
            int trims = 2 * Math.Min(vayus, partners) + (ids.Any(id => Named(id, "Plaguespreader")) ? 1 : 0) +
                        (ids.Any(id => Named(id, "Necro Gardna")) ? 1 : 0) +
                        (ids.Any(id => Named(id, "Blackwing")) && ids.Any(id => Dark(id) && Named(id, "Blackwing")) ? 1 : 0);
            return (darks, trims);
        }

        /// <summary>Dark Armed Dragon can't get back to 3 DARKs from this graveyard (see DadTrimRoom).</summary>
        protected static bool DadStuck(IEnumerable<int> graveIds) => DadTrimRoom(graveIds) is var room && room.Darks - room.Trims > 3;

        protected async Task<IList<byte>?> AllureBanishPickAsync(DecisionContext<SelectCardData> context, bool force = false)
        {
            if (!force && _lastStapleActivated != AllureOfDarknessId || (DHint)context.StateData.SelectionHint != DHint.REMOVE)
                return null;
            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            if (cards.Length == 0 || cards.Any(card => card.CurrentCoordinates.Location != (byte)CardLocation.Hand))
                return null;
            _lastStapleActivated = 0;
            var view = new AllureView(await GetBotHandCardsAsync(), (await GetBotMonsterCardsAsync()).Where(c => c.ID > 0).ToList(),
                (await GetBotGraveyardCardsAsync()).Select(c => c.ID).ToList());
            var ranked = cards.Select((card, i) => (Card: card, Index: i, Rank: AllureBanishRank(card, view))).OrderBy(x => x.Rank).ToList();
            var pick = ranked.First();
            context.Bot.LogNote($"deck brain: Allure of Darkness banishes {CardNames.Get(pick.Card.ID)} (least useful now: " +
                                $"{string.Join(", ", ranked.Select(x => $"{CardNames.Get(x.Card.ID)} {x.Rank:0.#}"))})");
            return new List<byte> { (byte)pick.Index };
        }

        private static readonly System.Text.RegularExpressions.Regex StopsAttacksText = new(
            @"cannot (declare an |declare |)attack|negate (the|an|that) attack|end the battle phase|change (all|that|the attacking)[^.]{0,40}position",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Why Lyla should attack before using her effect (it turns her to defence), or null (user, 2026-11-13: "valhalla
        /// wasn't going to be able to stop an attack so lyla could have attacked, and then activated her effect in mp2"):
        /// Main Phase 1 with a battle ahead, Lyla able to attack and with something to hit, and none of their spells/traps
        /// able to stop the attack (all face-up, none that stops attacks: a set card might be Mirror Force, so that one goes first).
        /// </summary>
        private async Task<string?> LylaAttacksFirstAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, DynamicCard lyla)
        {
            if ((DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !state.CanBattlePhase || context.Header.CurrentTurn <= 1)
                return null;
            if (lyla.CurrentCoordinates.Location != (byte)CardLocation.MonsterZone || !lyla.IsFaceup() || !lyla.IsAttack() || lyla.Attacked)
                return null;
            var spells = await GetEnemySpellCardsAsync();
            var enemy = (await GetEnemyMonsterCardsAsync()).Where(card => card.ID != 0 || !card.IsFaceup()).ToList();
            int Power(DynamicCard card) => !card.IsFaceup() ? OpponentMemory.FaceDownDefense(card) : card.IsAttack() ? card.Attack : card.Defense;
            var prey = enemy.Where(card => lyla.Attack > Power(card)).OrderByDescending(OpponentThreat).FirstOrDefault();
            if (enemy.Count > 0 && prey.CurrentCoordinates.Location == 0)
                return null;
            string hit = enemy.Count == 0 ? "a direct hit" : $"{(prey.IsFaceup() ? CardNames.Get(prey.ID) : "their face-down")}";
            if (spells.Any(card => !card.IsFaceup() || card.ID <= 0))
                return await LylaGambleAsync(context, lyla, enemy, prey, hit);
            foreach (var card in spells)
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && StopsAttacksText.IsMatch(info.CardText ?? ""))
                    return null;
            return $"their spells/traps ({string.Join(", ", spells.Select(card => CardNames.Get(card.ID)))}) can't stop her attack: she hits {hit} first";
        }

        /// <summary>
        /// Before ending the turn with more than 6 cards in hand (the End Phase discards the rest for nothing): a play that
        /// keeps a card instead. Spells/traps are set, traps first; then the deck's own last resort (DeckHandLimitPlay). Null
        /// when the hand is within the limit or nothing helps (the discard then takes the card that loses least).
        /// </summary>
        protected async Task<IdleCmdResponse?> HandLimitPlayAsync(DecisionContext<IdleCmdData> context)
        {
            var hand = await GetBotHandCardsAsync();
            int over = hand.Count - 6;
            if (over <= 0 || !Rule(DeckRules.AvoidHandLimitDiscard))
                return null;
            var sets = (context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.ID > 0)
                .OrderBy(card => card.HasType(CardType.Trap) ? 0 : 1);
            foreach (var card in sets)
            {
                if (!await SharedSpellSetAllowedAsync(card))
                    continue;
                context.Bot.LogNote($"deck brain: set {CardNames.Get(card.ID)}: {hand.Count} cards in hand, the End Phase would discard {over} for nothing");
                return card.SetSpellTrap();
            }
            if (await DeckHandLimitPlayAsync(context, over) is IdleCmdResponse deckPlay)
                return deckPlay;
            return null;
        }

        /// <summary>A deck's own last resort against the hand-limit discard (Machina: summon a Fortress). Default none.</summary>
        protected virtual Task<IdleCmdResponse?> DeckHandLimitPlayAsync(DecisionContext<IdleCmdData> context, int over) =>
            Task.FromResult<IdleCmdResponse?>(null);

        /// <summary>From this many LP of ours, Solemn Judgment is set after our other traps (early game). Tune from logs.</summary>
        [Tunable("From this many LP of ours, Solemn Judgment is set after our other traps (early game). Tune from logs.")]
        public int SolemnEarlyGameLifePoints { get; set; } = 6000;

        /// <summary>What an unused Normal Summon is worth (card units): a monster in hand worth less than this gets set rather than
        /// waste it. Tune from logs.</summary>
        [Tunable("What an unused Normal Summon is worth (card units): a monster in hand worth less than this gets set rather than waste it. Tune from logs.")]
        public double UnusedNormalSummonValue { get; set; } = 1.0;

        /// <summary>Chance to gamble Lyla's attack into their set card when we don't need it gone much. Tune from logs.</summary>
        [Tunable("Chance to gamble Lyla's attack into their set card when we don't need it gone much. Tune from logs.")]
        public double LylaGambleChance { get; set; } = 0.5;

        private (int Turn, string? Why) _lylaGamble = (-1, null);

        /// <summary>
        /// The greedy Lyla line (user, 2026-11-13): Lyla our only monster, they have a monster she beats and a set card.
        /// Attack first, then her effect in Main Phase 2: if it works she takes two cards instead of one; if their trap hits
        /// her instead, it's a similar outcome (she's expendable once her effect is used anyway). Rolled once a turn,
        /// weighted by how much we need their backrow gone: never with a big play waiting on it (as with MST bait), less
        /// with each strong summon we could still make.
        /// </summary>
        private async Task<string?> LylaGambleAsync(DecisionContext<IdleCmdData> context, DynamicCard lyla, List<DynamicCard> enemy,
            DynamicCard prey, string hit)
        {
            int turn = context.Header.CurrentTurn;
            if (_lylaGamble.Turn == turn)
                return _lylaGamble.Why;
            _lylaGamble = (turn, null);
            if (enemy.Count == 0 || prey.CurrentCoordinates.Location == 0 ||
                (await GetBotMonsterCardsAsync()).Any(card => card.ID > 0 && !SameZone(card, lyla)))
                return null;
            if (await BigPlayPendingAsync(context) is string bigPlay)
            {
                StapleNote(context, $"no Lyla gamble: their backrow must go for our play ({bigPlay})");
                return null;
            }
            int strong = await StrongSummonsAvailableAsync(context);
            double chance = LylaGambleChance * Math.Max(0, 1 - LylaGambleDropPerStrongSummon * strong);
            double roll = RollAt;
            bool go = roll < chance;
            StapleNote(context, $"Lyla gamble (attack {hit}, effect in Main Phase 2: two cards if their set card doesn't stop her)? chance {chance:P0}" +
                                $"{(strong > 0 ? $" ({strong} strong summon(s) of ours would like their backrow gone)" : "")}, rolled {roll:0.00} -> {(go ? "gamble" : "effect first")}");
            if (go)
                _lylaGamble = (turn, $"a gamble: she hits {hit} first, then takes a spell/trap in Main Phase 2 (their set card may stop her, a similar outcome)");
            return _lylaGamble.Why;
        }

        private readonly Random _lylaRandom = new();

        /// <summary>
        /// Backrow removal: Heavy Storm when it pushes a big play through (see HeavyStormReasonAsync), Lyla on one of their
        /// Spell/Traps, MST to clear the way for a big play (committing to it). Not into a negator (Stardust).
        /// </summary>
        protected async Task<IdleCmdResponse?> TryBackrowRemovalAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, int theirSpells, int ourOtherSpells)
        {
            if (TryGetActivable(state, HeavyStormId, out _) &&
                await HeavyStormReasonAsync(context, theirSpells, ourOtherSpells) is string stormWhy &&
                await DestructionNegatedAsync(context, HeavyStormId) == null &&
                TryActivateStaple(context, state, HeavyStormId, stormWhy, out var response, DeckRules.HeavyStorm))
                return response;
            if (theirSpells > 0 && TryGetActivable(state, LylaId, out var lyla) && await LylaAttacksFirstAsync(context, state, lyla) is string attackFirst &&
                Rule(DeckRules.LylaAfterBattle))
            {
                StapleNote(context, $"Lyla's effect waits for Main Phase 2: {attackFirst}");
                Hold(LylaId, MainAction.Activate, DeckRules.LylaAfterBattle);
            }
            else if (theirSpells > 0 && TryGetActivable(state, LylaId, out _) &&
                await DestructionNegatedAsync(context, LylaId) == null &&
                TryActivateStaple(context, state, LylaId, "destroy an opponent S/T", out response, DeckRules.LylaDestroy))
                return response;
            if (theirSpells > 0 && TryGetActivable(state, MysticalSpaceTyphoonId, out _) &&
                await BigPlayPendingAsync(context) is string mstPlay &&
                await DestructionNegatedAsync(context, MysticalSpaceTyphoonId) == null &&
                TryActivateStaple(context, state, MysticalSpaceTyphoonId, $"clear a spell/trap for a big play: {mstPlay}", out response, DeckRules.MstForBigPlay))
            {
                CommitToBigPlay(context.Header.CurrentTurn);
                return response;
            }
            return null;
        }

        /// <summary>
        /// Card Trooper: mill 3 for graveyard fuel (and 1900 ATK) while the deck can take it; with the deck too thin to
        /// mill, it stays at 400 ATK, so it goes to defence instead.
        /// </summary>
        protected async Task<IdleCmdResponse?> TryCardTrooperAsync(DecisionContext<IdleCmdData> context, IdleCmdData state)
        {
            // (Only reads the deck when Card Trooper can do something this turn.)
            bool inPlay = TryGetActivable(state, CardTrooperId, out _) || TryGetReposable(state, CardTrooperId, out _);
            if (!inPlay)
                return null;
            bool canMill = await TrooperCanMillAsync(context);
            if (canMill && TryActivateStaple(context, state, CardTrooperId, "mill for graveyard fuel", out var response, DeckRules.TrooperMill))
                return response;
            if (!canMill && Rule(DeckRules.TrooperDefenceWhenThin) && TryGetReposable(state, CardTrooperId, out var low) && low.IsFaceup() && low.IsAttack())
            {
                StapleNote(context, $"Card Trooper to defence: the deck is too thin to mill (under {MinDeckToMill} cards), so it stays at 400 ATK");
                return low.Reposition();
            }
            return null;
        }

        /// <summary>No good play this turn (Main Phase 1, Normal Summon unused): draw with Legacy of Yata-Garasu / Jar of Greed to look for one.</summary>
        protected IdleCmdResponse? TryDrawWhenNoPlay(DecisionContext<IdleCmdData> context, IdleCmdData state, bool normalSummonUsed)
        {
            if ((DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && !normalSummonUsed &&
                (TryActivateStaple(context, state, LegacyOfYataGarasuId, "no good play this turn: draw to look for one", out var response, DeckRules.DrawWhenNoPlay) ||
                 TryActivateStaple(context, state, JarOfGreedId, "no good play this turn: draw to look for one", out response, DeckRules.DrawWhenNoPlay)))
                return response;
            return null;
        }

        /// <summary>
        /// Card Trooper asks how many cards to send (1-3): always the most. Milling fewer is just bad (less ATK, less
        /// graveyard fuel); if milling isn't wanted, it isn't activated at all.
        /// </summary>
        public override async Task<int> OnAnnounceNumberAsync(DecisionContext<AnnounceNumberData> context, CancellationToken ct = default)
        {
            var options = context.StateData.SelectableOptions ?? Array.Empty<int>();
            if (_lastStapleActivated == CardTrooperId && options.Length > 0)
            {
                await PrepareContextAsync(context, ct);
                StapleNote(context, $"Card Trooper: send {options.Max()} cards");
                return options.Max();
            }
            return await base.OnAnnounceNumberAsync(context, ct);
        }

        // ------------------------------------------------------------------ deck hooks for the summon / set choice

        /// <summary>Safe Normal Summon order among the candidates (lower first; ties go to the bigger ATK). Default: no preference.</summary>
        protected virtual int SafeSummonPriority(DynamicCard card, List<DynamicCard> hand) => 0;

        /// <summary>Monsters worth Setting because losing them isn't a straight -1 (Ryko flips, Necro works from the GY), first = preferred.</summary>
        protected virtual int[] ValueSetIds => DefaultValueSets;

        private static readonly int[] DefaultValueSets = { RykoId };

        /// <summary>A value-set monster below this ATK is never the "safe" face-up Normal Summon: it's set instead.</summary>
        [Tunable("A value-set monster below this ATK is never the \"safe\" face-up Normal Summon: it's set instead.")]
        public int ValueSetSummonAttack { get; set; } = 1500;

        protected static int StaplePriorityIndex(int[] priority, DynamicCard card)
        {
            int index = Array.FindIndex(priority, id => card.IsCode(id));
            return index < 0 ? priority.Length : index;
        }

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

        protected async Task<IdleCmdResponse?> ChooseSafeSummonOrSet(
            DecisionContext<IdleCmdData> context,
            IdleCmdData state,
            List<DynamicCard> enemyMonsters,
            List<DynamicCard> botMonsters)
        {
            // For the tribute-summon check below: their spell/trap count, and our Level 5-6 monsters whose summon effect removes a card.
            _enemySpellCountNow = (await GetEnemySpellCardsAsync()).Count;
            int ourBestNow = botMonsters.Where(m => m.ID > 0 && m.IsFaceup()).Select(EffectiveAttack).DefaultIfEmpty(0).Max();
            _summonRemovalIds.Clear();
            foreach (var card in (state.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => card.Level is 5 or 6))
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && OnSummonTriggerText.IsMatch(info.CardText ?? "") &&
                    CardRemovalText.IsMatch(info.CardText ?? "") || card.ID > 0 && await TextMatchesAsync(card.ID, PlaceOnDeckText))
                    _summonRemovalIds.Add(card.ID);
            var summonable = (state.SummonableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.ID > 0)
                // Flip monsters (Ryko) only do anything when Set and flipped: never Normal Summon them face-up.
                .Where(card => !card.HasType(CardType.Flip))
                // Level 5+ only without tributes (Sirocco on an empty field); never tribute away our monsters, except a Lyla
                // we'd rather use up (spent after her effect, or her mill would deck us out): a Level 5-6 off her one tribute.
                // Round-robin 50 (user, 2026-11-13: "yes absolutely"): also off any cheap tribute (TributeCost: Treeborn, a monster
                // with a graveyard effect like Vayu, an expendable monster, a small token, a stolen one) when the new monster is stronger.
                .Where(card => card.Level <= 4 || (card.Level == 5 && botMonsters.Count == 0) ||
                               card.Level is 5 or 6 && botMonsters.Any(LylaToUseUp) ||
                               card.Level is 5 or 6 && CheapTribute(card).ID > 0 && Rule(DeckRules.TributeExpendable) ||
                               card.Level >= 7 && CheapTributes(card, 2).Count == 2 && Rule(DeckRules.TributeExpendable))
                .ToList();
            // A cheap tribute; or any weaker monster when the new one deals with something we otherwise can't (user, 2026-11-13:
            // "sometimes it will be correct to tribute a non-expendable monster if the monarch's effect (or atk) gets over
            // something that you otherwise can't deal with").
            bool Answers(DynamicCard summon) =>
                enemyMonsters.Any(m => m.IsFaceup() && m.GetRealPower() < summon.Attack && m.GetRealPower() >= ourBestNow) ||
                _summonRemovalIds.Contains(summon.ID) && enemyMonsters.Count + _enemySpellCountNow > 0;
            DynamicCard CheapTribute(DynamicCard summon) => botMonsters
                .Where(m => m.ID > 0 && EffectiveAttack(m) < summon.Attack && (TributeCost(m) <= ExpendableTributeCost || Answers(summon)))
                .OrderBy(TributeCost).FirstOrDefault();
            if (summonable.Any(card => card.Level is 5 or 6) && botMonsters.FirstOrDefault(LylaToUseUp) is { ID: > 0 } lylaTribute)
                PlanTributes(context.Header.CurrentTurn, new[] { lylaTribute },
                    $"a Level 5-6 off Lyla ({(DeckTooSmallForLyla ? "her mill would deck us out" : "spent after her effect")})");
            else if (summonable.FirstOrDefault(card => card.Level is 5 or 6) is { ID: > 0 } tributeSummon && CheapTribute(tributeSummon) is { ID: > 0 } cheapTribute)
                PlanTributes(context.Header.CurrentTurn, new[] { cheapTribute },
                    $"{CardNames.Get(tributeSummon.ID)} off {CardNames.Get(cheapTribute.ID)} (cost {TributeCost(cheapTribute):0.#}: expendable)");
            else if (summonable.FirstOrDefault(card => card.Level >= 7) is { ID: > 0 } bigSummon && CheapTributes(bigSummon, 2) is { Count: 2 } pair)
                PlanTributes(context.Header.CurrentTurn, pair,
                    $"{CardNames.Get(bigSummon.ID)} off {string.Join(" + ", pair.Select(t => CardNames.Get(t.ID)))} (two cheap tributes)");
            // Two tributes for a Level 7+: the cheapest pair, each weaker than the new monster (cheap, or it answers something).
            List<DynamicCard> CheapTributes(DynamicCard summon, int count) => botMonsters
                .Where(m => m.ID > 0 && EffectiveAttack(m) < summon.Attack && (TributeCost(m) <= ExpendableTributeCost || Answers(summon)))
                .OrderBy(TributeCost).Take(count).ToList();
            var settable = (state.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.ID > 0 && card.Level <= 4)
                .Where(card => !card.IsCode(CardTrooperId))
                .ToList();

            if (summonable.Count == 0 && settable.Count == 0)
                return null;

            int strongestEnemy = enemyMonsters
                .Where(card => card.IsFaceup() && card.IsAttack())
                .Select(card => card.Attack)
                .DefaultIfEmpty(0)
                .Max();

            // Summon something that beats (or meets nothing to fight) the opponent's strongest face-up attacker, and is a
            // real attacker: a weak monster (alone an Almost None clock or none, e.g. Plaguespreader at 400) face-up is
            // just exposed. Those go through the value-set logic below instead.
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            bool Weak(DynamicCard card) => BoardAssessment.SpeedOf(BoardAssessment.TurnsToWin(
                new[] { EffectiveAttack(card) }, Array.Empty<BoardAssessment.Defender>(), oppLp)) is ClockSpeed.AlmostNone or ClockSpeed.None;
            // The deck's summon order (e.g. Dark Grepher with nothing to discard goes last for Vayu).
            var safeHand = await GetBotHandCardsAsync();
            int SafePriority(DynamicCard card) => SafeSummonPriority(card, safeHand);
            // A small value-set monster (Shield Warrior, Necro Gardna, Vayu...) is worth more set than face-up: set, it bluffs a
            // Ryko and still reaches the graveyard where its effect is (user, 2026-11-11: "summoning shield warrior in atk
            // doesn't make much sense - you're better off bluffing that it's ryko").
            // Low LP (round-robin 3; 2026-11-13: Substitoad summoned at 100 LP "because 100 beats their strongest attacker, 0",
            // and an 800 attacker killed us through it): a monster that would be a lethal hole in attack position isn't summoned.
            var exposure = await LowLpExposureLimitAsync();
            bool Exposed(DynamicCard card) => exposure is { } limit && EffectiveAttack(card) <= limit.MaxAttack;
            if (exposure is { } shown && summonable.Any(Exposed) && Rule(DeckRules.LowLpNoExposure))
            {
                StapleNote(context, $"don't summon {string.Join(", ", summonable.Where(Exposed).Select(card => CardNames.Get(card.ID)))} in attack position: {shown.Why}");
                foreach (var card in summonable.Where(Exposed))
                    Hold(card.ID, MainAction.Summon, DeckRules.LowLpNoExposure);
                summonable = summonable.Where(card => !Exposed(card)).ToList();
            }
            // Plaguespreader is summoned face-up only as the first step of its synchro routine (TryPlagueSummonSynchroAsync), never
            // as an attacker; on its own it's set (user, 2026-11-13).
            var plagueWanted = summonable.FirstOrDefault(card => card.IsCode(PlaguespreaderZombieId) && card.Attack > strongestEnemy && !Weak(card));
            if (summonable.Any(card => card.IsCode(PlaguespreaderZombieId)) && Rule(DeckRules.PlagueSummonForSynchro))
            {
                foreach (var card in summonable.Where(card => card.IsCode(PlaguespreaderZombieId)))
                    Hold(card.ID, MainAction.Summon, DeckRules.PlagueSummonForSynchro);
                summonable = summonable.Where(card => !card.IsCode(PlaguespreaderZombieId)).ToList();
            }
            else
                plagueWanted = default;
            var safeSummon = summonable
                .Where(card => card.Attack > strongestEnemy && !Weak(card) && !(card.IsCode(ValueSetIds) && card.Attack < ValueSetSummonAttack))
                .OrderBy(SafePriority)
                .ThenByDescending(card => card.Attack)
                .FirstOrDefault();
            if (safeSummon.ID > 0 && Rule(DeckRules.SafeSummon))
            {
                StapleNote(context, $"normal summon {CardNames.Get(safeSummon.ID)} ({safeSummon.Attack} ATK beats strongest opponent attacker {strongestEnemy})");
                return safeSummon.NormalSummon();
            }
            // Plaguespreader would have been that summon: just getting it into play from the hand is a set, in Main Phase 2.
            if (plagueWanted.ID > 0 && (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main2 &&
                (state.MonsterSetableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card => card.IsCode(PlaguespreaderZombieId)) is { ID: > 0 } plagueSet)
            {
                StapleNote(context, "set Plaguespreader Zombie: into play from the hand without a synchro now, so set (in Main Phase 2), not summoned");
                return plagueSet.SetMonster();
            }

            // A face-up defence monster of theirs with an effect that our summon destroys in battle this turn (user, 2026-11-12:
            // "I would've attacked lyla with jinzo returner here a long time ago": Lyla in defence at 200, our 600 kept in hand
            // because it didn't beat their 2500). The kill is worth exposing the monster, even next to a bigger attacker.
            bool battleNow = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase && context.Header.CurrentTurn > 1;
            // (Not a Treeborn Frog that would just come back: TreebornAttackPointless, judged for a lone new attacker.)
            int theirSpellTraps = (await GetEnemySpellCardsAsync()).Count;
            var ourAttackersNow = botMonsters.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).Select(card => card.Attack).ToList();
            var prey = enemyMonsters.Where(card => card.IsFaceup() && !card.IsAttack() && card.ID > 0 && !card.HasType(CardType.Normal) &&
                                                   TreebornAttackPointless(default, card, enemyMonsters, ourAttackersNow.Append(summonable.Select(s => s.Attack).DefaultIfEmpty(0).Max()).ToList(), theirSpellTraps) == null)
                .OrderByDescending(OpponentThreat).FirstOrDefault();
            var killer = prey.ID > 0 && battleNow
                ? summonable.Where(card => card.Attack > prey.Defense && !AttacksOnlyDirectlyOnOmega(card.ID)).OrderBy(card => card.Attack).FirstOrDefault()
                : default;
            if (killer.ID > 0 && Rule(DeckRules.SummonToKill))
            {
                StapleNote(context, $"normal summon {CardNames.Get(killer.ID)}: it destroys {CardNames.Get(prey.ID)} ({prey.Defense} DEF) in battle this turn");
                CommitAttack(context.Header.CurrentTurn, killer.ID, prey, $"{CardNames.Get(killer.ID)} was summoned to destroy {CardNames.Get(prey.ID)} in battle");
                return killer.NormalSummon();
            }

            // Round-robin 34a (user, 2026-11-13): a monster that destroys one of their monsters in battle this turn, attack
            // position too, when the trade is even or better. If it's smaller than their biggest and nothing else removes that,
            // it dies next turn: counted (our card for theirs), which is usually still fine.
            var oursUp = botMonsters.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).ToList();
            if (battleNow)
            {
                foreach (var candidate in summonable.OrderBy(SafePriority).ThenBy(card => card.Attack))
                {
                    // Their monster it beats that none of ours already beats (one attack per target).
                    var victim = enemyMonsters.Where(card => card.IsFaceup() && card.ID > 0 && card.GetRealPower() < EffectiveAttack(candidate) &&
                                                             !oursUp.Any(own => EffectiveAttack(own) > card.GetRealPower()) &&
                                                             TreebornAttackPointless(default, card, enemyMonsters, ourAttackersNow.Append(candidate.Attack).ToList(), theirSpellTraps) == null)
                        .OrderByDescending(OpponentThreat).FirstOrDefault();
                    if (victim.ID <= 0)
                        continue;
                    var survivor = enemyMonsters.Where(card => card.IsFaceup() && card.IsAttack() && !SameZone(card, victim) && card.Attack > candidate.Attack)
                        .OrderByDescending(card => card.Attack).FirstOrDefault();
                    // Even or better: their monster is worth at least ours (ours expendable, or theirs as strong).
                    bool even = IsExpendable(candidate) || victim.GetRealPower() >= candidate.Attack - 500 || victim.HasType(CardType.Effect);
                    if (!even || !Rule(DeckRules.SummonToTrade))
                        continue;
                    StapleNote(context, $"normal summon {CardNames.Get(candidate.ID)}: it destroys {CardNames.Get(victim.ID)} ({victim.GetRealPower()}) in battle this turn" +
                                        (survivor.ID > 0 ? $"; their {CardNames.Get(survivor.ID)} ({survivor.Attack}) probably kills it next turn: our card for theirs, even" : ""));
                    CommitAttack(context.Header.CurrentTurn, candidate.ID, victim, $"{CardNames.Get(candidate.ID)} was summoned to destroy {CardNames.Get(victim.ID)} in battle");
                    return candidate.NormalSummon();
                }
            }

            // Round-robin 34b: a monster whose summon effect is worth a card (Armageddon Knight's DARK, Gearframe's search) is
            // summoned for it, even if it doesn't beat their monster: the effect pays for it.
            var handNow = await GetBotHandCardsAsync();
            foreach (var candidate in summonable.OrderBy(SafePriority))
            {
                if (await CardDatabase.GetAsync(candidate.ID) is not DBCardInfo info || !NormalSummonTriggerText.IsMatch(info.CardText ?? "") ||
                    await OnSummonEffectUselessAsync(candidate, handNow) || !Rule(DeckRules.SummonForEffect))
                    continue;
                StapleNote(context, $"normal summon {CardNames.Get(candidate.ID)} for its summon effect (it pays for the card even if it doesn't beat their monsters)");
                return candidate.NormalSummon();
            }

            // Sets and blockers wait for Main Phase 2 when we attack this turn: setting first gains nothing, and the
            // battle's outcome may change what's worth setting.
            if (await SetsWaitForMainPhase2Async(context) && Rule(DeckRules.HoldMonsterSetsForBattle))
            {
                if (summonable.Count + settable.Count > 0)
                    StapleNote(context, "hold monster sets until Main Phase 2");
                foreach (var card in settable)
                    Hold(card.ID, MainAction.SetMonster, DeckRules.HoldMonsterSetsForBattle);
                return null;
            }

            // Gorz in hand and an empty field: Gorz is the defence. A set card or blocker would switch it off.
            if (await KeepGorzLiveAsync(context, enemyMonsters) && Rule(DeckRules.KeepGorzLive))
            {
                StapleNote(context, "keep monsters in hand: the field is empty and Gorz is in hand, so it stays live");
                foreach (var card in settable)
                    Hold(card.ID, MainAction.SetMonster, DeckRules.KeepGorzLive);
                foreach (var card in summonable)
                    Hold(card.ID, MainAction.Summon, DeckRules.KeepGorzLive);
                return null;
            }

            // Monsters that gain value when Set and destroyed/flipped are worth setting anyway (not a straight -1).
            var valueSet = settable
                .Where(card => card.IsCode(ValueSetIds))
                .OrderBy(card => StaplePriorityIndex(ValueSetIds, card))
                .FirstOrDefault();
            // Against Grand Mole a set monster does nothing: Mole attacks it and returns it to our hand (Ryko included).
            string? bouncerHold = settable.Count > 0 ? SetIntoBouncerHold(context) : null;
            // Against Goyo Guardian a set monster it can beat is just taken: it destroys it and summons it to their field.
            var stealer = enemyMonsters.FirstOrDefault(card => StealsBattleVictim(card) && card.IsAttack());
            if (valueSet.ID > 0 && bouncerHold != null && Rule(DeckRules.NoSetIntoBouncer))
            {
                StapleNote(context, $"don't set {CardNames.Get(valueSet.ID)}: {bouncerHold}");
                Hold(valueSet.ID, MainAction.SetMonster, DeckRules.NoSetIntoBouncer);
            }
            else if (valueSet.ID > 0 && stealer.ID > 0 && stealer.Attack > valueSet.Defense && Rule(DeckRules.NoSetIntoStealer))
            {
                StapleNote(context, $"don't set {CardNames.Get(valueSet.ID)}: {CardNames.Get(stealer.ID)} would destroy it by battle and take it");
                Hold(valueSet.ID, MainAction.SetMonster, DeckRules.NoSetIntoStealer);
            }
            else if (valueSet.ID > 0 && Rule(DeckRules.ValueSet))
            {
                StapleNote(context, $"set {CardNames.Get(valueSet.ID)} face-down: it gains value when flipped/destroyed (or works from the graveyard)");
                return valueSet.SetMonster();
            }

            // Round-robin 34c: damage taken on an empty field two of our turns running: holding every monster is wrong, develop
            // the best one (the strongest that isn't a lethal hole; set when it can only be a hole face-up).
            if (BleedingTurns >= 2 && Rule(DeckRules.DevelopWhenBleeding))
            {
                var best = summonable.Where(card => !Weak(card)).OrderByDescending(card => card.Attack).FirstOrDefault();
                if (best.ID > 0)
                {
                    StapleNote(context, $"normal summon {CardNames.Get(best.ID)}: we've taken damage on an empty field {BleedingTurns} turns running, holding isn't working");
                    return best.NormalSummon();
                }
                var set = settable.OrderByDescending(card => card.Defense).FirstOrDefault();
                if (set.ID > 0)
                {
                    StapleNote(context, $"set {CardNames.Get(set.ID)}: we've taken damage on an empty field {BleedingTurns} turns running, holding isn't working");
                    return set.SetMonster();
                }
            }

            // Round-robin 5 / 14b (user, 2026-11-13: "a last resort behaviour"): their board (and hand) is likely lethal next
            // turn and a blocker makes it unlikely: set the best one. D.D. Crow only when there's no other monster ("any other
            // monster would be a better set").
            if (settable.Count > 0 || _neverSummonSet.ID > 0)
            {
                var (lethal, withBlocker, lethalWhy) = await OpponentLethalRiskAsync(enemyMonsters);
                if (lethal >= LastResortLethalChance && withBlocker <= lethal - LastResortBlockerDrop && Rule(DeckRules.LastResortBlocker))
                {
                    var blocker = settable.OrderByDescending(card => card.Defense).FirstOrDefault();
                    if (blocker.ID <= 0)
                        blocker = _neverSummonSet;
                    StapleNote(context, $"last resort: set {CardNames.Get(blocker.ID)} as a blocker: their lethal chance {lethal:P0}, {withBlocker:P0} with a blocker ({lethalWhy})");
                    return blocker.SetMonster();
                }
            }

            // The last chance this turn (Main Phase 2, or Main Phase 1 with no Battle Phase to come) with the Normal Summon
            // unused: a whole turn's Normal Summon thrown away is a real loss (user, 2026-11-13: "missing a normal summon/set
            // completely is a really bad idea and should be punished"). Not a blocker to stay alive: the card that loses least
            // by it, the one that pays off in the graveyard or is expendable first (Plaguespreader, a flip, Vayu), else the
            // least valuable in hand, set face-down. Kept when holding it is worth more than the Normal Summon.
            bool lastChance = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main2 ||
                              (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && !context.StateData.CanBattlePhase;
            if (lastChance && !NormalSummonUsed(context.Header) && settable.Count > 0 && Rule(DeckRules.UseNormalSummon))
            {
                var ranked = new List<(DynamicCard Card, double Value, string Why)>();
                foreach (var card in settable)
                {
                    var (value, why) = await HandCardValueAsync(card, botMonsters);
                    if (HasGraveyardEffect(card) || IsExpendable(card) || card.HasType(CardType.Flip))
                        value -= SetterHandDiscount;
                    ranked.Add((card, value, why));
                }
                var pick = ranked.OrderBy(x => x.Value).First();
                if (pick.Value < UnusedNormalSummonValue)
                {
                    StapleNote(context, $"set {CardNames.Get(pick.Card.ID)}: the Normal Summon would otherwise go unused this turn " +
                                        $"(it costs least to commit: {pick.Value:0.##} card(s) in hand, {pick.Why})");
                    return pick.Card.SetMonster();
                }
                StapleNote(context, $"Normal Summon unused: every monster in hand is worth more kept ({CardNames.Get(pick.Card.ID)} {pick.Value:0.##})");
            }

            // Anything else would just be thrown in front of an attack. No blockers: setting or summoning a monster only to
            // survive a turn is never worth it (user, 2026-11-10: "if you're setting blockers to stay alive you've probably
            // lost anyway"). Keep the card for a play.
            if (summonable.Count + settable.Count > 0)
                StapleNote(context, "keep monsters in hand: nothing they can beat, and no blockers: keep them for a play");
            foreach (var card in settable)
                Hold(card.ID, MainAction.SetMonster, DeckRules.NoBlockers);
            foreach (var card in summonable)
                Hold(card.ID, MainAction.Summon, DeckRules.NoBlockers);
            return null;
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
                Hold(RykoId, MainAction.SetMonster, DeckRules.NoRykoIntoFaceDown);
                return null;
            }
            if (SetIntoBouncerHold(context) is string bouncerHold && Rule(DeckRules.NoSetIntoBouncer))
            {
                StapleNote(context, $"don't set Ryko first: {bouncerHold}");
                Hold(RykoId, MainAction.SetMonster, DeckRules.NoSetIntoBouncer);
                return null;
            }
            if (await KeepGorzLiveAsync(context, enemyMonsters))
                return null;
            return "whatever attacks it flips it, and it destroys a card";
        }

        /// <summary>Whether our deck is big enough for Card Trooper to mill (see <see cref="MinDeckToMill"/>).</summary>
        protected async Task<bool> TrooperCanMillAsync<T>(DecisionContext<T> context)
        {
            int deck = (await CurrentField.GetBotField()).OwnSide.Main?.Length ?? 0;
            if (deck >= MinDeckToMill || !Rule(DeckRules.TrooperDeckGuard))
                return true;
            // Never say never: if Card Trooper at 1900 wins this turn and the deck has the 3 cards to mill, mill.
            if (deck >= 3 && (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && IsOwnTurn(context.Header))
            {
                var attackers = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                    .Select(EffectiveAttack).ToList();
                var defenders = BoardAssessment.Defenders(await GetEnemyMonsterCardsAsync(), OpponentMemory);
                if (BoardAssessment.TurnsToWin(attackers, defenders, await CurrentField.GetBotLifePoints(isOwnSide: false)) == 1)
                {
                    StapleNote(context, $"Card Trooper mills with only {deck} cards left in the deck: at 1900 it wins this turn");
                    return true;
                }
            }
            StapleNote(context, $"don't use Card Trooper: {deck} cards left in the deck (under {MinDeckToMill})");
            return false;
        }

        /// <summary>
        /// Card Trooper doesn't mill at all with fewer cards than this in the deck (it goes to defence instead). Low on
        /// purpose: running close to deck-out is fine.
        /// </summary>
        [Tunable("Card Trooper doesn't mill at all with fewer cards than this in the deck (it goes to defence instead). Low on purpose: running close to deck-out is fine.")]
        private static int MinDeckToMill = 5;

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
            if (DeckTooSmallForLyla && (state.SummonableCards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(LylaId)))
            {
                StapleNote(context, "don't summon Lyla: our deck is too small for her End Phase mill");
                state.SummonableCards = state.SummonableCards!.Where(card => !card.IsCode(LylaId)).ToArray();
            }
            // (A union is fine only if it can equip itself away: some Machine of ours with no union on it yet; a monster
            // holds one union at a time.)
            bool unionCanEquipAway = await UnionHostFreeAsync(botMonsters);
            var exposing = (state.SummonableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.ID > 0 && !(card.HasType(CardType.Union) && unionCanEquipAway) && SummonExposesChimeratech(card, botMonsters) != null).ToList();
            if (exposing.Count > 0 && await OpponentMayHaveChimeratechAsync(context) && Rule(DeckRules.NoChimeratechExposure))
            {
                StapleNote(context, $"don't summon {string.Join(", ", exposing.Select(card => CardNames.Get(card.ID)))} face-up: {SummonExposesChimeratech(exposing[0], botMonsters)}");
                state.SummonableCards = state.SummonableCards!.Where(card => !exposing.Any(e => e.ID == card.ID)).ToArray();
            }

            var developers = (state.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => card.ID > 0).ToList();

            // Card Trooper is especially good into a face-down monster (likely Ryko): if it's destroyed, we draw a card.
            // A face-down of theirs that's likely a Ryko (from the deck lists their cards fit; a deck without Ryko makes it not).
            bool opponentHasFaceDown = enemyMonsters.Any(card => !card.IsFaceup() && LikelyRyko(card));
            if (opponentHasFaceDown && developers.TryGetByID(CardTrooperId, out var trooperFirst) && Rule(DeckRules.TrooperIntoFaceDown))
            {
                StapleNote(context, "normal summon Card Trooper: mill 3 to become 1900 ATK, and it draws a card if the face-down (Ryko?) destroys it");
                return trooperFirst.NormalSummon();
            }

            // A set Ryko is usually the best first-turn play: whatever attacks it flips it, and it destroys a card.
            var ryko = (state.MonsterSetableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card => card.IsCode(RykoId));
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
                developers.TryGetByID(LylaId, out var lyla) && Rule(DeckRules.SummonLyla))
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
            if (developers.TryGetByID(CardTrooperId, out var trooper) && Rule(DeckRules.SummonTrooper))
            {
                StapleNote(context, $"normal summon Card Trooper: mill 3 to become 1900 ATK, draws a card when destroyed (LP {botLp})");
                return trooper.NormalSummon();
            }

            return await ChooseSafeSummonOrSet(context, state, enemyMonsters, botMonsters);
        }

        /// <summary>Below this the bot plays defensively; above it, life points matter little and board development comes first.</summary>
        [Tunable("Below this the bot plays defensively; above it, life points matter little and board development comes first.")]
        protected static int DangerLifePoints = 1500;

        /// <summary>The deck's own Normal Summons, between the staple ones and the safe-summon fallback. Default: none.</summary>
        protected virtual Task<IdleCmdResponse?> DeckSummonAsync(DecisionContext<IdleCmdData> context, IdleCmdData state,
            List<DynamicCard> developers, List<DynamicCard> hand, bool danger, int botLp) => Task.FromResult<IdleCmdResponse?>(null);

        /// <summary>Whether this on-summon monster's effect would do nothing now (e.g. Armageddon Knight with nothing left to send).</summary>
        protected virtual Task<bool> OnSummonEffectUselessAsync(DynamicCard card, List<DynamicCard> hand) => Task.FromResult(false);


        // ------------------------------------------------------------------ setting traps

        private int _redundantSetNotedTurn = -1;

        /// <summary>Spell/Traps worth setting from hand: every Trap, and MST (as bait). Deck brains add their own (e.g. Burial as bait).</summary>
        protected virtual bool IsSetCandidate(DynamicCard card) =>
            card.HasType(CardType.Trap) || card.IsCode(MysticalSpaceTyphoonId);

        /// <summary>
        /// Which trap to set now, if any, and whether an empty field keeps Gorz live (then nothing should be set):
        ///  - only once no attack is coming this turn (Main Phase 2, or a Main Phase 1 with no Battle Phase that does
        ///    something): setting before attacking gains nothing and lets a flipped Ryko destroy the card;
        ///  - storm-proof sets first (Jar of Greed, Trap Dustshoot), see SetOrder;
        ///  - no two set traps for the same issue (Mirror Force + Dimensional Prison), at most MaxExposedSets exposed;
        ///  - not while an empty field keeps Gorz live, and not into a likely Heavy Storm.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex ConditionalActivationText = new(
            @"^\s*(When|If|During|At the start|Activate only|Activate this card only|Discard)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Why setting this spell/trap just hands it to their Lyla, or null (user, 2026-11-13: "setting a card when i have lyla
        /// in attack is just throwing that card away - i can just use lyla to destroy it"): their face-up Lyla in attack
        /// position can use her effect on their turn, and this card can't be activated in response (a trap that waits for an
        /// attack or a summon, a normal or continuous spell). A free-chaining trap or a Quick-Play spell is fine: it answers.
        /// </summary>
        protected async Task<string?> LylaTakesSetAsync(DynamicCard card, List<DynamicCard> enemyMonsters)
        {
            if (!enemyMonsters.Any(m => m.IsFaceup() && m.IsAttack() && m.IsCode(LylaId)))
                return null;
            if (await CardDatabase.GetAsync(card.ID) is not DBCardInfo info)
                return null;
            bool trap = (info.Type & (int)CardType.Trap) != 0, quick = (info.Type & (int)CardType.QuickPlay) != 0;
            bool responds = (trap || quick) && !ConditionalActivationText.IsMatch(info.CardText ?? "");
            return responds ? null
                : "their Lyla is in attack position: her effect destroys it before it can be used (it can't be activated in response)";
        }

        protected async Task<(IdleCmdResponse? Set, bool BattleAhead, bool GorzLive)> ChooseTrapSetAsync(
            DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> enemyMonsters, List<DynamicCard> botMonsters, List<DynamicCard> hand)
        {
            // Anything we set waits for Main Phase 2 when we attack this turn.
            bool battleAhead = (state.SpellSetableCards ?? Array.Empty<DynamicCard>()).Any(card => card.ID > 0 && IsSetCandidate(card)) &&
                               await SetsWaitForMainPhase2Async(context);
            if (battleAhead && !Rule(DeckRules.HoldSetsForBattle))
                battleAhead = false;
            DynamicCard trap = default;
            // Solemn Judgment and Gorz in hand with our field empty: Solemn waits in hand (set, it switches Gorz off and
            // would be used up before Gorz could come); it's set once Gorz is out.
            bool solemnWaits = hand.Any(card => card.IsCode(GorzId)) && hand.Any(card => card.IsCode(SolemnJudgmentId)) &&
                               botMonsters.Count == 0 && !(await CurrentField.GetCardsInSpellZone()).Any(card => card.ID > 0) &&
                               Rule(DeckRules.SolemnWaitsForGorz);
            // Solemn Judgment is a late-game card (half our LP costs less the fewer we have): while our LP is high, other traps
            // are set first (user, 2026-11-13: "if you have other traps, prefer them in the early game").
            bool solemnLate = await CurrentField.GetBotLifePoints() >= SolemnEarlyGameLifePoints;
            if (!battleAhead)
            {
                var redundantNotes = new List<string>();
                foreach (var card in (state.SpellSetableCards ?? Array.Empty<DynamicCard>())
                             .Where(card => card.ID > 0 && IsSetCandidate(card) && !(solemnWaits && card.IsCode(SolemnJudgmentId)))
                             .OrderBy(card => solemnLate && card.IsCode(SolemnJudgmentId) ? 1 : 0)
                             .ThenBy(card => SetOrder(card.ID, enemyMonsters.Count)))
                {
                    if (await SetRedundantAsync(card.ID, hand.Count) is string redundant && Rule(DeckRules.NoRedundantSets))
                    {
                        redundantNotes.Add($"{CardNames.Get(card.ID)}: {redundant}");
                        Hold(card.ID, MainAction.SetSpell, DeckRules.NoRedundantSets);
                        continue;
                    }
                    if (await LylaTakesSetAsync(card, enemyMonsters) is string lylaWhy && Rule(DeckRules.NoSetIntoLyla))
                    {
                        redundantNotes.Add($"{CardNames.Get(card.ID)}: {lylaWhy}");
                        Hold(card.ID, MainAction.SetSpell, DeckRules.NoSetIntoLyla);
                        continue;
                    }
                    trap = card;
                    break;
                }
                if (trap.ID == 0 && redundantNotes.Count > 0 && _redundantSetNotedTurn != context.Header.CurrentTurn)
                {
                    _redundantSetNotedTurn = context.Header.CurrentTurn;
                    StapleNote(context, $"keep in hand: {string.Join("; ", redundantNotes)}");
                }
            }
            // Only worth a note when a card we'd actually set is being held back.
            var heldForMain2 = battleAhead
                ? (state.SpellSetableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card =>
                    card.ID > 0 && (card.HasType(CardType.Trap) || card.IsCode(MysticalSpaceTyphoonId)))
                : default;
            if (heldForMain2.ID > 0)
                StapleNote(context, $"hold {CardNames.Get(heldForMain2.ID)} until Main Phase 2 (set after attacking: a flipped Ryko could destroy it)");
            if (battleAhead)
                foreach (var card in (state.SpellSetableCards ?? Array.Empty<DynamicCard>()).Where(card => card.ID > 0 && IsSetCandidate(card)))
                    Hold(card.ID, MainAction.SetSpell, DeckRules.HoldSetsForBattle);
            bool gorzLive = await KeepGorzLiveAsync(context, enemyMonsters);
            if (trap.ID > 0 && gorzLive && Rule(DeckRules.KeepGorzLive))
            {
                StapleNote(context, $"don't set {CardNames.Get(trap.ID)}: keep the field empty so Gorz stays live");
                Hold(trap.ID, MainAction.SetSpell, DeckRules.KeepGorzLive);
                trap = default;
            }
            if (trap.ID > 0 && await HoldSetForHeavyStormAsync(context.Header.CurrentTurn, trap.ID) is string stormHold && Rule(DeckRules.HoldSetForStorm))
            {
                StapleNote(context, $"don't set {CardNames.Get(trap.ID)}: {stormHold}");
                Hold(trap.ID, MainAction.SetSpell, DeckRules.HoldSetForStorm);
                StormHoldNoted = true;
                trap = default;
            }
            if (trap.ID > 0 && Rule(DeckRules.SetTraps))
            {
                StapleNote(context, $"set trap {CardNames.Get(trap.ID)}");
                return (trap.SetSpellTrap(), battleAhead, gorzLive);
            }
            return (null, battleAhead, gorzLive);
        }
    }
}
