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
    /// The chance their hand holds Heavy Storm.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>Chance per card in the opponent's hand that it is Heavy Storm (one copy per deck). Tune from logs.</summary>
        [Tunable("Chance per card in the opponent's hand that it is Heavy Storm (one copy per deck). Tune from logs.")]
        public double HeavyStormChancePerCard { get; set; } = 0.025;

        private bool _knownHeavyStormInOpponentHand;

        // ------------------------------------------------------------------ the opponent's Heavy Storm

        /// <summary>Chance the opponent holds Heavy Storm: 0 once their copy is in the GY/banished, 1 if seen in hand.</summary>
        protected async Task<double> OpponentHeavyStormChanceAsync()
        {
            var enemy = (await CurrentField.GetBotField()).EnemySide;
            bool gone = (enemy.Grave ?? Array.Empty<int>()).Contains(HeavyStorm) || (enemy.Banish ?? Array.Empty<int>()).Contains(HeavyStorm);
            if (gone)
            {
                _knownHeavyStormInOpponentHand = false;
                return 0;
            }
            if (_knownHeavyStormInOpponentHand)
                return 1;
            int hand = enemy.Hand?.Length ?? 0;
            // Cards they held through a turn where they'd have Stormed us are probably not Heavy Storm; cards held while
            // they put their own spells/traps out are less likely to be; new draws count fully.
            _stormClearedCards = Math.Min(_stormClearedCards, hand);
            _stormSoftClearedCards = Math.Min(_stormSoftClearedCards, hand - _stormClearedCards);
            int fresh = hand - _stormClearedCards - _stormSoftClearedCards;
            double none = Math.Pow(1 - HeavyStormChancePerCard, fresh) *
                          Math.Pow(1 - HeavyStormChancePerCard * EvidenceResidual, _stormClearedCards) *
                          Math.Pow(1 - HeavyStormChancePerCard * _stormSoftResidual, _stormSoftClearedCards);
            return 1 - none;
        }
    }
}
