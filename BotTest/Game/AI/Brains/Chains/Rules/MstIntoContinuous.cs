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
    /// RULE deck.mst-into-continuous: our set MST at a face-up continuous spell/trap of theirs that matters.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // Face-up spell/trap text that actually gets in our way (or props up their board).
        private static readonly System.Text.RegularExpressions.Regex DisruptiveSpellText = new(
            @"cannot (attack|declare an attack|activate|special summon|normal summon|be normal summoned|conduct)|negate|" +
            @"monsters? your opponent controls (lose|cannot)|when this card leaves the field, destroy|" +
            @"(gains?|increase) [^.]{0,30}atk|your opponent (cannot|must)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Whether a face-up opponent spell/trap is worth spending targeted removal (MST) on, rather than one of their
        /// set cards: it disrupts us (stops attacks or summons, negates, weakens our monsters), props up their board
        /// (an equip, or Call of the Haunted / Premature Burial: destroying it destroys the monster too), or it is part of
        /// the Grand Mole lock (Ultimate Offering with a Mole around). Otherwise a set card, which might be Mirror Force
        /// or Torrential, matters more. Returns the reason, or null.
        /// </summary>
        protected async Task<string?> FaceUpSpellMattersAsync(DynamicCard card)
        {
            if (!card.IsFaceup() || card.ID <= 0 || card.CurrentCoordinates.Location != (byte)CardLocation.SpellZone)
                return null;
            if (card.HasType(CardType.Equip))
                return "an equip propping up their monster";
            if (await CardDatabase.GetAsync(card.ID) is not DBCardInfo info)
                return null;
            string text = info.CardText ?? "";
            if (ExtraSummonText.IsMatch(text) && BouncerThreat)
                return "it keeps their Grand Mole coming back";
            if (DisruptiveSpellText.IsMatch(text))
                return "it gets in our way";
            return null;
        }

        // The opponent's card MST was chained at (its zone), so the target prompt picks it.
        private int? _mstTargetZone;

        /// <summary>
        /// A continuous / equip / field spell or trap needs to stay on the field for its effect to resolve: chain MST to
        /// its activation and destroying it stops the effect too (Ultimate Offering's summon, Call of the Haunted's
        /// revive, ...). When the opponent has just activated something and one of their face-up cards like that is
        /// worth removing (FaceUpSpellMattersAsync), chain our MST (set, or in hand on our turn) at it. Returns the
        /// chain index, or null.
        /// </summary>
        protected async Task<int?> TryChainMstIntoContinuousAsync(DecisionContext<SelectChainData> context)
        {
            var options = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            int mst = Array.FindIndex(options, card => card.IsCode(MysticalSpaceTyphoon));
            if (mst < 0 || context.Header.LastChainPlayer != 1)
                return null; // no MST to chain, or it isn't the opponent who just activated something
            // Only in response to the continuous card's own effect. If a spell/trap just turned face-up, that's what they
            // activated (e.g. a D-Prison on our attacker): MST only goes at it if it is itself a continuous card that
            // matters; destroying an unrelated continuous card stops nothing.
            bool newActivation = _newFaceUpSpellZones.Count > 0;
            foreach (var card in (await GetEnemySpellCardsAsync()).Where(card => card.IsFaceup() &&
                         (card.HasType(CardType.Continuous) || card.HasType(CardType.Equip) || card.HasType(CardType.Field))))
            {
                if (newActivation && !_newFaceUpSpellZones.Contains(card.CurrentCoordinates.Sequence))
                    continue;
                if (await FaceUpSpellMattersAsync(card) is not string why)
                    continue;
                if (await DestructionNegatedAsync(context, MysticalSpaceTyphoon) != null || !Rule(DeckRules.MstIntoContinuous))
                    return null;
                _lastStapleActivated = MysticalSpaceTyphoon;
                _mstTargetZone = card.CurrentCoordinates.Sequence;
                context.Bot.LogNote($"deck brain: chain Mystical Space Typhoon at {CardNames.Get(card.ID)}: {why}; " +
                                    "destroying a continuous card in response stops its effect too");
                return mst;
            }
            return null;
        }
    }
}
