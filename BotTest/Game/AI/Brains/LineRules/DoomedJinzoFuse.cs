namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.doomed-jinzo-unfused
        /// Our turn ends with a Jinzo destroyed in our End Phase (revived by Jinzo - Returner) while Chimeratech Fortress Dragon could
        /// have been made from our face-up Cyber Dragon and it: this cost per such Jinzo (user, 2026-11-24: "it *must* fuse cyber dragon
        /// with jinzo just before moving into end phase"; "use as many jinzo's that are going to die anyway as possible").
        /// The same line's "don't summon alongside Cyber Dragon" holds (deck.no-chimeratech-exposure) are refunded once it makes
        /// Chimeratech after Returner's revival (the rule is relaxed on that line).
        /// </summary>
        [Tunable("Our turn ends with a Jinzo revived by Jinzo - Returner destroyed in our End Phase while Chimeratech Fortress Dragon could have been made from our face-up Cyber Dragon and it: this cost per Jinzo. User, 2026-11-24: \"it *must* fuse cyber dragon with jinzo just before moving into end phase\".")]
        public static readonly Tuned<double> DoomedJinzoUnfusedCost = new(HoldCosts.Critical);
    }
}
