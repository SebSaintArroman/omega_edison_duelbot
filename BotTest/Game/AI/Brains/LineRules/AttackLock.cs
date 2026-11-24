namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.attack-lock
        /// An effect of ours that stops our other monsters attacking (Sirocco's boost, Chaos Sorcerer's banish): this cost per
        /// other monster that could still attack (user, 2026-11-16).
        /// </summary>
        /// <summary>
        /// Per other monster of ours that could still attack, for an effect of ours that stops them attacking (Sirocco's boost, Chaos
        /// Sorcerer's banish): the downside of the effect, which a big enough gain (the boost winning a battle) overcomes. User,
        /// 2026-11-16: "stopping your things attacking is a negative part of an effect, it should have that".
        /// </summary>
        [Tunable("Per other monster of ours that could still attack, for an effect of ours that stops them attacking (Sirocco's boost, Chaos Sorcerer's banish): the downside of the effect, which a big enough gain (the boost winning a battle) overcomes. User, 2026-11-16: \"stopping your things attacking is a negative part of an effect, it should have that\".")]
        public static readonly Tuned<double> AttackLockCost = new(0.4);
    }
}
