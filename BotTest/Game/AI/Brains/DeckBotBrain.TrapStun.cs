using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Trap Stun (CSV S8, user 2026-11-16). It negates every trap effect on the field for the rest of the turn, the traps in the
    /// chain under it included:
    ///  - their Trap Stun on the chain is our last chance to use traps this turn: when we'd likely lose without them, chain them now
    ///    (more than one: the ones under Trap Stun are negated anyway, so the one-answer-per-issue rule doesn't count them), and
    ///    always chain Jar of Greed / Legacy of Yata-Garasu (free cards);
    ///  - once a Trap Stun has resolved, no trap of ours is activated for the rest of the turn (it would do nothing);
    ///  - our own Trap Stun: chained to the first trap of theirs that would stop our push (in our turn), never before it.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        protected const int TrapStunId = 59616123;
        private const int CallOfTheHauntedId = 97077563;

        // The turn a Trap Stun was seen face-up on the field (on the chain), and whether it has since left (resolved).
        private int _trapStunSeenTurn = -1;
        private bool _trapStunOnField;

        private async Task<bool> TrapStunFaceUpAsync(bool theirs)
        {
            var cards = theirs ? await GetEnemySpellCardsAsync() : await CurrentField.GetCardsInSpellZone();
            return cards.Any(card => card.IsFaceup() && card.IsCode(TrapStunId));
        }

        private static bool IsTrap(DynamicCard card) => (card.Type & 0x4) != 0;

        /// <summary>Our chain answer around Trap Stun, or null when it isn't involved.</summary>
        private async Task<int?> TrapStunAnswerAsync(DecisionContext<SelectChainData> context, int brainIndex)
        {
            var options = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            int turn = context.Header.CurrentTurn;
            bool theirsOnChain = await TrapStunFaceUpAsync(theirs: true);
            bool oursOnChain = await TrapStunFaceUpAsync(theirs: false);
            if (theirsOnChain || oursOnChain)
            {
                _trapStunSeenTurn = turn;
                _trapStunOnField = true;
            }
            else if (_trapStunSeenTurn == turn && _trapStunOnField)
                _trapStunOnField = false; // it left the field: resolved

            // Resolved this turn: our traps would do nothing.
            if (_trapStunSeenTurn == turn && !_trapStunOnField)
            {
                if (brainIndex >= 0 && brainIndex < options.Length && IsTrap(options[brainIndex]))
                {
                    if (!IsSearchTwin)
                        context.Bot.LogNote($"deck brain: no {CardNames.Get(options[brainIndex].ID)}: Trap Stun resolved this turn, every trap is negated");
                    return -1;
                }
                return null;
            }

            // Their Trap Stun on the chain: the last chance for our traps this turn.
            if (theirsOnChain)
            {
                int free = Array.FindIndex(options, c => c.CurrentCoordinates.Controller == 0 && c.IsCode(JarOfGreedId, LegacyOfYataGarasuId));
                if (free >= 0)
                {
                    if (!IsSearchTwin)
                        context.Bot.LogNote($"deck brain: chain {CardNames.Get(options[free].ID)} to their Trap Stun (a free card, before it's negated)");
                    return free;
                }
                if (await WeLoseWithoutTrapsAsync() is string why)
                {
                    // The brain's own pick if it is a trap; else the first trap of ours on offer (not Trap Stun itself).
                    int trap = brainIndex >= 0 && brainIndex < options.Length && IsTrap(options[brainIndex]) ? brainIndex
                        : Array.FindIndex(options, c => c.CurrentCoordinates.Controller == 0 && IsTrap(c) && !c.IsCode(TrapStunId));
                    if (trap >= 0)
                    {
                        if (!IsSearchTwin)
                            context.Bot.LogNote($"deck brain: chain {CardNames.Get(options[trap].ID)} to their Trap Stun, the last chance this turn: {why}");
                        return trap;
                    }
                }
                return null;
            }

            bool ownTurn = IsOwnTurn(context.Header);

            // Our own Trap Stun on the chain in our turn: our other traps resolve before it, so the ones worth using this turn go on top
            // now (Call of the Haunted, Jar of Greed...: user, 2026-11-16, "you could also chain those traps to your doomed trap stun").
            if (oursOnChain && ownTurn)
            {
                int ownTrap = brainIndex >= 0 && brainIndex < options.Length && IsTrap(options[brainIndex]) && !options[brainIndex].IsCode(TrapStunId) ? brainIndex
                    : Array.FindIndex(options, c => c.CurrentCoordinates.Controller == 0 && c.IsCode(CallOfTheHauntedId, JarOfGreedId, LegacyOfYataGarasuId));
                if (ownTrap >= 0)
                {
                    if (!IsSearchTwin)
                        context.Bot.LogNote($"deck brain: chain {CardNames.Get(options[ownTrap].ID)} on top of our Trap Stun: it resolves first, before every trap is negated");
                    return ownTrap;
                }
                return null;
            }

            int stun = Array.FindIndex(options, c => c.CurrentCoordinates.Controller == 0 && c.IsCode(TrapStunId));
            if (stun < 0)
                return null;
            bool theirTrapOnChain = _newFaceUpSpellZones.Count > 0 && (await GetEnemySpellCardsAsync())
                .Any(card => card.IsFaceup() && IsTrap(card) && _newFaceUpSpellZones.Contains(card.CurrentCoordinates.Sequence));
            bool doomed = await DoomedAsync(options[stun]);
            bool brainStuns = brainIndex == stun;

            // Our turn: chained to the first trap of theirs that would stop our push.
            if (ownTurn && theirTrapOnChain)
            {
                if (!IsSearchTwin)
                    context.Bot.LogNote("deck brain: chain Trap Stun to their trap: it stops our push otherwise");
                return stun;
            }
            if (doomed)
            {
                // Doomed (user, 2026-11-16): on their turn only when it negates something in this chain; on ours only when they still
                // have set cards that could be traps worth negating (our own traps worth using go on top of it, see above).
                // Not free (user, 2026-11-16: "its cost is you don't get to activate any more traps either that turn"). On their turn that
                // costs our other set traps for the rest of it: only worth it with none of those, or when their trap in this chain hurts us
                // beyond taking the Trap Stun (it targets something else of ours, or hits everything).
                int theirSets = (await GetEnemySpellCardsAsync()).Count(card => !card.IsFaceup());
                bool worth;
                if (ownTurn)
                    worth = theirSets > 0;
                else
                {
                    int ourOtherTraps = (await CurrentField.GetCardsInSpellZone()).Count(card => card.CurrentCoordinates.Controller == 0 && !card.IsFaceup() &&
                        card.ID > 0 && IsTrap(card) && !card.IsCode(TrapStunId) && !BaitCardIds.Contains(card.ID));
                    bool hurts = theirTrapOnChain && (await GetEnemySpellCardsAsync()).Any(card => card.IsFaceup() && IsTrap(card) &&
                        _newFaceUpSpellZones.Contains(card.CurrentCoordinates.Sequence) &&
                        ((card.CurrentChainTargets?.Length ?? 0) == 0 ||
                         card.CurrentChainTargets!.Any(t => t.Controller == 0 && !(t.Location == (byte)CardLocation.SpellZone && t.Sequence == options[stun].CurrentCoordinates.Sequence))));
                    worth = theirTrapOnChain && (ourOtherTraps == 0 || hurts);
                }
                if (worth)
                {
                    if (!IsSearchTwin)
                        context.Bot.LogNote(ownTurn
                            ? $"deck brain: chain our doomed Trap Stun: their {theirSets} set card(s) could be traps, negated for the rest of our turn"
                            : "deck brain: chain our doomed Trap Stun: it negates their trap in this chain");
                    return stun;
                }
                if (brainStuns)
                {
                    if (!IsSearchTwin)
                        context.Bot.LogNote(ownTurn ? "deck brain: let our doomed Trap Stun go: they have no set card it could negate"
                                                    : "deck brain: let our doomed Trap Stun go: nothing in this chain for it to negate");
                    return -1;
                }
            }
            return null;
        }

        /// <summary>Whether we likely lose without our traps now: we lose next turn as things stand, or their attackers can finish us this turn.</summary>
        private async Task<string?> WeLoseWithoutTrapsAsync()
        {
            if (await WeLoseNextTurnAsync())
                return "we lose next turn otherwise";
            if (IsOwnTurn(_currentHeader))
                return null;
            int lp = await CurrentField.GetBotLifePoints();
            int ourBest = (await GetBotMonsterCardsAsync()).Where(m => m.IsFaceup()).Select(m => m.GetRealPower()).DefaultIfEmpty(0).Max();
            int damage = (await GetEnemyMonsterCardsAsync()).Where(m => m.IsFaceup() && m.IsAttack() && !m.Attacked)
                .Select(m => m.Attack).OrderByDescending(a => a).Select((a, i) => i == 0 && ourBest > 0 ? Math.Max(0, a - ourBest) : a).Sum();
            return damage >= lp ? $"their attackers can deal {damage} of our {lp} LP this turn" : null;
        }
    }
}
