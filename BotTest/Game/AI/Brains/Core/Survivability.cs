using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Survivability (user, 2026-11-11): low on cards and with nothing set to answer their attacker, a monster of ours that
    /// they'll probably run over in their turn just hands them a free card. Then a bigger body is worth a resource (e.g.
    /// Plaguespreader into a synchro) even when it isn't otherwise needed. "This shouldn't come up often but it's worth
    /// having in the general brain for those edge cases."
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>Fewer than this many cards of ours (hand, monsters, spells/traps) counts as "low on cards".</summary>
        [Tunable("Fewer than this many cards of ours (hand, monsters, spells/traps) counts as \"low on cards\".")]
        public int SurvivabilityCardLimit { get; set; } = 3;

        /// <summary>How much less likely the new monster must be to be run over (0-1) for the spend to count. Tune from logs.</summary>
        [Tunable("How much less likely the new monster must be to be run over (0-1) for the spend to count. Tune from logs. was 0.3 (user, 2026-11-16: \"make them a little bit stronger\", slowly)")]
        public double SurvivabilityGain { get; set; } = 0.25; // was 0.3 (user, 2026-11-16: "make them a little bit stronger", slowly)

        /// <summary>
        /// Whether we're exposed: low on cards (fewer than SurvivabilityCardLimit in hand and on the field) and no set
        /// spell/trap of ours that could answer their attacker. Null if not; else how many cards we hold.
        /// </summary>
        private async Task<int?> ExposedLowOnCardsAsync()
        {
            int hand = (await GetBotHandCardsAsync()).Count;
            var spells = (await CurrentField.GetCardsInSpellZone()).Where(card => card.ID > 0 || card.Position != 0).ToList();
            int monsters = (await GetBotMonsterCardsAsync()).Count;
            int total = hand + spells.Count + monsters;
            if (total >= SurvivabilityCardLimit || spells.Any(card => !card.IsFaceup()))
                return null;
            return total;
        }

        /// <summary>
        /// The chance their next turn brings a monster that runs over a monster of ours with this ATK: their face-up monsters,
        /// monsters we know are in their hand (Normal Summonable ones surely, bigger ones half), their graveyard's monsters
        /// (a revive: a small chance each), and their unknown hand by the deck read (Normal Summonable monsters only). An equal
        /// ATK counts half (a trade).
        /// </summary>
        private async Task<double> RunOverChanceAsync(int atk)
        {
            var sources = new List<(int Atk, double Chance)>();
            foreach (var monster in (await GetEnemyMonsterCardsAsync()).Where(m => m.ID > 0 && m.IsFaceup()))
                sources.Add((monster.Attack, 1));
            static (int Atk, int Level, bool Monster)? Data(int id) =>
                Search.SearchHost.Available && Search.SearchHost.Cards.Get(id) is { } c ? (c.Attack, c.Level, (c.Type & 0x1) != 0 && !c.IsExtraDeckMonster) : null;
            var known = KnownEnemyHand();
            foreach (int id in known)
                if (Data(id) is { Monster: true } d)
                    sources.Add((d.Atk, d.Level <= 4 ? 1 : KnownTributeMonsterChance));
            foreach (int id in (await CurrentField.GetBotField()).EnemySide.Grave ?? Array.Empty<int>())
                if (id > 0 && Data(id) is { Monster: true } d)
                    sources.Add((d.Atk, GraveReviveChance));
            int unknownHand = Math.Max(0, ((await CurrentField.GetBotField()).EnemySide.Hand?.Length ?? 0) - known.Count);
            if (unknownHand > 0)
            {
                List<(int Id, double Chance)> likely;
                lock (OpponentDeck)
                    likely = OpponentDeck.LikelyMonsters(unknownHand + 1, 20); // +1: their draw
                foreach (var (id, chance) in likely)
                    if (Data(id) is { Monster: true, Level: <= 4 } d)
                        sources.Add((d.Atk, chance));
            }
            double survives = 1;
            foreach (var (sourceAtk, chance) in sources)
                if (sourceAtk > atk)
                    survives *= 1 - Math.Clamp(chance, 0, 1);
                else if (sourceAtk == atk)
                    survives *= 1 - Math.Clamp(chance, 0, 1) / 2;
            return 1 - survives;
        }

        /// <summary>
        /// Why this spend is worth it for survivability (see the class summary), or null: we're exposed, our best monster now
        /// is likely run over in their turn, and the new one (at newAtk) much less so.
        /// </summary>
        private async Task<string?> SurvivabilityReasonAsync(string what, int newAtk, IReadOnlyCollection<DynamicCard>? fieldMaterials)
        {
            if (newAtk <= 0 || await ExposedLowOnCardsAsync() is not int cards)
                return null;
            var ours = (await GetBotMonsterCardsAsync()).Where(m => m.ID > 0 && m.IsFaceup()).ToList();
            int bestNow = ours.Select(EffectiveAttack).DefaultIfEmpty(0).Max();
            if (newAtk <= bestNow)
                return null;
            double before = bestNow > 0 ? await RunOverChanceAsync(bestNow) : 1;
            double after = await RunOverChanceAsync(newAtk);
            if (before - after < SurvivabilityGain || !Rule(DeckRules.SpendForSurvivability))
                return null;
            return $"{what}: survivability: with {cards} card(s) left and nothing set to answer their attacker, our " +
                   (bestNow > 0 ? $"{bestNow}-ATK monster is likely run over in their turn ({before:P0}), a free card for them" : "board is empty") +
                   $"; at {newAtk} it's run over only {after:P0} of the time";
        }
    }
}
