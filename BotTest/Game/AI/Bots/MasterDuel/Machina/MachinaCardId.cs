namespace BotTest.Game.AI.Bots.MasterDuel.Machina
{
    /// <summary>
    /// Machina brain: card ids (official ids; Omega's own copies of Gearframe / Peacekeeper / Lyla / Ryko / Goyo /
    /// Brionac carry these as their alias, so IsCode matches both).
    /// Counts: 40 main + 15 extra.
    /// </summary>
    public static class MachinaCardId
    {
        // Main deck: monsters
        public const int Mechanicalchaser = 7359741;             // x1, L4 Machine (normal)
        public const int MachinaForce = 58054262;                // x2, L10 Machine: never summoned, Fortress fuel
        public const int GorzTheEmissaryOfDarkness = 44330098;   // x1
        public const int MachinaFortress = 5556499;              // x3, L7 Machine
        public const int CyberDragon = 70095154;                 // x2, L5 Machine
        public const int MachinaGearframe = 42940404;            // x3, L4 Machine union (Omega id 42940424)
        public const int LylaLightswornSorceress = 22624373;     // x1
        public const int CyberPhoenix = 3370104;                 // x1, L4 Machine
        public const int SuperNimbleMegaHamster = 5220687;       // x2, flip: sets a L3- Beast (Ryko) from deck
        public const int CardTrooper = 85087012;                 // x1, L3 Machine
        public const int MachinaPeacekeeper = 78349103;          // x2, L2 Machine union (Omega id 78349123)
        public const int PlaguespreaderZombie = 33420078;        // x1
        public const int RykoLightswornHunter = 21502796;        // x3, L2 Beast
        public const int DDCrow = 24508238;                      // x2

        // Main deck: spells
        public const int ChargeOfTheLightBrigade = 94886282;     // x1
        public const int HeavyStorm = 19613556;                  // x1
        public const int OverloadFusion = 3659803;               // x1
        public const int PotOfAvarice = 67169062;                // x2
        public const int MysticalSpaceTyphoon = 5318639;         // x1

        // Main deck: traps
        public const int BottomlessTrapHole = 29401950;          // x2
        public const int CompulsoryEvacuationDevice = 94192409;  // x1
        public const int DimensionalPrison = 70342110;           // x2
        public const int MirrorForce = 44095762;                 // x1
        public const int TorrentialTribute = 53582587;           // x1
        public const int TrapDustshoot = 64697231;               // x1
        public const int SolemnJudgment = 41420027;              // x1

        // MachinaJinzo variant (2026-11-10): Jinzo negates every trap (ours too); Jinzo - Returner revives it from the GY when
        // sent there (destroyed in our End Phase); Armageddon Knight sends a DARK from the deck; Dark Armed Dragon with exactly 3 DARKs.
        public const int Jinzo = 77585513;                       // x3, L6
        public const int JinzoReturner = 9418534;                // x3, L3
        public const int ArmageddonKnight = 28985331;            // x3
        public const int DarkArmedDragon = 65192027;             // x1
        public const int JarOfGreed = 83968380;                  // x3 (a trap: dead under our Jinzo)
        public const int FutureFusion = 77565204;                // x1: Overdragon's materials from the deck (a finishing push)

        // Extra deck
        public const int ChimeratechOverdragon = 64599569;
        public const int ChimeratechFortressDragon = 79229522;   // x2
        public const int XXSaberGottoms = 52352005;
        public const int TytannialPrincessOfCamellias = 51960178; // theirs: Gottoms is in the Extra Deck for her
        public const int RedDragonArchfiend = 70902743;
        public const int ColossalFighter = 23693634;
        public const int ThoughtRulerArchfiend = 70780151;
        public const int DarkEndDragon = 88643579;
        public const int StardustDragon = 44508094;
        public const int BlackRoseDragon = 73580471;
        public const int GoyoGuardian = 7391448;
        public const int BrionacDragonOfTheIceBarrier = 50321796;
        public const int FlamvellUruquizas = 53714009;
        public const int MagicalAndroid = 43385557;
        public const int AllyOfJusticeCatastor = 26593852;
    }
}

namespace BotTest.Game.AI.Bots.MasterDuel.Machina
{
    /// <summary>The Machina lists the brain plays (DeckBotBrain.Variant).</summary>
    public static class MachinaVariant
    {
        public const string Control = BotTest.Game.AI.Brains.DeckBotBrain.StandardVariant; // Machina Control
        public const string Jinzo = "Jinzo";  // MachinaJinzo: Jinzo, Armageddon Knight, Dark Armed Dragon, Future Fusion
    }
}
