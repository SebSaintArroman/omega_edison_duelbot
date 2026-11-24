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
    /// Changing battle positions: when a monster goes to attack or to defence.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>Whether this monster's attack adds pressure: a target worth attacking, or a direct hit that's not weak / changes the clock.</summary>
        private bool AttackMatters(DynamicCard card, List<DynamicCard> opponents) =>
            opponents.Count == 0
                ? !IsWeak(card) || ChangesClock(card, _ownMonsters.Where(own => own.IsFaceup() && own.IsAttack()))
                : opponents.Any(opponent => opponent.IsFaceup() && IsWorthAttacking(card, opponent));

        protected bool ShouldRepositionToAttack(DynamicCard card, IEnumerable<DynamicCard> opponentMonsters)
        {
            var opponents = opponentMonsters.ToList();
            // Flip our own face-down FLIP monster (Ryko) to use its effect: popping a face-down of theirs stops their Ryko
            // flipping for its own destroy, and destroys a battle recruiter outside battle. Worth it when they have a
            // threat on the field: any face-down monster (it could be one), a big face-up threat, or set Spell/Traps while
            // they threaten a monster of ours that isn't expendable. Not for any card.
            // Only with a card of theirs on the field for it to hit (seen 2026-11-10: Ryko flipped into their empty field and
            // had to destroy one of ours).
            // Only a flip that hits a card (Ryko's destroy), not one that only summons (seen 2026-11-10: Super-Nimble Mega
            // Hamster flipped into Archlord Kristya for nothing).
            if (!card.IsFaceup() && HasFlipEffect(card) && FlipHitsACard(card.ID) && !_enemyFieldEmpty && (_opponentHasThreat || SetSpellTrapsThreatenUs(card)) &&
                Rule(DeckRules.FlipForThreat))
                return true;
            // After the Battle Phase there's nothing to attack: attack position only exposes it (the flip above is the one reason).
            if ((DuelPhase)_currentHeader.CurrentPhase == DuelPhase.Main2)
                return false;
            // A face-down FLIP monster flipped with nothing of theirs to hit wastes the flip (and may have to hit our own card).
            if (!card.IsFaceup() && HasFlipEffect(card) && _enemyFieldEmpty)
                return false;
            if (MirrorDefenceReason(card.ID, card.Attack, countsAlreadyOnField: true, opponents, battleAhead: true,
                    AttackMatters(card, opponents), _mirrorRollTurn) != null)
                return false; // stays in defence this turn (Mirror Force roll; never when needed for lethal)
            var attackersUp = _ownMonsters.Where(own => own.IsFaceup() && own.IsAttack() && !own.Attacked && !SameZone(own, card)).ToList();
            // Empty field: only if its direct hit is worth it (a 200 Ryko's isn't, and it'd be left in attack position).
            // An expendable monster with a decent hit (a flipped Hamster, a used Armageddon Knight) attacks an open field anyway.
            if (!opponents.Any())
                return !IsWeak(card) || ChangesClock(card, attackersUp) || ExpendableHitter(card) || IsBeater(card);

            // One attack deals with one target: a monster only goes up if the targets it could attack outnumber the
            // attackers already up that could take them (else e.g. a 600 Necro Gardna and a 200 Ryko both go to attack
            // position for one face-down, one other monster attacks it, and both are left exposed).
            var targets = opponents.Where(opponent => IsWorthAttacking(card, opponent)).ToList();
            // An expendable monster (a Gorz token) takes an equal-ATK trade rather than sit in defence for good (user,
            // 2026-11-11: the token summoned in defence "seems to get stuck there").
            if (targets.Count == 0 && IsExpendable(card) && opponents.Any(opponent => IsGoodTrade(card, opponent, attackersUp.Count + 1)))
                return true;
            if (targets.Count == 0)
                return ClearsTheirBoard(attackersUp, opponents) && DirectHitMatters(card, attackersUp) && Rule(DeckRules.RepositionForDirectHit);
            // A face-down (assumed Ryko) is best attacked by a cheap monster when all we have out is cheap: Necro Gardna
            // into it wastes their Ryko. (With a good card out, Ryko would destroy that anyway: no special case.)
            if (IsCheapAttacker(card) && targets.Any(target => !target.IsFaceup() && LikelyRyko(target)) && !attackersUp.Any(IsCheapAttacker) &&
                CheapAttackDodgesRyko() && Rule(DeckRules.CheapIntoFaceDown))
                return true;
            // It could destroy something in attack position that our attackers already up won't get to (user, 2026-11-13: "or if
            // the defence position monster could destroy something if it were in attack. it should switch for that too"): with
            // it, more of their monsters are destroyed than without it (one attack per target).
            var worthTargets = opponents.Where(target => IsWorthAttacking(card, target) || attackersUp.Any(own => IsWorthAttacking(own, target))).ToList();
            if (Kills(attackersUp.Append(card).ToList(), worthTargets) > Kills(attackersUp, worthTargets) && Rule(DeckRules.RepositionToAttack))
                return true;
            // Do we want to attack with it at all? The attackers already up clear their monsters (one each), so this one gets
            // a direct hit: worth switching for when that hit matters (user, 2026-11-13, a Gorz token left in defence while
            // Grepher took their only monster: "do I want to attack with this defence position monster? if yes ... switch").
            return ClearsTheirBoard(attackersUp, opponents) && DirectHitMatters(card, attackersUp) && Rule(DeckRules.RepositionForDirectHit);
        }

        /// <summary>Whether these attackers can destroy every monster of theirs, one attack each.</summary>
        protected bool ClearsTheirBoard(List<DynamicCard> attackers, List<DynamicCard> opponents) =>
            Kills(attackers, opponents) == opponents.Count(card => card.ID != 0 || !card.IsFaceup());

        /// <summary>How many of these monsters of theirs our attackers can destroy, one attack each (the biggest first, each by
        /// the weakest attacker that beats it).</summary>
        protected int Kills(List<DynamicCard> attackers, List<DynamicCard> opponents)
        {
            int Power(DynamicCard card) => !card.IsFaceup() ? OpponentMemory.FaceDownDefense(card) : card.IsAttack() ? card.Attack : card.Defense;
            var left = attackers.Select(card => card.Attack).OrderBy(atk => atk).ToList();
            int kills = 0;
            foreach (var monster in opponents.Where(card => card.ID != 0 || !card.IsFaceup()).OrderByDescending(Power))
            {
                int index = left.FindIndex(atk => atk > Power(monster));
                if (index < 0)
                    continue;
                left.RemoveAt(index);
                kills++;
            }
            return kills;
        }

        /// <summary>Whether this monster's direct hit is worth making (as for an open field: not weak, or it speeds the clock).</summary>
        protected bool DirectHitMatters(DynamicCard card, List<DynamicCard> attackersUp) =>
            !IsWeak(card) || ChangesClock(card, attackersUp) || ExpendableHitter(card) || IsBeater(card);

        /// <summary>
        /// Switch to defence when the monster has nothing worth attacking and either it is weak (alone it would be an
        /// Almost None clock or none, e.g. Plaguespreader) or its DEF survives the opponent's strongest face-up attacker
        /// that would beat it in attack position. A monster that still makes a real clock stays in attack position.
        /// </summary>
        protected bool ShouldRepositionToDefense(DynamicCard card, IEnumerable<DynamicCard> opponentMonsters)
        {
            var opponents = opponentMonsters.ToList();
            int strongestOpponentAttack = opponents
                .Where(opponent => opponent.IsFaceup() && opponent.IsAttack())
                .Select(opponent => opponent.Attack)
                .DefaultIfEmpty(0)
                .Max();

            bool threatened = strongestOpponentAttack > card.Attack;
            bool survivesInDefense = card.Defense >= strongestOpponentAttack;
            // Low LP (round-robin 3 / 16; 2026-11-13: Armageddon Knight left in attack position at 600 LP against Fortress, and
            // Fortress killed us through it): a monster their attacker kills us through goes to defence, unless its attack
            // this turn wins or removes something (then the Battle Phase is its chance; after it, Main Phase 2 switches it).
            bool battleNow = (DuelPhase)_currentHeader.CurrentPhase == DuelPhase.Main1;
            if (_lowLpExposure is { } exposure && card.Attack <= exposure.MaxAttack &&
                !(battleNow && (opponents.Any(opponent => opponent.IsFaceup() && IsWorthAttacking(card, opponent)) ||
                                BoardAssessment.TurnsToWin(_ownMonsters.Where(own => own.IsFaceup() && own.IsAttack()).Select(EffectiveAttack),
                                    BoardAssessment.Defenders(opponents, OpponentMemory), _oppLp) == 1)) &&
                Rule(DeckRules.LowLpNoExposure))
            {
                _mirrorForceReason[card.CurrentCoordinates.Sequence] = exposure.Why;
                return true;
            }
            // E.g. Chaos Sorcerer: as good in defence for clearing monsters, so a reasonable Mirror Force chance weighs
            // against the pressure its attack adds (a roll), and lethal always keeps it up.
            if (MirrorDefenceReason(card.ID, card.Attack, countsAlreadyOnField: true, opponents, battleAhead: true,
                    AttackMatters(card, opponents), _mirrorRollTurn) is string mirrorDefence)
            {
                _mirrorForceReason[card.CurrentCoordinates.Sequence] = mirrorDefence;
                return true;
            }
            bool hasGoodAttack = opponents.Any(opponent => opponent.IsFaceup() && IsWorthAttacking(card, opponent));
            if (hasGoodAttack)
                return false; // it has something worth attacking
            if (ShelterFromMirrorForce(card, opponents, survivesInDefense))
                return true;
            // Plaguespreader is synchro material, never an attacker: a 400 body at their low LP still isn't one (user, 2026-11-13:
            // "why is plague not at very least turning to defence?!").
            bool weak = IsWeak(card) || card.IsCode(PlaguespreaderZombie);
            if (opponents.Count == 0)
            {
                // It will attack directly unless it's weak and its damage doesn't change the clock.
                var attackers = _ownMonsters.Where(own => own.IsFaceup() && own.IsAttack()).ToList();
                if (!weak || ChangesClock(card, attackers))
                    return false;
            }

            // A weak monster that isn't attacking has no reason to stay in attack position.
            return weak || (threatened && survivesInDefense);
        }
    }
}
