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
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// RULE deck.no-redundant-sets
        /// No two set traps for the same issue, and no more than MaxExposedSets exposed (SetRedundantAsync). Removes the
        /// redundant spell/trap sets from the options.
        /// </summary>
        private async Task<DecisionContext<IdleCmdData>> DropRedundantSetsAsync(DecisionContext<IdleCmdData> context)
        {
            if ((context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>()).Length == 0)
                return context;
            int handCount = (await GetBotHandCardsAsync()).Count;
            var keep = new List<DynamicCard>();
            foreach (var card in context.StateData.SpellSetableCards!)
                if (card.ID <= 0 || await SetRedundantAsync(card.ID, handCount) == null || !Rule(DeckRules.NoRedundantSets))
                    keep.Add(card);
            var noRedundant = context.StateData;
            noRedundant.SpellSetableCards = keep.ToArray();
            return context with { StateData = noRedundant };
        }

        // ------------------------------------------------------------------ overlapping sets


        /// <summary>
        /// Most exposed (not storm-proof) set cards we keep at once. Above this, one Heavy Storm is a disaster. Can be
        /// exceeded only when the opponent can no longer have Heavy Storm, or the card would be discarded at the End Phase.
        /// </summary>
        [Tunable("Most exposed (not storm-proof) set cards we keep at once. Above this, one Heavy Storm is a disaster. Can be exceeded only when the opponent can no longer have Heavy Storm, or the card would be discarded at the End Phase.")]
        public int MaxExposedSets { get; set; } = 2;

        /// <summary>Which issue a trap answers. Two set traps for the same issue only give a Heavy Storm more to take.</summary>
        protected enum TrapRole { None, Attack, Summon }

        protected virtual TrapRole RoleOf(int id) => id switch
        {
            MirrorForce or DimensionalPrison or SakuretsuArmor => TrapRole.Attack,
            TorrentialTribute or BottomlessTrapHole => TrapRole.Summon,
            _ => TrapRole.None,
        };

        /// <summary>Answers that deal with every attacker at once: a second attack answer next to one adds nothing.</summary>
        protected virtual bool AnswersEveryAttacker(int id) => id == MirrorForce;

        /// <summary>
        /// Returns why setting <paramref name="cardId"/> now would be redundant or overexposed, or null to set it.
        /// Redundant: a set trap already answers the same issue. A second attack answer is still set when the opponent has
        /// more big threats than our set single-target answers cover (and no Mirror Force is set). Overexposed: we already
        /// have <see cref="MaxExposedSets"/> exposed set cards. Neither applies to storm-proof cards, when the card would be
        /// discarded at the End Phase (<paramref name="handCount"/> 7+), or (for the cap) when they can't have Heavy Storm.
        /// </summary>
        protected async Task<string?> SetRedundantAsync(int cardId, int handCount)
        {
            if (IsStormFree(cardId) || handCount > 6)
                return null;
            var set = (await CurrentField.GetCardsInSpellZone())
                .Where(card => card.ID > 0 && !card.IsFaceup() && !IsStormFree(card.ID)).ToList();
            var role = RoleOf(cardId);
            // Only the answers we expect to need next turn (user, 2026-11-13, round-robin 4: Mirror Force + Bottomless + Jar on
            // turn 2, all taken by Heavy Storm; "I'd have set one real trap and one jar of greed and stopped there ... maybe even
            // 0 real traps if I'd set a ryko or a battle recruiter"). Later in the duel (higher risk tolerance) one spare.
            if (role != TrapRole.None)
            {
                var (have, haveWhat) = await SetAnswersAsync();
                var (need, needWhy) = await AnswersNeededNextTurnAsync();
                var (tol, tolWhy) = await RiskToleranceAsync();
                // After their Heavy Storm is gone, one more is fine (user, 2026-11-13: "after heavy storm more should be allowed"),
                // unless their deck punishes backrow other ways ("blackwings and glads can still punish it even after heavy storm").
                bool stormGone = await OpponentHeavyStormChanceAsync() < StormGoneChance;
                string? punisher = stormGone ? await OpponentBackrowPunisherAsync() : null;
                int allowed = Math.Max(1, need + (tol >= ExtraSetRiskTolerance ? 1 : 0) + (stormGone && punisher == null ? 1 : 0));
                if (have >= allowed && Rule(DeckRules.OnlyNeededAnswers))
                    return $"we already have {have} answer(s) set ({haveWhat}) and expect to need {need} next turn ({needWhy}; {tolWhy}" +
                           (stormGone ? punisher != null ? $"; their Heavy Storm is gone but {punisher}" : "; their Heavy Storm is gone: one more allowed" : "") +
                           "): only commit the answers we'll need";
            }
            if (role != TrapRole.None)
            {
                var same = set.Where(card => RoleOf(card.ID) == role).ToList();
                if (same.Count > 0)
                {
                    string have = string.Join(", ", same.Select(card => CardNames.Get(card.ID)));
                    if (role != TrapRole.Attack || same.Any(card => AnswersEveryAttacker(card.ID)))
                        return $"{have} already answers the same thing (one answer per issue; a second only gives Heavy Storm more)";
                    int big = 0;
                    foreach (var enemy in await GetEnemyMonsterCardsAsync())
                        if (enemy.ID > 0 && enemy.IsFaceup() && await IsBigThreatAsync(enemy))
                            big++;
                    if (big <= same.Count)
                        return $"{have} already answers their attacks ({big} big threat(s) on their side, {same.Count} answer(s) set)";
                }
            }
            // The opponent's deck can threaten our backrow from its board (X-Sabers: Emmersblade -> Hyunlei).
            if (await MatchupHoldSetAsync(set.Count) is string matchupHold)
                return matchupHold;
            if (set.Count >= MaxExposedSets && await OpponentHeavyStormChanceAsync() > 0)
            {
                // Under pressure the cap gives (CSV S3, user: "if they have two threats already on field and you only have enough traps
                // to answer those two threats you may need a third trap even if they might have heavy"): their big threats outnumber
                // or match our sets, or the risk tolerance is past half-way (low LP, a stall, the deck race, losing next turn).
                await RiskToleranceAsync();
                int bigThreats = (await GetEnemyMonsterCardsAsync()).Count(m => m.IsFaceup() && m.Attack >= StrongMonsterAttack);
                if (bigThreats >= set.Count || RiskPressNow >= OversetPressFrom)
                    return null;
                return $"already {set.Count} exposed set cards (max {MaxExposedSets}): one Heavy Storm would take them all";
            }
            return null;
        }

        /// <summary>
        /// Order to set in: attack answers first (Mirror Force first when they have 2+ monsters, since it takes them all,
        /// otherwise Dimensional Prison, which banishes), then summon answers, then other traps, then bait and
        /// storm-proof cards last (they don't count towards the cap, so they still go down after).
        /// </summary>
        protected int SetOrder(int cardId, int enemyMonsters) =>
            RoleOf(cardId) == TrapRole.Attack ? ((cardId == MirrorForce) == (enemyMonsters >= 2) ? 0 : 1)
            : RoleOf(cardId) == TrapRole.Summon ? 2
            : IsStormFree(cardId) ? 4
            : 3;
    }
}
