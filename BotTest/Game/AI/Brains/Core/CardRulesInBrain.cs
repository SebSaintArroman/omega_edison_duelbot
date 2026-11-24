using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using BotTest.Game.AI.Search;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    public abstract partial class DeckBotBrain
    {

        /// <summary>The card-rule context for a search of our deck (Reinforcement of the Army and the like): what's left in the deck
        /// (OwnDeckLeftAsync, or the cards on offer), our hand, the Normal Summon and Dark Grepher.</summary>
        private async Task<CardRuleRegistry.Context> OwnDeckContextAsync(BotStateHeader header,
            DuelBotWrapper? bot = null, IReadOnlyCollection<int>? offered = null)
        {
            var hand = (await CurrentField.GetBotField()).OwnSide.Hand ?? Array.Empty<int>();
            bool grepherAvailable = !WasCardActivatedThisTurn(DarkGrepher) &&
                (hand.Any(id => SearchHost.Is(id, DarkGrepher)) ||
                 (await GetBotMonsterCardsAsync()).Any(card => card.IsCode(DarkGrepher)));
            offered ??= await OwnDeckLeftAsync(bot);
            return new CardRuleRegistry.Context(offered, hand, 0, 0, 0, false,
                NormalSummonUsed: NormalSummonUsed(header), DarkGrepherAvailable: grepherAvailable);
        }

        /// <summary>The cards left in our deck: the server's list only when every card in it is known (its unknown cards come as 0),
        /// else our deck list less every card we've seen (hand, graveyard, banished, our own cards on our field). Empty without either.</summary>
        protected async Task<IReadOnlyCollection<int>> OwnDeckLeftAsync(DuelBotWrapper? bot)
        {
            var own = (await CurrentField.GetBotField()).OwnSide;
            if (own.Main is { Length: > 0 } knownDeck && knownDeck.All(id => id > 0))
                return knownDeck;
            if (bot == null || !SearchHost.Available)
                return Array.Empty<int>();
            var fieldCards = (await GetBotMonsterCardsAsync())
                .Concat(await GetPlayerCardsInZoneAsync(CardLocation.SpellZone))
                .Where(card => card.Owner == 0).Select(card => card.ID);
            var seen = (own.Hand ?? Array.Empty<int>()).Concat(own.Grave ?? Array.Empty<int>())
                .Concat(own.Banish ?? Array.Empty<int>()).Concat(fieldCards);
            return RemainingOwnDeck(bot, seen);
        }

        private async Task<bool> ReinforcementAllowedAsync(DecisionContext<IdleCmdData> decision)
        {
            var header = decision.Header;
            if (!NormalSummonUsed(header))
                return true;
            var context = await OwnDeckContextAsync(header, decision.Bot);
            if (CardRules.ReinforcementNeedsGardna(context))
                return true;
            var targets = new HashSet<int>();
            foreach (int id in context.OwnDeck.Where(id => id > 0).Distinct())
            {
                if (SearchHost.Available && SearchHost.Cards.Get(id) is { } card)
                {
                    if (CardRules.ReinforcementCanSearch(card.Type, card.Race, card.Level))
                        targets.Add(card.Alias > 0 ? card.Alias : id);
                }
                else if (await CardDatabase.GetAsync(id) is DBCardInfo info &&
                         CardRules.ReinforcementCanSearch(info.Type, info.Race, info.Level))
                    targets.Add(info.Alias > 0 ? info.Alias : id);
                if (targets.Count > 1)
                    break;
            }
            context = context with { ReinforcementHasSingleTarget = targets.Count == 1 };
            return (CardRuleRegistry.TagsFor(ReinforcementOfTheArmy, context) & TagRules.Tag.DoNotActivate) == 0;
        }

        private protected async Task<bool> GoodDiscardDeckSendAllowedAsync(int id, BotStateHeader header, DuelBotWrapper bot)
        {
            if (!CardRuleRegistry.RequiresGoodDiscardDeckSend(id))
                return true;
            var context = await OwnDeckContextAsync(header, bot);
            return (CardRuleRegistry.TagsFor(id, context) & TagRules.Tag.DoNotActivate) == 0;
        }

        private async Task<bool> SharedSpellSetAllowedAsync(DynamicCard card)
        {
            if (card.HasType(CardType.Spell) && !card.HasType(CardType.QuickPlay))
                return false;
            if (card.IsCode(TrapDustshoot))
                return ((await CurrentField.GetBotField()).EnemySide.Hand?.Length ?? 0) >= 3;
            if (card.IsCode(SpiritualWaterArtAoi))
                return (await GetBotMonsterCardsAsync()).Any(monster => (monster.Attribute & (int)CardAttribute.Water) != 0);
            return true;
        }

        private async Task<IdleCmdResponse?> MandatoryAllureAsync(IdleCmdData state)
        {
            if (TryGetActivable(state, AllureOfDarkness, out var allure) &&
                (await GetBotHandCardsAsync()).Any(card => card.HasType(CardType.Monster) &&
                    (card.Attribute & (int)CardAttribute.Dark) != 0))
                return allure.MainActivateFromIndex();
            return null;
        }

        private async Task<IdleCmdResponse> SetDustshootBeforeEndingAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse move)
        {
            if (move.Action != MainAction.ToEndPhase)
                return move;
            var dustshoot = (context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card => card.IsCode(TrapDustshoot));
            if (dustshoot.ID <= 0 || !await SharedSpellSetAllowedAsync(dustshoot))
                return move;
            context.Bot.LogNote("mandatory action: set Trap Dustshoot before ending the turn because the opponent has at least three cards in hand");
            return dustshoot.SetSpellTrap();
        }

        internal async Task PrepareIdleMoveRulesAsync(DecisionContext<IdleCmdData> context, bool normalSummonUsed,
            IEnumerable<int> activatedThisTurn)
        {
            await PrepareContextAsync(context);
            _normalSummonMadeTurn = normalSummonUsed ? context.Header.CurrentTurn : -1;
            _activatedThisTurn.Clear();
            foreach (int code in activatedThisTurn)
                MarkCardActivated(code);
        }

        private async Task<bool> TaggedMonsterSetAllowedAsync(DynamicCard card) =>
            !CardRuleRegistry.AnyCardWithTag(new[] { card.ID }, TagRules.Tag.DoNotSet) ||
            ((await GetBotMonsterCardsAsync()).Count == 0 &&
                TagRules.SetPreventsLethal(card, (await GetEnemyMonsterCardsAsync())
                    .Where(enemy => enemy.IsFaceup() && enemy.IsAttack()).Select(enemy => enemy.Attack),
                    await CurrentField.GetBotLifePoints()));

        internal async Task<HashSet<(MainAction Action, int Index)>> BlockedIdleMovesAsync(
            DecisionContext<IdleCmdData> context, IEnumerable<IdleCmdResponse> moves)
        {
            var allowed = await WithoutChimeratechSummonsAsync(context.StateData);
            var blocked = new HashSet<(MainAction, int)>();
            foreach (var move in moves)
                if (!await SharedIdleMoveAllowedAsync(context, move, allowed) || !await DeckIdleMoveAllowedAsync(context, move))
                    blocked.Add((move.Action, move.Index));
            return blocked;
        }

        /// <summary>A deck brain's own bans on idle moves, applied in the search's lines too (BlockedIdleMovesAsync). Default: none.</summary>
        protected virtual Task<bool> DeckIdleMoveAllowedAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse move) =>
            Task.FromResult(true);

        private async Task<bool> SharedIdleMoveAllowedAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse move, IdleCmdData? allowed = null)
        {
            var state = context.StateData;
            if (move.Action is MainAction.Summon or MainAction.SpSummon)
            {
                var summons = allowed ?? await WithoutChimeratechSummonsAsync(state);
                var cards = move.Action == MainAction.Summon ? summons.SummonableCards : summons.SpecialSummonableCards;
                return (cards ?? Array.Empty<DynamicCard>()).Any(card =>
                    card.ActionIndex is { } indexes && indexes.Length > (int)move.Action && indexes[(int)move.Action] == move.Index);
            }
            if (move.Action is MainAction.SetMonster or MainAction.SetSpell)
            {
                var candidates = move.Action == MainAction.SetMonster ? state.MonsterSetableCards : state.SpellSetableCards;
                var card = (candidates ?? Array.Empty<DynamicCard>()).FirstOrDefault(candidate =>
                    candidate.ActionIndex is { } indexes && indexes.Length > (int)move.Action &&
                    indexes[(int)move.Action] == move.Index);
                return !CardRuleRegistry.NeverSets(card) &&
                    (move.Action == MainAction.SetMonster
                        ? await TaggedMonsterSetAllowedAsync(card)
                        : card.ID <= 0 || await SharedSpellSetAllowedAsync(card));
            }
            if (move.Action == MainAction.Activate &&
                (state.ActivableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card =>
                    CardRuleRegistry.RequiresGoodDiscardDeckSend(card.ID) &&
                    (card.ActionActivateIndex ?? Array.Empty<ActivationCardIndex>()).Any(activation =>
                        activation.ActionIndex == move.Index)) is { ID: > 0 } sender)
                return await GoodDiscardDeckSendAllowedAsync(sender.ID, context.Header, context.Bot);
            if (move.Action == MainAction.Activate &&
                (state.ActivableCards ?? Array.Empty<DynamicCard>()).Any(card =>
                    card.IsCode(PlaguespreaderZombie) &&
                    (card.ActionActivateIndex ?? Array.Empty<ActivationCardIndex>()).Any(activation =>
                        activation.ActionIndex == move.Index)))
                return await CanRevivePlaguespreaderAsync(context, state, (await GetBotHandCardsAsync()).Count - 1);
            if (move.Action == MainAction.Activate &&
                (state.ActivableCards ?? Array.Empty<DynamicCard>()).Any(card =>
                    card.IsCode(ReinforcementOfTheArmy) &&
                    (card.ActionActivateIndex ?? Array.Empty<ActivationCardIndex>()).Any(activation =>
                        activation.ActionIndex == move.Index)))
                return await ReinforcementAllowedAsync(context);
            return true;
        }

        private async Task<int?> ApplySharedChainRulesAsync(DecisionContext<SelectChainData> context, int brainIndex)
        {
            if (context.StateData.Forced)
                return null;

            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            // Omega can offer Sirocco's boost after its summon even when the card rule leaves only pass.
            if (cards.Length > 0 && cards.All(card => card.CurrentCoordinates.Controller == 0 && card.IsCode(BlackwingSiroccoTheDawn)))
            {
                var ownMonsters = await GetBotMonsterCardsAsync();
                var ruleContext = new CardRuleRegistry.Context(Array.Empty<int>(), Array.Empty<int>(), 0, 0, 0, false,
                    OwnMonsterIds: ownMonsters.Select(monster => monster.ID).ToArray());
                if ((CardRuleRegistry.TagsFor(BlackwingSiroccoTheDawn, ruleContext) & TagRules.Tag.DoNotActivate) != 0)
                    return -1;
            }
            var phase = (DuelPhase)context.Header.CurrentPhase;
            bool ourTurn = IsOwnTurn(context.Header);
            int FirstOffered(params int[] ids) => Array.FindIndex(cards, card =>
                card.CurrentCoordinates.Controller == 0 && card.IsCode(ids));
            int Required(int index, string reason)
            {
                context.Bot.LogNote($"mandatory action: activate {OmegaBotWrapper.Logging.CardNames.Get(cards[index].ID)} ({reason})");
                return index;
            }

            int returner = FirstOffered(JinzoReturner);
            if (returner >= 0 && cards[returner].CurrentCoordinates.Location == (byte)CardLocation.Grave)
                return Required(returner, "sent to the graveyard");

            int early = FirstOffered(SpiritualWaterArtAoi, TrapDustshoot);
            if (early >= 0)
                return Required(early, "activate at the first opportunity");

            int drawTrap = FirstOffered(JarOfGreed, LegacyOfYataGarasu);
            if (drawTrap >= 0)
            {
                if (ourTurn && phase == DuelPhase.Standby &&
                    _opponentSummonedAndAttackedWithThreat == context.Header.CurrentTurn - 1)
                    return Required(drawTrap, "Standby Phase after the opponent summoned and attacked with a threat");
                if (ourTurn && phase == DuelPhase.Draw &&
                    await DoomedAsync(cards[drawTrap], context.Header))
                    return Required(drawTrap, "doomed during the Draw Phase");
            }

            if (brainIndex >= 0 && brainIndex < cards.Length && cards[brainIndex].IsCode(Waboku) &&
                !await DoomedAsync(cards[brainIndex], context.Header))
            {
                if (phase is not (DuelPhase.Damage or DuelPhase.DamageCal))
                    return -1;
                var ownBattler = await CurrentField.GetBotBattlingMonster(isOwnSide: true);
                var opponentBattler = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
                if (opponentBattler.ID <= 0 || !CardRules.GladiatorWouldBeDestroyed(ownBattler, opponentBattler))
                    return -1;
            }

            int necro = FirstOffered(NecroGardna);
            if (necro >= 0)
            {
                var card = cards[necro];
                if ((card.ChainOnlyOwnTargets ?? Array.Empty<CardCoordinate>()).Any(target => target.Controller == 1))
                    return Required(necro, "opponent targeted it in the graveyard");
                if (brainIndex == necro && (ourTurn || !IsBattlePhase(phase) || !await CurrentField.IsBotUnderAttack()))
                    return -1;
            }

            if (IsSearchTwin)
                return null;
            var boosts = cards.Select((card, index) => (Card: card, Index: index))
                .Where(offer => offer.Card.CurrentCoordinates.Controller == 0 &&
                    CardRules.IsAttackBoost(offer.Card.ID, offer.Card.CurrentCoordinates.Location)).ToList();
            if (boosts.Count == 0 || brainIndex >= 0 && boosts.All(offer => offer.Index != brainIndex))
                return null;

            foreach (var boost in boosts)
            {
                var card = boost.Card;
                if (card.CurrentCoordinates.Location == (byte)CardLocation.Grave
                    ? (card.ChainOnlyOwnTargets ?? Array.Empty<CardCoordinate>()).Any(target => target.Controller == 1)
                    : await DoomedAsync(card, context.Header))
                    return boost.Index;
            }

            if (!IsBattlePhase(phase))
                return brainIndex >= 0 ? -1 : null;
            // A battle in progress, read from the battling monsters: Omega's UnderAttack flag is only set in the attack declaration's
            // window and is false in the damage step's, where the boosts are offered (seen 2026-11-24: the Blackwing brain's Kalut onto
            // Shura, attacked by Gardestrike, was turned into a pass here; the search had passed Mirror Force counting on it).
            var battler = await CurrentField.GetBotBattlingMonster(isOwnSide: true);
            var enemy = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            bool inBattle = battler.ID > 0 && battler.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone &&
                            (ourTurn || enemy.ID > 0 && enemy.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone);
            if (!inBattle && !await CurrentField.IsBotUnderAttack(!ourTurn) || battler.ID <= 0)
                return brainIndex >= 0 ? -1 : null;

            DynamicCard? defender = enemy.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone ? enemy : null;
            int lp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            int required = CardRules.RequiredAttackBoost(battler, defender, defender == null, ourTurn, lp);
            if (required == int.MaxValue)
                return brainIndex >= 0 ? -1 : null;

            int pending = (await GetPlayerCardsInZoneAsync(CardLocation.SpellZone))
                .Where(card => card.IsFaceup() && (card.CurrentChainTargets ?? Array.Empty<CardCoordinate>()).Any(target =>
                    target.Controller == 0 && target.Location == (byte)CardLocation.MonsterZone &&
                    target.Sequence == battler.CurrentCoordinates.Sequence))
                .Sum(card => CardRules.AttackBoost(card.ID, card.CurrentCoordinates.Location, battler, defender));
            var offers = boosts.Select(offer => (offer.Index, Amount: CardRules.AttackBoost(offer.Card.ID,
                offer.Card.CurrentCoordinates.Location, battler, defender))).ToArray();
            var allowed = CardRules.MinimumAttackBoosts(offers, required - pending);
            if (allowed.Count == 0)
                return brainIndex >= 0 ? -1 : null;

            int chosen = allowed.Contains(brainIndex) ? brainIndex : offers.Where(offer => allowed.Contains(offer.Index))
                .OrderByDescending(offer => offer.Amount).First().Index;
            if (CardRules.AttackBoostChoosesTarget(cards[chosen].ID))
                PlanTarget(context.Header.CurrentTurn, battler, theirs: false);
            return chosen;
        }

        private static bool IsBattlePhase(DuelPhase phase) =>
            phase is DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Battle or DuelPhase.Damage or DuelPhase.DamageCal;

        protected virtual Task<bool> TreatBackrowAsDoomedAsync(bool ourTurn, DuelPhase phase) => Task.FromResult(false);
    }
}
