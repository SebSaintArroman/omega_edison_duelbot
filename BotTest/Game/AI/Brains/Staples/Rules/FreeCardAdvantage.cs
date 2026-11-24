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
    /// Free card advantage, first thing in the Main Phase: Upstart Goblin, Charge of the Light Brigade, Reinforcement of the
    /// Army, Allure of Darkness. RULES deck.upstart-goblin, deck.upstart-waits-for-lethal, deck.gamble-when-losing,
    /// deck.charge-of-the-light-brigade, deck.reinforcement-of-the-army, deck.allure-of-darkness.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>Free card advantage, first thing: Upstart Goblin, Charge of the Light Brigade, Reinforcement of the Army.</summary>
        protected async Task<IdleCmdResponse?> TryFreeCardAdvantageAsync(DecisionContext<IdleCmdData> context, IdleCmdData state)
        {
            IdleCmdResponse response;
            // Upstart Goblin gives them 1000 LP: when this turn's damage could be lethal, it waits for Main Phase 2 (user,
            // 2026-11-13, round-robin 38: "great rule. don't hold it indefinitely, but hold it for mp2 if you think you can kill
            // them that turn"; seen: Upstart first, then Swap Frog's hit left them at exactly 1000).
            if (TryGetActivable(state, UpstartGoblin, out _) && await UpstartWaitsForLethalAsync(context) is string upstartWait &&
                Rule(DeckRules.UpstartWaitsForLethal))
            {
                StapleNote(context, $"hold Upstart Goblin for Main Phase 2: {upstartWait}");
                Hold(UpstartGoblin, MainAction.Activate, DeckRules.UpstartWaitsForLethal);
            }
            else if (TryActivateStaple(context, state, UpstartGoblin, "draw a card", out response, DeckRules.UpstartGoblin)) return response;
            // Round-robin 31 (user, 2026-11-13: "yes, but only as a last resort"): when we lose next turn anyway, the gamble draws
            // go: a set Jar of Greed now, and Allure of Darkness with no DARK once nothing else is left in hand.
            if (await WeLoseNextTurnAsync())
            {
                if ((state.ActivableCards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(JarOfGreed)) && Rule(DeckRules.GambleWhenLosing) &&
                    TryActivateStaple(context, state, JarOfGreed, "draw now: we lose next turn otherwise", out response))
                    return response;
                bool allureAlone = _ownHandIds.All(id => id == AllureOfDarkness);
                if (_ownHandIds.Count(IsDarkMonsterId) == 0 && allureAlone && Rule(DeckRules.GambleWhenLosing) &&
                    TryActivateStaple(context, state, AllureOfDarkness, "draw 2 with no DARK to banish (it takes the hand, which is empty): we lose next turn otherwise", out response))
                    return response;
            }
            if (TryActivateStaple(context, state, ChargeOfTheLightBrigade, "mill 3 and search a Lightsworn", out response, DeckRules.ChargeOfTheLightBrigade)) return response;
            if (TryGetActivable(state, ReinforcementOfTheArmy, out _) &&
                await ReinforcementAllowedAsync(context) &&
                TryActivateStaple(context, state, ReinforcementOfTheArmy, "search a Warrior", out response, DeckRules.ReinforcementOfTheArmy)) return response;
            // Allure of Darkness: draw 2, then banish a DARK from the hand (the whole hand without one): only with a DARK we
            // can spare already in hand. Shared since 2026-11-10 (the Machina brain held it all game). A brain with its own
            // Allure rule (Blackwing) reaches it here first; its banish pick stays its own.
            int darks = _ownHandIds.Count(IsDarkMonsterId);
            if (darks > 0 && TryActivateStaple(context, state, AllureOfDarkness,
                    $"draw 2, then banish a spare DARK ({darks} in hand)", out response, DeckRules.AllureOfDarkness))
                return response;
            // No DARK to banish: it would take the whole hand. Held (a line that plays it pays for it).
            if (darks == 0 && TryGetActivable(state, AllureOfDarkness, out _))
                Hold(AllureOfDarkness, MainAction.Activate, DeckRules.AllureOfDarkness);
            return null;
        }

        /// <summary>
        /// Why Upstart Goblin waits for Main Phase 2: Main Phase 1 with a Battle Phase ahead, and our attack this turn (the
        /// board plus the best monster we can still Normal Summon) reaches their LP but not with Upstart's 1000 added. Null to use it.
        /// </summary>
        private async Task<string?> UpstartWaitsForLethalAsync(DecisionContext<IdleCmdData> context)
        {
            if ((DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !context.StateData.CanBattlePhase || context.Header.CurrentTurn <= 1)
                return null;
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            var defenders = BoardAssessment.Defenders(await GetEnemyMonsterCardsAsync(), OpponentMemory);
            var attacks = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).Select(EffectiveAttack).ToList();
            int summon = (context.StateData.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => card.Level <= 4)
                .Select(card => card.Attack).DefaultIfEmpty(0).Max();
            if (summon > 0)
                attacks.Add(summon);
            if (attacks.Count == 0 || BoardAssessment.TurnsToWin(attacks, defenders, oppLp) != 1)
                return null;
            return BoardAssessment.TurnsToWin(attacks, defenders, oppLp + 1000) == 1
                ? null
                : $"our attack this turn ({string.Join(" + ", attacks)}) reaches their {oppLp} LP, but not {oppLp + 1000}";
        }
    }
}
