using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>Generic keyword tags and the hard rules that those tags impose across all cards and decks.</summary>
    internal static class TagRules
    {
        /// <summary>Strategic state used by generic tag exceptions.</summary>
        internal enum BotStrategicState : byte
        {
            Base,
            Threatened,
            Desperate,
            Pressuring
        }

        internal static string DescribeStrategicState(BotStrategicState current, BotStrategicState? previous) =>
            previous is { } before && before != current ? $"{current} (changed from {before})" : current.ToString();

        [Flags]
        internal enum Tag
        {
            None = 0,
            SetOnly = 1,
            DoNotSummon = 2,
            DoNotSet = 4,
            NonQuickPlaySpell = 8,
            Bait = 16,
            GoodDiscard = 32,
            AnswersAnyThreat = 64,
            Threat = 128,
            ProtectsMonsters = 256,
            AlwaysSet = 512,
            ActivateAtFirstOpportunity = 1024,
            StatChangeEffect = 2048,
            DoNotActivate = 4096,
            DeclineEffectOnSummon = 8192,
            ActivateOnSummon = 16384,
            NeverSet = 32768,
            NeverSummonInDefence = 65536
        }

        /// <summary>`DoNotSummon` forbids Normal Summon, Special Summon, and setting unless its survival exception is active.</summary>
        public static bool ForbidsSummonOrSet(Tag tags, bool survivalExceptionActive) =>
            (tags & Tag.DoNotSummon) != 0 && !survivalExceptionActive;

        /// <summary>The shared DoNotSummon exception: no other summon, no monsters, and the opponent has next-turn lethal.</summary>
        public static bool DoNotSummonSurvivalException(bool anotherSummonAvailable, bool weHaveMonsters,
            bool opponentHasNextTurnLethal) =>
            !anotherSummonAvailable && !weHaveMonsters && opponentHasNextTurnLethal;

        internal static bool SetPreventsLethal(DynamicCard card, IEnumerable<int> enemyAttack, int lifePoints)
        {
            var attackers = enemyAttack.ToArray();
            return BoardAssessment.TurnsToWin(attackers, Array.Empty<BoardAssessment.Defender>(), lifePoints, maxTurns: 1) == 1 &&
                BoardAssessment.TurnsToWin(attackers,
                    new[] { new BoardAssessment.Defender(card.Defense, false, true, card.ID) }, lifePoints, maxTurns: 1) != 1;
        }

        /// <summary>
        /// Whether tags on a card forbid its Normal Summon. Exceptions declared for a generic tag apply to every
        /// card carrying that tag; a card-specific exception is checked by CardRules before calling this method.
        /// </summary>
        public static bool ForbidsNormalSummon(Tag tags, BotStrategicState state, bool hasTuner,
            bool hasAlternativeSynchro, int opponentLifePoints, int projectedDamage)
        {
            if ((tags & Tag.SetOnly) == 0)
                return false;

            if (projectedDamage > 0 && opponentLifePoints - projectedDamage <= 200)
                return false;

            // Generic Set Only exception: while Threatened, allow it when a Tuner is available and no other Synchro
            // answer is available.
            if (state == BotStrategicState.Threatened && hasTuner && !hasAlternativeSynchro)
                return false;

            return true;
        }
    }
}
