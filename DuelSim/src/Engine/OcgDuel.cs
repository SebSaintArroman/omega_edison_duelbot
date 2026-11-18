using System.Runtime.InteropServices;
using DuelSim.Data;
using DuelSim.Native;

namespace DuelSim.Engine;

/// <summary>
/// One duel inside the native engine. Not thread-safe: callers serialise access (DuelHost holds a lock around
/// every call). Script and card-data callbacks re-enter this object from inside native calls, on the same thread.
/// </summary>
internal sealed class OcgDuel : IDisposable
{
    // Delegates must outlive every native call that might invoke them: keep them in static fields.
    private static readonly OcgDataReader CardReaderDelegate = ReadCard;
    private static readonly OcgDataReaderDone CardReaderDoneDelegate = ReadCardDone;
    private static readonly OcgScriptReader ScriptReaderDelegate = ReadScript;
    private static readonly OcgLogHandler LogHandlerDelegate = HandleLog;

    private readonly CardStore _cards;
    private readonly ScriptStore _scripts;
    private GCHandle _self;
    private IntPtr _handle;

    public event Action<string>? EngineLog;

    public IntPtr Handle => _handle;

    private OcgDuel(CardStore cards, ScriptStore scripts)
    {
        _cards = cards;
        _scripts = scripts;
    }

    public static (int Major, int Minor) Version()
    {
        OcgApi.OCG_GetVersion(out int major, out int minor);
        return (major, minor);
    }

    public static OcgDuel Create(CardStore cards, ScriptStore scripts, ulong[] seed, ulong flags,
        int startingLp = 8000, int startingHand = 5, int drawPerTurn = 1)
    {
        if (IntPtr.Size != 4)
            throw new PlatformNotSupportedException("ocgcore.dll is 32-bit: run the simulator as an x86 process.");

        var duel = new OcgDuel(cards, scripts);
        duel._self = GCHandle.Alloc(duel);
        var payload = GCHandle.ToIntPtr(duel._self);
        var options = new DuelOptions
        {
            Seed0 = seed[0], Seed1 = seed[1], Seed2 = seed[2], Seed3 = seed[3],
            Flags = flags,
            Team1StartingLp = (uint)startingLp, Team1StartingDraw = (uint)startingHand, Team1DrawPerTurn = (uint)drawPerTurn,
            Team2StartingLp = (uint)startingLp, Team2StartingDraw = (uint)startingHand, Team2DrawPerTurn = (uint)drawPerTurn,
            CardReader = Marshal.GetFunctionPointerForDelegate(CardReaderDelegate), Payload1 = payload,
            ScriptReader = Marshal.GetFunctionPointerForDelegate(ScriptReaderDelegate), Payload2 = payload,
            LogHandler = Marshal.GetFunctionPointerForDelegate(LogHandlerDelegate), Payload3 = payload,
            CardReaderDone = Marshal.GetFunctionPointerForDelegate(CardReaderDoneDelegate), Payload4 = payload,
            EnableUnsafeLibraries = 0,
        };
        int status = OcgApi.OCG_CreateDuel(out duel._handle, ref options);
        if (status != 0 || duel._handle == IntPtr.Zero)
        {
            duel.Dispose();
            throw new InvalidOperationException($"OCG_CreateDuel failed with status {status}.");
        }

        // The engine only asks for card scripts by itself; the shared helpers are loaded by the host.
        foreach (var baseScript in new[] { "constant.lua", "utility.lua" })
        {
            if (!duel.LoadScript(baseScript))
                throw new InvalidOperationException($"Could not load {baseScript} from {scripts.Root}.");
        }
        return duel;
    }

    public bool LoadScript(string name)
    {
        var bytes = _scripts.Read(name);
        if (bytes == null)
            return false;
        return OcgApi.OCG_LoadScript(_handle, bytes, (uint)bytes.Length, name) != 0;
    }

    /// <summary>Runs a generated script (e.g. a puzzle that lays out a position). False when the engine rejects it.</summary>
    public bool LoadScriptText(string name, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return OcgApi.OCG_LoadScript(_handle, bytes, (uint)bytes.Length, name) != 0;
    }

    public void AddCard(int team, int code, uint location, uint sequence = 0, uint position = Ocg.PosFaceDownDefense)
    {
        var info = new NewCardInfo
        {
            Team = (byte)team,
            Duelist = 0,
            Code = (uint)code,
            Controller = (byte)team,
            Location = location,
            Sequence = sequence,
            Position = position,
        };
        OcgApi.OCG_DuelNewCard(_handle, ref info);
    }

    public void Start() => OcgApi.OCG_StartDuel(_handle);

    public int Process() => OcgApi.OCG_DuelProcess(_handle);

