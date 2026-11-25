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
            /// <summary>Far behind on card advantage (StandingState). Always does everything Threatened does too: check with ActsThreatened.</summary>
            Desperate,
            Pressuring,
            /// <summary>High LP, cards even: play a little more conservatively (StandingState; resource holds cost more).</summary>
            Conservative
        }

        /// <summary>
        /// Whether the Threatened behaviour applies: Threatened, or Desperate, which always gets everything Threatened does (user,
        /// 2026-11-24: "give desperate all things that threatened can do too. this should remain true always"). Every Threatened check
        /// goes through here, never "state == Threatened".
        /// </summary>
        /// <summary>A Set Only monster may be Normal Summoned when a Tuner is ready and no other Synchro answer is (StateTuning: on while
        /// Threatened, and so Desperate).</summary>
        [Tunable("A Set Only monster may be Normal Summoned when a Tuner is ready and no other Synchro answer is (the generic Set Only exception). Per strategic state: on while Threatened (Desperate follows Threatened).", StateDefaults = "Threatened=true")]
        internal static readonly Tuned<bool> SetOnlyTunerException = new(false);

        internal static bool ActsThreatened(BotStrategicState state) =>
            state is BotStrategicState.Threatened or BotStrategicState.Desperate;

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
            NeverSummonInDefence = 65536,
            // A single answer to an attack that waits for a bigger attacker still to come (weighed in HoldForBiggerAttackerAsync)
            // even when the current attack costs us something.
            SaveForBiggerAttacker = 131072,
            // Used right before our Battle Phase, as the last step of Main Phase 1: in the search, the Battle Phase isn't on offer until it's
            // used, and using it opens the position window (Sirocco's lethal boost; user, 2026-11-25).
            ActivateBeforeBattle = 262144
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

            // Generic Set Only exception: in the states it's on for (Threatened, and Desperate through its chain), allow it when a Tuner is
            // available and no other Synchro answer is available.
            if (SetOnlyTunerException.In(state) && hasTuner && !hasAlternativeSynchro)
                return false;

            return true;
        }
    }
}
