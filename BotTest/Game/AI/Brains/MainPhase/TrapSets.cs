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
    /// Which spell/trap to set (ChooseTrapSetAsync). RULE deck.no-set-into-lyla: no set card that can't respond into
    /// their face-up Lyla.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // ------------------------------------------------------------------ setting traps

        private int _redundantSetNotedTurn = -1;

        /// <summary>Spell/Traps worth setting from hand: every Trap, and MST (as bait). Deck brains add their own (e.g. Burial as bait).</summary>
        protected virtual bool IsSetCandidate(DynamicCard card) =>
            card.HasType(CardType.Trap) || card.IsCode(MysticalSpaceTyphoon);

        /// <summary>
        /// Which trap to set now, if any, and whether an empty field keeps Gorz live (then nothing should be set):
        ///  - only once no attack is coming this turn (Main Phase 2, or a Main Phase 1 with no Battle Phase that does
        ///    something): setting before attacking gains nothing and lets a flipped Ryko destroy the card;
        ///  - storm-proof sets first (Jar of Greed, Trap Dustshoot), see SetOrder;
        ///  - no two set traps for the same issue (Mirror Force + Dimensional Prison), at most MaxExposedSets exposed;
        ///  - not while an empty field keeps Gorz live, and not into a likely Heavy Storm.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex ConditionalActivationText = new(
            @"^\s*(When|If|During|At the start|Activate only|Activate this card only|Discard)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Why setting this spell/trap just hands it to their Lyla, or null (user, 2026-11-13: "setting a card when i have lyla
        /// in attack is just throwing that card away - i can just use lyla to destroy it"): their face-up Lyla in attack
        /// position can use her effect on their turn, and this card can't be activated in response (a trap that waits for an
        /// attack or a summon, a normal or continuous spell). A free-chaining trap or a Quick-Play spell is fine: it answers.
        /// </summary>
        protected async Task<string?> LylaTakesSetAsync(DynamicCard card, List<DynamicCard> enemyMonsters)
        {
            if (!enemyMonsters.Any(m => m.IsFaceup() && m.IsAttack() && m.IsCode(LylaLightswornSorceress)))
                return null;
            if (await CardDatabase.GetAsync(card.ID) is not DBCardInfo info)
                return null;
            bool trap = (info.Type & (int)CardType.Trap) != 0, quick = (info.Type & (int)CardType.QuickPlay) != 0;
            bool responds = (trap || quick) && !ConditionalActivationText.IsMatch(info.CardText ?? "");
            return responds ? null
                : "their Lyla is in attack position: her effect destroys it before it can be used (it can't be activated in response)";
        }

        protected async Task<(IdleCmdResponse? Set, bool BattleAhead, bool GorzLive)> ChooseTrapSetAsync(
            DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> enemyMonsters, List<DynamicCard> botMonsters, List<DynamicCard> hand)
        {
            // Anything we set waits for Main Phase 2 when we attack this turn.
            bool battleAhead = (state.SpellSetableCards ?? Array.Empty<DynamicCard>()).Any(card => card.ID > 0 && IsSetCandidate(card)) &&
                               await SetsWaitForMainPhase2Async(context);
            if (battleAhead && !Rule(DeckRules.HoldSetsForBattle))
                battleAhead = false;
            DynamicCard trap = default;
            // Solemn Judgment and Gorz in hand with our field empty: Solemn waits in hand (set, it switches Gorz off and
            // would be used up before Gorz could come); it's set once Gorz is out.
            bool solemnWaits = hand.Any(card => card.IsCode(GorzTheEmissaryOfDarkness)) && hand.Any(card => card.IsCode(SolemnJudgment)) &&
                               botMonsters.Count == 0 && !(await CurrentField.GetCardsInSpellZone()).Any(card => card.ID > 0) &&
                               Rule(DeckRules.SolemnWaitsForGorz);
            // Solemn Judgment is a late-game card (half our LP costs less the fewer we have): while our LP is high, other traps
            // are set first (user, 2026-11-13: "if you have other traps, prefer them in the early game").
            bool solemnLate = await CurrentField.GetBotLifePoints() >= SolemnEarlyGameLifePoints;
            if (!battleAhead)
            {
                var redundantNotes = new List<string>();
                foreach (var card in (state.SpellSetableCards ?? Array.Empty<DynamicCard>())
                             .Where(card => card.ID > 0 && IsSetCandidate(card) && !(solemnWaits && card.IsCode(SolemnJudgment)))
                             .OrderBy(card => solemnLate && card.IsCode(SolemnJudgment) ? 1 : 0)
                             .ThenBy(card => SetOrder(card.ID, enemyMonsters.Count)))
                {
                    if (await SetRedundantAsync(card.ID, hand.Count) is string redundant && Rule(DeckRules.NoRedundantSets))
                    {
                        redundantNotes.Add($"{CardNames.Get(card.ID)}: {redundant}");
                        Hold(card.ID, MainAction.SetSpell, DeckRules.NoRedundantSets);
                        continue;
                    }
                    if (await LylaTakesSetAsync(card, enemyMonsters) is string lylaWhy && Rule(DeckRules.NoSetIntoLyla))
                    {
                        redundantNotes.Add($"{CardNames.Get(card.ID)}: {lylaWhy}");
                        Hold(card.ID, MainAction.SetSpell, DeckRules.NoSetIntoLyla);
                        continue;
                    }
                    trap = card;
                    break;
                }
                if (trap.ID == 0 && redundantNotes.Count > 0 && _redundantSetNotedTurn != context.Header.CurrentTurn)
                {
                    _redundantSetNotedTurn = context.Header.CurrentTurn;
                    StapleNote(context, $"keep in hand: {string.Join("; ", redundantNotes)}");
                }
            }
            // Only worth a note when a card we'd actually set is being held back.
            var heldForMain2 = battleAhead
                ? (state.SpellSetableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card =>
                    card.ID > 0 && (card.HasType(CardType.Trap) || card.IsCode(MysticalSpaceTyphoon)))
                : default;
            if (heldForMain2.ID > 0)
                StapleNote(context, $"hold {CardNames.Get(heldForMain2.ID)} until Main Phase 2 (set after attacking: a flipped Ryko could destroy it)");
            if (battleAhead)
                foreach (var card in (state.SpellSetableCards ?? Array.Empty<DynamicCard>()).Where(card => card.ID > 0 && IsSetCandidate(card)))
                    Hold(card.ID, MainAction.SetSpell, DeckRules.HoldSetsForBattle);
            bool gorzLive = await KeepGorzLiveAsync(context, enemyMonsters);
            if (trap.ID > 0 && gorzLive && Rule(DeckRules.KeepGorzLive))
            {
                StapleNote(context, $"don't set {CardNames.Get(trap.ID)}: keep the field empty so Gorz stays live");
                Hold(trap.ID, MainAction.SetSpell, DeckRules.KeepGorzLive);
                trap = default;
            }
            if (trap.ID > 0 && await HoldSetForHeavyStormAsync(context.Header.CurrentTurn, trap.ID) is string stormHold && Rule(DeckRules.HoldSetForStorm))
            {
                StapleNote(context, $"don't set {CardNames.Get(trap.ID)}: {stormHold}");
                Hold(trap.ID, MainAction.SetSpell, DeckRules.HoldSetForStorm);
                StormHoldNoted = true;
                trap = default;
            }
            if (trap.ID > 0 && Rule(DeckRules.SetTraps))
            {
                StapleNote(context, $"set trap {CardNames.Get(trap.ID)}");
                return (trap.SetSpellTrap(), battleAhead, gorzLive);
            }
            return (null, battleAhead, gorzLive);
        }
    }
}
