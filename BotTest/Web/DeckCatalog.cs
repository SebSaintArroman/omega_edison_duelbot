using BotTest.Decks;
using BotTest.Game.AI.Bots.MasterDuel.Blackwing;
using BotTest.Game.AI.Bots.MasterDuel.GladiatorBeasts;
using BotTest.Game.AI.Bots.MasterDuel.HeroFrog;
using BotTest.Game.AI.Bots.MasterDuel.Machina;
using BotTest.Game.AI.Bots.MasterDuel.Vayu;
using BotTest.Game.AI.Heuristics;
using OmegaBotWrapper;
using OmegaBotWrapper.Heuristics;

namespace BotTest.Web;

public static class DeckCatalog
{
    public static readonly IReadOnlyList<DeckDefinition> Definitions =
    [
        CreateBrain("MasterDuel", "Master Duel", MasterDuelDeck.Vayu.ToString(), DeckManager.GetDeckName(MasterDuelDeck.Vayu), DeckManager.GetDeckHash(MasterDuelDeck.Vayu), () => new VayuBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator()) { Variant = VayuVariant.Standard }),
        CreateBrain("MasterDuel", "Master Duel", MasterDuelDeck.MachinaControl.ToString(), DeckManager.GetDeckName(MasterDuelDeck.MachinaControl), DeckManager.GetDeckHash(MasterDuelDeck.MachinaControl), () => new MachinaBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator()) { Variant = MachinaVariant.Control }),
        CreateBrain("MasterDuel", "Master Duel", MasterDuelDeck.HeroFrog.ToString(), DeckManager.GetDeckName(MasterDuelDeck.HeroFrog), DeckManager.GetDeckHash(MasterDuelDeck.HeroFrog), () => new HeroFrogBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator()) { Variant = HeroFrogVariant.Standard }),
        CreateBrain("MasterDuel", "Master Duel", MasterDuelDeck.Blackwing.ToString(), DeckManager.GetDeckName(MasterDuelDeck.Blackwing), DeckManager.GetDeckHash(MasterDuelDeck.Blackwing), () => new BlackwingBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator()) { Variant = BlackwingVariant.Standard }),
        // BlackwingHybrid: Blackwings leaning into the Vayu engine (Armageddon Knight, Dark Grepher, 3 Vayu, Burial, Return
        // from the Different Dimension). The Blackwing brain holds the Vayu brain's rules too; the variant flag tilts it.
        CreateBrain("MasterDuel", "Master Duel", MasterDuelDeck.BlackwingHybrid.ToString(), DeckManager.GetDeckName(MasterDuelDeck.BlackwingHybrid), DeckManager.GetDeckHash(MasterDuelDeck.BlackwingHybrid), () => new BlackwingBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator()) { Variant = BlackwingVariant.Hybrid }),
        CreateBrain("MasterDuel", "Master Duel", MasterDuelDeck.GladiatorControl.ToString(), DeckManager.GetDeckName(MasterDuelDeck.GladiatorControl), DeckManager.GetDeckHash(MasterDuelDeck.GladiatorControl), () => new GladiatorBeastsBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator()) { Variant = GladVariant.Standard }),
        // A Machina variant (Jinzo, Armageddon Knight, Dark Armed Dragon): the same brain, its rules follow the list.
        CreateBrain("MasterDuel", "Master Duel", MasterDuelDeck.MachinaJinzo.ToString(), DeckManager.GetDeckName(MasterDuelDeck.MachinaJinzo), DeckManager.GetDeckHash(MasterDuelDeck.MachinaJinzo), () => new MachinaBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator()) { Variant = MachinaVariant.Jinzo }),
    ];

    public static IReadOnlyList<FormatOption> GetFormatOptions()
    {
        return Definitions
            .GroupBy(definition => new { definition.FormatId, definition.FormatName })
            .Select(group => new FormatOption(
                group.Key.FormatId,
                group.Key.FormatName,
                group.Select(definition => new DeckOptionDto(definition.DeckId, definition.DeckName, definition.AssetDeckId)).ToArray()))
            .ToArray();
    }

    public static DuelBotWrapper CreateBot(string formatId, string deckId)
    {
        var definition = Definitions.FirstOrDefault(item =>
            item.FormatId.Equals(formatId, StringComparison.OrdinalIgnoreCase) &&
            item.DeckId.Equals(deckId, StringComparison.OrdinalIgnoreCase));

        if (definition is null)
            throw new InvalidOperationException($"No bot implementation found for format '{formatId}' and deck '{deckId}'.");

        return definition.Factory();
    }

    private static DeckDefinition CreateBrain(
        string formatId,
        string formatName,
        string deckId,
        string deckName,
        string deckHash,
        Func<IBotBrain> brainFactory) =>
        new(formatId, formatName, deckId, deckName, deckId, () => new DuelBotWrapper(deckName, deckHash, brainFactory()));
}

public sealed record DeckDefinition(
    string FormatId,
    string FormatName,
    string DeckId,
    string DeckName,
    string AssetDeckId,
    Func<DuelBotWrapper> Factory);
