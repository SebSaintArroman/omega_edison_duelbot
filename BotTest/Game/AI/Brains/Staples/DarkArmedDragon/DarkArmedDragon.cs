using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Dark Armed Dragon as a shared staple (user, 2026-11-13: "allure rules can be moved to staples, along with DAD"): the DARK
    /// count in our graveyard and what can be spared. Its rules are in Staples/DarkArmedDragon/Rules (the trim to exactly 3
    /// DARKs, and when its effect is worth a DARK); its targets are in DeckBotBrain.Targeting; its summon stays with each deck.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>The turn Plaguespreader left the graveyard to trim the DARK count for DAD (its summon follows).</summary>
        protected int PlagueForDadTurn { get; set; } = -1;

        protected async Task<int> DarksInGraveAsync() =>
            (await GetBotGraveyardCardsAsync()).Count(card => card.HasType(CardType.Monster) && (card.Attribute & (int)CardAttribute.Dark) != 0);

        protected async Task<bool> DadInHandAsync() =>
            (await GetBotHandCardsAsync()).Any(card => card.IsCode(DarkArmedDragon));

        /// <summary>
        /// Whether Dark Armed Dragon's effect (banish a DARK from the graveyard: destroy a card) is worth it now. Free while the
        /// graveyard has DARKs with no graveyard effect of their own; with only play pieces left (Vayu and the partners it needs,
        /// Plaguespreader, Necro Gardna), only to clear a likely Mirror Force before DAD attacks, on a big threat, or when holding
        /// back loses. Never while another DAD in hand needs the 3 DARKs. Returns the reason, or null (held).
        /// </summary>
        /// <summary>
        /// DARKs in our graveyard Dark Armed Dragon can banish at no real cost (not Vayu and the partners it needs,
        /// Plaguespreader, Necro Gardna): the same count DadEffectWhyAsync uses, without its notes.
        /// </summary>
        protected async Task<int> DadSpareDarksAsync()
        {
            var grave = await GetBotGraveyardCardsAsync();
            var darks = grave.Where(card => card.HasType(CardType.Monster) && (card.Attribute & (int)CardAttribute.Dark) != 0).ToList();
            int vayus = darks.Count(card => card.IsCode(BlackwingVayuTheEmblemOfHonor));
            int keptPartners = Math.Min(grave.Count(card => card.IsCode(VayuPartnerIds)), vayus);
            int playPieces = vayus + keptPartners + darks.Count(card => card.IsCode(PlaguespreaderZombie, NecroGardna));
            return Math.Max(0, darks.Count - playPieces);
        }
    }
}
