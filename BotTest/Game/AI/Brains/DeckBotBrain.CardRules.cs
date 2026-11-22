using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    public abstract partial class DeckBotBrain
    {
        private async Task<bool> SharedSpellSetAllowedAsync(DynamicCard card)
        {
            if (card.HasType(CardType.Spell) && !card.HasType(CardType.QuickPlay))
                return false;
            if (card.IsCode(TrapDustshootId))
                return ((await CurrentField.GetBotField()).EnemySide.Hand?.Length ?? 0) >= 3;
            if (card.IsCode(6540606))
                return (await GetBotMonsterCardsAsync()).Any(monster => (monster.Attribute & (int)CardAttribute.Water) != 0);
            return true;
        }

        private async Task<IdleCmdResponse?> MandatoryAllureAsync(IdleCmdData state)
        {
            if (TryGetActivable(state, AllureOfDarknessId, out var allure) &&
                (await GetBotHandCardsAsync()).Any(card => card.HasType(CardType.Monster) &&
                    (card.Attribute & (int)CardAttribute.Dark) != 0))
                return allure.MainActivateFromIndex();
            return null;
        }

        private async Task<IdleCmdResponse> SetDustshootBeforeEndingAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse move)
        {
            if (move.Action != MainAction.ToEndPhase)
                return move;
            var dustshoot = (context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card => card.IsCode(TrapDustshootId));
            if (dustshoot.ID <= 0 || !await SharedSpellSetAllowedAsync(dustshoot))
                return move;
            context.Bot.LogNote("mandatory action: set Trap Dustshoot before ending the turn because the opponent has at least three cards in hand");
            return dustshoot.SetSpellTrap();
        }

        private async Task<bool> SharedIdleMoveAllowedAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse move)
        {
            var state = context.StateData;
            if (move.Action == MainAction.SetSpell)
            {
                var card = (state.SpellSetableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(candidate =>
                    candidate.ActionIndex is { Length: > (int)MainAction.SetSpell } indexes &&
                    indexes[(int)MainAction.SetSpell] == move.Index);
                return card.ID <= 0 || await SharedSpellSetAllowedAsync(card);
            }
            if (move.Action == MainAction.Activate &&
                (state.ActivableCards ?? Array.Empty<DynamicCard>()).Any(card =>
                    card.IsCode(PlaguespreaderZombieId) &&
                    (card.ActionActivateIndex ?? Array.Empty<ActivationCardIndex>()).Any(activation =>
                        activation.ActionIndex == move.Index)))
                return await CanRevivePlaguespreaderAsync(context, state, (await GetBotHandCardsAsync()).Count - 1);
            return true;
        }

        private async Task<int?> ApplySharedChainRulesAsync(DecisionContext<SelectChainData> context, int brainIndex)
        {
            if (context.StateData.Forced)
                return null;

            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            var phase = (DuelPhase)context.Header.CurrentPhase;
            bool ourTurn = IsOwnTurn(context.Header);
            int FirstOffered(params int[] ids) => Array.FindIndex(cards, card =>
                card.CurrentCoordinates.Controller == 0 && card.IsCode(ids));
            int Required(int index, string reason)
            {
                context.Bot.LogNote($"mandatory action: activate {OmegaBotWrapper.Logging.CardNames.Get(cards[index].ID)} ({reason})");
                return index;
            }

            int returner = FirstOffered(9418534);
            if (returner >= 0 && cards[returner].CurrentCoordinates.Location == (byte)CardLocation.Grave)
                return Required(returner, "sent to the graveyard");

            int early = FirstOffered(6540606, TrapDustshootId);
            if (early >= 0)
                return Required(early, "activate at the first opportunity");

            int drawTrap = FirstOffered(JarOfGreedId, LegacyOfYataGarasuId);
            if (drawTrap >= 0)
            {
                if (ourTurn && phase == DuelPhase.Standby &&
                    _opponentSummonedAndAttackedWithThreat == context.Header.CurrentTurn - 1)
                    return Required(drawTrap, "Standby Phase after the opponent summoned and attacked with a threat");
                if (ourTurn && phase == DuelPhase.Draw &&
                    await DoomedAsync(cards[drawTrap], context.Header))
                    return Required(drawTrap, "doomed during the Draw Phase");
            }

            if (brainIndex >= 0 && brainIndex < cards.Length && cards[brainIndex].IsCode(WabokuId) &&
                !await DoomedAsync(cards[brainIndex], context.Header))
            {
                if (phase is not (DuelPhase.Damage or DuelPhase.DamageCal))
                    return -1;
                var ownBattler = await CurrentField.GetBotBattlingMonster(isOwnSide: true);
                var opponentBattler = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
                if (opponentBattler.ID <= 0 || !CardRules.GladiatorWouldBeDestroyed(ownBattler, opponentBattler))
                    return -1;
            }

            int necro = FirstOffered(4906301);
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

            if (!IsBattlePhase(phase) || !await CurrentField.IsBotUnderAttack(!ourTurn))
                return brainIndex >= 0 ? -1 : null;
            var battler = await CurrentField.GetBotBattlingMonster(isOwnSide: true);
            if (battler.ID <= 0)
                return brainIndex >= 0 ? -1 : null;

            var enemy = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
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
