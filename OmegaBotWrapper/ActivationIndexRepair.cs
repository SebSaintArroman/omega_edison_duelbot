using OmegaBotWrapper.Struct;

namespace OmegaBotWrapper
{
    /// <summary>
    /// Rebuilds each activable card's ActionActivateIndex from the prompt's positional data: ActivableCards[i] is action
    /// i, with description ActivableDescriptions[i]. The per-card list Omega sends can carry a stray entry from another
    /// card (seen 2026-11-11: Vayu's list began {desc 0, index 0}, Heavy Storm's slot, so activating Vayu activated Heavy
    /// Storm instead).
    /// </summary>
    internal static class ActivationIndexRepair
    {
        public static void Repair(DynamicCard[]? cards, int[]? descriptions)
        {
            if (cards == null || cards.Length == 0)
                return;
            static bool Same(in DynamicCard a, in DynamicCard b) =>
                a.ID == b.ID &&
                a.CurrentCoordinates.Controller == b.CurrentCoordinates.Controller &&
                a.CurrentCoordinates.Location == b.CurrentCoordinates.Location &&
                a.CurrentCoordinates.Sequence == b.CurrentCoordinates.Sequence;
            var rebuilt = new ActivationCardIndex[cards.Length][];
            for (int i = 0; i < cards.Length; i++)
            {
                var entries = new List<ActivationCardIndex>();
                for (int j = 0; j < cards.Length; j++)
                    if (Same(cards[i], cards[j]))
                        entries.Add(new ActivationCardIndex
                        {
                            ActionDescription = descriptions != null && j < descriptions.Length ? descriptions[j] : 0,
                            ActionIndex = j
                        });
                // This card's own action first, so MainActivateFromIndex() with no index picks the listed option.
                entries.Sort((a, b) => (a.ActionIndex == i ? 0 : 1).CompareTo(b.ActionIndex == i ? 0 : 1));
                rebuilt[i] = entries.ToArray();
            }
            for (int i = 0; i < cards.Length; i++)
                cards[i].ActionActivateIndex = rebuilt[i];
        }
    }
}
