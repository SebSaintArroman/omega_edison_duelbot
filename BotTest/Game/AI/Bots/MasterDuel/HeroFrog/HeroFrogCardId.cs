namespace BotTest.Game.AI.Bots.MasterDuel.HeroFrog
{
    /// <summary>
    /// HeroFrog deck (Frog Monarchs + an Elemental HERO / Miracle Fusion package): card ids (official ids; Omega's
    /// pre-errata copies of Stratos / Swap Frog / Brain Control / Absolute Zero are the official id + 20, see
    /// <see cref="HeroFrogBotBrain.IsId"/>). Counts: 40 main + 15 extra (list updated 2026-11-09: no Des Frog; Compulsory, Jar of Greed, 2 Raigeki Break, 1 Aoi).
    /// </summary>
    public static class HeroFrogCardId
    {
        // Main deck: monsters
        public const int GorzTheEmissaryOfDarkness = 44330098;   // x1
        public const int CaiusTheShadowMonarch = 9748752;        // x3, L6: banish a card (+1000 if a DARK monster)
        public const int RaizaTheStormMonarch = 73125233;        // x2, L6: a card to the top of the deck
        public const int ThestalosTheFirestormMonarch = 26205777; // x2, L6: random discard
        public const int DesFrog = 84451804;                     // not in the current list (kept for the logic)
        public const int ElementalHeroStratos = 40044918;        // x1, L4: pop S/Ts per HERO, or search a HERO
        public const int ElementalHeroOcean = 37195861;          // x1, L4 WATER HERO: Standby, a HERO back to the hand
        public const int JunkSynchron = 63977008;                // x1, L3 Tuner: revives a L2- from the graveyard
        public const int SwapFrog = 9126351;                     // x3, L2 WATER: SS by discarding a WATER; sends a L2- WATER Aqua to the GY
        public const int EvilHeroInfernalProdigy = 50304345;     // x1, L2: SS with no monsters (tribute fodder)
        public const int DupeFrog = 46239604;                    // x2, L2: attack lock; leaves the field -> search a Frog
        public const int Substitoad = 20663556;                  // x3, L1: tribute 1 -> SS a Frog from the deck; Frogs battle-proof
        public const int TreebornFrog = 12538374;                // x2, L1: back every Standby Phase with no S/T of ours
        public const int BattleFader = 19665973;                 // x1

        // Main deck: spells
        public const int BrainControl = 87910978;                // x1
        public const int EEmergencyCall = 213326;                // x1
        public const int HeavyStorm = 19613556;                  // x1
        public const int MiracleFusion = 45906428;               // x3
        public const int OneForOne = 2295440;                    // x1
        public const int ReinforcementOfTheArmy = 32807846;      // x1
        public const int UpstartGoblin = 70368879;               // x2
        public const int EnemyController = 98045062;             // x2, quick-play

        // Main deck: traps
        public const int RaigekiBreak = 4178474;                 // x1
        public const int SpiritualWaterArtAoi = 6540606;         // x2
        public const int TrapDustshoot = 64697231;               // x1
        public const int CompulsoryEvacuationDevice = 94192409;  // x1
        public const int JarOfGreed = 83968380;                  // x1

        // Extra deck
        public const int ElementalHeroAbsoluteZero = 40854197;   // x3: leaves the field -> destroy all their monsters
        public const int ElementalHeroGaia = 16304628;           // x1
        public const int GaiaKnight = 97204936;                  // x1
        public const int MistWurm = 27315304;                    // x1
        public const int ColossalFighter = 23693634;             // x2
        public const int ThoughtRulerArchfiend = 70780151;       // x1
        public const int DarkEndDragon = 88643579;               // x1
        public const int StardustDragon = 44508094;              // x1
        public const int BlackRoseDragon = 73580471;             // x1
        public const int FlamvellUruquizas = 53714009;           // x1
        public const int MagicalAndroid = 43385557;              // x1
        public const int AllyOfJusticeCatastor = 26593852;       // x1
    }
}

namespace BotTest.Game.AI.Bots.MasterDuel.HeroFrog
{
    /// <summary>The HeroFrog lists the brain plays (DeckBotBrain.Variant). One so far; more to come.</summary>
    public static class HeroFrogVariant
    {
        public const string Standard = BotTest.Game.AI.Brains.DeckBotBrain.StandardVariant; // HeroFrog
    }
}
