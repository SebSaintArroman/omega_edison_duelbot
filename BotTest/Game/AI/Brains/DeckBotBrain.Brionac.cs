using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Brionac, Dragon of the Ice Barrier, shared by every deck that runs it (moved from the Machina brain, 2026-11-10):
    /// discard any number of cards to return as many of their cards to the hand. An Extra Deck monster or token goes
    /// back for good (Stardust Dragon can't stop it: a bounce isn't destruction), a big threat at least has to be summoned
    /// again, and bouncing everything in our way can be lethal. Seen 2026-11-10: Blackwing held Brionac against Stardust
    /// and King of the Beasts with three cards in hand, because only its 2300 ATK was weighed.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// Whether this hand card may go to Brionac's discard for a bounce that isn't lethal (lethal discards anything).
        /// Decks keep their key cards (Machina: Fortress, Gearframe, traps).
        /// </summary>
        protected virtual bool BrionacMayDiscard(DynamicCard card) => true;

        /// <summary>How much we'd rather keep this hand card (lowest is discarded first). Decks rank their own cards.</summary>
        protected virtual async Task<double> BrionacKeepValueAsync(DynamicCard card)
        {
            var grave = (await CurrentField.GetBotField()).OwnSide.Grave ?? Array.Empty<int>();
            var (score, _) = await EffectEvaluator.ScoreAsync(card.ID, grave);
            return score + (card.HasType(CardType.Monster) ? BotTest.Game.AI.Search.MctsEvaluator.AtkValue(card.Attack) : 0);
        }

        private int _brionacTurn = -1;
        private List<int> _brionacDiscards = new();
        private List<(int Location, int Sequence)> _brionacTargets = new();

        /// <summary>
        /// Our face-up Brionac (once per turn): bounce their monsters for the win, or each Extra Deck monster / token / big
        /// threat of theirs (one discard each, cheapest first), or a set card in our way before we attack.
        /// </summary>
        protected async Task<IdleCmdResponse?> TryBrionacBounceAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, string rule = DeckRules.BrionacBounce)
        {
            var brionac = BounceSynchroIds.Select(id => TryGetActivable(state, id, out var card) ? card : default).FirstOrDefault(card => card.ID > 0);
            if (brionac.ID <= 0 || _brionacTurn == context.Header.CurrentTurn)
                return null;
            var hand = await GetBotHandCardsAsync();
            if (hand.Count == 0)
                return null;
            var keep = new List<double>();
            foreach (var card in hand)
                keep.Add(await BrionacKeepValueAsync(card));
            var cheapest = hand.Select((card, i) => (Card: card, Keep: keep[i])).OrderBy(x => x.Keep).Select(x => x.Card).ToList();
            var spare = cheapest.Where(BrionacMayDiscard).ToList();

            var enemy = await GetEnemyMonsterCardsAsync();
            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase && context.Header.CurrentTurn > 1;
            List<DynamicCard> targets;
            List<DynamicCard> discards;
            string why;

            // Lethal: every monster of theirs back to the hand and our attackers (Brionac included) cover their life points.
            int ourAttack = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).Sum(EffectiveAttack);
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            if (battleAhead && enemy.Count > 0 && enemy.Count <= hand.Count && ourAttack >= oppLp)
            {
                targets = enemy;
                discards = cheapest.Take(enemy.Count).ToList();
                why = $"bounce {(enemy.Count == 1 ? Describe(enemy[0]) : $"all {enemy.Count} of their monsters")}: our {ourAttack} attack is lethal ({oppLp} LP)";
            }
            else
            {
                targets = new List<DynamicCard>();
                foreach (var card in enemy.Where(card => card.IsFaceup() && card.ID > 0).OrderByDescending(card => BounceRemoves(card)).ThenByDescending(TargetThreat))
                    if (BounceRemoves(card) || await IsBigThreatAsync(card))
                        targets.Add(card);
                targets = targets.Take(spare.Count).ToList();
                // A set card in our way before we attack: only with a card to spare beyond the monsters' discards.
                if (battleAhead && spare.Count > targets.Count + 1 && (await GetBotMonsterCardsAsync()).Any(card => card.IsFaceup() && card.IsAttack()) &&
                    (await GetEnemySpellCardsAsync()).FirstOrDefault(card => !card.IsFaceup()) is { } set && set.CurrentCoordinates.Location != 0)
                    targets.Add(set);
                if (targets.Count == 0)
                    return null;
                discards = spare.Take(targets.Count).ToList();
                why = $"bounce {string.Join(", ", targets.Select(Describe))}";
            }
            if (!Rule(rule))
                return null;
            // A monster whose effect works from the hand (Kalut, Honest, Gorz) goes back last while another target exists
            // (user, 2026-11-12, round-robin 44b: Kalut bounced instead of Blizzard became their combat trick). Harmless when
            // every monster of theirs goes (no battling monster to boost), or when one of our hits alone is lethal (Gorz can't
            // come out of a hit that ends the duel).
            bool allGo = enemy.All(card => targets.Any(t => t.CurrentCoordinates.Sequence == card.CurrentCoordinates.Sequence &&
                                                            t.CurrentCoordinates.Location == card.CurrentCoordinates.Location));
            int bestHit = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                .Select(EffectiveAttack).DefaultIfEmpty(0).Max();
            if (!allGo && bestHit < oppLp)
            {
                var fromHand = new HashSet<int>();
                foreach (var card in targets)
                    if (card.ID > 0 && await CardDatabase.GetAsync(card.ID) is DBCardInfo info && WorksFromHandText.IsMatch(info.CardText ?? ""))
                        fromHand.Add(card.CurrentCoordinates.Sequence);
                targets = targets.OrderBy(card => card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone &&
                                                  fromHand.Contains(card.CurrentCoordinates.Sequence) ? 1 : 0).ToList();
            }
            _brionacTurn = context.Header.CurrentTurn;
            _brionacDiscards = discards.Select(card => card.ID).ToList();
            _brionacTargets = targets.Select(card => ((int)card.CurrentCoordinates.Location, (int)card.CurrentCoordinates.Sequence)).ToList();
            StapleNote(context, $"activate Brionac: discard {string.Join(", ", discards.Select(card => CardNames.Get(card.ID)))} to {why}");
            return brionac.MainActivateFromIndex();

            static string Describe(DynamicCard card) =>
                card.ID <= 0 ? "a face-down card"
                : BounceRemoves(card) ? $"{CardNames.Get(card.ID)} (back to the Extra Deck for good)"
                : CardNames.Get(card.ID);
        }

        /// <summary>A monster effect used from the hand (Kalut, Honest, Gorz, Battle Fader...): back in the hand it's a trick again.</summary>
        private static readonly System.Text.RegularExpressions.Regex WorksFromHandText = new(
            @"discard this card|send this card from your hand|special summon this card from your hand",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Brionac's discard and targets, as planned when it was activated. Null when this isn't that prompt.</summary>
        protected IList<byte>? BrionacPick(DecisionContext<SelectCardData> context)
        {
            var state = context.StateData;
            var cards = state.SelectableCards ?? Array.Empty<DynamicCard>();
            var hint = (DHint)state.SelectionHint;
            var indexed = cards.Select((card, i) => (Card: card, Index: i)).ToList();
            if (hint == DHint.DISCARD && _brionacDiscards.Count > 0 &&
                indexed.All(x => x.Card.CurrentCoordinates.Controller == 0 && x.Card.CurrentCoordinates.Location == (byte)CardLocation.Hand))
            {
                var picks = new List<int>();
                foreach (int id in _brionacDiscards)
                    if (indexed.FirstOrDefault(x => x.Card.IsCode(id) && !picks.Contains(x.Index)) is { Card.ID: > 0 } hit)
                        picks.Add(hit.Index);
                _brionacDiscards.Clear();
                if (picks.Count < Math.Max(1, state.Minimum) || state.Maximum > 0 && picks.Count > state.Maximum)
                    return null;
                StapleNote(context, $"Brionac discards {string.Join(", ", picks.Select(i => CardNames.Get(cards[i].ID)))}");
                return picks.Select(i => (byte)i).ToList();
            }
            if (hint == DHint.RTOHAND && _brionacTargets.Count > 0)
            {
                int count = Math.Max(1, state.Minimum);
                // In the planned order (hand-effect monsters last), so fewer bounces than planned keep the best ones.
                var picks = indexed.Where(x => x.Card.CurrentCoordinates.Controller != 0 &&
                                               _brionacTargets.Contains(((int)x.Card.CurrentCoordinates.Location, (int)x.Card.CurrentCoordinates.Sequence)))
                    .OrderBy(x => _brionacTargets.IndexOf(((int)x.Card.CurrentCoordinates.Location, (int)x.Card.CurrentCoordinates.Sequence)))
                    .Select(x => x.Index).Take(count).ToList();
                _brionacTargets.Clear();
                if (picks.Count < count)
                    return null; // the board changed: the shared target choice fills it
                StapleNote(context, $"Brionac returns {string.Join(", ", picks.Select(i => cards[i].ID > 0 ? CardNames.Get(cards[i].ID) : "a face-down card"))}");
                return picks.Select(i => (byte)i).ToList();
            }
            return null;
        }
    }
}
