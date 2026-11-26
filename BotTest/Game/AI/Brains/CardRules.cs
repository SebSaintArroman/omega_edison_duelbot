using BotTest.Game.AI.Bots.MasterDuel.GladiatorBeasts;
using BotTest.Game.AI.Bots.MasterDuel.HeroFrog;
using BotTest.Game.AI.Bots.MasterDuel.Machina;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>Per-card rules. Shared keyword behavior is defined in TagRules.cs.</summary>
    internal static partial class CardRules
    {

        internal static bool ThreatTriggersOnNormalSummon(int code) =>
            Search.SearchHost.Available && Search.SearchHost.Cards.Get(code)?.Text is { } text &&
            text.Contains("Normal Summoned", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("If this card", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("When this card", StringComparison.OrdinalIgnoreCase));

        internal static bool ReinforcementCanSearch(int type, long race, int level) =>
            (type & (int)CardType.Monster) != 0 && (race & (int)CardRace.Warrior) != 0 && level is > 0 and <= 4;

        /// <summary>A DARK monster with the GoodDiscard tag: what Dark Grepher's effect can discard and is happy to.</summary>
        internal static bool IsGoodDarkDiscard(int id) =>
            CardRuleRegistry.AnyCardWithTag(new[] { id }, TagRules.Tag.GoodDiscard) &&
            (Search.SearchHost.Type(id) & 0x1) != 0 && (Search.SearchHost.Attribute(id) & 0x20) != 0;

        internal static bool ReinforcementNeedsGardna(CardRuleRegistry.Context context) =>
            context.DarkGrepherAvailable && context.OwnDeck.Any(id => Search.SearchHost.Is(id, NecroGardna)) &&
            !context.OwnHand.Any(IsGoodDarkDiscard);

        internal static readonly int[] SwapFrogDeckTargets = { CardId.TreebornFrog, CardId.SwapFrog, CardId.DupeFrog, CardId.Unifrog, CardId.FlipFlopFrog, CardId.Substitoad };

        internal static bool IsGladiatorBeast(int id) =>
            (Search.SearchHost.Type(id) & 0x1) != 0 &&
            Search.SearchHost.Cards.Get(id)?.SetCodes().Any(code => (code & 0x0fff) == 0x19) == true;

        internal static bool GladiatorWouldBeDestroyed(DynamicCard own, DynamicCard opponent)
        {
            if (!IsGladiatorBeast(own.ID) || (opponent.Position & (byte)CardPosition.FaceUpAttack) == 0)
                return false;
            return (own.Position & (byte)CardPosition.FaceUpAttack) != 0
                ? opponent.Attack >= own.Attack
                : opponent.Attack > own.Defense;
        }

        internal static readonly CardRuleRegistry.Rule[] Definitions =
        {
            // Ryko, Lightsworn Hunter
            new(new[] { CardId.RykoLightswornHunter, RykoLightswornHunterPreErrata }, TagRules.Tag.SetOnly),

            // Super-Nimble Mega Hamster
            // Card-specific exception: while there are no Rykos in the deck, disable the SetOnly tag.
            new(new[] { CardId.SuperNimbleMegaHamster }, TagRules.Tag.SetOnly,
                TagsToDisableWhen: context =>
                    !context.OwnDeck.Any(id => Search.SearchHost.Is(id, CardId.RykoLightswornHunter))
                        ? TagRules.Tag.SetOnly : TagRules.Tag.None),

            // Card Trooper: activate its summon effect only while at least five cards remain in our deck,
            // and choose three when its effect asks how many cards to send.
            new(new[] { CardTrooper }, TagRules.Tag.ActivateOnSummon,
                TagsToDisableWhen: context => context.OwnDeck.Count < 5
                    ? TagRules.Tag.ActivateOnSummon
                    : TagRules.Tag.None,
                SelectOptionIndex: (_, optionCount) => optionCount > 0 ? optionCount - 1 : null),

            // Armageddon Knight
            new(new[] { ArmageddonKnight }, TagRules.Tag.ActivateOnSummon | TagRules.Tag.DoNotSet,
                SendGoodDiscardFromDeck: true),

            // Reinforcement of the Army
            // Its search takes Necro Gardna when Dark Grepher needs a discard and the hand has none (ReinforcementNeedsGardna).
            new(new[] { ReinforcementOfTheArmy }, TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context => !context.NormalSummonUsed || context.ReinforcementHasSingleTarget ||
                    ReinforcementNeedsGardna(context) ? TagRules.Tag.DoNotActivate : TagRules.Tag.None,
                ForcedSearchPick: (context, offered) =>
                    offered.Any(id => Search.SearchHost.Is(id, NecroGardna)) && ReinforcementNeedsGardna(context) ? NecroGardna : null),

            // Dark Grepher: activate only when a DARK GoodDiscard card is available in our hand.
            // While it's on our field, its discard takes a GoodDiscard card when one is offered (only DARKs are offered).
            new(new[] { DarkGrepher }, TagRules.Tag.ActivateOnSummon | TagRules.Tag.DoNotSet,
                TagsToDisableWhen: context => context.OwnHand.Any(IsGoodDarkDiscard)
                    ? TagRules.Tag.None
                    : TagRules.Tag.ActivateOnSummon,
                DiscardPickTag: TagRules.Tag.GoodDiscard,
                SendGoodDiscardFromDeck: true),

            // Machina Gearframe
            new(new[] { CardId.MachinaGearframe }, TagRules.Tag.ActivateOnSummon | TagRules.Tag.NeverSet),

            // Elemental HERO Stratos
            new(new[] { CardId.ElementalHEROStratos }, TagRules.Tag.ActivateOnSummon),

            // Blackwing - Blizzard the Far North
            new(new[] { BlackwingBlizzardTheFarNorth }, TagRules.Tag.ActivateOnSummon),

            // Lyla's optional effect is declined only in her summon window.
            new(new[] { CardId.LylaLightswornSorceress, CardId.LylaLightswornSorceressPreErrata }, TagRules.Tag.DeclineEffectOnSummon),
    
            // Versago the Destroyer
            // Its fusion-substitute effect works from the GY too: 0.5 there (user, 2026-11-26: "versago's effect score should carry on applying
            // in the graveyard (it's not huge, but it should have one, ~0.5)").
            new(new[] { CardId.VersagoTheDestroyer }, TagRules.Tag.None, Correction: new(Grave: 0.5)),

            // Skill Successor
            // AlwaysSet while a Gladiator Beast of ours is face-up in attack position (user, 2026-11-26: set this turn, it's live for the
            // next; the lines end before it pays, so ending the turn with it in hand looked as good).
            new(new[] { CardId.SkillSuccessor }, TagRules.Tag.StatChangeEffect | TagRules.Tag.DoNotActivate | TagRules.Tag.AlwaysSet,
                TagsToDisableWhen: context =>
                    (context.IsDoomed || context.InDamageStep ? TagRules.Tag.DoNotActivate : TagRules.Tag.None) |
                    (context.OwnFaceUpAttackers?.Any(monster => IsGladiatorBeast(monster.Id)) == true ? TagRules.Tag.None : TagRules.Tag.AlwaysSet)),

            // Gladiator Beast War Chariot
            // AlwaysSet while a Gladiator Beast of ours is face-up, either position (its negate needs one; user, 2026-11-26).
            new(new[] { CardId.GladiatorBeastWarChariot }, TagRules.Tag.AlwaysSet,
                TagsToDisableWhen: context =>
                    context.OwnFaceUpMonsterIds?.Any(IsGladiatorBeast) == true ? TagRules.Tag.None : TagRules.Tag.AlwaysSet),
        
            // Honest
            new(new[] { CardId.Honest }, TagRules.Tag.StatChangeEffect), 

            // Blackwing - Kalut the Moon Shadow
            new(new[] { BlackwingKalutTheMoonShadow }, TagRules.Tag.StatChangeEffect), 

            // Shrink
            new(new[] { CardId.Shrink }, TagRules.Tag.StatChangeEffect), 

            // Swap Frog: activate its summon effect while any listed Frog target remains in our deck.
            new(new[] { CardId.SwapFrog }, TagRules.Tag.ActivateOnSummon,
                TagsToDisableWhen: context => context.OwnDeck.Any(id =>
                    SwapFrogDeckTargets.Any(targetId => Search.SearchHost.Is(id, targetId)))
                    ? TagRules.Tag.None
                    : TagRules.Tag.ActivateOnSummon,
                TargetPriority: (_, hint, card) => hint == DHint.TOGRAVE &&
                    card.CurrentCoordinates is { Controller: 0, Location: (byte)CardLocation.Deck }
                    ? Search.SearchHost.Is(card.ID, CardId.TreebornFrog) ? 2 : Search.SearchHost.Is(card.ID, CardId.Substitoad) ? 1 : 0
                    : 0),

            // Battle Fader
            new(new[] { CardId.BattleFader }, TagRules.Tag.DoNotSummon),

            // D.D. Crow: DoNotActivate is lifted only while one of its reasons holds (CrowMayActivate). TagsToDisableWhen returns the
            // tags to REMOVE: until 2026-11-24 this was inverted, so Crow was free with no reason and blocked when it had one.
            new(new[] { CardId.DDCrow }, TagRules.Tag.DoNotSummon | TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context => CrowMayActivate(context)
                    ? TagRules.Tag.DoNotActivate
                    : TagRules.Tag.None,
                AllowedTargets: CrowTargets),

            // Gorz the Emissary of Darkness
            // Card-specific exceptions disable the complete DoNotSummon rule below 1500 of our LP,
            // or when no other available summon answers an opponent's threat.
            // Correction: in hand it answers a direct attack with Gorz plus a token, far more than its text scores (user,
            // 2026-11-13: "id have defo taken gorz" over Freed with Trap Dustshoot; "Gorz should be a hand-only bonus").
            new(new[] { GorzTheEmissaryOfDarkness }, TagRules.Tag.DoNotSummon,
                TagsToDisableWhen: context => context.OwnLifePoints < 1500 || context.NoOtherAnswer
                    ? TagRules.Tag.DoNotSummon
                    : TagRules.Tag.None,
                Correction: new(Hand: 3)),

            // Neo-Spacian Grand Mole
            // Correction: returns what battles it (and itself), stalling attacks and bouncing synchros; replaces the old
            // removal-only bouncer bonus (user, 2026-11-13: "mole should be a general bonus").
            new(new[] { NeoSpacianGrandMole }, TagRules.Tag.None, Correction: new(General: 4)),

            // Tytannial, Princess of Camellias
            // Correction: in hand only, a large negative one: a Level 8 that needs two tributes is a weak card to hold, so Trap Dustshoot
            // doesn't return it (user, 2026-11-25: "give tytannial a large negative modifier when in hand only, so that it doesn't get
            // returned in trap dustshoot picks"). Its revealed-hand score was 10.1 against 7.7 for the next card (Debris Dragon).
            // Not while its holder can Tribute Summon it (normal summon available, two monsters to tribute): then it keeps its full value
            // (user, 2026-11-25).
            new(new[] { TytannialPrincessOfCamellias }, TagRules.Tag.None, Correction: new(Hand: -8, HandUnlessTributable: true)),

            // Archlord Kristya
            // Correction: stops every Special Summon (ours included) on top of a 2800 body (user, 2026-11-13: "increase the
            // threat rating of archlord krystia by something huge - maybe 5"; Solemn should have stopped it on its summon).
            // Threat: always a threat, whatever its ATK against ours (user, 2026-11-24).
            new(new[] { ArchlordKristya }, TagRules.Tag.Threat, Correction: new(General: 5)),

            // Jar of Greed and Legacy of Yata-Garasu
            // About to be removed anyway: DoNotActivate lifts, and the doomed-card logic uses it.
            // AlwaysSet: every copy in hand is set in the set window (user, 2026-11-24: "jar and yata should have always set tag";
            // seen live: the set plan set one Jar of Greed and ended the turn with the other in hand).
            new(new[] { JarOfGreed, LegacyOfYataGarasu }, TagRules.Tag.Bait | TagRules.Tag.DoNotActivate | TagRules.Tag.AlwaysSet,
                TagsToDisableWhen: context => context.IsDoomed || context.DrawTrapException
                    ? TagRules.Tag.DoNotActivate
                    : TagRules.Tag.None),

            // Mystical Space Typhoon
            // Not activated with no spell/trap of theirs worth removing: it could only destroy our own card (DuelSim 2026-11-24: lines
            // fired it at our own set cards, so setting looked worse than ending the turn; once for real, on our Torrential mid-battle).
            // It never targets our own cards, even when it's about to be removed anyway (user, 2026-11-24); the one exception is our own
            // Future Fusion in the Draw Phase (Machina Jinzo). Also lifted when the caller can't count their spells/traps (-1).
            new(new[] { MysticalSpaceTyphoon }, TagRules.Tag.Bait | TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context => context.OpponentRemovableSpellTraps != 0 || context.OwnFutureFusionInDrawPhase
                    ? TagRules.Tag.DoNotActivate
                    : TagRules.Tag.None),

            // Waboku
            new(new[] { CardId.Waboku }, TagRules.Tag.ProtectsMonsters | TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context => context.IsDoomed || context.InDamageStep && context.GladiatorWouldBeDestroyed
                    ? TagRules.Tag.DoNotActivate
                    : TagRules.Tag.None),

            // Blackwing - Sirocco the Dawn
            // Never without another Blackwing of ours on the field. Never onto their empty field, unless the boost makes one lethal attack;
            // a one-attack lethal (CardRuleHelpers/SiroccoHelper.cs: no set cards, no Necro Gardna / Honest / Kalut, through or piercing a
            // monster) always, right before the Battle Phase (user, 2026-11-25: "don't use sirocco onto an empty board unless it gives you a
            // lethal attack in which case always do"; "only do this right before battle, in the atk position change window").
            // Its target: our Bora when the boost is that lethal (user, 2026-11-25).
            new(new[] { BlackwingSiroccoTheDawn }, TagRules.Tag.GoodDiscard | TagRules.Tag.DoNotActivate | TagRules.Tag.ActivateBeforeBattle,
                TagsToDisableWhen: SiroccoTagsToDisable, AllowedTargets: SiroccoTargets),

            // Blackwing - Vayu the Emblem of Honor
            new(new[] { BlackwingVayuTheEmblemOfHonor }, TagRules.Tag.GoodDiscard),

            // Blackwing - Elphin the Raven
            new(new[] { BlackwingElphinTheRaven }, TagRules.Tag.GoodDiscard),

            // Machina Fortress
            // Threat: always a threat on their side, whatever its ATK against ours (user, 2026-11-24).
            new(new[] { CardId.MachinaFortress }, TagRules.Tag.GoodDiscard | TagRules.Tag.AnswersAnyThreat | TagRules.Tag.NeverSummonInDefence | TagRules.Tag.Threat,
                TagsToDisableWhen: context => !context.ThreatAttackPosition
                    ? TagRules.Tag.AnswersAnyThreat
                    : TagRules.Tag.None),

            // Necro Gardna
            new(new[] { NecroGardna }, TagRules.Tag.GoodDiscard | TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context => context.OpponentIsAttacking || context.IsDoomed
                    ? TagRules.Tag.DoNotActivate
                    : TagRules.Tag.None),

            // The engine only offers Returner's graveyard trigger and legal Jinzo targets.
            new(new[] { CardId.JinzoReturner }, TagRules.Tag.ActivateAtFirstOpportunity),

            // Plaguespreader Zombie: revive for a Synchro that answers a threat or goes for game,
            // or when the board can already leave the opponent below 400 LP.
            new(new[] { PlaguespreaderZombie }, TagRules.Tag.GoodDiscard | TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context => context.PlaguespreaderMayRevive
                    ? TagRules.Tag.DoNotActivate
                    : TagRules.Tag.None),

            // Dark Armed Dragon
            // Its effect never while Stardust Dragon is on their field (it negates the destroy): no on-summon activation either. Still
            // summoned, it can attack Stardust (user, 2026-11-25: "it shouldn't use its effect at all while stardust is on the field
            // (that's not to say don't summon it though - it can attack stardust). add this all as hard card rules").
            new(new[] { DarkArmedDragon }, TagRules.Tag.AnswersAnyThreat | TagRules.Tag.ActivateOnSummon | TagRules.Tag.NeverSummonInDefence | TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context => context.StardustUp ? TagRules.Tag.ActivateOnSummon : TagRules.Tag.DoNotActivate,
                TargetPriority: (summonWindow, hint, card) => summonWindow && hint == DHint.DESTROY &&
                    (card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone ||
                     card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone &&
                     (card.Position & ((byte)CardPosition.FaceUpAttack | (byte)CardPosition.FaceUpDefence)) != 0) ? 1 : 0),

            // Spiritual Water Art - Aoi
            new(new[] { CardId.SpiritualWaterArtAoi }, TagRules.Tag.DoNotSet | TagRules.Tag.ActivateAtFirstOpportunity,
                TagsToDisableWhen: context => context.OwnMonsterIds?.Any(id =>
                    (Search.SearchHost.Attribute(id) & 0x2) != 0) == true
                    ? TagRules.Tag.DoNotSet
                    : TagRules.Tag.None),

            // Chaos Sorcerer
            new(new[] { ChaosSorcerer }, TagRules.Tag.AnswersAnyThreat),

            // Icarus Attack
            // Never while Stardust Dragon is on their field: it negates the destroy, and the tribute is lost with it (user, 2026-11-25:
            // Icarus went off into Stardust, which negated it, Vayu tributed for nothing).
            new(new[] { IcarusAttack }, TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context => context.StardustUp ? TagRules.Tag.None : TagRules.Tag.DoNotActivate),

            // Dimensional Prison
            // This tag does not apply to Thought Ruler Archfiend.
            // SaveForBiggerAttacker: with fewer answers than attackers and their attacks together not lethal, a direct hit
            // doesn't force it out: it's weighed against the attackers still to come (user, 2026-11-24: D-Prison went on
            // Gardestrike's 2100 with Faultroll and Stardust behind it, 7000 total into our 7400 LP).
            new(new[] { DimensionalPrison }, TagRules.Tag.AnswersAnyThreat | TagRules.Tag.ProtectsMonsters | TagRules.Tag.SaveForBiggerAttacker,
                TagsToDisableWhen: context =>
                    (Search.SearchHost.Is(context.ThreatCardId, CardId.ThoughtRulerArchfiend) ? TagRules.Tag.AnswersAnyThreat : TagRules.Tag.None) |
                    (context.AttackAnswersScarce && context.NonLethalDirectAttack ? TagRules.Tag.None : TagRules.Tag.SaveForBiggerAttacker)),

            // Bottomless Trap Hole
            // It cannot answer a threat that was already on the field when Bottomless was set, or while Stardust is present.
            new(new[] { BottomlessTrapHole }, TagRules.Tag.AnswersAnyThreat | TagRules.Tag.ProtectsMonsters,
                TagsToDisableWhen: context => context.ThreatWasAlreadyOnFieldWhenTrapWasSet || context.OpponentHasStardustDragon
                    ? TagRules.Tag.AnswersAnyThreat
                    : TagRules.Tag.None),

            // Mirror Force
            // It does not answer threats while Stardust Dragon is present.
            new(new[] { MirrorForce }, TagRules.Tag.AnswersAnyThreat | TagRules.Tag.ProtectsMonsters,
                TagsToDisableWhen: context => context.OpponentHasStardustDragon
                    ? TagRules.Tag.AnswersAnyThreat
                    : TagRules.Tag.None),

            // Torrential Tribute
            // It does not answer threats while Stardust Dragon is present.
            new(new[] { TorrentialTribute }, TagRules.Tag.AnswersAnyThreat,
                TagsToDisableWhen: context => context.OpponentHasStardustDragon
                    ? TagRules.Tag.AnswersAnyThreat
                    : TagRules.Tag.None),

            // Compulsory Evacuation Device
            // It does not answer a monster whose effect triggers on Normal Summon.
            // SaveForBiggerAttacker: as Dimensional Prison (user, 2026-11-24).
            new(new[] { CardId.CompulsoryEvacuationDevice }, TagRules.Tag.AnswersAnyThreat | TagRules.Tag.ProtectsMonsters | TagRules.Tag.SaveForBiggerAttacker,
                TagsToDisableWhen: context =>
                    (context.ThreatTriggersWhenNormalSummoned ? TagRules.Tag.AnswersAnyThreat : TagRules.Tag.None) |
                    (context.AttackAnswersScarce && context.NonLethalDirectAttack ? TagRules.Tag.None : TagRules.Tag.SaveForBiggerAttacker)),

            // Trap Dustshoot
            // Its hand-count condition disables the opposite set rule at each threshold.
            new(new[] { TrapDustshoot }, TagRules.Tag.DoNotSet | TagRules.Tag.AlwaysSet | TagRules.Tag.ActivateAtFirstOpportunity,
                TagsToDisableWhen: context => context.OpponentHandCount >= 3
                    ? TagRules.Tag.DoNotSet
                    : TagRules.Tag.AlwaysSet),

            // Allure of Darkness
            // Card-specific exception: with no DARK monster in hand, turn off that tag.
            new(new[] { AllureOfDarkness }, TagRules.Tag.ActivateAtFirstOpportunity,
                TagsToDisableWhen: context => context.OwnHand.Any(id =>
                    (Search.SearchHost.Type(id) & 0x1) != 0 && (Search.SearchHost.Attribute(id) & 0x20) != 0) ||
                    DeckBotBrain.AllureAsLastCard &&
                    context.OwnHand.Count == 1 && context.OwnHand.Any(id => Search.SearchHost.Is(id, AllureOfDarkness))
                    ? TagRules.Tag.None
                    : TagRules.Tag.ActivateAtFirstOpportunity),

            // Pot of Greed: activated at the first chance, always (a fixed rule, user 2026-11-12).
            new(new[] { PotOfGreed }, TagRules.Tag.ActivateAtFirstOpportunity),

            // Upstart Goblin: leave it available when the existing board clock says lethal is possible this turn.
            new(new[] { UpstartGoblin }, TagRules.Tag.ActivateAtFirstOpportunity,
                TagsToDisableWhen: context => context.CanWinThisTurn
                    ? TagRules.Tag.ActivateAtFirstOpportunity
                    : TagRules.Tag.None),
        };

        // Rules shared by categories of cards. Keep these after the individual card definitions.
        internal static readonly CardRuleRegistry.CategoryRule[] Categories =
        {
            // Non-Quick-Play Spells cannot be set.
            new(id => (Search.SearchHost.Type(id) & 0x2) != 0 && (Search.SearchHost.Type(id) & 0x10000) == 0,
                TagRules.Tag.NonQuickPlaySpell),

            // Level 5+ monsters do not use Tribute Sets.
            new(id => Search.SearchHost.Available && Search.SearchHost.Cards.Get(id) is { Level: >= 5 } card &&
                (card.Type & (int)CardType.Monster) != 0, TagRules.Tag.NeverSet),

            // Gladiator Beasts aren't set (they want to battle and tag out), except the one a routine sets, or with Waboku in
            // hand to keep a set one alive.
            new(IsGladiatorBeast, TagRules.Tag.DoNotSet,
                TagsToDisableWhen: (id, context) =>
                    context.PlannedSetCardId > 0 && Search.SearchHost.Is(id, context.PlannedSetCardId) ||
                    context.OwnHand.Any(code => Search.SearchHost.Is(code, CardId.Waboku))
                        ? TagRules.Tag.DoNotSet : TagRules.Tag.None),
        };

    }
}
