namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.effect-after-battle
        /// A monster effect of ours on offer in Main Phase 1, used only in Main Phase 2 after a battle that could have removed
        /// that monster: this cost (user, 2026-11-18: only in genuine danger).
        /// </summary>
        [Tunable("A monster effect of ours on offer in Main Phase 1, used only in Main Phase 2 after a Battle Phase while their board could remove that monster in the battle (a face-down flip, Fortress's battle trigger): this cost. User, 2026-11-18: \"if a monster's effect could be used in mp1 or mp2 with no difference, it should be done in mp1\".")]
        public static readonly Tuned<double> EffectAfterBattleCost = new(0.5);
    }
}
