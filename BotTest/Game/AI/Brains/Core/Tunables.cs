namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Numbers of the shared brain's rules that used to be written inline (user, 2026-11-16: "ALL of these moved to one location that I
    /// can manually tweak"). Each is listed in bot-tuning.txt under DeckBotBrain; the values here are the built-in ones.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // ------------------------------------------------------------------ battle and damage

        [Tunable("A crash's damage counts in full when it would leave us under this many LP; otherwise at CrashSafeDamageShare.")]
        public static readonly Tuned<int> CrashDangerLp = new(1500);
        [Tunable("The share of a crash's damage that counts while we stay above CrashDangerLp.")]
        public static readonly Tuned<double> CrashSafeDamageShare = new(0.25);
        [Tunable("Their direct hit that would finish us: its cost (rough card points) when weighing an answer to it.")]
        public static readonly Tuned<double> LethalHitCost = new(100);
        [Tunable("Their direct hit that leaves us within reach of their board's next attack: its cost (rough card points).")]
        public static readonly Tuned<double> NearLethalHitCost = new(2);
        [Tunable("Any other direct hit of theirs costs its ATK / this (rough card points).")]
        public static readonly Tuned<double> DirectHitScale = new(2000);
        [Tunable("Their attacker that survives and then destroys one of our spells/traps (Lyla): that extra loss (rough card points).")]
        public static readonly Tuned<double> SurvivorSpellKillCost = new(2.5);

        // ------------------------------------------------------------------ card values in rough card points (hand, answers)

        [Tunable("A monster of ours in rough card points: this + ATK/1000 (+ ExtraDeckMonsterValue for a Synchro/Fusion/Xyz).")]
        public static readonly Tuned<double> CardValueBase = new(2);
        [Tunable("An Extra Deck monster's extra worth over its ATK, in rough card points.")]
        public static readonly Tuned<double> ExtraDeckMonsterValue = new(1.5);
        [Tunable("A spell/trap in hand, in rough card points.")]
        public static readonly Tuned<double> SpellTrapCardValue = new(2.5);
        [Tunable("Gorz in hand, in rough card points.")]
        public static readonly Tuned<double> GorzHandValue = new(4);
        [Tunable("A card that likes being set (graveyard effect, expendable, flip) counts this much less when choosing what to set with the last Normal Summon.")]
        public static readonly Tuned<double> SetterHandDiscount = new(0.5);

        // ------------------------------------------------------------------ Gorz

        [Tunable("Gorz in their hand: its chance shrinks to this share on a Quick clock (they've had to use it already).")]
        public static readonly Tuned<double> GorzQuickClockFactor = new(0.5);
        [Tunable("Gorz in their hand: its chance shrinks to this share on a Long clock.")]
        public static readonly Tuned<double> GorzLongClockFactor = new(0.8);
        [Tunable("Gorz in their hand: its chance shrinks to this share when we can answer Gorz.")]
        public static readonly Tuned<double> GorzAnswerableFactor = new(0); // user, 2026-11-16: no Gorz fear when we can deal with Gorz (was 0.25)

        // ------------------------------------------------------------------ risk

        [Tunable("Ryko play-around: a face-down is treated as Ryko when its Ryko chance is at least RykoPlayAroundChance x (this + risk tolerance).")]
        public static readonly Tuned<double> RykoRiskOffset = new(0.5);
        [Tunable("Risk tolerance never goes below this.")]
        public static readonly Tuned<double> RiskMin = new(0.05);
        [Tunable("A risk-scaled chance: chance x (this + RiskScaledSlope x risk tolerance).")]
        public static readonly Tuned<double> RiskScaledBase = new(0.6);
        [Tunable("A risk-scaled chance: chance x (RiskScaledBase + this x risk tolerance).")]
        public static readonly Tuned<double> RiskScaledSlope = new(0.8);
        [Tunable("Hold-back chances (a push into their set cards, a summon into Ryko, Heavy Storm exposure) scale with (this - risk tolerance).")]
        public static readonly Tuned<double> RiskHoldCeiling = new(1.5);
        [Tunable("Sheltering from Mirror Force: the threshold is ShelterMirrorChance x (this + risk tolerance).")]
        public static readonly Tuned<double> ShelterRiskOffset = new(0.5);
        [Tunable("Their chance of lethal on us from which we treat it as likely (racing, holding back).")]
        public static readonly Tuned<double> LikelyLethalChance = new(0.5);
        [Tunable("A push that shortens our clock by this many turns counts as a certain push (shorter: a share of it).")]
        public static readonly Tuned<double> PushClockTurnsForCertain = new(4);

        // ------------------------------------------------------------------ tributes (the order of what we give up first)

        [Tunable("Tribute order: a monster costing at most this counts as expendable (fine to tribute or spend).")]
        public static readonly Tuned<double> ExpendableTributeCost = new(1.5);
        [Tunable("Tribute cost of Treeborn Frog (the most expendable: it comes back).")]
        public static readonly Tuned<double> TributeTreeborn = new(0.1);
        [Tunable("Tribute cost of a Lyla that's used her effect.")]
        public static readonly Tuned<double> TributeLylaSpent = new(1.3);
        [Tunable("Tribute cost of an expendable monster with a graveyard effect (it wants to be there).")]
        public static readonly Tuned<double> TributeGraveEffect = new(1.0);
        [Tunable("Tribute cost of any other expendable monster: this + ATK/10000.")]
        public static readonly Tuned<double> TributeExpendable = new(1.2);
        [Tunable("Tokens under this ATK are nearly free to tribute (TributeTokenSmall); from it, TributeTokenBig and up by ATK.")]
        public static readonly Tuned<int> TributeTokenFrom = new(1500);
        [Tunable("Tribute cost of a small token: this + ATK/10000.")]
        public static readonly Tuned<double> TributeTokenSmall = new(1.5);
        [Tunable("Tribute cost of a token at TributeTokenFrom ATK or more: this + (ATK - TributeTokenFrom)/1000.")]
        public static readonly Tuned<double> TributeTokenBig = new(1.65);
        [Tunable("Tribute cost of everything else: this + ATK/1000.")]
        public static readonly Tuned<double> TributeOther = new(3);

        // ------------------------------------------------------------------ Allure of Darkness: what to banish (low first)

        [Tunable("Allure banish rank (low = banished first): Gorz while we have a monster out.")]
        public static readonly Tuned<double> AllureRankGorzWithMonsters = new(0.5);
        [Tunable("Allure banish rank: Gorz with our field empty.")]
        public static readonly Tuned<double> AllureRankGorz = new(4);
        [Tunable("Allure banish rank: a Dark Armed Dragon that can't be made live.")]
        public static readonly Tuned<double> AllureRankDadStuck = new(0.5);
        [Tunable("Allure banish rank: a Dark Armed Dragon within a DARK of live.")]
        public static readonly Tuned<double> AllureRankDadNearLive = new(5.5);
        [Tunable("Allure banish rank: a Dark Armed Dragon with one DARK in the graveyard.")]
        public static readonly Tuned<double> AllureRankDadOneDark = new(3);
        [Tunable("Allure banish rank: any other Dark Armed Dragon.")]
        public static readonly Tuned<double> AllureRankDadOther = new(1.2);
        [Tunable("Allure banish rank: Kalut with a Blackwing to boost.")]
        public static readonly Tuned<double> AllureRankKalutUseful = new(4);
        [Tunable("Allure banish rank: Kalut with nothing to boost.")]
        public static readonly Tuned<double> AllureRankKalutIdle = new(1);
        [Tunable("Allure banish rank: Blizzard with a Level 4 or lower Blackwing in the graveyard.")]
        public static readonly Tuned<double> AllureRankBlizzardUseful = new(4.5);
        [Tunable("Allure banish rank: Blizzard with nothing to revive.")]
        public static readonly Tuned<double> AllureRankBlizzardIdle = new(1.5);
        [Tunable("Allure banish rank: Vayu.")]
        public static readonly Tuned<double> AllureRankVayu = new(3.5);
        [Tunable("Allure banish rank: anything else (this - ATK/10000).")]
        public static readonly Tuned<double> AllureRankOther = new(3);

        // ------------------------------------------------------------------ staples

        [Tunable("Lyla gamble: its chance drops by this share per strong summon we have instead.")]
        public static readonly Tuned<double> LylaGambleDropPerStrongSummon = new(0.5);
        [Tunable("Solemn on a summon: the gain of negating a monster (cards).")]
        public static readonly Tuned<double> SolemnSummonGain = new(1);
        [Tunable("Solemn on a summon: the extra gain when it's a tribute summon (cards).")]
        public static readonly Tuned<double> SolemnTributeBonus = new(0.5);
        [Tunable("Solemn on a summon: the extra gain when its effect would take a card of ours (cards).")]
        public static readonly Tuned<double> SolemnSavedCardGain = new(1);
        [Tunable("Solemn on a summon: negate when gain minus cost is at least this (cards).")]
        public static readonly Tuned<double> SolemnMinNetGain = new(0.5);
        [Tunable("Their Heavy Storm chance below which it's treated as gone (one more set allowed).")]
        public static readonly Tuned<double> StormGoneChance = new(0.05);
        [Tunable("Risk tolerance from which one more exposed set is allowed.")]
        public static readonly Tuned<double> ExtraSetRiskTolerance = new(0.7);
        [Tunable("Pressure (risk tolerance above its base, 0-1) from which the exposed-sets cap relaxes.")]
        public static readonly Tuned<double> OversetPressFrom = new(0.5);
        [Tunable("A set card we've learned isn't Mirror Force (or isn't Torrential) counts this share of its stop chance.")]
        public static readonly Tuned<double> PartialReadFactor = new(0.5);
        [Tunable("Test for Torrential: a tester must be worth this much less than what it protects.")]
        public static readonly Tuned<double> TorrentialTestMargin = new(0.5);

        // ------------------------------------------------------------------ survivability

        [Tunable("Survivability: a Level 5+ monster we know is in their hand counts this chance of being summoned to attack.")]
        public static readonly Tuned<double> KnownTributeMonsterChance = new(0.5);
        [Tunable("Survivability: each monster in their graveyard counts this chance of coming back to attack.")]
        public static readonly Tuned<double> GraveReviveChance = new(0.15);
    }
}
