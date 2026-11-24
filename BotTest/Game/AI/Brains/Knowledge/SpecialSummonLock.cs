using BotTest.Game.Field;
using BotTest.Utils;
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
    /// A face-up card that stops Special Summons (theirs or both players), read from card text.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// The face-up card that stops us Special Summoning (Archlord Kristya: "Neither player can Special Summon"), or null.
        /// While it's up, every Special Summon effect of ours is worth nothing (user, 2026-11-13: "all special summon effects
        /// should have a score of 0 while krystia is on the field"): e.g. Hamster isn't set for its flip (as a last-resort
        /// blocker it still can be).
        /// </summary>
        protected string? SpecialSummonLockedBy { get; private set; }

        private static readonly System.Text.RegularExpressions.Regex NeitherPlayerSpecialText = new(
            @"neither player can special summon", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex OpponentCannotSpecialText = new(
            @"your opponent cannot special summon", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private async Task UpdateSpecialSummonLockAsync(FieldInfo field)
        {
            SpecialSummonLockedBy = null;
            async Task Check(CardInfo side, bool theirs)
            {
                foreach (var zone in side.MonsterZones ?? Array.Empty<CardArray>())
                {
                    if ((zone.Cards ?? Array.Empty<int>()) is not { Length: > 0 } ids || ids[0] <= 0 || SpecialSummonLockedBy != null)
                        continue;
                    if (await CardDatabase.GetAsync(ids[0]) is DBCardInfo info &&
                        (NeitherPlayerSpecialText.IsMatch(info.CardText ?? "") || theirs && OpponentCannotSpecialText.IsMatch(info.CardText ?? "")))
                        SpecialSummonLockedBy = CardNames.Get(ids[0]);
                }
            }
            await Check(field.EnemySide, theirs: true);
            await Check(field.OwnSide, theirs: false);
        }
    }
}
