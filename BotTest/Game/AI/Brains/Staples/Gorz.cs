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
    /// Our Gorz the Emissary of Darkness: keeping it live (an empty field), when to hold plays for it, and drawing for it.
    /// RULES deck.keep-gorz-live, deck.gorz-chain, deck.draw-for-gorz.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // ------------------------------------------------------------------ our Gorz

        /// <summary>
        /// Gorz in hand and nothing on our field: Gorz answers a direct attack (and deters it), so don't set cards or
        /// blockers that would switch it off. Unless Gorz can't save us: the opponent's biggest face-up attacker
        /// already deals lethal damage in one hit.
        /// </summary>
        protected async Task<bool> KeepGorzLiveAsync<T>(DecisionContext<T> context, List<DynamicCard> enemyMonsters)
        {
            if (!(await GetBotHandCardsAsync()).Any(card => card.IsCode(GorzTheEmissaryOfDarkness)))
                return false;
            if ((await GetBotMonsterCardsAsync()).Count > 0 || (await CurrentField.GetCardsInSpellZone()).Any(card => card.ID > 0))
                return false;
            int biggest = enemyMonsters.Where(card => card.IsFaceup()).Select(card => card.Attack).DefaultIfEmpty(0).Max();
            if (biggest >= await CurrentField.GetBotLifePoints())
            {
                context.Bot.LogNote($"deck brain: Gorz can't save us from a {biggest} ATK hit: play normally");
                return false;
            }
            // A direct hit that discards from our hand (X-Saber Airbellum) can take Gorz before it's used: an empty field
            // isn't a safe plan then, so develop the board instead of holding everything back for Gorz.
            foreach (var enemy in enemyMonsters.Where(card => card.IsFaceup() && card.ID > 0))
            {
                if (await CardDatabase.GetAsync(enemy.ID) is DBCardInfo info && HandDiscardOnHitText.IsMatch(info.CardText ?? ""))
                {
                    context.Bot.LogNote($"deck brain: don't keep the field empty for Gorz: their {CardNames.Get(enemy.ID)}'s direct hit can discard it; develop instead");
                    return false;
                }
            }
            // Not always the priority: weigh what Gorz would get us (a 2700 body plus a token copying their biggest hit,
            // if they attack into it at all) against what our other plays would do this turn. Too complex to simulate
            // fully, so it's a probability, rolled once per turn: hold chance = base + per 1000 of their biggest
            // attacker - per alternative play we have, kept within 0-100% (it can be a certain hold or a certain play).
            int turn = context.Header.CurrentTurn;
            if (_gorzHoldRoll.Turn != turn)
            {
                int plays = await AlternativePlaysAsync();
                // Solemn Judgment in hand too: it waits for Gorz (see ChooseTrapSetAsync), so it isn't another play, and
                // Gorz gets more priority than usual (Solemn is set right after it comes out).
                bool solemnInHand = (await GetBotHandCardsAsync()).Any(card => card.IsCode(SolemnJudgment));
                if (solemnInHand)
                    plays = Math.Max(0, plays - 1);
                double chance = Math.Clamp(GorzHoldBase + GorzHoldPerThousand * biggest / 1000.0 - GorzHoldPerPlay * plays +
                                           (solemnInHand ? GorzHoldWithSolemn : 0), GorzHoldMin, GorzHoldMax);
                double roll = RollAt;
                string why = $"Gorz would bring a 2700 body + a token copying their biggest hit ({biggest}); we have {plays} other play(s) this turn" +
                             $"{(solemnInHand ? ", Solemn Judgment waits in hand for Gorz (+priority)" : "")}: hold chance {chance:P0}, rolled {roll:0.00}";
                _gorzHoldRoll = (turn, roll < chance, why);
                context.Bot.LogNote(roll < chance
                    ? $"deck brain: keep the field empty so Gorz stays live ({why})"
                    : $"deck brain: play normally instead of keeping Gorz live ({why})");
            }
            return _gorzHoldRoll.Hold;
        }

        private (int Turn, bool Hold, string Why) _gorzHoldRoll = (-1, false, "");

        /// <summary>Hold-for-Gorz chance before adjustments. Tune from logs.</summary>
        [Tunable("Hold-for-Gorz chance before adjustments. Tune from logs.")]
        public double GorzHoldBase { get; set; } = 0.45;
        /// <summary>Added per 1000 ATK of their biggest attacker (the token copies that hit). Tune from logs.</summary>
        [Tunable("Added per 1000 ATK of their biggest attacker (the token copies that hit). Tune from logs.")]
        public double GorzHoldPerThousand { get; set; } = 0.1;
        /// <summary>Taken off per other play we could make this turn. Tune from logs.</summary>
        [Tunable("Taken off per other play we could make this turn. Tune from logs.")]
        public double GorzHoldPerPlay { get; set; } = 0.15;
        /// <summary>Added to the hold-for-Gorz chance when Solemn Judgment is in hand too (it waits for Gorz). Tune from logs.</summary>
        [Tunable("Added to the hold-for-Gorz chance when Solemn Judgment is in hand too (it waits for Gorz). Tune from logs.")]
        public double GorzHoldWithSolemn { get; set; } = 0.3;
        [Tunable("")]
        public double GorzHoldMin { get; set; } = 0;
        [Tunable("")]
        public double GorzHoldMax { get; set; } = 1;

        /// <summary>
        /// How many real plays we have this turn besides keeping Gorz live (for the Gorz hold roll). Default: monsters
        /// we could summon and traps we could set. Deck brains add their own (graveyard plays).
        /// </summary>
        protected virtual async Task<int> AlternativePlaysAsync()
        {
            var hand = await GetBotHandCardsAsync();
            return hand.Count(card => card.ID > 0 && !card.IsCode(GorzTheEmissaryOfDarkness) &&
                                      (card.HasType(CardType.Monster) || card.HasType(CardType.Trap)));
        }

        /// <summary>
        /// Gorz triggers on each direct hit, and its token copies the damage, so wait for the opponent's biggest
        /// attacker. Use it now if this attacker is the biggest one left, or if taking every remaining attack before
        /// Gorz comes out would lose the game. Returns the reason to summon now, or null to wait.
        /// </summary>
        // A battle-damage effect that takes a card from our hand (X-Saber Airbellum discards one at random).
        protected static readonly System.Text.RegularExpressions.Regex HandDiscardOnHitText = new(
            @"inflicts? battle damage[^.]{0,120}?(discard|send)[^.]{0,60}?hand",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        protected async Task<string?> GorzNowAsync<T>(DecisionContext<T> context, List<DynamicCard> enemyMonsters)
        {
            var attacker = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            if (attacker.ID <= 0)
                return "special summon Gorz (attacker unknown)";

            // A hit whose effect takes a card from our hand (X-Saber Airbellum) could take Gorz itself: use it now.
            if (await CardDatabase.GetAsync(attacker.ID) is DBCardInfo attackerInfo && HandDiscardOnHitText.IsMatch(attackerInfo.CardText ?? ""))
                return $"special summon Gorz: {CardNames.Get(attacker.ID)}'s hit would discard from our hand, and that could be Gorz";

            if (enemyMonsters.Count == 0)
                enemyMonsters = await GetEnemyMonsterCardsAsync();
            var stillToAttack = enemyMonsters
                .Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked && !SameZone(card, attacker))
                .ToList();
            int biggestLeft = stillToAttack.Select(card => card.Attack).DefaultIfEmpty(0).Max();
            if (attacker.Attack >= biggestLeft)
                return $"special summon Gorz: {CardNames.Get(attacker.ID)} ({attacker.Attack}) is the biggest attacker, biggest token";

            int lp = await CurrentField.GetBotLifePoints();
            int remainingDamage = stillToAttack.Sum(card => card.Attack);
            if (remainingDamage >= lp || !Rule(DeckRules.GorzWaitsForBiggest))
                return $"special summon Gorz: waiting for the {biggestLeft} attacker would lose ({remainingDamage} damage still to come vs {lp} LP)";

            context.Bot.LogNote($"deck brain: hold Gorz: {CardNames.Get(attacker.ID)} ({attacker.Attack}) isn't the biggest; wait for the {biggestLeft} attacker ({remainingDamage} damage to come vs {lp} LP)");
            return null;
        }

        /// <summary>
        /// The Jar of Greed / Gorz trick: Gorz needs us to control no cards when the direct attack hits. If a set draw
        /// card (Jar of Greed, Legacy of Yata-Garasu) is our only card, flip it in response to the direct attack: it
        /// draws a card and clears the way for Gorz. Returns the chain index, or null.
        /// </summary>
        protected async Task<int?> TryDrawCardForGorzAsync(DecisionContext<SelectChainData> context)
        {
            var options = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            int setDraw = Array.FindIndex(options, card =>
                card.IsCode(JarOfGreed, LegacyOfYataGarasu) && card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone);
            if (setDraw < 0 || IsOwnTurn(context.Header))
                return null;
            if (!(await GetBotHandCardsAsync()).Any(card => card.IsCode(GorzTheEmissaryOfDarkness)) ||
                (await GetBotMonsterCardsAsync()).Count > 0 ||
                (await CurrentField.GetCardsInSpellZone()).Count(card => card.ID > 0) != 1 ||
                !await CurrentField.IsBotUnderAttack())
                return null;
            // The attack must still be live: if our answer already removed the attacker (Dimensional Prison banished it),
            // there's no hit coming and the trick only shows them Gorz.
            var liveAttacker = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            var theirMonsters = await GetEnemyMonsterCardsAsync();
            if (liveAttacker.ID <= 0 || !theirMonsters.Any(card => card.ID == liveAttacker.ID &&
                                                                card.CurrentCoordinates.Sequence == liveAttacker.CurrentCoordinates.Sequence))
            {
                context.Bot.LogNote($"deck brain: keep {CardNames.Get(options[setDraw].ID)} down: the attacker is already gone, no hit is coming");
                return null;
            }
            // Only on the attack we want Gorz on (the biggest one, or the one waiting would lose to): clearing the field
            // earlier just tells them Gorz is in hand, and they can hold back their bigger attacker.
            if (await GorzNowAsync(context, await GetEnemyMonsterCardsAsync()) is not string gorzWhy)
            {
                context.Bot.LogNote($"deck brain: keep {CardNames.Get(options[setDraw].ID)} down for now: this isn't the attack we want " +
                                    "Gorz on, and clearing the field early would show them Gorz");
                return null;
            }
            if (!Rule(DeckRules.DrawForGorz))
                return null;
            context.Bot.LogNote($"deck brain: chain {CardNames.Get(options[setDraw].ID)}: it's our only card and the attack is direct, " +
                                $"so drawing with it enables Gorz on this attack ({gorzWhy})");
            return setDraw;
        }
    }
}
