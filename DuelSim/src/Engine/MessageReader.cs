using DuelSim.Native;

namespace DuelSim.Engine;

/// <summary>Card location as the engine writes it (loc_info): absolute controller.</summary>
internal readonly record struct LocInfo(byte Controller, byte Location, uint Sequence, uint Position)
{
    public (byte, byte, uint) Key => (Controller, Location, Sequence);
}

/// <summary>Little-endian reader over one engine message or query buffer.</summary>
internal sealed class MessageReader
{
    private readonly byte[] _buffer;
    private int _position;
    private readonly int _end;

    public MessageReader(byte[] buffer, int start = 0, int? length = null)
    {
        _buffer = buffer;
        _position = start;
        _end = start + (length ?? buffer.Length - start);
    }

    public int Position => _position;
    public int Remaining => _end - _position;

    private void Need(int count)
    {
        if (_position + count > _end)
            throw new InvalidDataException($"Engine message too short: need {count} bytes at {_position}, end {_end}.");
    }

    public byte U8() { Need(1); return _buffer[_position++]; }
    public ushort U16() { Need(2); var v = BitConverter.ToUInt16(_buffer, _position); _position += 2; return v; }
    public short I16() { Need(2); var v = BitConverter.ToInt16(_buffer, _position); _position += 2; return v; }
    public uint U32() { Need(4); var v = BitConverter.ToUInt32(_buffer, _position); _position += 4; return v; }
    public int I32() { Need(4); var v = BitConverter.ToInt32(_buffer, _position); _position += 4; return v; }
    public ulong U64() { Need(8); var v = BitConverter.ToUInt64(_buffer, _position); _position += 8; return v; }
    public void Skip(int count) { Need(count); _position += count; }

    /// <summary>loc_info: controller u8, location u8, sequence u32, position u32.</summary>
    public LocInfo Loc() => new(U8(), U8(), U32(), U32());
}

/// <summary>One card's state from OCG_DuelQuery / OCG_DuelQueryLocation.</summary>
internal sealed class CardQuery
{
    public int Code;
    public uint Position;
    public int Alias;
    public int Type;
    public int Level;
    public int Rank;
    public int Attribute;
    public long Race;
    public int Attack;
    public int Defense;
    public int BaseAttack;
    public int BaseDefense;
    public uint Reason;
    public LocInfo? ReasonCard;
    public LocInfo? EquipTarget;
    public LocInfo[] Targets = Array.Empty<LocInfo>();
    public int[] Overlays = Array.Empty<int>();
    public (ushort Type, ushort Count)[] Counters = Array.Empty<(ushort, ushort)>();
    public int Owner;
    public uint Status;
    public bool IsPublic;
    public int LScale;
    public int RScale;
    public int LinkRating;
    public int LinkMarker;

    /// <summary>Reads one card. Returns null for an empty slot (a bare zero length).</summary>
    public static CardQuery? Read(MessageReader reader)
    {
        ushort first = reader.U16();
        if (first == 0)
            return null;
        var card = new CardQuery();
        ushort length = first;
        while (true)
        {
            int start = reader.Position;
            uint flag = reader.U32();
            if (flag == Ocg.QueryEnd)
                break;
            switch (flag)
            {
                case Ocg.QueryCode: card.Code = reader.I32(); break;
                case Ocg.QueryPosition: card.Position = reader.U32(); break;
                case Ocg.QueryAlias: card.Alias = reader.I32(); break;
                case Ocg.QueryType: card.Type = reader.I32(); break;
                case Ocg.QueryLevel: card.Level = reader.I32(); break;
                case Ocg.QueryRank: card.Rank = reader.I32(); break;
                case Ocg.QueryAttribute: card.Attribute = reader.I32(); break;
                case Ocg.QueryRace: card.Race = (long)reader.U64(); break;
                case Ocg.QueryAttack: card.Attack = reader.I32(); break;
                case Ocg.QueryDefense: card.Defense = reader.I32(); break;
                case Ocg.QueryBaseAttack: card.BaseAttack = reader.I32(); break;
                case Ocg.QueryBaseDefense: card.BaseDefense = reader.I32(); break;
                case Ocg.QueryReason: card.Reason = reader.U32(); break;
                case Ocg.QueryReasonCard: card.ReasonCard = ReadOptionalLoc(reader); break;
                case Ocg.QueryEquipCard: card.EquipTarget = ReadOptionalLoc(reader); break;
                case Ocg.QueryTargetCard:
                {
                    uint count = reader.U32();
                    if (count != 0)
                        card.Targets = new LocInfo[count];
                    for (int i = 0; i < count; i++)
                        card.Targets[i] = reader.Loc();
                    break;
                }
                case Ocg.QueryOverlayCard:
                {
                    uint count = reader.U32();
                    if (count != 0)
                        card.Overlays = new int[count];
                    for (int i = 0; i < count; i++)
                        card.Overlays[i] = reader.I32();
                    break;
                }
                case Ocg.QueryCounters:
                {
                    uint count = reader.U32();
                    if (count != 0)
                        card.Counters = new (ushort, ushort)[count];
                    for (int i = 0; i < count; i++)
                    {
                        uint packed = reader.U32();
                        card.Counters[i] = ((ushort)(packed & 0xffff), (ushort)(packed >> 16));
                    }
                    break;
                }
                case Ocg.QueryOwner: card.Owner = reader.U8(); break;
                case Ocg.QueryStatus: card.Status = reader.U32(); break;
                case Ocg.QueryIsPublic: card.IsPublic = reader.U8() != 0; break;
                case Ocg.QueryLScale: card.LScale = reader.I32(); break;
                case Ocg.QueryRScale: card.RScale = reader.I32(); break;
                case Ocg.QueryLink: card.LinkRating = reader.I32(); card.LinkMarker = reader.I32(); break;
                default:
                    reader.Skip(length - 4);
                    break;
            }
            // Keep in step with the declared length whatever the field held.
            int consumed = reader.Position - start;
            if (consumed < length)
                reader.Skip(length - consumed);
            length = reader.U16();
        }
        return card;
    }

    private static LocInfo? ReadOptionalLoc(MessageReader reader)
    {
        var loc = reader.Loc();
        return loc.Location == 0 ? null : loc;
    }

    /// <summary>Reads an OCG_DuelQueryLocation buffer: u32 size, then one entry per slot (null = empty).</summary>
    public static List<CardQuery?> ReadLocation(byte[] buffer, int count) => ReadLocation(buffer, count, Read);

    public static List<int?> ReadLocationCodes(byte[] buffer, int count) => ReadLocation(buffer, count, ReadCode);

    private static int? ReadCode(MessageReader reader)
    {
        ushort length = reader.U16();
        if (length == 0)
            return null;
        int code = 0;
        while (true)
        {
            uint flag = reader.U32();
            if (flag == Ocg.QueryEnd)
                return code;
            if (length < 4 || (flag == Ocg.QueryCode && length < 8))
                throw new InvalidDataException("Invalid engine card query field length.");
            if (flag == Ocg.QueryCode)
            {
                code = reader.I32();
                reader.Skip(length - 8);
            }
            else
                reader.Skip(length - 4);
            length = reader.U16();
        }
    }

    private static List<T> ReadLocation<T>(byte[] buffer, int count, Func<MessageReader, T> read)
    {
        var result = new List<T>();
        if (count < 4)
            return result;
        uint size = BitConverter.ToUInt32(buffer, 0);
        var inner = new MessageReader(buffer, 4, (int)Math.Min(size, (uint)count - 4));
        while (inner.Remaining >= 2)
            result.Add(read(inner));
        return result;
    }
}
