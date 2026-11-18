using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Staple synchros (Catastor, Goyo, Stardust, Black Rose, Colossal Fighter, ...) are shared by many decks, so what
    /// they can do is read from the bot's actual Extra Deck and the cards' text here, not hard-coded per deck:
    ///  - which synchro a given Level makes, and which opponent threat it answers (Catastor destroys any non-DARK
    ///    monster it battles, whatever its ATK; others answer by out-sizing the threat),
    ///  - their battle behaviour (Catastor-type destruction) is in DeckBotBrain's attack scoring.
    /// Deck brains decide when a synchro is worth the cards (their own resource rules), using these answers.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>A synchro we could summon, read from our Extra Deck.</summary>
        protected readonly record struct SynchroOption(int Id, int Level, int Attack, CardAttribute? DestroysAllBut, int Attribute = 0, bool Pierces = false);

        /// <summary>The distinct synchros left in our Extra Deck, with their Catastor-type battle effect if any.</summary>
        protected async Task<List<SynchroOption>> ExtraDeckSynchrosAsync()
        {
            var ids = (await CurrentField.GetCardsInLocation(CardLocation.Extra)).Select(card => card.ID).Where(id => id > 0).Distinct().ToList();
            await LearnBattleDestroyersAsync(ids);
            var options = new List<SynchroOption>();
            foreach (int id in ids)
            {
                if (await CardDatabase.GetAsync(id) is not DBCardInfo info || (info.Type & (int)CardType.Synchro) == 0)
                    continue;
                options.Add(new SynchroOption(id, info.Level, info.Attack,
                    BattleDestroyersExcept.TryGetValue(id, out var spared) ? spared : null, info.Attribute,
                    Pierces: (info.CardText ?? "").Contains("piercing", StringComparison.OrdinalIgnoreCase)));
            }
            return options;
        }

        /// <summary>The monster the last SynchroAnswer said this synchro battles (beats, trades with, destroys), or null (a bounce, no answer).</summary>
        protected DynamicCard? SynchroAnswerTarget { get; private set; }

        /// <summary>
        /// Which face-up opponent threat (a monster our board can't already get past) this synchro answers, and how:
        /// a Catastor-type destroys any monster of another Attribute it battles; any synchro answers a threat it
        /// out-sizes (or trades with at equal ATK in attack position). Returns the reason, or null.
        /// </summary>
        /// <param name="discards">Hand cards we'd still hold after summoning it: Brionac's bounce needs one per card.</param>
        protected string? SynchroAnswer(SynchroOption synchro, List<DynamicCard> enemyMonsters, IEnumerable<int> ourAttack, int discards = 0)
        {
            SynchroAnswerTarget = null;
            int best = ourAttack.DefaultIfEmpty(0).Max();
            var threats = enemyMonsters
                // A battle-proof monster (Gellenduo) walls our attacks whatever its size: a threat too.
                .Where(card => card.IsFaceup() && card.ID > 0 && !BouncesAttacker(card) && (card.GetRealPower() >= best || IsBattleProof(card)))
                .OrderByDescending(TargetThreat)
                .ToList();
            // Brionac bounces (a discard each), whatever their ATK: an Extra Deck monster goes back for good (seen 2026-11-10:
            // Brionac held against Stardust Dragon and King of the Beasts with three cards in hand).
            if (BounceSynchroIds.Contains(synchro.Id) && discards > 0)
            {
                var bounced = threats.OrderByDescending(BounceRemoves).ThenByDescending(TargetThreat).Take(discards).ToList();
                if (bounced.Count > 0)
                    return $"{CardNames.Get(synchro.Id)} bounces {string.Join(" and ", bounced.Select(card => CardNames.Get(card.ID) + (BounceRemoves(card) ? " (back to the Extra Deck)" : "")))} " +
                           $"(discard {bounced.Count})";
            }
            foreach (var threat in threats)
            {
                // Can't be destroyed by battle: out-attacking it isn't an answer. Piercing damage past it in defence is (user,
                // 2026-11-13: against Gellenduo "both brionac and flamvell uruquazas are great picks - I'd have gone for
                // uruquazas"); Brionac's bounce is above.
                if (IsBattleProof(threat))
                {
                    if (synchro.Pierces && !threat.IsAttack() && synchro.Attack > threat.Defense)
                    {
                        SynchroAnswerTarget = threat;
                        return $"{CardNames.Get(synchro.Id)} ({synchro.Attack}) pierces past {CardNames.Get(threat.ID)} (can't be destroyed by battle, " +
                               $"DEF {threat.Defense}): {synchro.Attack - threat.Defense} damage an attack";
                    }
                    continue;
                }
                if (synchro.DestroysAllBut is CardAttribute spared && (threat.Attribute & (int)spared) == 0)
                {
                    SynchroAnswerTarget = threat;
                    return $"{CardNames.Get(synchro.Id)} destroys any non-{spared} monster it battles: answers {CardNames.Get(threat.ID)} ({threat.GetRealPower()})";
                }
                // Their Catastor-type destroys our synchro by effect when they battle, whatever the ATK: not an answer.
                if (DestroysAttributeInBattle(threat.ID, synchro.Attribute))
                    continue;
                if (synchro.Attack > threat.GetRealPower())
                {
                    SynchroAnswerTarget = threat;
                    return $"{CardNames.Get(synchro.Id)} ({synchro.Attack}) beats {CardNames.Get(threat.ID)} ({threat.GetRealPower()})";
                }
                if (synchro.Attack == threat.GetRealPower() && threat.IsAttack())
                {
                    SynchroAnswerTarget = threat;
                    return $"{CardNames.Get(synchro.Id)} ({synchro.Attack}) trades with {CardNames.Get(threat.ID)} ({threat.GetRealPower()})";
                }
            }
            return null;
        }

        /// <summary>
        /// The best synchro of this Level in our Extra Deck: one that answers a threat (Catastor into a non-DARK boss
        /// beats a bigger vanilla body), otherwise the biggest. Null if we have none of that Level.
        /// </summary>
        // Brionac, Dragon of the Ice Barrier (official and Omega's pre-errata id): discard cards to return as many cards to the hand.
        protected static readonly int[] BounceSynchroIds = { 50321796, 50321816 };

        /// <summary>
        /// A synchro of this Level that wins this turn, with the reason, or null. Our other attackers plus the synchro must
        /// cover their life points once nothing blocks: with no monster of theirs left, or with a bouncer (Brionac) that can
        /// return every monster of theirs (one discard each, from <paramref name="handAfterCost"/>). Lethal comes first: it
        /// beats any synchro that only answers a threat (seen 2026-11-10: Goyo picked over a lethal Brionac).
        /// </summary>
        protected async Task<(SynchroOption Synchro, string Answer)?> LethalSynchroForLevelAsync(int level, List<DynamicCard> enemyMonsters,
            int otherAttackers, int handAfterCost, int oppLp, bool canAttack)
        {
            if (!canAttack || oppLp <= 0)
                return null;
            foreach (var synchro in (await ExtraDeckSynchrosAsync()).Where(s => s.Level == level).OrderByDescending(s => s.Attack))
            {
                int bounces = BounceSynchroIds.Contains(synchro.Id) ? Math.Min(handAfterCost, enemyMonsters.Count) : 0;
                if (enemyMonsters.Count - bounces > 0 || synchro.Attack + otherAttackers < oppLp)
                    continue;
                string how = bounces > 0 ? $"bounce {(bounces == 1 ? CardNames.Get(enemyMonsters[0].ID) : $"their {bounces} monsters")} (discard {bounces}) and attack" : "attack into their empty field";
                return (synchro, $"lethal: {CardNames.Get(synchro.Id)} can {how} for {synchro.Attack + otherAttackers} into {oppLp} LP");
            }
            return null;
        }

        protected async Task<(SynchroOption Synchro, string? Answer)?> BestSynchroForLevelAsync(
            int level, List<DynamicCard> enemyMonsters, IEnumerable<int> ourAttack)
        {
            var candidates = (await ExtraDeckSynchrosAsync()).Where(s => s.Level == level).ToList();
            if (candidates.Count == 0)
                return null;
            var attack = ourAttack.ToList();
            var answering = candidates
                .Select(s => (Synchro: s, Answer: SynchroAnswer(s, enemyMonsters, attack)))
                .Where(x => x.Answer != null)
                .OrderByDescending(x => x.Synchro.Attack)
                .FirstOrDefault();
            if (answering.Answer != null)
                return answering;
            var biggest = candidates.OrderByDescending(s => s.Attack).First();
            return (biggest, null);
        }
    }
}
