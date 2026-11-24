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
    /// Monsters that destroy what they battle by effect (Catastor), learned from card text.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // Our monsters that destroy any monster they battle unless it has a given Attribute (Ally of Justice Catastor:
        // "battles a non-DARK monster ... destroy"), filled from card text. Id -> the Attribute that is spared.
        // Known ones up front (Ally of Justice Catastor, official and Omega ids): never left to a card text lookup that can fail.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, CardAttribute> BattleDestroyersExcept = new()
        {
            [AllyOfJusticeCatastor] = CardAttribute.Dark,
            [AllyOfJusticeCatastorPreErrata] = CardAttribute.Dark,
        };
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> CheckedBattleDestroyers = new();

        private static readonly System.Text.RegularExpressions.Regex BattleDestroyerText = new(
            @"battles a non-(dark|light|earth|water|fire|wind|divine) monster[^.]*destroy",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Reads our monsters' text once each for Catastor-like battle destruction.</summary>
        protected static async Task LearnBattleDestroyersAsync(IEnumerable<int> ourMonsterIds)
        {
            foreach (int id in ourMonsterIds.Where(id => id > 0 && !CheckedBattleDestroyers.ContainsKey(id)).Distinct().ToList())
            {
                // Marked as checked only once its text is read: a failed lookup (server busy) is retried next time instead of
                // leaving a Catastor unknown for the rest of the session (seen 2026-11-10: Goyo attacked into Catastor).
                if (await CardDatabase.GetAsync(id) is not DBCardInfo info)
                    continue;
                CheckedBattleDestroyers.TryAdd(id, 0);
                var match = BattleDestroyerText.Match(info.CardText ?? "");
                if (match.Success && System.Enum.TryParse<CardAttribute>(match.Groups[1].Value, ignoreCase: true, out var spared))
                    BattleDestroyersExcept[id] = spared;
            }
        }

        /// <summary>
        /// Whether their monster with this id destroys a monster of this Attribute by its effect when they battle (their
        /// Catastor vs our non-DARK monster), whoever attacks.
        /// </summary>
        protected static bool DestroysAttributeInBattle(int monsterId, int attribute) =>
            monsterId > 0 && BattleDestroyersExcept.TryGetValue(monsterId, out var spared) && (attribute & (int)spared) == 0;

        /// <summary>Whether the attacker's effect destroys this (face-up) defender whatever the ATK (Catastor vs non-DARK).</summary>
        protected static bool DestroysByEffectInBattle(DynamicCard attacker, DynamicCard defender) =>
            BattleDestroyersExcept.TryGetValue(attacker.ID, out var spared) && defender.IsFaceup() &&
            (defender.Attribute & (int)spared) == 0;
    }
}
