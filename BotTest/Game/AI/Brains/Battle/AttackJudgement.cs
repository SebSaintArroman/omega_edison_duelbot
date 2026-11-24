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
    /// How good an attack is: attack scores, threat sizes, which of our monsters are expendable, cheap, weak or beaters.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// Monsters that aren't worth spending protection on (Necro Gardna, sheltering from Mirror Force, a trap just to
        /// save them): a Level 4 or lower main-deck monster under 2000 ATK has usually done its job (Armageddon Knight,
        /// Dark Grepher) and is easy to replace; tokens likewise. Losing one to a trap even helps: the trap is spent.
        /// Deck brains add their own (e.g. a synchro that is material from the graveyard).
        /// </summary>
        /// (Tokens like any other small monster: under StrongMonsterAttack. How expendable is tapered in TributeCost: user,
        /// 2026-11-13, "tokens aren't inherently expendable ... tapered".)
        protected virtual bool IsExpendable(DynamicCard card) =>
            IsTemporaryMonster(card) || // leaves soon whatever we do (Returner's Jinzo, Darius' revived monster): never protected
            card.HasType(CardType.Token) && EffectiveAttack(card) < StrongMonsterAttack || IsSpentFlipMonster(card) || LylaToUseUp(card) ||
            (ReSummonCost(card) < 100 && card.Level <= 4 && card.Attack < StrongMonsterAttack);

        /// <summary>
        /// "Weak" in clock terms: on its own, hitting directly every turn, it would be Almost None (9-10 turns) or no
        /// clock at all (e.g. Plaguespreader). A 1400 ATK Gorz token is a 6-turn clock against 8000 LP: a real attacker.
        /// </summary>
        /// <summary>
        /// A beater: BeaterAttack+ ATK (Gearframe 1800, Mechanicalchaser 1850): a monster you want attacking whenever it can,
        /// whatever its effect.
        /// </summary>
        protected bool IsBeater(DynamicCard card) => card.IsFaceup() && card.Attack >= BeaterAttack;

        /// <summary>ATK from which a monster is a beater (attacks an open field whenever it can). Tune from logs.</summary>
        [Tunable("ATK from which a monster is a beater (attacks an open field whenever it can). Tune from logs.")]
        public int BeaterAttack { get; set; } = 1700;

        /// <summary>An expendable monster whose hit is still decent (ExpendableHitterMinAtk+): worth attacking an open field with.</summary>
        protected bool ExpendableHitter(DynamicCard card) => IsExpendable(card) && card.Attack >= ExpendableHitterMinAtk && Rule(DeckRules.ExpendableAttacksOpenField);

        /// <summary>ATK from which an expendable monster attacks an open field (1100 Hamster, 1400 Armageddon Knight; not a 200 Ryko). Tune from logs.</summary>
        [Tunable("ATK from which an expendable monster attacks an open field (1100 Hamster, 1400 Armageddon Knight; not a 200 Ryko). Tune from logs.")]
        public int ExpendableHitterMinAtk { get; set; } = 1000;

        protected bool IsWeak(DynamicCard card) =>
            BoardAssessment.SpeedOf(BoardAssessment.TurnsToWin(new[] { card.Attack }, Array.Empty<BoardAssessment.Defender>(), _oppLp))
                is ClockSpeed.AlmostNone or ClockSpeed.None;

        /// <summary>Whether adding this monster's direct attacks makes the board win in fewer turns (or this turn).</summary>
        protected bool ChangesClock(DynamicCard card, IEnumerable<DynamicCard> attackers)
        {
            var others = attackers.Where(own => !SameZone(own, card)).Select(own => own.Attack).ToList();
            int without = BoardAssessment.TurnsToWin(others, Array.Empty<BoardAssessment.Defender>(), _oppLp);
            others.Add(card.Attack);
            int with = BoardAssessment.TurnsToWin(others, Array.Empty<BoardAssessment.Defender>(), _oppLp);
            return with < without;
        }

        // Cards from different API calls are different structs; the zone identifies them.
        private static bool SameZone(DynamicCard a, DynamicCard b) =>
            a.CurrentCoordinates.Controller == b.CurrentCoordinates.Controller &&
            a.CurrentCoordinates.Location == b.CurrentCoordinates.Location &&
            a.CurrentCoordinates.Sequence == b.CurrentCoordinates.Sequence;

        /// <summary>Effect points (EffectEvaluator score without its ATK part) of opponent monsters seen, by card id.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, double> EffectPointsCache = new();

        /// <summary>ATK-equivalent of one effect point in TargetThreat: effects and ATK balanced.</summary>
        [Tunable("ATK-equivalent of one effect point in TargetThreat: effects and ATK balanced.")]
        public static readonly Tuned<int> EffectPointValue = new(500);

        /// <summary>
        /// A cheap monster to send into a possible Ryko / unknown face-down: losing it costs little (expendable) or even
        /// helps (Necro Gardna works from the graveyard). If it's a Ryko, the flip wastes their Ryko on it.
        /// </summary>
        /// (Never a big monster: a semi-expendable 2000+ body like Armed Wing is still worth more than a dodge.)
        protected bool IsCheapAttacker(DynamicCard card) =>
            (IsExpendable(card) || HasGraveyardEffect(card)) && card.Attack < StrongMonsterAttack;

        /// <summary>
        /// Whether the rest of our board wins this turn once both the attacker and its target leave the field
        /// (the bouncer returns both): then losing a token or synchro to it is worth it.
        /// </summary>
        protected bool LethalIfBothRemoved(DynamicCard attacker, DynamicCard target)
        {
            var rest = _ownMonsters
                .Where(own => own.IsFaceup() && own.IsAttack() && !own.Attacked && !SameZone(own, attacker))
                .Select(own => own.Attack);
            var defenders = BoardAssessment.Defenders(_opponents.Where(card => !SameZone(card, target)), OpponentMemory);
            return BoardAssessment.TurnsToWin(rest, defenders, _oppLp) == 1;
        }

        /// <summary>
        /// What it costs us if this monster is returned to the hand: a token or Extra Deck monster is lost (never
        /// battle a bouncer with them), a Level 5+ costs a tribute or special condition, a Level 4 or lower just a Normal Summon.
        /// </summary>
        protected static int ReSummonCost(DynamicCard card) =>
            card.HasType(CardType.Token) || card.HasType(CardType.Synchro) || card.HasType(CardType.Fusion) ||
            card.HasType(CardType.Xyz) || card.HasType(CardType.Link) ? 100
            : card.Level >= 5 ? 2 : 1;

        /// <summary>
        /// Whether a winning attack is worth making. Recruiters and floaters (Giant Rat, Card Trooper, Kittytail...) are
        /// still worth destroying when we win the battle: floating never makes a monster not worth destroying (user rule,
        /// 2026-11-09). What we don't do is crash into them (see IsGoodTrade). Not into a bouncer that would cost us a
        /// token / Extra Deck monster, unless that wins the game.
        /// </summary>
        protected bool IsWorthAttacking(DynamicCard attacker, DynamicCard target)
        {
            if (GetAttackScore(attacker, target) <= 0)
                return false;
            if (BouncesAttacker(target) && ReSummonCost(attacker) >= 100 && !LethalIfBothRemoved(attacker, target))
                return false; // it would be returned to the hand and lost, and it doesn't win the game
            return true;
        }

        /// <summary>Attack-score units (TargetThreat) for a graveyard piece of ours lost to banishment (Vayu). Tune from logs.</summary>
        [Tunable("Attack-score units (TargetThreat) for a graveyard piece of ours lost to banishment (Vayu). Tune from logs.")]
        public int GraveyardPieceThreat { get; set; } = 1000;

        /// <summary>Attack-score units (TargetThreat) for a hand card spent on a battle boost (Kalut). Tune from logs.</summary>
        [Tunable("Attack-score units (TargetThreat) for a hand card spent on a battle boost (Kalut). Tune from logs.")]
        public int HandBoostCardThreat { get; set; } = 1000;

        /// <summary>
        /// Whether taking this recruiter is worth it (user, 2026-11-17: "if I had this monster, on the field able to attack next turn, would
        /// that speed up my opponent's clock?" and "only ... if you have a way of protecting that recruiter from being attacked. it's risky to
        /// take opponents recruiters so this should only be done very rarely"): with it attacking next turn alongside our face-up attack-position
        /// monsters, their life points fall in fewer of our turns, and a set card of ours can protect it.
        /// </summary>
        private bool StealSpeedsUpClock(DynamicCard attacker, DynamicCard target)
        {
            if (!_weCanProtect)
                return false;
            int total = _ownMonsters.Where(card => card.IsFaceup() && card.IsAttack()).Sum(card => Math.Max(0, card.Attack));
            int with = total + Math.Max(0, target.Attack);
            if (with <= 0 || _theirLifePoints <= 0)
                return false;
            int Turns(int atk) => atk <= 0 ? int.MaxValue : (_theirLifePoints + atk - 1) / atk;
            return Turns(with) < Turns(total);
        }

        /// <summary>Assumed ATK/DEF of the monster a recruiter brings out (they fetch small monsters, usually 1500 or less).</summary>
        [Tunable("Assumed ATK/DEF of the monster a recruiter brings out (they fetch small monsters, usually 1500 or less).")]
        public int AssumedRecruitPower { get; set; } = 1500;

        /// <summary>
        /// Whether destroying this floater lets the rest of our attacks win this turn. A recruiter brings out a
        /// replacement, assumed to block one more attack in defence position.
        /// </summary>
        private bool LethalIfFloaterRemoved(DynamicCard attacker, DynamicCard target)
        {
            var rest = _ownMonsters
                .Where(own => own.IsFaceup() && own.IsAttack() && !own.Attacked && !SameZone(own, attacker))
                .Select(own => own.Attack);
            var defenders = BoardAssessment.Defenders(_opponents.Where(card => !SameZone(card, target)), OpponentMemory);
            if (_recruiters.Contains(target.ID))
                defenders.Add(new BoardAssessment.Defender(AssumedRecruitPower, false, false, 0));
            return BoardAssessment.TurnsToWin(rest, defenders, _oppLp) == 1;
        }

        /// <summary>ATK our hand can add to this attacker in the damage step (Kalut: +1400 to a Blackwing). Default: none.</summary>
        protected virtual int HandBattleBoost(DynamicCard attacker) => 0;

        /// <summary>Attack score for running into a known Honest (or Kalut) when the attack wins without it: a trade, below any kill (10,000+).</summary>
        [Tunable("Attack score for running into a known Honest (or Kalut) when the attack wins without it: a trade, below any kill (10,000+).")]
        public int HonestTradeScore { get; set; } = 6_000;


        [Tunable("Honest counts as suspected in their hand from this deck-read chance (or when known).")]
        public static readonly Tuned<double> HonestSuspectChance = new(0.3);

        /// <summary>Honest is known in their hand, or the deck read puts it there at HonestSuspectChance or more.</summary>
        protected bool HonestSuspected(int theirHandCount) =>
            KnownEnemyHand().Any(id => id == Honest || Search.SearchHost.CanonicalId(id) == Honest) ||
            (OpponentDeck.HandChance(Honest, theirHandCount) ?? 0) >= HonestSuspectChance;

        /// <summary>
        /// What a card we know is in their hand adds to their monster in a battle with this attacker: Honest gives a LIGHT
        /// monster the attacker's ATK, Kalut +1400 to a Blackwing. 0 when we know of none.
        /// </summary>
        protected int EnemyHandBattleBoost(DynamicCard defender, DynamicCard attacker)
        {
            if (!defender.IsFaceup())
                return 0;
            var known = KnownEnemyHand();
            if (known.Any(id => id == Honest || Search.SearchHost.CanonicalId(id) == Honest) && (defender.Attribute & 0x10) != 0 && defender.IsAttack())
                return attacker.Attack;
            if (known.Any(id => id == BlackwingKalutTheMoonShadow || Search.SearchHost.CanonicalId(id) == BlackwingKalutTheMoonShadow) &&
                Search.SearchHost.Available && Search.SearchHost.Cards.Get(defender.ID)?.Name?.StartsWith("Blackwing") == true)
                return 1400;
            return 0;
        }

        protected int GetAttackScore(DynamicCard attacker, DynamicCard defender)
        {
            int attackerPower = attacker.Attack;
            int defenderPower = defender.GetRealPower();
            // A boost from our hand in the damage step (Kalut's +1400) counts when it turns a lost or tied battle into a win: the
            // brain plans its attacks with it (user, 2026-11-10), and the chain rules then use it at the battle.
            if (attackerPower <= defenderPower && HandBattleBoost(attacker) is int boost and > 0 && attackerPower + boost > defenderPower)
                attackerPower += boost;
            // ...and theirs, when we know it's in their hand (user, 2026-11-11: the known Honest): it wins their monster the battle.
            // An attack that wins without it still goes, as a trade: our attacker for their Honest, which has to be got past
            // some time (user, 2026-11-11: "it still should've attacked"). Below any real kill.
            int handBoost = EnemyHandBattleBoost(defender, attacker);
            if (handBoost > 0 && attackerPower > defenderPower && attackerPower <= defenderPower + handBoost &&
                !DestroysByEffectInBattle(defender, attacker))
                return HonestTradeScore + TargetThreat(defender) / 4;
            defenderPower += handBoost;

            // Their Catastor-type monster destroys ours by its effect when they battle (whatever the ATK, and not by battle,
            // so no "destroyed by battle" payoff either): never attack into it, unless ours destroys it the same way.
            if (DestroysByEffectInBattle(defender, attacker) && !DestroysByEffectInBattle(attacker, defender))
                return 0;

            bool effectKill = DestroysByEffectInBattle(attacker, defender);
            if (attackerPower <= defenderPower && !effectKill)
                return 0;
            // "Cannot be destroyed by battle": it survives, so in defence attacking it does nothing. In attack position the
            // damage still goes through (a small score: below any kill), unless it also stops the battle damage (a live
            // Blackwing Armor Master: "you take no battle damage"). Both checks say no when its effects are negated.
            if (IsBattleProof(defender) && !effectKill)
                return defender.IsFaceup() && defender.IsAttack() && !StopsBattleDamage(defender)
                    ? 10_000 + (attackerPower - defenderPower) / 4
                    : 0;

            // "Once per turn, cannot be destroyed by battle" (Gemini Soldier): the first attack this turn only uses up
            // the protection (a small score: it does let a second attack finish it); once we've battled it this turn
            // its protection is gone, so a second attack destroys it, worth its full threat plus a bonus for finishing it.
            if (_oncePerTurnBattleProtected.Contains(defender.ID))
            {
                bool battled = _battledThisTurn.Contains((CurrentHeader.CurrentTurn, defender.CurrentCoordinates.Sequence, defender.ID));
                if (!battled)
                    return 10_000 + TargetThreat(defender) / 4;
                return 10_000 + 2 * TargetThreat(defender) + 1_000;
            }
            if (effectKill && attackerPower <= defenderPower)
                return 10_000 + 2 * TargetThreat(defender); // destroyed before damage: no battle damage either way

            // Killing the bigger threat matters far more than the battle damage (life points matter little), so the
            // target's threat counts double and damage only a quarter. E.g. Brionac (2300) over Moja (100) even though
            // hitting Moja deals more damage, and Muka Muka (2000/1400 in DEF) over a 1400/1400 token.
            int score = 10_000 + 2 * TargetThreat(defender);

            if (defender.IsFaceup() && defender.IsAttack())
                score += (attackerPower - defenderPower) / 4;
            // What the attack costs us (user, 2026-11-13: Vayu + Kalut into a face-up D.D. Warrior Lady, banishing Vayu, when
            // Card Trooper was there for Vayu alone: "why not just attack into card trooper?"):
            //  - a target that banishes / returns what battles it (not Grand Mole: its own rules) takes our attacker too, and
            //    a graveyard piece (Vayu) loses its graveyard value with it;
            //  - a win that needs a boost from our hand (Kalut) spends that card.
            if (defender.IsFaceup() && _targetRemovesAttacker.Contains(defender.ID) && !BouncesAttacker(defender))
                score -= 2 * (TargetThreat(attacker) + (HasGraveyardEffect(attacker) ? GraveyardPieceThreat : 0));
            if (attacker.Attack <= defender.GetRealPower() && attackerPower > defender.GetRealPower())
                score -= 2 * HandBoostCardThreat;
            return score;
        }

        /// <summary>
        /// How dangerous a face-up opponent monster is: its ATK (or DEF if higher), plus a bonus for having an effect
        /// and for being an Extra Deck monster (synchros like Brionac usually carry strong effects).
        /// </summary>
        protected int TargetThreat(DynamicCard defender)
        {
            int threat = Math.Max(defender.Attack, defender.Defense);
            // A monster whose effects are negated (e.g. a synchro summoned by Vayu) is just its body: no effect or Extra
            // Deck bonus. So at equal ATK a negated synchro is worth less than a monster with live effects, and crashing
            // it into one is a good trade.
            if (IsEffectNegated(defender))
                return threat;
            // A flip monster face-up has used its effect (a flipped Ryko): it's just its stats too (user, 2026-11-13: "because all
            // of their effects have been used, the only thing that mattered there should've been their stats").
            if (IsSpentFlipMonster(defender))
                return threat;
            // Effects balanced against ATK: about 500 per effect point (a strong effect of 4-6 points is worth a 2000-3000
            // swing), read from the effect scores cached when we read their monsters; a flat 500 if not scored yet.
            if (EffectPointsCache.TryGetValue(defender.ID, out double points))
                threat += (int)(points * EffectPointValue);
            else if (defender.HasType(CardType.Effect))
                threat += 500;
            if (defender.HasType(CardType.Synchro) || defender.HasType(CardType.Fusion) ||
                defender.HasType(CardType.Xyz) || defender.HasType(CardType.Link))
                threat += 500;
            return threat;
        }
    }
}
