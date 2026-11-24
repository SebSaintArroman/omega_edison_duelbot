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
    /// Battle protection (shared staples, user 2026-11-11: "shield warrior and waboku should both be considered when deciding
    /// when to attack, not just when being attacked (but also then)"): Shield Warrior banished from our graveyard keeps our
    /// battling monster from being destroyed by that battle; Waboku keeps all of ours alive and stops the battle damage. So a
    /// monster that gains from battling (a Gladiator Beast tags out after it battled) may attack into one that beats it, and in
    /// the damage step, on either turn, our battling monster that would die is saved.
    /// </summary>
    public abstract partial class DeckBotBrain
    {

        /// <summary>
        /// Why this attacker gains just from battling, whatever the result (a Gladiator Beast's tag-out), or null. Default:
        /// nothing. Decks override.
        /// </summary>
        protected virtual string? BattleGain(DynamicCard attacker) => null;

        /// <summary>What can keep our battling monster from being destroyed this battle: Shield Warrior in our graveyard, a set Waboku. Null if nothing.</summary>
        private async Task<(string Name, bool StopsDamage)?> BattleProtectionAsync()
        {
            var grave = await GetBotGraveyardCardsAsync();
            if (grave.Any(card => card.IsCode(ShieldWarrior)))
                return ("Shield Warrior (banished from the graveyard)", false);
            var spells = await CurrentField.GetCardsInSpellZone();
            if (spells.Any(card => card.ID > 0 && card.IsCode(Waboku)))
                return ("Waboku", true);
            return null;
        }

        /// <summary>
        /// An attack that gains from battling (BattleGain) into a monster that would beat our attacker, made safe by Shield
        /// Warrior / Waboku: our monster survives, battles, and gets its payoff (a tag-out). The biggest threat we can afford
        /// the damage from: any amount we survive (none with Waboku). Null when there's no such attack.
        /// </summary>
        private async Task<BattleCmdResponse?> TryProtectedAttackAsync(DecisionContext<BattleCmdData> context,
            List<DynamicCard> attackableCards, List<DynamicCard> opponentMonsters)
        {
            var protection = await BattleProtectionAsync();
            int lp = await CurrentField.GetBotLifePoints();
            static int Power(DynamicCard card) => card.IsAttack() ? card.Attack : card.Defense;
            // A face-up defence-position monster can't destroy our attacker: battling it is safe without protection, for the
            // damage (user, 2026-11-13, round-robin 29b: "glads will sometimes want to attack into defence position monsters if
            // there's no more favourable attack target even if they can't kill that monster (eg a retiari attacking into a face
            // up hamster if the only other monster is a set ryko), just so that they can tag out"). The damage keeps us above
            // DangerLifePoints.
            bool Safe(DynamicCard attacker, DynamicCard target) =>
                protection is { } p ? p.StopsDamage || Power(target) - attacker.Attack < lp // any damage we survive (user, 2026-11-11)
                                    : !target.IsAttack() && lp - Math.Max(0, target.Defense - attacker.Attack) > DangerLifePoints;
            var pick = attackableCards
                .Select(attacker => (Attacker: attacker, Gain: BattleGain(attacker)))
                .Where(x => x.Gain != null)
                .SelectMany(x => opponentMonsters
                    .Where(target => target.IsFaceup() && Power(target) >= x.Attacker.Attack && !BouncesAttacker(target) && Safe(x.Attacker, target))
                    .Select(target => (x.Attacker, Target: target, x.Gain)))
                .OrderByDescending(x => TargetThreat(x.Target))
                .FirstOrDefault();
            if (pick.Gain == null || !Rule(DeckRules.ProtectedAttack))
                return null;
            string how = protection is { } used ? $"{used.Name} keeps it alive" : $"it's in defence, so ours survives ({Math.Max(0, pick.Target.Defense - pick.Attacker.Attack)} damage)";
            AimAt(context.Bot, pick.Target, AimKind.Deliberate, $"attack {CardNames.Get(pick.Target.ID)} ({Power(pick.Target)}) with {CardNames.Get(pick.Attacker.ID)} " +
                                $"({pick.Attacker.Attack}): {how}, and {pick.Gain}");
            return pick.Attacker.DeclareAttack();
        }

        /// <summary>
        /// In battle, either turn: our battling monster would be destroyed (or the damage would kill us), so Shield Warrior from
        /// the graveyard (free) or Waboku saves it. Returns the chain index, or null.
        /// </summary>
        protected async Task<int?> TryChainBattleProtectionAsync(DecisionContext<SelectChainData> context, DynamicCard[] options)
        {
            int shield = Array.FindIndex(options, card => card.IsCode(ShieldWarrior) && card.CurrentCoordinates.Controller == 0 &&
                                                          card.CurrentCoordinates.Location == (byte)CardLocation.Grave);
            int waboku = Array.FindIndex(options, card => card.IsCode(Waboku) && card.CurrentCoordinates.Controller == 0);
            if (shield < 0 && waboku < 0)
                return null;
            var ours = await CurrentField.GetBotBattlingMonster(isOwnSide: true);
            var theirs = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            if (ours.ID <= 0 || theirs.ID <= 0)
                return null;
            // The battling monster as reported can be stale: the battle is over and ours already gone (CSV M9: Waboku chained after
            // Laquari had died to Honest). Only a monster of ours still on the field in that zone is protected.
            if (!(await GetBotMonsterCardsAsync()).Any(m => m.ID == ours.ID && m.IsFaceup() &&
                                                           m.CurrentCoordinates.Sequence == ours.CurrentCoordinates.Sequence))
                return null;
            bool weAttack = IsOwnTurn(context.Header);
            // Whether ours is destroyed by this battle, and the damage to us.
            bool dies;
            int damage;
            if (weAttack)
            {
                int theirPower = theirs.IsAttack() ? theirs.Attack : theirs.Defense;
                dies = theirs.IsAttack() && theirs.Attack >= ours.Attack;
                damage = Math.Max(0, theirPower - ours.Attack);
            }
            else
            {
                int ourPower = ours.IsAttack() ? ours.Attack : ours.Defense;
                dies = ours.IsAttack() ? theirs.Attack >= ours.Attack : theirs.Attack > ours.Defense;
                damage = ours.IsAttack() ? Math.Max(0, theirs.Attack - ours.Attack) : 0;
            }
            int lp = await CurrentField.GetBotLifePoints();
            bool lethal = damage >= lp;
            if (lethal && waboku >= 0 && Rule(DeckRules.BattleProtection))
            {
                context.Bot.LogNote($"deck brain: Waboku: {CardNames.Get(theirs.ID)}'s battle damage ({damage}) would kill us");
                return waboku;
            }
            // Only monsters that gain from battling are protected (Gladiator Beasts: user, 2026-11-11, "don't use shield warrior
            // or waboku to protect monsters that aren't glads"); and not once Waboku already covers this turn.
            if (!dies || BattleGain(ours) is not string gain || _wabokuTurn == context.Header.CurrentTurn)
                return null;
            string why = $"{CardNames.Get(ours.ID)} would be destroyed by {CardNames.Get(theirs.ID)}; kept alive, {gain}";
            // Waboku covers every battle this turn, Shield Warrior one: Waboku when more than one battle needs help (several of
            // their attackers bigger than our Gladiator Beasts, or several of ours to attack into bigger monsters), else Shield
            // Warrior (user, 2026-11-11).
            int needing = await BattlesNeedingHelpAsync(weAttack);
            bool preferWaboku = needing >= 2 || shield < 0;
            if (preferWaboku && waboku >= 0 && Rule(DeckRules.BattleProtection))
            {
                _wabokuTurn = context.Header.CurrentTurn;
                context.Bot.LogNote($"deck brain: Waboku: {why}" + (needing >= 2 ? $" ({needing} battles need help this turn: Waboku covers them all)" : ""));
                return waboku;
            }
            if (shield >= 0 && Rule(DeckRules.BattleProtection))
            {
                context.Bot.LogNote($"deck brain: banish Shield Warrior from the graveyard: {why}");
                return shield;
            }
            return null;
        }

        private int _wabokuTurn = -1;

        /// <summary>
        /// How many battles this Battle Phase would destroy one of our monsters that gains from battling (Gladiator Beasts),
        /// this one included: on their turn, their attack-position monsters yet to attack that beat one of ours; on ours, our
        /// monsters yet to attack (and the one attacking) that only have bigger monsters of theirs to battle.
        /// </summary>
        private async Task<int> BattlesNeedingHelpAsync(bool weAttack)
        {
            var ours = (await GetBotMonsterCardsAsync()).Where(m => m.ID > 0 && m.IsFaceup() && BattleGain(m) != null).ToList();
            var theirs = (await GetEnemyMonsterCardsAsync()).Where(m => m.ID > 0).ToList();
            static int Power(DynamicCard card) => card.IsAttack() ? card.Attack : card.Defense;
            if (weAttack)
                // This battle, plus our monsters yet to attack that have nothing of theirs they'd beat.
                return 1 + ours.Count(m => m.IsAttack() && !m.Attacked && theirs.Any(t => t.IsFaceup()) &&
                                           !theirs.Any(t => t.IsFaceup() && Power(t) < m.Attack));
            // This attack, plus their attack-position monsters yet to attack that beat one of ours.
            return 1 + theirs.Count(t => t.IsFaceup() && t.IsAttack() && !t.Attacked && ours.Any(m => t.Attack >= Power(m)));
        }
    }
}
