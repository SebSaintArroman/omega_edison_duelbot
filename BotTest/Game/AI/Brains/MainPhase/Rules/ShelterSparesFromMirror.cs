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
    /// RULE deck.shelter-spares-from-mirror: spare attackers go to defence before battle against a likely Mirror Force
    /// (round-robin 43).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// Mirror Force destroys every attack-position monster when one of them attacks. A monster that isn't attacking
        /// this turn while others are is safer in defence. If its DEF survives the opponent's strongest attacker there
        /// is no downside, so always; otherwise roll with the chance Mirror Force is set (a weak DEF can be run over).
        /// Rolled once per monster per turn.
        /// </summary>
        protected bool ShelterFromMirrorForce(DynamicCard card, List<DynamicCard> opponents, bool survivesInDefense)
        {
            if (MirrorForceChance <= 0)
                return false;
            // Not worth protecting: an expendable monster lost to Mirror Force at least spends it, and it keeps the
            // clock up. A low DEF is weak in defence anyway.
            if (IsExpendable(card) || (!survivesInDefense && card.Defense < card.Attack / 2))
                return false;
            bool othersAttack = _ownMonsters.Any(own =>
                !SameZone(own, card) && own.IsFaceup() && own.IsAttack() &&
                (opponents.Count == 0 ? !IsWeak(own) : opponents.Any(target => target.IsFaceup() && IsWorthAttacking(own, target))));
            if (!othersAttack)
                return false;

            int zone = card.CurrentCoordinates.Sequence;
            if (!_mirrorRolls.TryGetValue(zone, out bool shelter))
            {
                double roll = RollAt;
                shelter = survivesInDefense || roll < MirrorForceChance;
                _mirrorRolls[zone] = shelter;
                _mirrorForceReason[zone] = survivesInDefense
                    ? $"not attacking while others do; Mirror Force chance {MirrorForceChance:P0} and its DEF holds, so no downside"
                    : $"not attacking while others do; Mirror Force chance {MirrorForceChance:P0}, rolled {roll:0.00} -> {(shelter ? "shelter" : "stay")}";
            }
            return shelter;
        }

        // ------------------------------------------------------------------ answering an attack

        /// <summary>
        /// During the opponent's Battle Phase: their monsters that can still attack this turn (face-up, attack position,
        /// not yet attacked, and not the one attacking right now: the "attacked" flag is only set once its battle ends,
        /// e.g. when it flipped our Ryko). Removal at that point should hit one of these (e.g. Ryko's flip into the
        /// Pegasus still to come, not the Falcon that already attacked). Empty outside their Battle Phase.
        /// </summary>
        protected async Task<Func<DynamicCard, bool>> StillToAttackThisTurnAsync(BotStateHeader header)
        {
            var phase = (DuelPhase)header.CurrentPhase;
            bool theirBattle = !IsOwnTurn(header) &&
                               phase is DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Damage or DuelPhase.DamageCal or DuelPhase.Battle;
            if (!theirBattle)
                return _ => false;
            var current = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            bool NotCurrent(DynamicCard card) => !(current.ID > 0 && card.CurrentCoordinates.Sequence == current.CurrentCoordinates.Sequence);
            // Omega never sets "attacked" on their monsters, and an attack we get no prompt for (seen 2026-11-13: Nova Summoner
            // destroyed our Ryko unseen) leaves no trace: each monster of ours lost since their Battle Phase began counts as one
            // attack (one of them the current battler's). When those cover all their other attackers, none is still to come
            // (user: "nova had already attacked so was no threat").
            var theirAttackers = (await GetEnemyMonsterCardsAsync()).Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack()).ToList();
            int lost = _ourMonstersBeforeTheirBattle.Turn == header.CurrentTurn
                ? Math.Max(0, _ourMonstersBeforeTheirBattle.Count - (await GetBotMonsterCardsAsync()).Count(card => card.ID != 0))
                : 0;
            int earlierAttacks = lost - (current.ID > 0 ? 1 : 0);
            int others = theirAttackers.Count(NotCurrent);
            if (others > 0 && earlierAttacks >= others)
                return _ => false;
            var attacked = _theirAttackedZones.Turn == header.CurrentTurn ? _theirAttackedZones.Zones : new HashSet<int>();
            return card => card.CurrentCoordinates.Controller == 1 &&
                           card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone &&
                           card.IsFaceup() && card.IsAttack() && !card.Attacked && NotCurrent(card) &&
                           !attacked.Contains(card.CurrentCoordinates.Sequence);
        }
    }
}
