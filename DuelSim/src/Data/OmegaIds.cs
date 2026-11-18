namespace DuelSim.Data;

/// <summary>
/// Omega has its own ids for some cards that EDOPro doesn't know, mainly pre-errata versions (e.g. Lyla 22624393,
/// Ryko 21502816, Goyo Guardian 7391468, Brionac 50321816). Omega numbers them official id + 20 and reports the official
/// id as their Alias. EDOPro has no pre-errata scripts for these, so the simulator plays them as the official card:
/// a copy of the official card's data under the Omega id (Alias = official id, just as Omega reports it) and a stub
/// script that loads the official script (Duel.LoadCardScriptAlias). The bots still see the Omega id.
/// Rules difference: the card plays with its current (errata'd) text.
/// </summary>
public static class OmegaIds
{
    private const int OmegaOffset = 20;

    /// <summary>Registers every unknown id in the deck that maps to an official card. Returns "omega -> official" notes.</summary>
    public static List<string> Resolve(IEnumerable<int> ids, CardStore cards, ScriptStore scripts)
    {
        var notes = new List<string>();
        foreach (int id in ids.Distinct())
        {
            bool known = cards.Get(id) != null;
            bool scripted = scripts.Read($"c{id}.lua") != null || cards.Get(id) is { Type: var t } && (t & 0x10) != 0 && (t & 0x20) == 0;
            if (known && scripted)
                continue;
            int official = cards.Get(id)?.Alias is > 0 and var alias ? alias : id - OmegaOffset;
            if (cards.Get(official) is not { } officialCard || scripts.Read($"c{official}.lua") == null)
                continue;
            cards.AddAlias(id, officialCard);
            if (scripts.Read($"c{id}.lua") != null)
            {
                // A hand-written pre-errata script (DuelSim/scripts/pre-errata).
                notes.Add($"{id} -> {officialCard.Name} ({official}), pre-errata script");
                continue;
            }
            scripts.AddVirtual($"c{id}.lua", $"--Omega id {id}: plays as {officialCard.Name} ({official})\nDuel.LoadCardScriptAlias({official})\n");
            notes.Add($"{id} -> {officialCard.Name} ({official}), current text (no pre-errata script)");
        }
        return notes;
    }
}
