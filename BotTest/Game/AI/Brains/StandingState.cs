using DuelSim.Engine;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// The bot's standing in the duel from life points and card counts (user, 2026-11-24), feeding the strategic state:
    ///  - Desperate: they're DesperateCardGap or more ahead of us on card score ("also enter desperate if opponent has 3+ more card
    ///    advantage score than the bot");
    ///  - Conservative: our LP at least ConservativeLpFrom and neither side more than ConservativeCardGap ahead ("when its lifepoints
    ///    are high and neither person has much card advantage on the other, it should play a little more conservatively"). Resource
    ///    holds ([ResourceHold] rules) then cost their own Conservative hold cost (StateTuning: HoldCost.<rule>@Conservative).
    /// Card score: each side's whole card advantage score as the search counts it (BoardEvaluator.CardAdvantageScores: hand count
    /// and values, monsters, spells/traps and set trap values, graveyard values, Vayu pairs; user, 2026-11-24: "it should be the total
    /// card advantage score").
    /// Priority (ThreatenedState.Assessment): Desperate, Threatened, Pressuring, Conservative, Base (user: "prefer pressuring over
    /// conservative if both are hit at the same time").
    /// </summary>
    internal static class StandingState
    {
        [Tunable("Conservative strategic state from this many LP of ours (with the card scores close, see ConservativeCardGap). User, 2026-11-24: \"lower that lifepoint requirement to 5500\".")]
        public static readonly Tuned<int> ConservativeLpFrom = new(5500);

        [Tunable("Card advantage scores (each side's whole CardAdvantageDelta count) at most this far apart count as neither side having much card advantage: the Conservative strategic state (see ConservativeLpFrom).")]
        public static readonly Tuned<double> ConservativeCardGap = new(1.0);

        [Tunable("The Desperate strategic state when their card advantage score (each side's whole CardAdvantageDelta count) is this much above ours. User, 2026-11-24: \"enter desperate if opponent has 3+ more card advantage score than the bot\".")]
        public static readonly Tuned<double> DesperateCardGap = new(3.0);

        /// <summary>Desperate, Conservative, or Base, from our LP and both card scores.</summary>
        public static TagRules.BotStrategicState Assess(int ourLp, double ourScore, double theirScore) =>
            theirScore - ourScore >= DesperateCardGap ? TagRules.BotStrategicState.Desperate
            : ourLp >= ConservativeLpFrom && Math.Abs(ourScore - theirScore) <= ConservativeCardGap ? TagRules.BotStrategicState.Conservative
            : TagRules.BotStrategicState.Base;

        /// <summary>The standing of a position: our LP and both sides' card advantage scores by this evaluator.</summary>
        public static TagRules.BotStrategicState Assess(BoardEvaluator evaluator, SideSummary ours, SideSummary theirs)
        {
            var (our, their) = evaluator.CardAdvantageScores(ours, theirs);
            return Assess(ours.LifePoints, our, their);
        }

        /// <summary>Why the state is what it is, for the log.</summary>
        public static string Describe(int ourLp, double ourScore, double theirScore) =>
            $"LP {ourLp}, card score {ourScore:0.#} vs {theirScore:0.#}";
    }
}
