using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Struct;
using System.Text.RegularExpressions;
using static BotTest.Game.AI.CardId;

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
            SkillSuccessor or Honest or BlackwingKalutTheMoonShadow or RushRecklessly or Reinforcements or RisingEnergy or BlusteringWinds => true,
            IndomitableGladiatorBeast => location != (int)CardLocation.Grave,
            _ => PrintedAttackBoost(id) > 0
        };

        internal static bool AttackBoostChoosesTarget(int id) => AttackBoostId(id) is not (Honest or BlackwingKalutTheMoonShadow);

        internal static int AttackBoost(int id, int location, DynamicCard battler, DynamicCard? opponent) =>
            AttackBoostId(id) switch
            {
                SkillSuccessor => location == (int)CardLocation.Grave ? 800 : 400,
                Honest => (battler.Attribute & (int)CardAttribute.Light) != 0 ? Math.Max(0, opponent?.Attack ?? 0) : 0,
                BlackwingKalutTheMoonShadow => Search.SearchHost.Available && Search.SearchHost.Cards.Get(battler.ID)?.SetCodes()
                    .Any(code => (code & 0x0fff) == 0x33) == true ? 1400 : 0, // 0x33: the "Blackwing" archetype
                RushRecklessly => 700,
                Reinforcements => 500,
                RisingEnergy => 1500,
                BlusteringWinds => 800,
                IndomitableGladiatorBeast when location != (int)CardLocation.Grave && IsGladiatorBeast(battler.ID) => 500,
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

        /// <summary>
        /// THE attack-boost rule, in one place (user, 2026-11-26: "don't write it in three places then. write it just once"): which of our
        /// offered boosts may be chained in a battle. Only the fewest that make our battling monster win its battle (or the game), counting
        /// the boosts already on the chain (<paramref name="pending"/>); none with no battle, no attacker of ours, or nothing to win: an unknown
        /// face-down defender reads as DEF 0, so a boost into it is never needed. Callers add their own "doomed: use it anyway" exception.
        /// The search's chain filter (SearchMoveRules.ChainOptions) and the brain's chain rules (CardRulesInBrain) both call this.
        /// </summary>
        internal static HashSet<int> AllowedAttackBoosts(IEnumerable<(int Index, int Id, int Location)> boosts, DynamicCard? attacker,
            DynamicCard? defender, bool direct, bool ourTurn, int opponentLifePoints, int pending)
        {
            if (attacker is not { } battler)
                return new();
            // Our battler targeted by their chain (Dimensional Prison, Sakuretsu Armor): it's about to go, a boost is thrown away (DuelSim
            // 2026-11-26: Skill Successor on Darius in answer to their Dimensional Prison; the rule gate stopped it).
            if ((battler.ChainOnlyOwnTargets ?? Array.Empty<CardCoordinate>()).Any(link => link.Controller == 1))
                return new();
            int required = RequiredAttackBoost(battler, defender, direct, ourTurn, opponentLifePoints);
            if (required == int.MaxValue)
                return new();
            return MinimumAttackBoosts(boosts.Select(b => (b.Index, AttackBoost(b.Id, b.Location, battler, defender))), required - pending);
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
