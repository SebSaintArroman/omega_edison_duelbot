namespace BotTest.Game.AI.Bots.MasterDuel.GladiatorBeasts
{
    /// <summary>
    /// Gladiator Beasts brain, Gladiator Control deck (Gladiator Beasts + Raigeki Breaks + a Lightsworn package): card ids (official ids; Omega's own
    /// copies, official + 20, are matched by IsCode through the card database). Counts: 40 main + 10 extra (list
    /// 2026-11-10).
    /// </summary>
    public static class GladCardId
    {
        // Gladiator Beasts: each one tags out at the end of the Battle Phase if it battled (shuffle it into the deck,
        // Special Summon another Gladiator Beast from the deck), and gets its effect when a Gladiator Beast summons it.
        public const int Laquari = 78868776;   // x3, L4 1800: 2100 when tagged in
        public const int Darius = 25924653;    // x3, L4 1700: tagged in, revives a Gladiator Beast from the GY (negated)
        public const int Equeste = 57731460;   // x2, L4 1600: tagged in, adds a Gladiator Beast card from the GY to the hand
        public const int Bestiari = 41470137;  // x1, L4 1500: tagged in, destroys a Spell/Trap on the field; Gyzarus material
        public const int Retiari = 612115;     // x2, L3 1200: tagged in, banishes a card from their GY
        public const int Murmillo = 5975022;   // x1, L3 800: tagged in, destroys a face-up monster (mandatory: never with only ours)

        // Contact Fusions (shuffle the materials on our field into the deck).
        public const int Gyzarus = 48156348;    // x3, L6 2400: Bestiari + 1 Gladiator Beast; on summon destroys up to 2 cards;
                                                //     end of Battle Phase: back to the Extra Deck, 2 Gladiator Beasts from the deck
        public const int Heraklinos = 27346636; // x2, L8 3000: Laquari + 2 Gladiator Beasts; discard 1: negate a Spell/Trap

        public const int ShieldWarrior = 95360850;            // x2: banish from the GY, our monsters survive this battle
        public const int CardTrooper = 85087012;              // x1
        public const int Ryko = 21502796;                     // x2 (Omega 21502816)
        public const int Lyla = 22624373;                     // x1 (Omega 22624393)
        public const int ChargeOfTheLightBrigade = 94886282;  // x1

        public const int HeavyStorm = 19613556;
        public const int SmashingGround = 97169186;
        public const int UpstartGoblin = 70368879;             // x2
        public const int IndomitableGladiatorBeast = 55136228; // x1: +500 to a Gladiator Beast; from the GY back to the hand
        public const int MysticalSpaceTyphoon = 5318639;

        public const int BottomlessTrapHole = 29401950;  // x2
        public const int DimensionalPrison = 70342110;   // x2
        public const int MirrorForce = 44095762;
        public const int RaigekiBreak = 4178474;         // x3: the discards
        public const int SkillSuccessor = 73729209;      // x2
        public const int TorrentialTribute = 53582587;
        public const int TrapStun = 59616123;
        public const int Waboku = 12607053;
        public const int CallOfTheHaunted = 97077563;
        public const int WarChariot = 96216229;          // counter trap: negate an Effect Monster's effect while we have a Gladiator Beast

        public const int StardustDragon = 44508094;
        public const int ChimeratechFortressDragon = 79229522;

        /// <summary>The main-deck Gladiator Beasts.</summary>
        public static readonly int[] Glads = { Laquari, Darius, Equeste, Bestiari, Retiari, Murmillo };

        /// <summary>Gladiator Beast monsters on the field (main deck and Contact Fusions).</summary>
        public static readonly int[] GladMonsters = { Laquari, Darius, Equeste, Bestiari, Retiari, Murmillo, Gyzarus, Heraklinos };

        /// <summary>"Gladiator Beast" cards that can pay Indomitable's recycle from the GY (monsters and War Chariot; not itself).</summary>
        public static readonly int[] GladCards = { Laquari, Darius, Equeste, Bestiari, Retiari, Murmillo, Gyzarus, Heraklinos, WarChariot };
    }
}

namespace BotTest.Game.AI.Bots.MasterDuel.GladiatorBeasts
{
    /// <summary>The Gladiator Beasts lists the brain plays (DeckBotBrain.Variant). One so far; more to come.</summary>
    public static class GladVariant
    {
        public const string Standard = BotTest.Game.AI.Brains.DeckBotBrain.StandardVariant; // Gladiator Control
    }
}
