namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// DeckRules rules, by name (generated from the Rule(...) gates in the code). Each gate fires with the probability tuning
    /// gives it (default 1: as written). See RuleGates and DuelSim's evolve.
    /// Every rule that rejects a move also registers a hold (DeckBotBrain.Hold) where it does so, and the play search pays the
    /// rule's cost for lines that make the move anyway: declare the cost on the constant with [HoldCost(HoldCosts.Strong)]
    /// when a rule matters more than the Standard 1.5 cards (user, 2026-11-11: all deck rules work through the hold system).
    /// </summary>
    [RuleSet(typeof(DeckBotBrain))]
    public static class DeckRules
    {
        public const string AttackRememberedFaceDown = "deck.attack-remembered-face-down";
        public const string BottomlessOverTorrential = "deck.bottomless-over-torrential";
        public const string BottomlessTrapHole = "deck.bottomless-trap-hole";
        public const string ChainBait = "deck.chain-bait";
        /// <summary>Burial from a Different Dimension isn't set while none of our monsters are banished: nothing to return, so it's neither
        /// a play nor bait (user, 2026-11-15). With banished monsters: bait while they're not needed yet, activated when they are.</summary>
        public const string NoEmptyBurialSet = "deck.no-empty-burial-set";
        /// <summary>Torrential suspected and more than one monster we could summon: the less important one goes first when it puts on
        /// pressure (it can attack this turn), the more important one waits; if they Torrential it, the better one comes anyway, if not,
        /// it isn't needed yet (user, 2026-11-15: "test for torrential").</summary>
        [HoldCost(HoldCosts.Light)] // not very important for now (user, 2026-11-15: "i dont want it doing anything unexpected")
        [TrapFear]
        public const string TestForTorrential = "deck.test-for-torrential";
        /// <summary>A card that only changes ATK/DEF (Skill Successor, Rush Recklessly...) waits for the damage step, unless doomed
        /// (user, 2026-11-15: "skill successor should only be used when in the damage step or when doomed").</summary>
        [HoldCost(HoldCosts.Strong)]
        public const string StatBoostInDamageStep = "deck.stat-boost-in-damage-step";
        /// <summary>Waboku in our own turn only when the brain chose it: a Gladiator Beast kept alive attacking into something bigger so it
        /// can tag out (and deal with it after), or a doomed Waboku (CSV S8).</summary>
        [HoldCost(HoldCosts.Strong)]
        public const string WabokuOwnTurn = "deck.waboku-own-turn";
        /// <summary>Our spell/trap removal (MST, Dust Tornado, Heavy Storm) only in our Main Phase 1 before a summon or an attack, to clear the
        /// way for our monsters (user, 2026-11-26); not when doomed or at a face-up card of theirs that matters.</summary>
        [HoldCost(HoldCosts.Standard)]
        public const string BackrowRemovalProtects = "deck.backrow-removal-protects";
        public const string ChargeOfTheLightBrigade = "deck.charge-of-the-light-brigade";
        public const string CheapIntoFaceDown = "deck.cheap-into-face-down";
        public const string CheapestIntoBouncer = "deck.cheapest-into-bouncer";
        public const string ChimeratechSafetyFuse = "deck.chimeratech-safety-fuse";
        public const string CompulsorySavesOwn = "deck.compulsory-saves-own";
        /// <summary>On their turn, Compulsory Evacuation Device prefers to wait for the attack, unless doomed: a small hold, there are lots of
        /// reasons to use it early (user, 2026-11-25). Held in the earlier windows (CompulsoryWaitsForAttack.cs).</summary>
        [HoldCost(HoldCosts.Light)]
        public const string CompulsoryOnAttack = "deck.compulsory-on-attack";
        /// <summary>Bouncing a monster of theirs that Special Summons itself from the hand (Fortress, Faultroll) outside an attack: a large hold
        /// (user, 2026-11-25). Selection/Rules/BounceSelfSummoner.cs.</summary>
        [HoldCost(HoldCosts.Strong)]
        public const string BounceSelfSummoner = "deck.bounce-self-summoner";
        public const string CrashForBattlePayoff = "deck.crash-for-battle-payoff";
        /// <summary>A free kill on a monster that doesn't replace itself before one on a recruiter (XX-Saber Emmersblade): damage and a cleared
        /// monster over a kill that gets them a monster from the deck (user, 2026-11-25).</summary>
        [HoldCost(HoldCosts.Light)] // user, 2026-11-25: 0.75
        public const string KillBeforeRecruiter = "deck.kill-before-recruiter";
        /// <summary>Attacking a monster whose destruction rewards them (recruit, draw) with no plain kill on offer: discouraged, not prevented
        /// (user, 2026-11-25). Battle/Rules/KillBeforeRecruiter.cs.</summary>
        [HoldCost(0.1)] // user, 2026-11-25: barely there, so the bot never skips an attack over it; a lever for tuning later
        public const string AttackIntoBattleReward = "deck.attack-into-battle-reward";
        public const string CrashTrades = "deck.crash-trades";
        public const string DDCrowChain = "deck.dd-crow-chain";
        /// <summary>Banishing from their graveyard without a planned target: a card with a graveyard effect first, then a Fairy, then other monsters (user, 2026-11-14 / 2026-11-17).</summary>
        [HoldCost(HoldCosts.Light)]
        public const string GraveBanishTarget = "deck.grave-banish-target";
        /// <summary>Reviving our own monster whose effect works from the graveyard (Call of the Haunted on Shield Warrior; user, 2026-11-16).</summary>
        public const string NoReviveGraveEffect = "deck.no-revive-grave-effect";
        /// <summary>Goyo Guardian (any monster that takes what it destroys by battle) into a battle recruiter, unless it takes a turn off our clock (user, 2026-11-17).</summary>
        [HoldCost(HoldCosts.Strong)] // "this should only be done very rarely"
        public const string NoStealRecruiter = "deck.no-steal-recruiter";
        /// <summary>A monster whose effect works from the hand (Battle Fader, Honest, Kalut) isn't set (user, 2026-11-18: "NEVER").</summary>
        [HoldCost(HoldCosts.Critical)]
        public const string NoHandTrapSet = "deck.no-hand-trap-set";
        /// <summary>On their turn, a monster we summon goes to defence when one of their attackers can still beat it.</summary>
        public const string DefenceOnTheirTurn = "deck.defence-on-their-turn";
        public const string DimensionalPrison = "deck.dimensional-prison";
        public const string DrawForGorz = "deck.draw-for-gorz";
        public const string DrawWhenNoPlay = "deck.draw-when-no-play";
        public const string ExpendableAttacksOpenField = "deck.expendable-attacks-open-field";
        public const string FlipBeforeSummon = "deck.flip-before-summon";
        public const string FlipForThreat = "deck.flip-for-threat";
        public const string GorzChain = "deck.gorz-chain";
        public const string GorzWaitsForBiggest = "deck.gorz-waits-for-biggest";
        public const string HeavyStorm = "deck.heavy-storm";
        /// <summary>Heavy Storm isn't used to push a big play past one set card when our set Solemn Judgment already can.</summary>
        public const string StormNotWithSolemn = "deck.storm-not-with-solemn";
        /// <summary>Attack into a bigger monster when Shield Warrior / Waboku keeps ours alive and battling pays (a tag-out).</summary>
        public const string ProtectedAttack = "deck.protected-attack";
        /// <summary>Shield Warrior from the graveyard / Waboku when our battling monster would die (either turn), Waboku against lethal damage.</summary>
        public const string BattleProtection = "deck.battle-protection";
        public const string AllureOfDarkness = "deck.allure-of-darkness";
        /// <summary>Low on cards with nothing set: spend for a bigger body that their next turn likely can't run over.</summary>
        public const string SpendForSurvivability = "deck.spend-for-survivability";
        /// <summary>Return from the Different Dimension on our turn when the returned monsters make lethal.</summary>
        public const string ReturnDimensionLethal = "deck.return-dimension-lethal";
        /// <summary>Return from the Different Dimension in their Battle Phase, as a last resort: its monsters block a lethal attack.</summary>
        public const string ReturnDimensionBlockLethal = "deck.return-dimension-block-lethal";
        public const string HoldForBiggerAttacker = "deck.hold-for-bigger-attacker";
        public const string HoldForGorz = "deck.hold-for-gorz";
        [ResourceHold]
        public const string HoldForThreatOrWin = "deck.hold-for-threat-or-win";
        public const string HoldMonsterSetsForBattle = "deck.hold-monster-sets-for-battle";
        [TrapFear]
        public const string HoldSetForStorm = "deck.hold-set-for-storm";
        public const string HoldSetsForBattle = "deck.hold-sets-for-battle";
        [TrapFear]
        public const string HoldSetsForStorm = "deck.hold-sets-for-storm";
        public const string HoldSynchroIntoBouncer = "deck.hold-synchro-into-bouncer";
        [TrapFear]
        public const string HoldWhenAlreadyLethal = "deck.hold-when-already-lethal";
        public const string KeepGorzLive = "deck.keep-gorz-live";
        /// <summary>An attack the brain committed to (a lethal push, a monster summoned to battle): leaving the Battle Phase
        /// instead breaks the plan (user, 2026-11-13, the 12:30 Gorz duel: the search dropped a push the brain had reasons for).</summary>
        [HoldCost(HoldCosts.Strong)]
        public const string KeepCommittedAttack = "deck.keep-committed-attack";        /// <summary>No valuable attacker into a face-down that likely banishes / bounces what battles it (D.D. Warrior Lady).</summary>
        public const string AvoidBattleRemoval = "deck.avoid-battle-removal";
        /// <summary>Lyla attacks first and uses her effect in Main Phase 2 when their spells/traps can't stop her attack.</summary>
        public const string LylaAfterBattle = "deck.lyla-after-battle";
        /// <summary>A defence-position monster switches to attack when our attackers already up clear their monsters and its
        /// direct hit matters.</summary>
        public const string RepositionForDirectHit = "deck.reposition-for-direct-hit";
        /// <summary>The Normal Summon isn't left unused at the end of the turn: the cheapest monster to commit is set.</summary>
        public const string UseNormalSummon = "deck.use-normal-summon";
        /// <summary>Over the hand limit before the End Phase: set spells/traps (traps first), then the deck's last resort.</summary>
        public const string AvoidHandLimitDiscard = "deck.avoid-hand-limit-discard";
        /// <summary>No set spell/trap that can't respond into their face-up attack-position Lyla (her effect takes it).</summary>
        public const string NoSetIntoLyla = "deck.no-set-into-lyla";
        /// <summary>A monster summoned on our turn goes in defence when, in attack position, their bigger monster's hit on it would be lethal.</summary>
        public const string DefenceWhenHitIsLethal = "deck.defence-when-hit-is-lethal";
        public const string LylaDestroy = "deck.lyla-destroy";
        public const string MirrorForceMulti = "deck.mirror-force-multi";
        public const string MirrorForceSingle = "deck.mirror-force-single";
        public const string MoleLock = "deck.mole-lock";
        public const string MstForBigPlay = "deck.mst-for-big-play";
        public const string MstIntoContinuous = "deck.mst-into-continuous";
        public const string NoChimeratechExposure = "deck.no-chimeratech-exposure";
        [TrapFear]
        public const string NoChipIntoBackrow = "deck.no-chip-into-backrow";
        public const string NoDestroyIntoNegator = "deck.no-destroy-into-negator";
        public const string NoRedundantSets = "deck.no-redundant-sets";
        public const string NoRykoIntoFaceDown = "deck.no-ryko-into-face-down";
        public const string NoSetIntoBouncer = "deck.no-set-into-bouncer";
        public const string NoSetIntoStealer = "deck.no-set-into-stealer";
        public const string NoTrapIntoNegator = "deck.no-trap-into-negator";
        public const string OnSummonIntoBouncer = "deck.on-summon-into-bouncer";
        public const string OneAnswerPerSummon = "deck.one-answer-per-summon";
        public const string OneHitLethalFirst = "deck.one-hit-lethal-first";
        public const string PushForClock = "deck.push-for-clock";
        public const string ReinforcementOfTheArmy = "deck.reinforcement-of-the-army";
        public const string RepositionToAttack = "deck.reposition-to-attack";
        public const string RepositionToDefence = "deck.reposition-to-defence";
        public const string SafeSummon = "deck.safe-summon";
        public const string SaveMoleForBetterAnswer = "deck.save-mole-for-better-answer";
        public const string SetRyko = "deck.set-ryko";
        public const string SetRykoFirstTurn = "deck.set-ryko-first-turn";
        public const string SetTraps = "deck.set-traps";
        public const string SetsAfterBattle = "deck.sets-after-battle";
        public const string SkipEmptyBattlePhase = "deck.skip-empty-battle-phase";
        public const string SkipUnworthyAttacks = "deck.skip-unworthy-attacks";
        public const string SolemnJudgment = "deck.solemn-judgment";
        public const string SolemnWaitsForGorz = "deck.solemn-waits-for-gorz";
        public const string SpareRemovalTrap = "deck.spare-removal-trap";
        public const string SpendForBlockerWhenLow = "deck.spend-for-blocker-when-low";
        public const string SpendForEffectAnswer = "deck.spend-for-effect-answer";
        /// <summary>A synchro (or other spend) whose field materials do nothing where they are, and which runs over what nothing ready can: made, not held.</summary>
        public const string IdleMaterialsSynchro = "deck.idle-materials-synchro";
        /// <summary>Normal Summon a monster that destroys a face-up defence-position effect monster of theirs in battle this turn (Lyla at 200 DEF), even if it doesn't beat their best attacker.</summary>
        public const string SummonToKill = "deck.summon-to-kill";
        /// <summary>A set card of ours about to be removed by their effect is activated if it helps, including Torrential when the wipe is favorable.</summary>
        public const string DoomedCardUsed = "deck.doomed-card-used";
        public const string BrionacBounce = "deck.brionac-bounce";
        public const string TurnPlannerCombo = "deck.turn-planner-combo";
        public const string FieldTunerSynchro = "deck.field-tuner-synchro";
        /// <summary>Plaguespreader Zombie is Normal Summoned only for a synchro straight after (a routine); otherwise it's set.</summary>
        public const string PlagueSummonForSynchro = "deck.plague-summon-for-synchro";
        /// <summary>Lyla (Normal Summon + effect) clears a spell/trap before a synchro play, like MST.</summary>
        public const string LylaClearsTheWay = "deck.lyla-clears-the-way";
        public const string SpendForLethal = "deck.spend-for-lethal";
        public const string SpendRemovalOnBouncer = "deck.spend-removal-on-bouncer";
        public const string SpendToAnswerThreat = "deck.spend-to-answer-threat";
        public const string SpendWhenRacing = "deck.spend-when-racing";
        public const string SpendWhenRefunded = "deck.spend-when-refunded";
        public const string StrongestIntoFaceDown = "deck.strongest-into-face-down";
        [TrapFear]
        public const string SummonInDefenceUnderMirror = "deck.summon-in-defence-under-mirror";
        public const string SummonLyla = "deck.summon-lyla";
        public const string SummonTrooper = "deck.summon-trooper";
        public const string TorrentialTribute = "deck.torrential-tribute";
        public const string TrapDustshoot = "deck.trap-dustshoot";
        /// <summary>Trap Dustshoot is set when their hand has 3+ cards: ending the turn with it in hand is held (user, 2026-11-18).</summary>
        [HoldCost(HoldCosts.Strong)]
        public const string SetTrapDustshoot = "deck.set-trap-dustshoot";
        public const string TrooperDeckGuard = "deck.trooper-deck-guard";
        public const string TrooperDefenceWhenThin = "deck.trooper-defence-when-thin";
        public const string TrooperIntoFaceDown = "deck.trooper-into-face-down";
        public const string TrooperMill = "deck.trooper-mill";
        public const string UpstartGoblin = "deck.upstart-goblin";
        public const string ValueSet = "deck.value-set";
        public const string WeakDontAttackDirectly = "deck.weak-dont-attack-directly";
        /// <summary>A spend (synchro, Vayu, Plaguespreader, a special summon from hand) the spend rules found no reason for: held.</summary>
        [ResourceHold]
        public const string HoldSpend = "deck.hold-spend";
        /// <summary>No blockers: a monster summoned or set only because it can't beat anything (never for survival).</summary>
        public const string NoBlockers = "deck.no-blockers";
        /// <summary>No attack when no attack gains anything (no kill, no safe damage, no payoff).</summary>
        public const string NoAttackWithoutGain = "deck.no-attack-without-gain";
        /// <summary>A free extra body the attack doesn't need waits while they have unknown set cards (round-robin 33; risk tolerance).</summary>
        public const string SpareBodyWaits = "deck.spare-body-waits";
        /// <summary>Spare attackers go to defence before battle against a likely Mirror Force (round-robin 43).</summary>
        [TrapFear]
        public const string ShelterSparesFromMirror = "deck.shelter-spares-from-mirror";
        /// <summary>Only the answers we expect to need next turn are set (round-robin 4).</summary>
        public const string OnlyNeededAnswers = "deck.only-needed-answers";
        /// <summary>At low LP a monster isn't left (or summoned) in attack position where their attacker kills us through it (round-robin 3).</summary>
        public const string LowLpNoExposure = "deck.low-lp-no-exposure";
        /// <summary>Last resort: a blocker is set when it (with our other answers) stops their lethal next turn (round-robin 5).</summary>
        public const string LastResortBlocker = "deck.last-resort-blocker";
        /// <summary>Brain Control as a staple: take their monster for a kill, a tribute / synchro, or lethal (round-robin 36).</summary>
        public const string BrainControl = "deck.brain-control";
        /// <summary>Smashing Ground on a monster we can't attack over (round-robin 47).</summary>
        public const string SmashingGround = "deck.smashing-ground";
        /// <summary>A monster that destroys one of theirs in battle this turn is summoned (round-robin 34a).</summary>
        public const string SummonToTrade = "deck.summon-to-trade";
        /// <summary>A monster whose summon effect is worth a card is summoned for it (round-robin 34b).</summary>
        public const string SummonForEffect = "deck.summon-for-effect";
        /// <summary>Damage on an empty field two turns running: develop the best monster (round-robin 34c).</summary>
        public const string DevelopWhenBleeding = "deck.develop-when-bleeding";
        /// <summary>Upstart Goblin waits while their LP are within this turn's reach (round-robin 38).</summary>
        public const string UpstartWaitsForLethal = "deck.upstart-waits-for-lethal";
        /// <summary>Gamble draws (Allure without a DARK, Upstart, Jar) when we lose next turn anyway (round-robin 31).</summary>
        public const string GambleWhenLosing = "deck.gamble-when-losing";
        /// <summary>A tribute summon off an expendable monster (round-robin 50).</summary>
        public const string TributeExpendable = "deck.tribute-expendable";
        /// <summary>Set MST fired at a fresh set card before a key monster attacks (round-robin 9).</summary>
        public const string MstBeforeKeyAttack = "deck.mst-before-key-attack";
        /// <summary>A synchro made in Main Phase 1 when it hits harder than its materials (round-robin 30).</summary>
        public const string SynchroBeforeBattle = "deck.synchro-before-battle";
        /// <summary>A non-expendable summon into a likely Ryko waits for next turn (round-robin 11; medium weight).</summary>
        public const string RykoHoldsBigSummon = "deck.ryko-holds-big-summon";
        /// <summary>D.D. Crow banishes a DARK first when exactly 3 would let their DAD out (round-robin 14a).</summary>
        public const string CrowStopsDad = "deck.crow-stops-dad";
        /// <summary>Dark Armed Dragon: trim the graveyard to exactly 3 DARKs (shared staple).</summary>
        public const string DadTrim = "deck.dad-trim";
        /// <summary>Dark Armed Dragon's effect when it's worth a DARK.</summary>
        public const string DadEffect = "deck.dad-effect";
        /// <summary>DAD's effect held when only graveyard play pieces are left to banish.</summary>
        public const string HoldDadEffectForPieces = "deck.hold-dad-effect-for-pieces";
        /// <summary>D.D. Crow banishes a self-reviving monster (Fortress) when we have no set answer to it (round-robin 14a).</summary>
        public const string CrowStopsReviver = "deck.crow-stops-reviver";
        /// <summary>A doomed monster of ours is used as a cost / material in response (round-robin 19).</summary>
        public const string DoomedMonsterUsed = "deck.doomed-monster-used";
    }
}
