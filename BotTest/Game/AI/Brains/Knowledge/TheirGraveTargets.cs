using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// The cards in their graveyard their chain link targets, read from each graveyard card's own "targeted by" lists (OwnTargets /
    /// ChainOnlyOwnTargets) on a fresh card read, the way ChainBait finds their removal aimed at our set cards. Used when the activating
    /// card itself reports no target (live 2026-11-25: Debris Dragon's revive targeted Dandylion, no target was reported, and D.D. Crow
    /// banished a Lonefire Blossom; user: "you need to find a way to see which is being targeted. you managed it for other things").
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>Their graveyard indexes some card targets now (empty: none reported). Logged either way, so the logs show whether
        /// Omega reports graveyard targets at all.</summary>
        protected async Task<List<int>> TheirGraveTargetsAsync(DuelBotWrapper bot)
        {
            var found = new List<int>();
            var grave = (await CurrentField.GetBotField()).EnemySide.Grave ?? Array.Empty<int>();
            var probed = new List<string>();
            for (int i = 0; i < grave.Length; i++)
            {
                // Omega's controller numbering for their side isn't fixed (ChainBait, TheirChainLinkAsync): both reads, the one holding this card.
                foreach (int controller in new[] { 1, 0 })
                {
                    DynamicCard card;
                    try { card = await CurrentField.GetBotFieldCard(controller, (int)CardLocation.Grave, i); }
                    catch { continue; }
                    if (card.ID <= 0 || card.ID != grave[i])
                        continue;
                    int marks = (card.OwnTargets?.Length ?? 0) + (card.ChainOnlyOwnTargets?.Length ?? 0) +
                                (card.CurrentChainTargets?.Length ?? 0) + (card.TargetCards?.Length ?? 0);
                    probed.Add($"{OmegaBotWrapper.Logging.CardNames.Get(card.ID)}={marks}");
                    if ((card.OwnTargets?.Length ?? 0) + (card.ChainOnlyOwnTargets?.Length ?? 0) > 0)
                        found.Add(i);
                    break;
                }
            }
            bot.LogNote($"deck brain: graveyard target probe: {(found.Count > 0 ? string.Join(", ", found.Select(i => OmegaBotWrapper.Logging.CardNames.Get(grave[i]))) + " targeted" : "no targeted card")} " +
                        $"[{string.Join(" ", probed)}]");
            return found;
        }
    }
}
