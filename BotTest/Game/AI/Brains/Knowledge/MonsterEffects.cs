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
    /// What monsters do, read from their card text: reward when destroyed, battle-proof, graveyard and flip effects,
    /// attack locks.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // ------------------------------------------------------------------ monsters that reward being destroyed

        /// <summary>
        /// Opponent monsters whose text rewards their owner when destroyed or sent to the graveyard
        /// (Kittytail, Sangan, Giant Rat, ...): killing them hands the opponent a card.
        /// </summary>
        private readonly HashSet<int> _rewardOnDestroy = new();

        /// <summary>"Your opponent cannot target other monsters for attacks, except this card" (Dupe Frog and co.).</summary>
        private static readonly System.Text.RegularExpressions.Regex AttackLockText = new(
            @"cannot target (other )?monsters( you control)? for attacks?,? except (this card|this one)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// The monsters among theirs that we can actually attack: with a face-up attack lock out (Dupe Frog: "cannot target
        /// other monsters for attacks, except this card"), only the lock monsters (round-robin 21: Armageddon Knight was sent
        /// at "the unknown face-down", and the server offered only Dupe Frog, 2000 DEF, three turns running).
        /// </summary>
        protected async Task<List<DynamicCard>> AttackableTargetsAsync(List<DynamicCard> theirMonsters)
        {
            var locks = new List<DynamicCard>();
            foreach (var card in theirMonsters.Where(card => card.ID > 0 && card.IsFaceup() && !card.IsDisabled()))
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && AttackLockText.IsMatch(info.CardText ?? ""))
                    locks.Add(card);
            return locks.Count > 0 ? locks : theirMonsters;
        }

        private static readonly System.Text.RegularExpressions.Regex RewardOnDestroyText = new(
            @"(destroyed|sent (from the field )?to the (graveyard|gy))[^.]{0,120}?\b(add|special summon|draw)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        // Monsters that can't be destroyed by battle once per turn (Gemini Soldier), and the ones we've battled this turn.
        private readonly HashSet<int> _oncePerTurnBattleProtected = new();
        private readonly HashSet<(int Turn, int Zone, int Id)> _battledThisTurn = new();

        // Always battle-proof (Blackwing Armor Master): "This card cannot be destroyed by battle". Its effect is off when
        // negated (a Vayu-summoned Armor Master), so check IsEffectNegated before relying on it.
        private readonly HashSet<int> _battleProof = new();
        private static readonly System.Text.RegularExpressions.Regex BattleProofText = new(
            @"(^|[.:;]\s*|this card )cannot be destroyed by battle(?! once per turn)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Whether this monster survives any battle right now (battle-proof text, effects not negated).</summary>
        protected bool IsBattleProof(DynamicCard card) => _battleProof.Contains(card.ID) && !IsEffectNegated(card);

        // "You take no battle damage from attacks/battles involving this card" (Blackwing Armor Master).
        private readonly HashSet<int> _stopsBattleDamage = new();
        private static readonly System.Text.RegularExpressions.Regex StopsBattleDamageText = new(
            @"(take|takes) no battle damage from (attacks|battles) involving this card",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Whether battles with this monster deal its controller no damage right now (effects not negated).</summary>
        protected bool StopsBattleDamage(DynamicCard card) => _stopsBattleDamage.Contains(card.ID) && !IsEffectNegated(card);

        private static readonly System.Text.RegularExpressions.Regex OncePerTurnBattleProtectionText = new(
            @"once per turn[^.]{0,80}cannot be destroyed by battle|cannot be destroyed by battle once per turn",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        protected async Task RefreshRewardOnDestroyAsync(IEnumerable<DynamicCard> opponentMonsters)
        {
            var opponents = opponentMonsters.ToList();
            // Their Catastor-type monsters (destroy any monster of another Attribute they battle).
            await LearnBattleDestroyersAsync(opponents.Where(card => card.IsFaceup()).Select(card => card.ID));
            // Effect scores for attack targeting (TargetThreat), and once-per-turn battle protection.
            if (opponents.Any(card => card.IsFaceup() && card.ID > 0 && !EffectPointsCache.ContainsKey(card.ID)))
            {
                var grave = (await CurrentField.GetBotField()).EnemySide.Grave ?? Array.Empty<int>();
                foreach (var card in opponents.Where(card => card.IsFaceup() && card.ID > 0 && !EffectPointsCache.ContainsKey(card.ID)))
                {
                    if (await CardDatabase.GetAsync(card.ID) is not DBCardInfo info)
                        continue;
                    var (score, _) = await EffectEvaluator.ScoreOnFieldAsync(card.ID, grave);
                    EffectPointsCache[card.ID] = Math.Max(0, score - BoardEvaluator.AtkValue(info.Attack));
                    if (OncePerTurnBattleProtectionText.IsMatch(info.CardText ?? ""))
                        _oncePerTurnBattleProtected.Add(card.ID);
                }
            }
            foreach (var card in opponents.Where(card => card.IsFaceup() && card.ID > 0 && !_targetRemovesAttacker.Contains(card.ID)))
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo removerInfo && RemovesBattledText.IsMatch(removerInfo.CardText ?? ""))
                    _targetRemovesAttacker.Add(card.ID);
            foreach (var card in opponents.Where(card => card.IsFaceup() && card.ID > 0 && !_rewardOnDestroy.Contains(card.ID)))
            {
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && RewardOnDestroyText.IsMatch(info.CardText ?? ""))
                {
                    _rewardOnDestroy.Add(card.ID);
                    if (RecruitText.IsMatch(info.CardText ?? ""))
                    {
                        _recruiters.Add(card.ID);
                        if (!RecruitOnAnyDestructionText.IsMatch(info.CardText ?? ""))
                            _battleOnlyRecruiters.Add(card.ID);
                    }
                }
            }
            foreach (var card in opponents.Where(card => card.IsFaceup() && card.ID > 0 && !_graveyardEffect.Contains(card.ID)))
            {
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info)
                {
                    if (EffectEvaluator.HasGraveyardEffect(info.CardText ?? ""))
                        _graveyardEffect.Add(card.ID);
                    if (BattleProofText.IsMatch(info.CardText ?? "") && !OncePerTurnBattleProtectionText.IsMatch(info.CardText ?? ""))
                        _battleProof.Add(card.ID);
                    if (StopsBattleDamageText.IsMatch(info.CardText ?? ""))
                        _stopsBattleDamage.Add(card.ID);
                    if (FlipEffectText.IsMatch(info.CardText ?? ""))
                        _flipEffect.Add(card.ID);
                    if (StealsBattleVictimText.IsMatch(info.CardText ?? ""))
                        _stealsBattleVictim.Add(card.ID);
                }
            }
            foreach (var card in opponents.Where(card => card.IsFaceup() && card.ID > 0 && !_bouncesAttacker.Contains(card.ID)))
            {
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && BouncesAttackerText.IsMatch(info.CardText ?? ""))
                {
                    _bouncesAttacker.Add(card.ID);
                    BoardAssessment.KnownAttackerBouncers.TryAdd(card.ID, 0); // so clock / lethal maths account for it
                }
            }

            _ownMonsters = await GetBotMonsterCardsAsync();
            CheckPendingPlacement(_currentHeader, _ownMonsters);
            _ownSpellTrapCount = (await CurrentField.GetCardsInSpellZone()).Count();
            _opponentHasThreat = false;
            foreach (var enemy in opponents)
                if (!enemy.IsFaceup() || await IsBigThreatAsync(enemy))
                {
                    _opponentHasThreat = true;
                    break;
                }
            // Our own monsters' graveyard / FLIP effects too (we see our face-down cards), for the attack choices below.
            foreach (var card in _ownMonsters.Where(card => card.ID > 0 && !_ownTextRead.Contains(card.ID)))
            {
                _ownTextRead.Add(card.ID);
                if (await CardDatabase.GetAsync(card.ID) is not DBCardInfo info)
                    continue;
                if (EffectEvaluator.HasGraveyardEffect(info.CardText ?? ""))
                    _graveyardEffect.Add(card.ID);
                if (FlipEffectText.IsMatch(info.CardText ?? ""))
                    _flipEffect.Add(card.ID);
            }
            await LearnBattleDestroyersAsync(_ownMonsters.Where(card => card.IsFaceup()).Select(card => card.ID));
            _opponents = opponents;
            _oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            var enemySpellTraps = await GetEnemySpellCardsAsync();
            _enemyFieldEmpty = opponents.Count == 0 && enemySpellTraps.Count == 0;
            _opponentSetSpellTraps = enemySpellTraps.Count(card => !card.IsFaceup());
            var enemySide = (await CurrentField.GetBotField()).EnemySide;
            TrackOpponentGorz(opponents, enemySide);
            TrackBouncerInHand(opponents, enemySide);
            await RefreshMoleLockAsync(opponents);
            int turns = BoardAssessment.TurnsToWin(
                _ownMonsters.Where(card => card.IsFaceup() && card.IsAttack()).Select(card => card.Attack),
                BoardAssessment.Defenders(opponents, OpponentMemory), _oppLp);
            _clockSpeed = BoardAssessment.SpeedOf(turns);
        }

        protected bool RewardsDestruction(DynamicCard target) => _rewardOnDestroy.Contains(target.ID);

        // Opponent monsters with an effect that works from the graveyard (Vayu, Plaguespreader, Necro Gardna).
        private readonly HashSet<int> _graveyardEffect = new();

        // Opponent monsters that take what they destroy by battle (Goyo Guardian: "destroys an opponent's monster by
        // battle ... Special Summon that monster to your field").
        private readonly HashSet<int> _stealsBattleVictim = new();
        private static readonly System.Text.RegularExpressions.Regex StealsBattleVictimText = new(
            @"destroys an opponent's monster by battle[^.]*special summon that monster to your field",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// A face-up opponent monster (effects live) that takes the monster it destroys by battle (Goyo Guardian). Setting
        /// or leaving a weak monster in front of it just hands them that monster.
        /// </summary>
        protected bool StealsBattleVictim(DynamicCard card) =>
            card.IsFaceup() && _stealsBattleVictim.Contains(card.ID) && !IsEffectNegated(card);

        /// <summary>Whether this monster (ours or theirs) has an effect that works once it's in the graveyard.</summary>
        protected bool HasGraveyardEffect(DynamicCard target) => _graveyardEffect.Contains(target.ID);

        // Our monsters whose text has been read, and the ones with a FLIP effect (Ryko).
        private readonly HashSet<int> _ownTextRead = new();
        private readonly HashSet<int> _flipEffect = new();
        private static readonly System.Text.RegularExpressions.Regex FlipEffectText = new(
            @"^\s*flip:", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Whether this monster of ours has a FLIP effect (Ryko).</summary>
        protected bool HasFlipEffect(DynamicCard card) => _flipEffect.Contains(card.ID);

        /// <summary>Whether this monster's FLIP effect destroys, banishes or returns a card (Ryko), from its text. Unknown: yes.</summary>
        protected static bool FlipHitsACard(int code)
        {
            if (!Search.SearchHost.Available)
                return true;
            int official = Search.SearchHost.CanonicalId(code) is > 0 and var o ? o : code;
            if (Search.SearchHost.Cards.Get(official) is not { } card)
                return true;
            string text = card.Text;
            int flip = text.IndexOf("FLIP:", StringComparison.OrdinalIgnoreCase);
            // No FLIP effect, nothing a flip can hit (2026-11-17: a set Honest, "return this card ... to your hand", read as Ryko 100%).
            if (flip < 0)
                return false;
            string effect = text[flip..];
            return effect.Contains("destroy", StringComparison.OrdinalIgnoreCase) ||
                   effect.Contains("banish", StringComparison.OrdinalIgnoreCase) ||
                   effect.Contains("return", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A face-up FLIP monster (a flipped Ryko): its effect is spent, so it is completely expendable. It never counts in
        /// Mirror Force / Torrential Tribute maths (losing it costs nothing).
        /// </summary>
        protected bool IsSpentFlipMonster(DynamicCard card) => card.IsFaceup() && HasFlipEffect(card);

        private static readonly System.Text.RegularExpressions.Regex AttacksDirectlyText = new(
            @"can attack (your opponent )?directly", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// A monster that can attack directly (Jinzo - Returner): on Omega its attack is always a direct one, even into monsters, as Omega never
        /// asks "attack directly?" (seen 2026-11-17: Returner, summoned to destroy Lyla, hit directly). It can't be planned to battle a monster.
        /// </summary>
        protected static bool AttacksOnlyDirectlyOnOmega(int code) =>
            code > 0 && Search.SearchHost.Available && Search.SearchHost.Cards.Get(code)?.Text is string text && AttacksDirectlyText.IsMatch(text);

        // "Special Summon that monster to your field" after destroying it by battle: Goyo Guardian and the like.
        private static readonly System.Text.RegularExpressions.Regex StealsOnBattleText = new(
            @"destroys? [^.]{0,40}monster by battle[^.]{0,80}Special Summon (that|the destroyed) monster to your (side of the )?field",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>A monster that takes what it destroys by battle (Goyo Guardian).</summary>
        protected static bool StealsWhatItDestroys(DynamicCard attacker) =>
            attacker.ID > 0 && Search.SearchHost.Available && Search.SearchHost.Cards.Get(attacker.ID)?.Text is string text && StealsOnBattleText.IsMatch(text);
    }
}
