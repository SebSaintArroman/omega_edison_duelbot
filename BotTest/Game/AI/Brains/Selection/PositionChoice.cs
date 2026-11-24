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
    /// The position prompt (a summon's battle position). RULE deck.summon-in-defence-under-mirror: against a likely Mirror
    /// Force, a monster that doesn't need to attack is summoned in defence.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // ------------------------------------------------------------------ defence against Mirror Force

        /// <summary>Mirror Force chance from which monsters that work as well in defence go to defence. Tune from logs.</summary>
        [Tunable("Mirror Force chance from which monsters that work as well in defence go to defence. Tune from logs.")]
        public double MirrorDefenceChance { get; set; } = 0.05;

        /// <summary>
        /// Monsters whose main value is an effect that works in defence too (Chaos Sorcerer banishes from defence):
        /// with any reasonable chance of Mirror Force they go to defence, losing only pressure.
        /// </summary>
        protected virtual IReadOnlyCollection<int> DefenceUnderMirrorForce => Array.Empty<int>();

        /// <summary>
        /// When its attack does matter (pressure: a target worth hitting, or a faster clock), defence is a roll: chance
        /// MirrorForceChance x this weight (e.g. 9% -> 27%, 28% -> 84%). Tune from logs.
        /// </summary>
        [Tunable("When its attack does matter (pressure: a target worth hitting, or a faster clock), defence is a roll: chance MirrorForceChance x this weight (e.g. 9% -> 27%, 28% -> 84%). Tune from logs.")]
        public double MirrorDefenceWeight { get; set; } = 3;

        private readonly Dictionary<(int Turn, int CardId), double> _mirrorDefenceRolls = new();

        /// <summary>
        /// Why <paramref name="cardId"/> should be in defence (a DefenceUnderMirrorForce card with a reasonable Mirror
        /// Force chance), or null. Its effect works in defence, so defence only costs pressure:
        ///  - its attack is needed for lethal this turn: never defence;
        ///  - its attack adds nothing (<paramref name="attackMatters"/> false): defence;
        ///  - otherwise a probabilistic call: defence with chance MirrorForceChance x MirrorDefenceWeight (rolled once
        ///    per card per turn), so it still attacks often when Mirror Force is unlikely.
        /// </summary>
        protected string? MirrorDefenceReason(int cardId, int attack, bool countsAlreadyOnField, IEnumerable<DynamicCard> opponents,
            bool battleAhead, bool attackMatters, int turn)
        {
            if (!DefenceUnderMirrorForce.Contains(cardId) || MirrorForceChance < MirrorDefenceChance)
                return null;
            if (battleAhead)
            {
                var board = _ownMonsters.Where(own => own.IsFaceup() && own.IsAttack() && !(countsAlreadyOnField && own.ID == cardId))
                    .Select(own => own.Attack).Append(attack).ToList();
                if (BoardAssessment.TurnsToWin(board, BoardAssessment.Defenders(opponents.ToList(), OpponentMemory), _oppLp) == 1)
                    return null; // its attack wins the game
            }
            if (!attackMatters)
                return $"Mirror Force chance {MirrorForceChance:P0} and its attack adds nothing now: its effect works just as well in defence";
            double defend = Math.Min(1, MirrorForceChance * MirrorDefenceWeight);
            if (!_mirrorDefenceRolls.TryGetValue((turn, cardId), out double roll))
            {
                if (_mirrorDefenceRolls.Count > 32)
                    _mirrorDefenceRolls.Clear();
                roll = _mirrorDefenceRolls[(turn, cardId)] = RollAt;
            }
            return roll < defend
                ? $"Mirror Force chance {MirrorForceChance:P0}: defence chance {defend:P0} (its attack would add pressure), rolled {roll:0.00}; its effect works just as well in defence"
                : null;
        }

        public CardPosition? RequiredSummonPosition(SelectPositionData state) =>
            CardRuleRegistry.RequiredSummonPosition(state.CardID, state.Positions);

        public override async Task<CardPosition> OnSelectPositionAsync(DecisionContext<SelectPositionData> context, CancellationToken ct = default)
        {
            if (RequiredSummonPosition(context.StateData) is CardPosition required)
                return required;
            var positions = context.StateData.Positions ?? Array.Empty<byte>();
            int id = context.StateData.CardID;
            // Summoned on their turn (Gorz and its token, a Battle Fader, a trap monster): attack position only if it beats
            // every attacker of theirs that can still attack; otherwise defence takes no damage (seen 2026-11-10: the Gorz
            // token went in attack position with Faultroll and Hyunlei still to attack).
            if (context.Header.CurrentPlayer == 1 && positions.Contains((byte)CardPosition.FaceUpDefence) &&
                positions.Contains((byte)CardPosition.FaceUpAttack))
            {
                await PrepareContextAsync(context, ct);
                int attack = await CardDatabase.GetAsync(id) is DBCardInfo summoned ? summoned.Attack : 0;
                var attackers = (await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).ToList();
                var beats = attackers.Where(card => card.Attack > attack).OrderByDescending(card => card.Attack).FirstOrDefault();
                if (beats.ID > 0 && Rule(DeckRules.DefenceOnTheirTurn))
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(id)} in defence: their {CardNames.Get(beats.ID)} ({beats.Attack}) can still attack it");
                    return CardPosition.FaceUpDefence;
                }
            }
            // Our turn at low life points: in attack position, a bigger monster of theirs attacking it next turn deals the
            // difference, and when that alone is lethal, defence (user, 2026-11-13: "summoning armed wing in attack lost the
            // game ... because gorz attacked armed wing"). Not when our board wins this turn.
            if (context.Header.CurrentPlayer == 0 && positions.Contains((byte)CardPosition.FaceUpDefence) &&
                positions.Contains((byte)CardPosition.FaceUpAttack))
            {
                await PrepareContextAsync(context, ct);
                int attack = await CardDatabase.GetAsync(id) is DBCardInfo summoned ? summoned.Attack : 0;
                int ourLp = await CurrentField.GetBotLifePoints();
                var theirs = (await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack()).ToList();
                var killer = theirs.Where(card => card.Attack - attack >= ourLp).OrderByDescending(card => card.Attack).FirstOrDefault();
                int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
                bool winning = BoardAssessment.TurnsToWin((await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                    .Select(EffectiveAttack).Append(attack).ToList(), BoardAssessment.Defenders(await GetEnemyMonsterCardsAsync(), OpponentMemory), oppLp) == 1;
                if (killer.ID > 0 && !winning && Rule(DeckRules.DefenceWhenHitIsLethal))
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(id)} in defence: in attack position their {CardNames.Get(killer.ID)} ({killer.Attack}) " +
                                        $"attacking it deals {killer.Attack - attack}, lethal at our {ourLp} LP");
                    return CardPosition.FaceUpDefence;
                }
            }
            if (DefenceUnderMirrorForce.Contains(id) && positions.Contains((byte)CardPosition.FaceUpDefence))
            {
                await PrepareContextAsync(context, ct);
                await RefreshBackrowOddsAsync(context);
                _ownMonsters = await GetBotMonsterCardsAsync();
                int attack = await CardDatabase.GetAsync(id) is DBCardInfo info ? info.Attack : 0;
                bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && context.Header.CurrentTurn > 1;
                var enemy = await GetEnemyMonsterCardsAsync();
                // Summoned now: if it will use its effect this turn (a face-up target), it can't attack anyway. Otherwise its
                // attack matters when there's a battle ahead and something to hit (or a direct attack).
                bool usesEffect = enemy.Any(card => card.IsFaceup() && card.ID > 0);
                bool attackMatters = battleAhead && !usesEffect;
                if (MirrorDefenceReason(id, attack, countsAlreadyOnField: false, enemy, battleAhead, attackMatters, context.Header.CurrentTurn) is string why &&
                    Rule(DeckRules.SummonInDefenceUnderMirror))
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(id)} in defence: {why}");
                    return CardPosition.FaceUpDefence;
                }
            }
            return await base.OnSelectPositionAsync(context, ct);
        }
    }
}
