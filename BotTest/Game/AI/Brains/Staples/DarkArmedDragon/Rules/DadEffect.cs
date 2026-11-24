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
    /// RULES deck.dad-effect and deck.hold-dad-effect-for-pieces: when Dark Armed Dragon's effect is worth banishing a DARK.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>Mirror Force chance from which DAD clears a set card before attacking, even at the cost of a graveyard piece.</summary>
        [Tunable("Mirror Force chance from which DAD clears a set card before attacking, even at the cost of a graveyard piece.")]
        public double DadClearBackrowMirrorChance { get; set; } = 0.1;

        /// <summary>Why destroying one of their monsters with Dark Armed Dragon makes our board lethal this turn, or null.</summary>
        private async Task<string?> DadMakesLethalAsync(List<DynamicCard> enemyMonsters, int theirSpells)
        {
            var attackers = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                .Select(EffectiveAttack).ToList();
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            if (BoardAssessment.TurnsToWin(attackers, BoardAssessment.Defenders(enemyMonsters, OpponentMemory), oppLp) == 1)
                return null; // already lethal: no need
            foreach (var victim in enemyMonsters)
            {
                var rest = enemyMonsters.Where(card => !SameZone(card, victim)).ToList();
                if (BoardAssessment.TurnsToWin(attackers, BoardAssessment.Defenders(rest, OpponentMemory), oppLp) != 1)
                    continue;
                if (rest.Count == 0 && theirSpells == 0 && _knownGorzInOpponentHand && attackers.All(atk => atk < oppLp))
                    return null; // their field empty: the known Gorz stops the push
                return $"destroying their {DescribeTarget(victim)} makes our board lethal this turn ({string.Join(" + ", attackers)} into {oppLp} LP): " +
                       "worth a graveyard play piece";
            }
            return null;
        }

        protected async Task<string?> DadEffectWhyAsync<T>(DecisionContext<T> context)
        {
            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            var enemySpells = await GetEnemySpellCardsAsync();
            if (enemyMonsters.Count == 0 && enemySpells.Count == 0)
                return null;
            var grave = await GetBotGraveyardCardsAsync();
            var darks = grave.Where(card => card.HasType(CardType.Monster) && (card.Attribute & (int)CardAttribute.Dark) != 0).ToList();
            if (darks.Count == 0)
                return null;
            int vayus = darks.Count(card => card.IsCode(BlackwingVayuTheEmblemOfHonor));
            int partners = grave.Count(card => card.IsCode(VayuPartnerIds));
            int keptPartners = Math.Min(partners, vayus);
            int playPieces = vayus + keptPartners + darks.Count(card => card.IsCode(PlaguespreaderZombie, NecroGardna));
            int spare = darks.Count - playPieces;
            if (await DadInHandAsync() && darks.Count <= 3)
            {
                StapleNote(context, "hold Dark Armed Dragon's effect: another Dark Armed Dragon in hand needs the 3 DARKs in the graveyard");
                Hold(DarkArmedDragon, MainAction.Activate, DeckRules.DadEffect);
                return null;
            }
            string targets = $"{enemyMonsters.Count} monster(s), {enemySpells.Count} spell/trap(s)";
            if (spare > 0)
                return $"Dark Armed Dragon is on the field and the graveyard has {spare} DARK(s) with no graveyard effect to banish: " +
                       $"destroying one of their cards ({targets}) costs nothing that matters (repeatable)";
            bool attackingSoon = IsOwnTurn(context.Header) && (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1;
            int setCards = enemySpells.Count(card => !card.IsFaceup());
            if (attackingSoon && setCards > 0 && MirrorForceChance >= DadClearBackrowMirrorChance)
                return $"we attack with Dark Armed Dragon this turn and they have {setCards} set card(s) (Mirror Force chance {MirrorForceChance:P0}): " +
                       "clear one first, even though the cost is a graveyard piece";
            // Destroying their blocker makes our board lethal this turn: worth a graveyard piece (user, 2026-11-13: "banishing
            // gardna for dad isn't something you should really do, [but] in this case you would have lethal"). Not into a
            // known Gorz: with their field emptied, the first direct hit brings it instead of the win.
            if (attackingSoon && enemyMonsters.Count > 0 && await DadMakesLethalAsync(enemyMonsters, enemySpells.Count) is string lethalWhy)
                return lethalWhy;
            foreach (var enemy in enemyMonsters.Where(card => card.IsFaceup()))
                if (await IsBigThreatAsync(enemy))
                    return $"destroy their {CardNames.Get(enemy.ID)} (a big threat), even though the cost is a graveyard play piece";
            if (await WeLoseNextTurnAsync())
                return "holding back loses: destroy one of their cards even though the cost is a graveyard play piece";
            if (!Rule(DeckRules.HoldDadEffectForPieces))
                return "rule skipped: destroy one of their cards even though the cost is a graveyard play piece";
            StapleNote(context, $"hold Dark Armed Dragon's effect: the only DARKs left to banish have graveyard effects (Vayu pieces, " +
                                $"Plaguespreader, Necro Gardna), and nothing of theirs ({targets}) is worth one");
            Hold(DarkArmedDragon, MainAction.Activate, DeckRules.HoldDadEffectForPieces);
            return null;
        }
    }
}
