using Microsoft.Data.Sqlite;
using OmegaBotWrapper.Struct;

namespace DuelSim.Data;

/// <summary>One card's static data, as stored in an EDOPro .cdb (BabelCDB).</summary>
public sealed record CardRecord(
    int Id, int Ot, int Alias, long SetCode, int Type, int Attack, int Defense, int LevelRaw, long Race, int Attribute,
    long Category, string Name, string Text)
{
    public int Level => LevelRaw & 0xff;
    public int LScale => (LevelRaw >> 24) & 0xff;
    public int RScale => (LevelRaw >> 16) & 0xff;
    public bool IsLink => (Type & 0x4000000) != 0;
    public int LinkMarker => IsLink ? Defense : 0;
    public int RealDefense => IsLink ? 0 : Defense;
    public bool IsExtraDeckMonster => (Type & (0x40 | 0x2000 | 0x800000 | 0x4000000)) != 0;

    public IEnumerable<ushort> SetCodes()
    {
        for (int i = 0; i < 4; i++)
        {
            var code = (ushort)((SetCode >> (i * 16)) & 0xffff);
            if (code != 0)
                yield return code;
        }
    }

    /// <summary>The card as Omega's /card-list endpoint describes it.</summary>
    public DBCardInfo ToDbCardInfo() => new()
    {
        ID = Id,
        OT = (byte)Ot,
        Alias = Alias,
        SetCode = SetCode,
        Type = Type,
        Level = (sbyte)Level,
        LScale = (sbyte)LScale,
        RScale = (sbyte)RScale,
        LinkMarker = (ushort)LinkMarker,
        Category = (int)Category,
        Attribute = (byte)Attribute,
        Race = (int)Race,
        Attack = Attack,
        Defense = RealDefense,
        SetID = string.Empty,
        Name = Name,
        CardText = Text,
        PendulumText = string.Empty,
    };
}

/// <summary>Every card in one or more .cdb files, loaded into memory once.</summary>
public sealed class CardStore
{
    // Concurrent: search duels read cards (engine callbacks) on several threads while Omega-only ids get aliases added.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, CardRecord> _cards = new();

    public int Count => _cards.Count;

    public static CardStore Load(params string[] cdbPaths)
    {
        var store = new CardStore();
        foreach (var path in cdbPaths.Where(File.Exists))
            store.LoadFile(path);
        if (store.Count == 0)
            throw new FileNotFoundException("No card database found. Run setup.ps1 first.", string.Join(", ", cdbPaths));
        return store;
    }

    private void LoadFile(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT d.id, d.ot, d.alias, d.setcode, d.type, d.atk, d.def, d.level, d.race, d.attribute, d.category, t.name, t.desc " +
            "FROM datas d LEFT JOIN texts t ON t.id = d.id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var record = new CardRecord(
                Id: (int)reader.GetInt64(0),
                Ot: (int)reader.GetInt64(1),
                Alias: (int)reader.GetInt64(2),
                SetCode: reader.GetInt64(3),
                Type: (int)reader.GetInt64(4),
                Attack: (int)reader.GetInt64(5),
                Defense: (int)reader.GetInt64(6),
                LevelRaw: (int)reader.GetInt64(7),
                Race: reader.GetInt64(8),
                Attribute: (int)reader.GetInt64(9),
                Category: reader.GetInt64(10),
                Name: reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                Text: reader.IsDBNull(12) ? string.Empty : reader.GetString(12));
            _cards[record.Id] = record;
        }
    }

    public CardRecord? Get(int id) => _cards.TryGetValue(id, out var card) ? card : null;

    /// <summary>Adds a card under another id that plays as <paramref name="official"/> (see OmegaIds).</summary>
    public void AddAlias(int id, CardRecord official) =>
        _cards[id] = official with { Id = id, Alias = official.Id };

    public IEnumerable<CardRecord> All => _cards.Values;
}
