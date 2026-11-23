using static BotTest.Game.AI.Bots.MasterDuel.Blackwing.BlackwingCardId;
using BotTest.Game.AI.Bots.MasterDuel.GladiatorBeasts;
using BotTest.Game.AI.Bots.MasterDuel.HeroFrog;
using BotTest.Game.AI.Bots.MasterDuel.Machina;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>Per-card rules. Shared keyword behavior is defined in TagRules.cs.</summary>
    internal static partial class CardRules
    {
        private const int Honest = 37742478;
        private const int Shrink = 55713623;
        private const int Unifrog = 56052205;
        private const int FlipFlopFrog = 81278754;
        private const int ThoughtRulerArchfiend = 70780151;

        internal static bool ThreatTriggersOnNormalSummon(int code) =>
            Search.SearchHost.Available && Search.SearchHost.Cards.Get(code)?.Text is { } text &&
            text.Contains("Normal Summoned", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("If this card", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("When this card", StringComparison.OrdinalIgnoreCase));

        internal static bool ReinforcementCanSearch(int type, long race, int level) =>
            (type & (int)CardType.Monster) != 0 && (race & (int)CardRace.Warrior) != 0 && level is > 0 and <= 4;

        internal static readonly int[] GrepherDiscards =
            { NecroGardna, PlaguespreaderZombie, BlackwingVayuTheEmblemOfHonor, BlackwingSiroccoTheDawn, BlackwingElphinTheRaven };

        internal static bool ReinforcementNeedsGardna(CardRuleRegistry.Context context) =>
            context.DarkGrepherAvailable && context.OwnDeck.Any(id => Search.SearchHost.Is(id, NecroGardna)) &&
            !context.OwnHand.Any(id => GrepherDiscards.Any(discard => Search.SearchHost.Is(id, discard)));

        internal static readonly int[] SwapFrogDeckTargets = { HeroFrogCardId.TreebornFrog, HeroFrogCardId.SwapFrog, HeroFrogCardId.DupeFrog, Unifrog, FlipFlopFrog, HeroFrogCardId.Substitoad };

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
            new(new[] { MachinaCardId.RykoLightswornHunter, RykoLightswornHunter }, TagRules.Tag.SetOnly),

            // Super-Nimble Mega Hamster
            // Card-specific exception: while there are no Rykos in the deck, disable the SetOnly tag.
            new(new[] { MachinaCardId.SuperNimbleMegaHamster }, TagRules.Tag.SetOnly,
                TagsToDisableWhen: context =>
                    !context.OwnDeck.Any(id => Search.SearchHost.Is(id, MachinaCardId.RykoLightswornHunter))
                        ? TagRules.Tag.SetOnly : TagRules.Tag.None),

            // Card Trooper: activate its summon effect only while at least five cards remain in our deck,
            // and choose three when its effect asks how many cards to send.
            new(new[] { CardTrooper }, TagRules.Tag.ActivateOnSummon,
                TagsToDisableWhen: context => context.OwnDeck.Count < 5
                    ? TagRules.Tag.ActivateOnSummon
                    : TagRules.Tag.None,
                SelectOptionIndex: (_, optionCount) => optionCount > 0 ? optionCount - 1 : null),

            // Armageddon Knight
            new(new[] { ArmageddonKnight }, TagRules.Tag.ActivateOnSummon),

            // Reinforcement of the Army
            new(new[] { ReinforcementOfTheArmy }, TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context => !context.NormalSummonUsed || context.ReinforcementHasSingleTarget ||
                    ReinforcementNeedsGardna(context) ? TagRules.Tag.DoNotActivate : TagRules.Tag.None),

            // Dark Grepher: activate only when a GoodDiscard card is available in our hand.
            new(new[] { DarkGrepher }, TagRules.Tag.ActivateOnSummon,
                TagsToDisableWhen: context => CardRuleRegistry.AnyCardWithTag(context.OwnHand, TagRules.Tag.GoodDiscard)
                    ? TagRules.Tag.None
                    : TagRules.Tag.ActivateOnSummon),

            // Machina Gearframe
            new(new[] { MachinaCardId.MachinaGearframe }, TagRules.Tag.ActivateOnSummon),

            // Elemental HERO Stratos
            new(new[] { HeroFrogCardId.ElementalHeroStratos }, TagRules.Tag.ActivateOnSummon),

            // Blackwing - Blizzard the Far North
            new(new[] { BlackwingBlizzardTheFarNorth }, TagRules.Tag.ActivateOnSummon),

            // Lyla's optional effect is declined only in her summon window.
            new(new[] { MachinaCardId.LylaLightswornSorceress }, TagRules.Tag.DeclineEffectOnSummon),
    
            // Skill Successor
            new(new[] { GladCardId.SkillSuccessor }, TagRules.Tag.StatChangeEffect | TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context =>  context.IsDoomed || context.InDamageStep
                    ? TagRules.Tag.DoNotActivate
                    : TagRules.Tag.None),
        
            // Honest
            new(new[] { Honest }, TagRules.Tag.StatChangeEffect), 

            // Blackwing - Kalut the Moon Shadow
            new(new[] { BlackwingKalutTheMoonShadow }, TagRules.Tag.StatChangeEffect), 

            // Shrink
            new(new[] { Shrink }, TagRules.Tag.StatChangeEffect), 

            // Swap Frog: activate its summon effect while any listed Frog target remains in our deck.
            new(new[] { HeroFrogCardId.SwapFrog }, TagRules.Tag.ActivateOnSummon,
                TagsToDisableWhen: context => context.OwnDeck.Any(id =>
                    SwapFrogDeckTargets.Any(targetId => Search.SearchHost.Is(id, targetId)))
                    ? TagRules.Tag.None
                    : TagRules.Tag.ActivateOnSummon,
                TargetPriority: (_, hint, card) => hint == DHint.TOGRAVE &&
                    card.CurrentCoordinates is { Controller: 0, Location: (byte)CardLocation.Deck }
                    ? Search.SearchHost.Is(card.ID, HeroFrogCardId.TreebornFrog) ? 2 : Search.SearchHost.Is(card.ID, HeroFrogCardId.Substitoad) ? 1 : 0
                    : 0),

            // Battle Fader
            new(new[] { HeroFrogCardId.BattleFader }, TagRules.Tag.DoNotSummon),

            // D.D. Crow
            new(new[] { MachinaCardId.DDCrow }, TagRules.Tag.DoNotSummon | TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context => CrowMayActivate(context)
                    ? TagRules.Tag.None
                    : TagRules.Tag.DoNotActivate,
                AllowedTargets: CrowTargets),

            // Gorz the Emissary of Darkness
            // Card-specific exceptions disable the complete DoNotSummon rule below 1500 of our LP,
            // or when no other available summon answers an opponent's threat.
            new(new[] { GorzTheEmissaryOfDarkness }, TagRules.Tag.DoNotSummon,
                TagsToDisableWhen: context => context.OwnLifePoints < 1500 || context.NoOtherAnswer
                    ? TagRules.Tag.DoNotSummon
                    : TagRules.Tag.None),

            // Jar of Greed and Legacy of Yata-Garasu
            new(new[] { JarOfGreed, LegacyOfYataGarasu }, TagRules.Tag.Bait | TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context => context.IsDoomed || context.DrawTrapException
                    ? TagRules.Tag.DoNotActivate
                    : TagRules.Tag.None),

            // Mystical Space Typhoon
            new(new[] { MysticalSpaceTyphoon }, TagRules.Tag.Bait),

            // Waboku
            new(new[] { GladCardId.Waboku }, TagRules.Tag.ProtectsMonsters | TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context => context.IsDoomed || context.InDamageStep && context.GladiatorWouldBeDestroyed
                    ? TagRules.Tag.DoNotActivate
                    : TagRules.Tag.None),

            // Blackwing - Sirocco the Dawn
            new(new[] { BlackwingSiroccoTheDawn }, TagRules.Tag.GoodDiscard | TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context => context.OwnMonsterIds?.Any(id =>
                    Search.SearchHost.Is(id, BlackwingVayuTheEmblemOfHonor) ||
                    Search.SearchHost.Is(id, BlackwingBlizzardTheFarNorth) ||
                    Search.SearchHost.Is(id, BlackwingBoraTheSpear) ||
                    Search.SearchHost.Is(id, BlackwingShuraTheBlueFlame) ||
                    Search.SearchHost.Is(id, BlackwingGaleTheWhirlwind) ||
                    Search.SearchHost.Is(id, BlackwingKalutTheMoonShadow) ||
                    Search.SearchHost.Is(id, BlackwingArmedWing) ||
                    Search.SearchHost.Is(id, BlackwingArmorMaster) ||
                    Search.SearchHost.Is(id, BlackwingSilverwindTheAscendant)) == true
                    ? TagRules.Tag.DoNotActivate
                    : TagRules.Tag.None),

            // Blackwing - Vayu the Emblem of Honor
            new(new[] { BlackwingVayuTheEmblemOfHonor }, TagRules.Tag.GoodDiscard),

            // Machina Fortress
            new(new[] { MachinaCardId.MachinaFortress }, TagRules.Tag.GoodDiscard | TagRules.Tag.AnswersAnyThreat,
                TagsToDisableWhen: context => !context.ThreatAttackPosition
                    ? TagRules.Tag.AnswersAnyThreat
                    : TagRules.Tag.None),

            // Necro Gardna
            new(new[] { NecroGardna }, TagRules.Tag.GoodDiscard | TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context => context.OpponentIsAttacking || context.IsDoomed
                    ? TagRules.Tag.DoNotActivate
                    : TagRules.Tag.None),

            // The engine only offers Returner's graveyard trigger and legal Jinzo targets.
            new(new[] { MachinaCardId.JinzoReturner }, TagRules.Tag.ActivateAtFirstOpportunity),

            // Plaguespreader Zombie: revive for a Synchro that answers a threat or goes for game,
            // or when the board can already leave the opponent below 400 LP.
            new(new[] { PlaguespreaderZombie }, TagRules.Tag.GoodDiscard | TagRules.Tag.DoNotActivate,
                TagsToDisableWhen: context => context.PlaguespreaderMayRevive
                    ? TagRules.Tag.DoNotActivate
                    : TagRules.Tag.None),

            // Dark Armed Dragon
            new(new[] { DarkArmedDragon }, TagRules.Tag.AnswersAnyThreat | TagRules.Tag.ActivateOnSummon,
                TargetPriority: (summonWindow, hint, card) => summonWindow && hint == DHint.DESTROY &&
                    (card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone ||
                     card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone &&
                     (card.Position & ((byte)CardPosition.FaceUpAttack | (byte)CardPosition.FaceUpDefence)) != 0) ? 1 : 0),

            // Spiritual Water Art - Aoi
            new(new[] { HeroFrogCardId.SpiritualWaterArtAoi }, TagRules.Tag.DoNotSet | TagRules.Tag.ActivateAtFirstOpportunity,
                TagsToDisableWhen: context => context.OwnMonsterIds?.Any(id =>
                    (Search.SearchHost.Attribute(id) & 0x2) != 0) == true
                    ? TagRules.Tag.DoNotSet
                    : TagRules.Tag.None),

            // Chaos Sorcerer
            new(new[] { ChaosSorcerer }, TagRules.Tag.AnswersAnyThreat),

            // Dimensional Prison
            // This tag does not apply to Thought Ruler Archfiend.
            new(new[] { DimensionalPrison }, TagRules.Tag.AnswersAnyThreat | TagRules.Tag.ProtectsMonsters,
                TagsToDisableWhen: context => Search.SearchHost.Is(context.ThreatCardId, ThoughtRulerArchfiend)
                    ? TagRules.Tag.AnswersAnyThreat
                    : TagRules.Tag.None),

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
            new(new[] { HeroFrogCardId.CompulsoryEvacuationDevice }, TagRules.Tag.AnswersAnyThreat | TagRules.Tag.ProtectsMonsters,
                TagsToDisableWhen: context => context.ThreatTriggersWhenNormalSummoned
                    ? TagRules.Tag.AnswersAnyThreat
                    : TagRules.Tag.None),

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
                    context.StrategicState == TagRules.BotStrategicState.Desperate &&
                    context.OwnHand.Count == 1 && context.OwnHand.Any(id => Search.SearchHost.Is(id, AllureOfDarkness))
                    ? TagRules.Tag.None
                    : TagRules.Tag.ActivateAtFirstOpportunity),

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
        };

    }
}
