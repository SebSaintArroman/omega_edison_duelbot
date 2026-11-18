using System.Runtime.InteropServices;

namespace DuelSim.Native;

/// <summary>
/// P/Invoke bindings for the EDOPro rules engine (ocgcore, API 11.0; see vendor/core-src/ocgapi.h).
/// The shipped Windows DLL is 32-bit, so struct layouts below are the MSVC x86 ones (pointers are 4 bytes,
/// 64-bit fields are 8-aligned) and every call is cdecl.
/// </summary>
internal static class OcgApi
{
    private const string Dll = "ocgcore";

    public const int StatusEnd = 0, StatusAwaiting = 1, StatusContinue = 2;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void OCG_GetVersion(out int major, out int minor);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int OCG_CreateDuel(out IntPtr duel, ref DuelOptions options);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void OCG_DestroyDuel(IntPtr duel);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void OCG_DuelNewCard(IntPtr duel, ref NewCardInfo info);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void OCG_StartDuel(IntPtr duel);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int OCG_DuelProcess(IntPtr duel);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr OCG_DuelGetMessage(IntPtr duel, out uint length);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void OCG_DuelSetResponse(IntPtr duel, byte[] buffer, uint length);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int OCG_LoadScript(IntPtr duel, byte[] buffer, uint length, [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint OCG_DuelQueryCount(IntPtr duel, byte team, uint location);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr OCG_DuelQuery(IntPtr duel, out uint length, ref QueryInfo info);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr OCG_DuelQueryLocation(IntPtr duel, out uint length, ref QueryInfo info);

    public static byte[] Copy(IntPtr ptr, uint length)
    {
        if (ptr == IntPtr.Zero || length == 0)
            return Array.Empty<byte>();
        var bytes = new byte[length];
        Marshal.Copy(ptr, bytes, 0, (int)length);
        return bytes;
    }
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void OcgDataReader(IntPtr payload, uint code, IntPtr data);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void OcgDataReaderDone(IntPtr payload, IntPtr data);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int OcgScriptReader(IntPtr payload, IntPtr duel, IntPtr name);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void OcgLogHandler(IntPtr payload, IntPtr text, int type);

/// <summary>OCG_DuelOptions, x86 layout.</summary>
[StructLayout(LayoutKind.Explicit, Size = 104)]
internal struct DuelOptions
{
    [FieldOffset(0)] public ulong Seed0;
    [FieldOffset(8)] public ulong Seed1;
    [FieldOffset(16)] public ulong Seed2;
    [FieldOffset(24)] public ulong Seed3;
    [FieldOffset(32)] public ulong Flags;
    [FieldOffset(40)] public uint Team1StartingLp;
    [FieldOffset(44)] public uint Team1StartingDraw;
    [FieldOffset(48)] public uint Team1DrawPerTurn;
    [FieldOffset(52)] public uint Team2StartingLp;
    [FieldOffset(56)] public uint Team2StartingDraw;
    [FieldOffset(60)] public uint Team2DrawPerTurn;
    [FieldOffset(64)] public IntPtr CardReader;
    [FieldOffset(68)] public IntPtr Payload1;
    [FieldOffset(72)] public IntPtr ScriptReader;
    [FieldOffset(76)] public IntPtr Payload2;
    [FieldOffset(80)] public IntPtr LogHandler;
    [FieldOffset(84)] public IntPtr Payload3;
    [FieldOffset(88)] public IntPtr CardReaderDone;
    [FieldOffset(92)] public IntPtr Payload4;
    [FieldOffset(96)] public byte EnableUnsafeLibraries;
}

/// <summary>OCG_NewCardInfo, x86 layout.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct NewCardInfo
{
    [FieldOffset(0)] public byte Team;
    [FieldOffset(1)] public byte Duelist;
    [FieldOffset(4)] public uint Code;
    [FieldOffset(8)] public byte Controller;
    [FieldOffset(12)] public uint Location;
    [FieldOffset(16)] public uint Sequence;
    [FieldOffset(20)] public uint Position;
}

/// <summary>OCG_QueryInfo, x86 layout.</summary>
[StructLayout(LayoutKind.Explicit, Size = 20)]
internal struct QueryInfo
{
    [FieldOffset(0)] public uint Flags;
    [FieldOffset(4)] public byte Controller;
    [FieldOffset(8)] public uint Location;
    [FieldOffset(12)] public uint Sequence;
    [FieldOffset(16)] public uint OverlaySequence;
}

/// <summary>Offsets of OCG_CardData (x86), which the engine hands to the card reader to fill in.</summary>
internal static class CardDataLayout
{
    public const int Code = 0, Alias = 4, SetCodes = 8, Type = 12, Level = 16, Attribute = 20, Race = 24,
        Attack = 32, Defense = 36, LScale = 40, RScale = 44, LinkMarker = 48;
}

/// <summary>Engine constants used by the simulator (vendor/core-src/common.h).</summary>
internal static class Ocg
{
    public const uint LocDeck = 0x01, LocHand = 0x02, LocMZone = 0x04, LocSZone = 0x08, LocGrave = 0x10,
        LocRemoved = 0x20, LocExtra = 0x40, LocOverlay = 0x80;

    public const uint PosFaceUpAttack = 0x1, PosFaceDownAttack = 0x2, PosFaceUpDefense = 0x4, PosFaceDownDefense = 0x8,
        PosFaceUp = 0x5, PosFaceDown = 0xa;

    public const uint TypeMonster = 0x1, TypeSpell = 0x2, TypeTrap = 0x4, TypeFusion = 0x40, TypeSynchro = 0x2000,
        TypeXyz = 0x800000, TypeToken = 0x4000, TypeLink = 0x4000000, TypePendulum = 0x1000000;

    public const uint ReasonSpSummon = 0x800;
    public const uint StatusDisabled = 0x1;

    // Duel rule flags.
    public const ulong DuelPZone = 0x800, DuelSeparatePZone = 0x1000, DuelEmZone = 0x2000, DuelFsxMmZone = 0x4000,
        DuelTrapMonstersNotUseZone = 0x8000, DuelTriggerOnlyInLocation = 0x20000, DuelPseudoShuffle = 0x10;
    /// <summary>Master Rule 5 (current rules), as EDOPro defines DUEL_MODE_MR5.</summary>
    public const ulong DuelModeMr5 = DuelPZone | DuelEmZone | DuelFsxMmZone | DuelTrapMonstersNotUseZone | DuelTriggerOnlyInLocation;
    /// <summary>
    /// DUEL_OCG_OBSOLETE_IGNITION: the old OCG priority rule, as on Omega (Edison). After the turn player's summon, or a
    /// chain ending in the Main Phase, their face-up monsters' ignition effects can be activated in a chain window: Dark
    /// Armed Dragon, Dark Grepher, Lyla, a union's equip (user, 2026-11-13: "it's meant to be the same as omega").
    /// </summary>
    public const ulong DuelPriority = 0x100;
    public const ulong Duel1stTurnDraw = 0x200, Duel1FaceupField = 0x400, DuelReturnToDeckTriggers = 0x10000,
        DuelSpsummonOnceOldNegate = 0x40000, DuelCannotSummonOathOld = 0x80000;
    /// <summary>
    /// The Edison-era rules Omega plays, as EDOPro defines DUEL_MODE_MR1 (vendor/core-src/common.h): priority, a draw on the
    /// first turn, one face-up Field Spell, no Extra Monster Zone or Pendulum Zones, and the old rulings for once-per-turn
    /// Special Summons, return-to-deck triggers and summon oaths (user, 2026-11-13: "there shouldn't be any differences").
    /// </summary>
    public const ulong DuelModeEdison = DuelPriority | Duel1stTurnDraw | Duel1FaceupField | DuelSpsummonOnceOldNegate |
        DuelReturnToDeckTriggers | DuelCannotSummonOathOld | DuelStoreAttackReplays;

    /// <summary>
    /// No replay prompt: when their field changes during our attack (a monster revived by Call of the Haunted against a direct attack),
    /// the attack just ends and counts as made. Live Omega does this (CSV M8, 2026-11-14 15:20 game: Laquari's direct attack, Lyla
    /// revived, no prompt reached the bot and Laquari stood as attacked); without the flag the core asked a yes/no the bot never gets live.
    /// </summary>
    public const ulong DuelStoreAttackReplays = 0x20000000;

    public const uint QueryCode = 0x1, QueryPosition = 0x2, QueryAlias = 0x4, QueryType = 0x8, QueryLevel = 0x10,
        QueryRank = 0x20, QueryAttribute = 0x40, QueryRace = 0x80, QueryAttack = 0x100, QueryDefense = 0x200,
        QueryBaseAttack = 0x400, QueryBaseDefense = 0x800, QueryReason = 0x1000, QueryReasonCard = 0x2000,
        QueryEquipCard = 0x4000, QueryTargetCard = 0x8000, QueryOverlayCard = 0x10000, QueryCounters = 0x20000,
        QueryOwner = 0x40000, QueryStatus = 0x80000, QueryIsPublic = 0x100000, QueryLScale = 0x200000,
        QueryRScale = 0x400000, QueryLink = 0x800000, QueryIsHidden = 0x1000000, QueryCover = 0x2000000,
        QueryEnd = 0x80000000;

    public const uint QueryAll = QueryCode | QueryPosition | QueryAlias | QueryType | QueryLevel | QueryRank |
        QueryAttribute | QueryRace | QueryAttack | QueryDefense | QueryBaseAttack | QueryBaseDefense | QueryReason |
        QueryReasonCard | QueryEquipCard | QueryTargetCard | QueryOverlayCard | QueryCounters | QueryOwner |
        QueryStatus | QueryIsPublic | QueryLScale | QueryRScale | QueryLink;
}

/// <summary>Engine message ids (MSG_*).</summary>
internal static class Msg
{
    public const byte Retry = 1, Hint = 2, Waiting = 3, Start = 4, Win = 5, UpdateData = 6, UpdateCard = 7,
        SelectBattleCmd = 10, SelectIdleCmd = 11, SelectEffectYn = 12, SelectYesNo = 13, SelectOption = 14,
        SelectCard = 15, SelectChain = 16, SelectPlace = 18, SelectPosition = 19, SelectTribute = 20, SortChain = 21,
        SelectCounter = 22, SelectSum = 23, SelectDisfield = 24, SortCard = 25, SelectUnselectCard = 26,
        ConfirmDeckTop = 30, ConfirmCards = 31, ShuffleDeck = 32, ShuffleHand = 33, NewTurn = 40, NewPhase = 41,
        Move = 50, PosChange = 53, Set = 54, Swap = 55, Summoning = 60, Summoned = 61, SpSummoning = 62,
        SpSummoned = 63, FlipSummoning = 64, FlipSummoned = 65, Chaining = 70, Chained = 71, ChainSolving = 72,
        ChainSolved = 73, ChainEnd = 74, ChainNegated = 75, ChainDisabled = 76, CardSelected = 80,
        RandomSelected = 81, BecomeTarget = 83, Draw = 90, Damage = 91, Recover = 92, Equip = 93, LpUpdate = 94,
        CardTarget = 96, CancelTarget = 97, PayLpCost = 100, Attack = 110, Battle = 111, AttackDisabled = 112,
        DamageStepStart = 113, DamageStepEnd = 114, TossCoin = 130, TossDice = 131, RockPaperScissors = 132,
        HandRes = 133, AnnounceRace = 140, AnnounceAttrib = 141, AnnounceCard = 142, AnnounceNumber = 143,
        CardHint = 160, PlayerHint = 165;

    public const byte HintSelectMsg = 3;
}
