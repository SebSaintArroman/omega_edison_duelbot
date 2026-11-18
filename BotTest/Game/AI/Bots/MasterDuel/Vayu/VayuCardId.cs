namespace BotTest.Game.AI.Bots.MasterDuel.Vayu
{
    /// <summary>
    /// Card IDs for the Vayu (Blackwing / Dark Armed Dragon) deck, as decoded from the deck hash.
    /// Cards marked "pre-errata" use the server's un-errata'd ID (official ID + 20).
    /// Counts: 40 main + 15 extra = 55. Side (2): Elphin, Necro Gardna.
    /// </summary>
    public static class VayuCardId
    {
        // Monsters
        public const int BlackwingVayuTheEmblemOfHonor = 72714392;   // x3, Tuner L1
        public const int BlackwingSiroccoTheDawn = 75498415;         // x3, L5
        public const int BlackwingElphinTheRaven = 11613567;         // side only, L6
        public const int DarkGrepher = 14536035;                     // x3, L4
        public const int ArmageddonKnight = 28985331;                // x3, L4
        public const int NecroGardna = 4906301;                      // x1 (+1 side), L3
        public const int CardTrooper = 85087012;                     // x1, L3
        public const int PlaguespreaderZombie = 33420078;            // x1, Tuner L2
        public const int LylaLightswornSorceress = 22624393;         // x1, L4 (pre-errata, official 22624373)
        public const int RykoLightswornHunter = 21502816;            // x2, L2 Flip (pre-errata, official 21502796)
        public const int DarkArmedDragon = 65192027;                 // x1, L7
        public const int ChaosSorcerer = 9596126;                    // x1, L6
        public const int GorzTheEmissaryOfDarkness = 44330098;       // x1, L7

        // Spells
        public const int UpstartGoblin = 70368879;                   // x3
        public const int MiracleDig = 6343408;                       // x2
        public const int ReinforcementOfTheArmy = 32807846;          // x1
        public const int ChargeOfTheLightBrigade = 94886282;         // x1
        public const int HeavyStorm = 19613556;                      // x1
        public const int MysticalSpaceTyphoon = 5318639;             // x1, Quick-Play
        public const int BurialFromADifferentDimension = 48976825;   // x1, Quick-Play

        // Traps
        public const int BottomlessTrapHole = 29401950;              // x2
        public const int DimensionalPrison = 70342110;               // x2
        public const int JarOfGreed = 83968380;                      // x2
        public const int LegacyOfYataGarasu = 30461781;              // draw card, same handling as Jar of Greed
        public const int MirrorForce = 44095762;                     // x1
        public const int TorrentialTribute = 53582587;               // x1
        public const int TrapDustshoot = 64697231;                   // x1

        // Extra Deck - Synchro
        public const int BlackwingArmedWing = 76913983;              // x3, L6
        public const int BlackwingArmorMaster = 69031175;            // x2, L7
        public const int BlackwingSilverwindTheAscendant = 33236860; // x2, L8
        public const int BlackRoseDragon = 73580471;                 // x1, L7
        public const int StardustDragon = 44508094;                  // x1, L8
        public const int ColossalFighter = 23693634;                 // x1, L8
        public const int DarkEndDragon = 88643579;                   // x1, L8
        public const int BrionacDragonOfTheIceBarrier = 50321816;    // x1, L6 (pre-errata, official 50321796)
        public const int GoyoGuardian = 7391468;                     // x1, L6 (pre-errata, official 7391448)
        public const int AllyOfJusticeCatastor = 26593852;           // x1, L5 DARK: destroys a non-DARK monster it battles

        // Extra Deck - Fusion
        public const int ChimeratechFortressDragon = 79229522;       // x1
    }
}

namespace BotTest.Game.AI.Bots.MasterDuel.Vayu
{
    /// <summary>The Vayu lists the brain plays (DeckBotBrain.Variant). One so far; more to come.</summary>
    public static class VayuVariant
    {
        public const string Standard = BotTest.Game.AI.Brains.DeckBotBrain.StandardVariant; // Vayu
    }
}
