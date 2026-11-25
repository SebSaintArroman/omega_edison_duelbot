using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    internal static partial class CardRules
    {
        private static readonly int[] OtherBlackwings =
        {
            BlackwingVayuTheEmblemOfHonor, BlackwingBlizzardTheFarNorth, BlackwingBoraTheSpear, BlackwingShuraTheBlueFlame,
            BlackwingGaleTheWhirlwind, BlackwingKalutTheMoonShadow, BlackwingArmedWing, BlackwingArmorMaster,
            BlackwingSilverwindTheAscendant,
        };

        private static bool IsBlackwingCard(int id) =>
            Search.SearchHost.Available && Search.SearchHost.Cards.Get(id)?.SetCodes().Any(code => (code & 0x0fff) == 0x33) == true;

        /// <summary>
        /// Whether Sirocco's boost makes one lethal attack (user, 2026-11-25). The boosted Blackwing gets the ATK of every other face-up
        /// Blackwing of ours and is the only one to attack, so its ATK is all our attack-position Blackwings' together. Lethal needs:
        ///  1. no face-down spell/trap of theirs;
        ///  2. no Necro Gardna in their graveyard, no Honest or Kalut known in their hand, and no Honest suspected there (user,
        ///     2026-11-25: "yes count suspicion");
        ///  3. their field empty, or an attack-position monster the attack goes through for lethal, or (with our Bora, which takes the
        ///     boost: SiroccoTargets) a defence-position monster it pierces for lethal: a face-down one at the highest-DEF Normal Summon in
        ///     their list (OpponentLikelySetDefense).
        /// Anything unknown (their field, backrow or hand not given): not lethal.
        /// </summary>
        private static bool SiroccoLethal(CardRuleRegistry.Context context)
        {
            int lp = context.OpponentLifePoints;
            if (context.OwnFaceUpAttackers is not { } attackers || context.OpponentMonsters is not { } theirs || lp <= 0 ||
                context.OpponentSetSpellTraps != 0)
                return false;
            if (context.HonestSuspected || (context.OpponentGrave ?? Array.Empty<int>()).Any(id => Search.SearchHost.Is(id, NecroGardna)) ||
                (context.OpponentHand ?? Array.Empty<int>()).Any(id => Search.SearchHost.Is(id, Honest) || Search.SearchHost.Is(id, BlackwingKalutTheMoonShadow)))
                return false;
            int total = attackers.Where(m => IsBlackwingCard(m.Id)).Sum(m => m.Attack);
            if (total < lp)
                return false;
            if (theirs.Count == 0)
                return true;
            bool bora = attackers.Any(m => Search.SearchHost.Is(m.Id, BlackwingBoraTheSpear));
            foreach (var m in theirs)
            {
                if (m.FaceUp && m.AttackPosition)
                {
                    if (total > m.Attack && total - m.Attack >= lp)
                        return true;
                    continue;
                }
                int defense = m.FaceUp ? m.Defense : context.OpponentLikelySetDefense;
                if (bora && defense >= 0 && total > defense && total - defense >= lp)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Sirocco's tags to lift (CardRules: DoNotActivate and ActivateBeforeBattle both start on). DoNotActivate goes with another
        /// Blackwing of ours on the field and their field not empty, or empty with the boost a one-attack lethal. ActivateBeforeBattle (used
        /// right before the Battle Phase, as Main Phase 1's last step) stays only for a one-attack lethal (SiroccoLethal) with our Battle Phase
        /// still ahead. Their field unknown (no OpponentMonsterIds): only the other-Blackwing rule.
        /// </summary>
        private static TagRules.Tag SiroccoTagsToDisable(CardRuleRegistry.Context context)
        {
            bool otherBlackwing = context.OwnMonsterIds?.Any(id => OtherBlackwings.Any(b => Search.SearchHost.Is(id, b))) == true;
            bool open = context.OpponentMonsterIds is { Count: 0 };
            bool lethal = otherBlackwing && SiroccoLethal(context);
            var lift = TagRules.Tag.None;
            if (otherBlackwing && (!open || lethal))
                lift |= TagRules.Tag.DoNotActivate;
            if (!(lethal && context.BattleAhead))
                lift |= TagRules.Tag.ActivateBeforeBattle;
            return lift;
        }

        /// <summary>Sirocco's target: our Bora when the boost is the one-attack lethal (SiroccoLethal) and Bora is on offer; otherwise any
        /// (user, 2026-11-25: "just do the bora restriction if there's the lethal rules applying"; Shura stays a good target otherwise).</summary>
        private static IReadOnlyCollection<int> SiroccoTargets(CardRuleRegistry.Context context, IReadOnlyCollection<int> candidates)
        {
            var bora = candidates.Where(id => Search.SearchHost.Is(id, BlackwingBoraTheSpear)).ToArray();
            return bora.Length > 0 && SiroccoLethal(context) ? bora : candidates;
        }
    }
}