    public byte[] GetMessages()
    {
        var ptr = OcgApi.OCG_DuelGetMessage(_handle, out uint length);
        return OcgApi.Copy(ptr, length);
    }

    public void SetResponse(byte[] response) => OcgApi.OCG_DuelSetResponse(_handle, response, (uint)response.Length);

    public uint QueryCount(int team, uint location) => OcgApi.OCG_DuelQueryCount(_handle, (byte)team, location);

    public byte[] Query(int controller, uint location, uint sequence, uint flags, uint overlaySequence = 0)
    {
        var info = new QueryInfo { Flags = flags, Controller = (byte)controller, Location = location, Sequence = sequence, OverlaySequence = overlaySequence };
        var ptr = OcgApi.OCG_DuelQuery(_handle, out uint length, ref info);
        return OcgApi.Copy(ptr, length);
    }

    public byte[] QueryLocation(int controller, uint location, uint flags)
    {
        var info = new QueryInfo { Flags = flags, Controller = (byte)controller, Location = location };
        var ptr = OcgApi.OCG_DuelQueryLocation(_handle, out uint length, ref info);
        return OcgApi.Copy(ptr, length);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            OcgApi.OCG_DestroyDuel(_handle);
            _handle = IntPtr.Zero;
        }
        if (_self.IsAllocated)
            _self.Free();
    }

    private static OcgDuel From(IntPtr payload) => (OcgDuel)GCHandle.FromIntPtr(payload).Target!;

    private static void ReadCard(IntPtr payload, uint code, IntPtr data)
    {
        var duel = From(payload);
        var card = duel._cards.Get((int)code);
        Marshal.WriteInt32(data, CardDataLayout.Code, (int)code);
        if (card == null)
        {
            duel.EngineLog?.Invoke($"card data missing for {code}");
            for (int offset = 4; offset < 52; offset += 4)
                Marshal.WriteInt32(data, offset, 0);
            Marshal.WriteIntPtr(data, CardDataLayout.SetCodes, AllocSetCodes(Array.Empty<ushort>()));
            return;
        }
        Marshal.WriteInt32(data, CardDataLayout.Alias, card.Alias);
        Marshal.WriteIntPtr(data, CardDataLayout.SetCodes, AllocSetCodes(card.SetCodes().ToArray()));
        Marshal.WriteInt32(data, CardDataLayout.Type, card.Type);
        Marshal.WriteInt32(data, CardDataLayout.Level, card.Level);
        Marshal.WriteInt32(data, CardDataLayout.Attribute, card.Attribute);
        Marshal.WriteInt64(data, CardDataLayout.Race, card.Race);
        Marshal.WriteInt32(data, CardDataLayout.Attack, card.Attack);
        Marshal.WriteInt32(data, CardDataLayout.Defense, card.RealDefense);
        Marshal.WriteInt32(data, CardDataLayout.LScale, card.LScale);
        Marshal.WriteInt32(data, CardDataLayout.RScale, card.RScale);
        Marshal.WriteInt32(data, CardDataLayout.LinkMarker, card.LinkMarker);
    }

    private static IntPtr AllocSetCodes(ushort[] codes)
    {
        // Zero-terminated uint16 array; freed in ReadCardDone once the engine has copied it.
        var ptr = Marshal.AllocHGlobal((codes.Length + 1) * sizeof(ushort));
        for (int i = 0; i < codes.Length; i++)
            Marshal.WriteInt16(ptr, i * 2, (short)codes[i]);
        Marshal.WriteInt16(ptr, codes.Length * 2, 0);
        return ptr;
    }

    private static void ReadCardDone(IntPtr payload, IntPtr data)
    {
        var setCodes = Marshal.ReadIntPtr(data, CardDataLayout.SetCodes);
        if (setCodes != IntPtr.Zero)
            Marshal.FreeHGlobal(setCodes);
    }

    private static int ReadScript(IntPtr payload, IntPtr duelHandle, IntPtr namePtr)
    {
        var duel = From(payload);
        var name = Marshal.PtrToStringAnsi(namePtr) ?? string.Empty;
        var bytes = duel._scripts.Read(name);
        if (bytes == null)
            return 0;
        return OcgApi.OCG_LoadScript(duelHandle, bytes, (uint)bytes.Length, name);
    }

    private static void HandleLog(IntPtr payload, IntPtr text, int type)
    {
        var duel = From(payload);
        var message = Marshal.PtrToStringAnsi(text) ?? string.Empty;
        string kind = type switch { 0 => "error", 1 => "script", 2 => "debug", _ => "log" };
        duel.EngineLog?.Invoke($"[{kind}] {message}");
    }
}
