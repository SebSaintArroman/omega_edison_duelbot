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
    /// <summary>
    /// Big threats against small ones: which of their monsters our answers should be saved for.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // ------------------------------------------------------------------ big vs small threats

        /// <summary>Effect score (see <see cref="EffectEvaluator"/>) from which a monster counts as a big threat on its effect alone.</summary>
        [Tunable("Effect score (see EffectEvaluator) from which a monster counts as a big threat on its effect alone.")]
        public double StrongEffectScore { get; set; } = 6;

        /// <summary>
        /// A big threat is worth spending resources (removal, an Extra Deck summon, a trap) on:
        ///  - 2000+ ATK, an Extra Deck monster, or a bouncer (Grand Mole),
        ///  - it beats a monster of ours that isn't expendable,
        ///  - or its effect is strong (score at least <see cref="StrongEffectScore"/>, e.g. Thought Ruler Archfiend).
        /// Everything else is a small threat (Giant Rat, Kinka-byo, a 1600 beater): handled by the clock, not by spending
        /// on it. A recruiter (Giant Rat) only counts on ATK or what it beats: its effect is a reason not to destroy it.
        /// Face-down cards (assumed Ryko) are never big threats.
        /// </summary>
        protected async Task<bool> IsBigThreatAsync(int id, int power)
        {
            if (id <= 0)
                return false;
            if (power >= StrongMonsterAttack)
                return true;
            if (_bouncesAttacker.Contains(id) || BoardAssessment.KnownAttackerBouncers.ContainsKey(id))
                return true;
            if (await CardDatabase.GetAsync(id) is DBCardInfo info &&
                (info.Type & ((int)CardType.Synchro | (int)CardType.Fusion | (int)CardType.Xyz | (int)CardType.Link)) != 0)
                return true;
            // On our turn, a monster that negates our attacks is a wall against our clock (weak only as an attacker).
            if (IsOwnTurn(_currentHeader) && await CardDatabase.GetAsync(id) is DBCardInfo negInfo &&
                AttackNegateText.IsMatch(negInfo.CardText ?? ""))
                return true;
            var own = await GetBotMonsterCardsAsync();
            if (own.Any(card => card.IsFaceup() && !IsExpendable(card) && power >= card.GetRealPower()))
                return true;
            if (_recruiters.Contains(id))
                return false;
            var grave = (await CurrentField.GetBotField()).EnemySide.Grave ?? Array.Empty<int>();
            return (await EffectEvaluator.ScoreOnFieldAsync(id, grave)).Score >= StrongEffectScore;
        }

        protected Task<bool> IsBigThreatAsync(DynamicCard enemy) => IsBigThreatAsync(enemy.ID, enemy.GetRealPower());

        /// <summary>"Negate the attack" (XX-Saber Fulhelmknight): weak against us when they attack, a wall when we do.</summary>
        protected static readonly System.Text.RegularExpressions.Regex AttackNegateText = new(
            @"\bnegate[^.]{0,20}\battack\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The big threats among <paramref name="threats"/> (see <see cref="IsBigThreatAsync(int, int)"/>).</summary>
        protected async Task<List<BoardAssessment.Defender>> BigThreatsAsync(IEnumerable<BoardAssessment.Defender> threats)
        {
            await RefreshRewardOnDestroyAsync(await GetEnemyMonsterCardsAsync());
            var big = new List<BoardAssessment.Defender>();
            foreach (var threat in threats)
                if (await IsBigThreatAsync(threat.Id, threat.Power))
                    big.Add(threat);
            return big;
        }
    }
}
