using OmegaBotWrapper.Enum;

namespace DuelSim.Engine;

/// <summary>
/// A decision the engine is waiting on, in the shape Omega's API gives it to a bot: a BotState plus the matching
/// *Data struct (IdleCmdData, SelectCardData...). Translate turns the bot's response packet into the engine's response
/// format; Fallbacks are safe answers used when the bot fails to answer or answers something the engine rejects.
/// </summary>
internal sealed class Prompt
{
    public required int Player { get; init; }
    public required BotState State { get; init; }
    public required byte MessageId { get; init; }
    public required object Data { get; init; }
    public required Func<byte[], byte[]?> Translate { get; init; }
    public List<byte[]> Fallbacks { get; init; } = new();
    /// <summary>Answered by the simulator without asking the bot (Omega never shows these).</summary>
    public byte[]? Auto { get; init; }
    public string Summary { get; init; } = string.Empty;

    public int Attempts { get; set; }
    public TaskCompletionSource<byte[]> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Builders for engine response buffers.</summary>
internal static class Responses
{
    public static byte[] Int32(int value) => BitConverter.GetBytes(value);

    public static byte[] Int64(long value) => BitConverter.GetBytes(value);

    /// <summary>Card list response (parse_response_cards type 0): [i32 0][u32 count][u32 index...]. Empty = cancel (-1).</summary>
    public static byte[] Cards(IReadOnlyList<int> indexes)
    {
        if (indexes.Count == 0)
            return Int32(-1);
        var bytes = new byte[8 + indexes.Count * 4];
        BitConverter.GetBytes(0).CopyTo(bytes, 0);
        BitConverter.GetBytes((uint)indexes.Count).CopyTo(bytes, 4);
        for (int i = 0; i < indexes.Count; i++)
            BitConverter.GetBytes((uint)indexes[i]).CopyTo(bytes, 8 + i * 4);
        return bytes;
    }

    /// <summary>The bot's [u8 count][u8 index...] packet, as a list.</summary>
    public static List<int> ReadByteList(byte[] packet)
    {
        var list = new List<int>();
        if (packet.Length == 0)
            return list;
        int count = packet[0];
        for (int i = 0; i < count && 1 + i < packet.Length; i++)
            list.Add(packet[1 + i]);
        return list;
    }

    public static int ReadInt(byte[] packet)
    {
        var padded = new byte[4];
        Array.Copy(packet, padded, Math.Min(4, packet.Length));
        return BitConverter.ToInt32(padded, 0);
    }
}
