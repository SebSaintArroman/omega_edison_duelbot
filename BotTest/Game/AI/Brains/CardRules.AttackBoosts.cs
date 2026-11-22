using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Struct;
using System.Text.RegularExpressions;

namespace BotTest.Game.AI.Brains
{
    internal static partial class CardRules
    {
        private static readonly Regex SingleTargetAttackBoost = new(
            @"^(?:You can )?Target 1 (?:face-up )?monster (?:you control|on the field); it gains (?<amount>\d+) ATK until the end of (?:this|the) turn\.?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static int AttackBoostId(int id) => Search.SearchHost.CanonicalId(id) is > 0 and var alias ? alias : id;

        internal static bool IsAttackBoost(int id, int location) => AttackBoostId(id) switch
        {
            73729209 or 37742478 or 85215458 or 70046172 or 17814387 or 78211862 or 22346472 => true,
            55136228 => location != (int)CardLocation.Grave,
            _ => PrintedAttackBoost(id) > 0
        };

        internal static bool AttackBoostChoosesTarget(int id) => AttackBoostId(id) is not (37742478 or 85215458);

        internal static int AttackBoost(int id, int location, DynamicCard battler, DynamicCard? opponent) =>
            AttackBoostId(id) switch
            {
                73729209 => location == (int)CardLocation.Grave ? 800 : 400, // Skill Successor
                37742478 => (battler.Attribute & 0x10) != 0 ? Math.Max(0, opponent?.Attack ?? 0) : 0, // Honest
                85215458 => Search.SearchHost.Available && Search.SearchHost.Cards.Get(battler.ID)?.SetCodes()
                    .Any(code => (code & 0x0fff) == 0x33) == true ? 1400 : 0, // Kalut
                70046172 => 700, // Rush Recklessly
                17814387 => 500, // Reinforcements
                78211862 => 1500, // Rising Energy
                22346472 => 800, // Blustering Winds
                55136228 when location != (int)CardLocation.Grave && IsGladiatorBeast(battler.ID) => 500,
                _ => PrintedAttackBoost(id)
            };

        private static int PrintedAttackBoost(int id)
        {
            if (!Search.SearchHost.Available || Search.SearchHost.Cards.Get(id) is not { } card ||
                (card.Type & 0x6) == 0 || (card.Type & 0x60000) != 0)
                return 0;
            var match = SingleTargetAttackBoost.Match(card.Text.Trim());
            return match.Success && int.TryParse(match.Groups["amount"].Value, out int amount) ? amount : 0;
        }

        internal static HashSet<int> MinimumAttackBoosts(IEnumerable<(int Index, int Amount)> offers, int required)
        {
            if (required <= 0)
                return new();

            var strongest = offers.Where(offer => offer.Amount > 0).OrderByDescending(offer => offer.Amount).ToArray();
            int count = 0;
            int total = 0;
            while (count < strongest.Length && total < required)
                total += strongest[count++].Amount;
            if (total < required)
                return new();

            // Retain every card that can belong to a winning combination of the minimum size.
            int strongestWithoutLast = total - strongest[count - 1].Amount;
            return strongest.Take(count).Concat(strongest.Skip(count)
                .Where(offer => strongestWithoutLast + offer.Amount >= required))
                .Select(offer => offer.Index).ToHashSet();
        }

        internal static int RequiredAttackBoost(DynamicCard ours, DynamicCard? opponent, bool direct, bool ourTurn,
            int opponentLifePoints)
        {
            if ((ours.Position & (byte)CardPosition.FaceUpAttack) == 0)
                return int.MaxValue;

            bool opponentInAttack = opponent is { } card && (card.Position & (byte)CardPosition.FaceUpAttack) != 0;
            int power = opponentInAttack ? opponent!.Value.Attack : opponent?.Defense ?? 0;
            int toWinBattle = opponent.HasValue && (ours.Attack < power || opponentInAttack && ours.Attack == power)
                ? power + 1 - ours.Attack : int.MaxValue;
            bool piercing = ourTurn && opponent.HasValue && Search.SearchHost.Available &&
                (Search.SearchHost.Cards.Get(ours.ID)?.Text.Contains("inflict piercing battle damage",
                    StringComparison.OrdinalIgnoreCase) ?? false);
            int toWinGame = opponentInAttack || ourTurn && direct || piercing
                ? opponentLifePoints + power - ours.Attack : int.MaxValue;
            return toWinGame <= 0 ? int.MaxValue : Math.Min(toWinBattle, toWinGame);
        }
    }
}
