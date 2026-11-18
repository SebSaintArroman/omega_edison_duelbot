namespace BotTest.Game.AI.Search
{
    /// <summary>
    /// A per-card correction to a card's effect points (EffectEvaluator), added before the taper (MctsEvaluator.Taper): a rough
    /// last-resort lever for a card the scorer gets wrong (user, 2026-11-13: "give every card a 'card correction' score. set
    /// these all as 0 for now ... don't give any cards this without my explicit approval"). Every card not listed is 0.
    /// Each entry: the card id, the correction in effect points (General: wherever the card is scored; Hand: only while it's
    /// in a hand), and a comment with the date and why (the user's call).
    /// </summary>
    internal static class CardCorrections
    {
        private static readonly Dictionary<int, (double General, double Hand)> Corrections = new()
        {
            // Gorz the Emissary of Darkness: in hand it answers a direct attack with Gorz plus a token, far more than its text
            // scores (user, 2026-11-13: "id have defo taken gorz" over Freed with Trap Dustshoot; "Gorz should be a hand-only bonus").
            [44330098] = (0, 3),
            // Neo-Spacian Grand Mole: returns what battles it (and itself), stalling attacks and bouncing synchros; replaces the
            // old removal-only bouncer bonus (user, 2026-11-13: "mole should be a general bonus").
            [80344569] = (6, 0),
            // Archlord Kristya: stops every Special Summon (ours included) on top of a 2800 body (user, 2026-11-13: "increase the
            // threat rating of archlord krystia by something huge - maybe 5"; Solemn should have stopped it on its summon).
            [59509952] = (5, 0),
        };

        /// <summary>The correction for this card where it is: General, plus Hand while it's in a hand.</summary>
        public static double Get(int code, bool inHand = false)
        {
            if (!Corrections.TryGetValue(code, out var c) &&
                !(SearchHost.Available && SearchHost.CanonicalId(code) is > 0 and var official && Corrections.TryGetValue(official, out c)))
                return 0;
            return c.General + (inHand ? c.Hand : 0);
        }
    }
}
