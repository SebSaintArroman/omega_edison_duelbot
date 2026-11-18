using System.IO.Compression;

namespace DuelSim.Data;

public sealed record DeckList(string Name, IReadOnlyList<int> Main, IReadOnlyList<int> Extra, IReadOnlyList<int> Side);

/// <summary>
/// Decodes Omega deck codes (the DeckHash strings in BotTest's DeckManager): base64 of a raw-deflated block holding
/// the main/extra(/side) counts followed by the 32-bit card ids.
/// </summary>
public static class DeckCode
{
    public static byte[] Inflate(string code)
    {
        var compressed = Convert.FromBase64String(code.Trim().Replace('-', '+').Replace('_', '/'));
        using var input = new MemoryStream(compressed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Packs a deck block back into a deck code: raw deflate, then base64 (the reverse of <see cref="Inflate"/>).</summary>
    public static string Deflate(byte[] block)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(block, 0, block.Length);
        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>
    /// A deck code with cards added to or removed from the main/extra part (the side part and the trailing cover id stay as
    /// they are). Ids are Omega's own (copy them from a decoded deck when a card has an Omega-only id).
    /// </summary>
    public static string Edit(string code, IEnumerable<int> add, IEnumerable<int> remove)
    {
        var bytes = Inflate(code);
        int mainCount = bytes[0];
        int sideCount = bytes[1];
        int idCount = (bytes.Length - 2) / 4;
        var ids = Enumerable.Range(0, idCount).Select(i => BitConverter.ToInt32(bytes, 2 + i * 4)).ToList();
        var main = ids.Take(mainCount).ToList();
        var rest = ids.Skip(mainCount).ToList(); // side cards, then the cover id
        foreach (int id in remove)
            if (!main.Remove(id))
                throw new InvalidDataException($"Card {id} isn't in the deck's main/extra part.");
        main.AddRange(add);
        if (main.Count > 255)
            throw new InvalidDataException("A deck code holds at most 255 main/extra cards.");
        var block = new List<byte> { (byte)main.Count, (byte)sideCount };
        foreach (int id in main.Concat(rest))
            block.AddRange(BitConverter.GetBytes(id));
        return Deflate(block.ToArray());
    }

    public static DeckList Decode(string name, string code, CardStore cards, ScriptStore scripts, Action<string>? note = null)
    {
        var deck = Decode(name, code, cards);
        foreach (var line in OmegaIds.Resolve(deck.Main.Concat(deck.Extra).Concat(deck.Side), cards, scripts))
            note?.Invoke($"{name}: Omega id {line}");
        // Re-split now that the Omega-only ids have card data.
        return Decode(name, code, cards);
    }

    public static DeckList Decode(string name, string code, CardStore cards)
    {
        var bytes = Inflate(code);
        // Layout: [main+extra count][side count] then the ids (main and extra mixed, side after), then one more id
        // that isn't a deck card (it would be a 4th copy in every deck checked; probably the deck's cover card).
        // Main and extra are split by card type.
        int mainCount = bytes[0];
        int sideCount = bytes[1];
        int idCount = (bytes.Length - 2) / 4;
        if (mainCount + sideCount > idCount)
            throw new InvalidDataException($"Deck code for {name}: header says {mainCount}+{sideCount} cards but holds {idCount} ids.");
        var ids = new List<int>(mainCount + sideCount);
        for (int i = 0; i < mainCount + sideCount; i++)
            ids.Add(BitConverter.ToInt32(bytes, 2 + i * 4));

        var main = new List<int>();
        var extra = new List<int>();
        foreach (var id in ids.Take(mainCount))
        {
            if (cards.Get(id) is { IsExtraDeckMonster: true })
                extra.Add(id);
            else
                main.Add(id);
        }
        return new DeckList(name, main, extra, ids.Skip(mainCount).ToList());
    }
}
