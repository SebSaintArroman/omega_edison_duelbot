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
    /// The board as read at the last decision: our monsters, theirs, life points, our clock.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // Refreshed each decision: how fast our board kills the opponent, and our monsters' battle stats.
        private ClockSpeed _clockSpeed = ClockSpeed.None;
        private List<DynamicCard> _ownMonsters = new();
        /// <summary>Our monsters at this decision.</summary>
        protected IReadOnlyList<DynamicCard> OwnMonsters => _ownMonsters;
        private int _oppLp;
        private List<DynamicCard> _opponents = new();
        /// <summary>Their monsters at this decision.</summary>
        protected IReadOnlyList<DynamicCard> OpponentMonsters => _opponents;
        private bool _enemyFieldEmpty;

        // Our Spell/Trap Zone card count, refreshed with the field.
        private int _ownSpellTrapCount;

        // A threat on their field worth a FLIP destroy: any face-down monster (unknown: it could be one) or a big face-up
        // threat (see IsBigThreatAsync). Refreshed with the field.
        private bool _opponentHasThreat;

        // Their set Spell/Trap cards, refreshed with the field.
        private int _opponentSetSpellTraps;

        /// <summary>How many set (face-down) Spell/Traps the opponent has, as of the last field refresh.</summary>
        protected int OpponentSetSpellTraps => _opponentSetSpellTraps;

        /// <summary>
        /// Whether their set Spell/Traps are a threat worth popping: they threaten our monsters that aren't expendable
        /// (Mirror Force, Bottomless...), not our life points. So only while we have such a monster out (it's what we're
        /// doing something with this turn); with only expendable bodies out there's nothing for them to hit.
        /// </summary>
        protected bool SetSpellTrapsThreatenUs(DynamicCard except) =>
            _opponentSetSpellTraps > 0 &&
            _ownMonsters.Any(own => own.ID > 0 && own.IsFaceup() && !SameZone(own, except) && !IsExpendable(own));

        // Their life points as read at this decision (PrepareContextAsync sets it from the field every time; the clock checks below).
        // 0 until the first read: then no clock speed-up is assumed.
        private int _theirLifePoints;

        // Whether a set spell/trap of ours can protect a monster (Waboku, Mirror Force...: CardValues.Protection), at this decision.
        private bool _weCanProtect;

        // Our hand, from the last field refresh (ids).
        private int[] _ownHandIds = Array.Empty<int>();
    }
}
