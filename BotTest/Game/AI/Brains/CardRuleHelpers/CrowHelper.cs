using static BotTest.Game.AI.CardId;
namespace BotTest.Game.AI.Brains
{
    internal static partial class CardRules
    {
        private static bool CrowMayActivate(CardRuleRegistry.Context context)
        {
            var opponentGrave = context.OpponentGrave ?? Array.Empty<int>();
            var opponentHand = context.OpponentHand ?? Array.Empty<int>();
            bool possibleInOpponentHand(int id) => context.UnknownOpponentHandCards ||
                opponentHand.Any(code => Search.SearchHost.Is(code, id));
            int darks = opponentGrave.Count(id => (Search.SearchHost.Type(id) & 0x1) != 0 &&
                (Search.SearchHost.Attribute(id) & 0x20) != 0);
            int fairies = opponentGrave.Count(id => (Search.SearchHost.Type(id) & 0x1) != 0 &&
                (Search.SearchHost.Race(id) & 0x4) != 0);
            bool targetsTheirGrave = (context.TargetedOpponentGraveCards?.Count ?? 0) > 0;
            bool chainsVayu = context.ActiveChainPlayer == 1 && Search.SearchHost.Is(context.ActiveChainCardId, BlackwingVayuTheEmblemOfHonor);
            bool chainsPlaguespreader = context.ActiveChainPlayer == 1 && Search.SearchHost.Is(context.ActiveChainCardId, PlaguespreaderZombie);
            bool oneFortressInGrave = opponentGrave.Count(id => Search.SearchHost.Is(id, MachinaFortress)) == 1;
            bool DADCouldBeSummoned = darks == 3 && possibleInOpponentHand(DarkArmedDragon);
            bool KristyaCouldBeSummoned = fairies == 4 && possibleInOpponentHand(ArchlordKristya);
            bool KingOfBeastsCouldReturn = (context.OpponentMonsterIds ?? Array.Empty<int>())
                .Any(id => Search.SearchHost.Is(id, Moja)) &&
                opponentGrave.Count(id => Search.SearchHost.Is(id, KingOfTheBeasts)) == 1;
            bool GardnaThreat = (context.OwnGrave ?? Array.Empty<int>()).Any(id => Search.SearchHost.Is(id, NecroGardna)) &&
                (!context.IsOurTurn || context.OurFaceUpAttackers > 1);

            return targetsTheirGrave || chainsVayu || chainsPlaguespreader || oneFortressInGrave ||
                DADCouldBeSummoned || KristyaCouldBeSummoned || KingOfBeastsCouldReturn || GardnaThreat;
        }

        private static IReadOnlyCollection<int> CrowTargets(CardRuleRegistry.Context context, IReadOnlyCollection<int> candidates)
        {
            var targets = new HashSet<int>();
            bool Available(int id) => candidates.Any(code => Search.SearchHost.Is(code, id));
            void Add(int id)
            {
                if (Available(id)) targets.Add(Search.SearchHost.CanonicalId(id) is > 0 and var canonical ? canonical : id);
            }
            var opponentGrave = context.OpponentGrave ?? Array.Empty<int>();
            bool InAnyGrave(int id) => opponentGrave.Concat(context.OwnGrave ?? Array.Empty<int>())
                .Any(code => Search.SearchHost.Is(code, id));

            foreach (int target in context.TargetedOpponentGraveCards ?? Array.Empty<int>()) Add(target);
            if (context.ActiveChainPlayer == 1 && Search.SearchHost.Is(context.ActiveChainCardId, BlackwingVayuTheEmblemOfHonor))
            {
                var pair = (context.ActiveChainTargetIds ?? Array.Empty<int>()).Where(id => InAnyGrave(id) && Available(id)).ToArray();
                if (Available(context.ActiveChainCardId) && pair.Length != 1) Add(context.ActiveChainCardId);
                else foreach (int id in pair) Add(id);
            }
            if (context.ActiveChainPlayer == 1 && Search.SearchHost.Is(context.ActiveChainCardId, PlaguespreaderZombie))
                Add(context.ActiveChainCardId);
            if (opponentGrave.Count(id => Search.SearchHost.Is(id, MachinaFortress)) == 1) Add(MachinaFortress);

            bool possibleInOpponentHand(int id) => context.UnknownOpponentHandCards ||
                (context.OpponentHand ?? Array.Empty<int>()).Any(code => Search.SearchHost.Is(code, id));
            int darks = opponentGrave.Count(id => (Search.SearchHost.Type(id) & 0x1) != 0 &&
                (Search.SearchHost.Attribute(id) & 0x20) != 0);
            if (darks == 3 && possibleInOpponentHand(DarkArmedDragon))
                foreach (int id in opponentGrave.Where(id => (Search.SearchHost.Type(id) & 0x1) != 0 &&
                             (Search.SearchHost.Attribute(id) & 0x20) != 0)) Add(id);
            int fairies = opponentGrave.Count(id => (Search.SearchHost.Type(id) & 0x1) != 0 &&
                (Search.SearchHost.Race(id) & 0x4) != 0);
            if (fairies == 4 && possibleInOpponentHand(ArchlordKristya))
                foreach (int id in opponentGrave.Where(id => (Search.SearchHost.Type(id) & 0x1) != 0 &&
                             (Search.SearchHost.Race(id) & 0x4) != 0)) Add(id);
            if ((context.OpponentMonsterIds ?? Array.Empty<int>()).Any(id => Search.SearchHost.Is(id, Moja)) &&
                opponentGrave.Count(id => Search.SearchHost.Is(id, KingOfTheBeasts)) == 1) Add(KingOfTheBeasts);
            if ((context.OwnGrave ?? Array.Empty<int>()).Any(id => Search.SearchHost.Is(id, NecroGardna)) &&
                (!context.IsOurTurn || context.OurFaceUpAttackers > 1)) Add(NecroGardna);

            return targets.Count > 0 ? targets.ToArray() : candidates;
        }
    }
}
