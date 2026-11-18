#pragma warning disable CS8618
namespace BotTest.Decks
{
    /// <summary>
    /// Represents a deck option with an ID and name.
    /// </summary>
    public class DeckOption
    {
        public object Id { get; set; }
        public string Name { get; set; }
    }

    /// <summary>
    /// Enum to identify available Master Duel decks.
    /// </summary>
    public enum MasterDuelDeck
    {
        Vayu,
        MachinaControl,
        HeroFrog,
        Blackwing,
        GladiatorControl,
        MachinaJinzo,
        BlackwingHybrid
    }

    /// <summary>
    /// Manages deck names and hashes, providing access to deck data and options.
    /// </summary>
    /// <remarks>
    /// Every Master Duel bot deck holds 3x Pot of Greed (55144522), new decks included (user, 2026-11-12): add them to a new
    /// hash with <c>DuelSim deck-edit &lt;hash&gt; +55144522 +55144522 +55144522</c>, no need to rebuild the deck on Omega.
    /// The bots activate Pot of Greed at the first chance (DeckBotBrain.RefineIdleAsync), then search afresh.
    /// </remarks>
    public static class DeckManager
    {
        private static readonly Dictionary<MasterDuelDeck, (string DeckName, string DeckHash)> _decks = new()
        {
            {
                MasterDuelDeck.Vayu,
                (
                    DeckName: "Miracle Vayu",
                    DeckHash: "s2KMPvCIuShnCdO9nEkM65kbWGA4+exdBhjutIhk/Oy+C45t73oxqAQLsep9/suYIO0BxjM6QllguOvmctYSd23GD6cSGEB42ewvjPn7DFlgeGfwa6b36oEMcYsPMILwvVBDFhCucWRkBWGhmwuYfs8yZO43vs3sVeAJx0GfdrDUqP9mBGGnzkzG39M9WPcpL2diD5VlAeHt+5NY3pwoYLCfO5UFhmfc/M9kc2oqY73pRrA4AA=="
                )
            },
            {
                MasterDuelDeck.MachinaControl,
                (
                    DeckName: "Machina Control",
                    DeckHash: "s2L461PAUHatlBmEi3KWMAmfDGGAYaOJuiwgzGHRzwTDnRaRjBU5xgz+K/3BWCVYiNW5eTULCOt9/suYIO0Bx37fS8G46+Zy1hJ3bUbpG+YMaq8ZWEDYq8CTGYbfqwcyxC0+wAjCks5zWe+FGrKAsNDNBUy/Zxky9xvfZv7NUsEkuO0mc9CnHSwgzHpFjvn6Y0sWp85Mxu8sFiy/p3uw7lNezrR9fxLLmxMFDDNu/meKnGPMfJVpGpPNqamMIH8CAA=="
                )
            },
            {
                MasterDuelDeck.HeroFrog,
                (
                    DeckName: "Hero Frog Monarchs",
                    DeckHash: "s2IsylnCJHBwCgMMF56NZgHhwHv9jCDcxZnMFDrBnMnA7ALzY8duBhiOnPqfqaT/IBiz+FozwjBb0H4GEDYV0WEMy7Jl9XNkZihx12b8U7mHCYYnsCkzLJv9hTF/nyFLG88NVhCWdJ7LWuPIyKp1yJ4BhO3OJjP0G99m9irwhOOTSflMMBz0aQdLxpkFjE6dmYz7lJczbd+fxBIWdYzlzYkChhk3/zNFzjFmrt07n9nm1FTGH9N3M771DGcG+QcA"
                )
            },
            {
                MasterDuelDeck.Blackwing,
                (
                    DeckName: "Blackwing",
                    DeckHash: "s2KMPvCIuShnCdN65gYWGP7a0cgMw0f2vGaC4UceIqwwzLtCjuFkfgwjDM/oCGV53yTGEJZly1rirs3oVeDJDMPv1QMZvF4WsoJw3OIDjCB8L9SQBYS3nprCXB5nCMc1joysMCx0cwHT71mGzP3Gt5l/s1QwBX3awVKj/psRhJ06Mxl/T/dg3ae8nIk9VJYFhLfvT2J5c6KAwX7uVBYYnnHzP5PNqamM9aYbweIA"
                )
            },
            {
                MasterDuelDeck.GladiatorControl,
                (
                    DeckName: "Gladiator Control",
                    DeckHash: "M2XUKNzMAsO6U7oZYbjTIpKx5WUBMwjvPFHBJBzOyQDC77SjGYLE17OCsEqwEGuCtAcjCHfdXM5a4q7N6FXgyQzDSutPsebvM2QB4Sf+nszv1QMZ4hYfYAThe6GGLCAsdHMBk9YhewYY3smSygLCv2cZMlev7mX2jTvAYO15knWpyh7WM24LGUE46NMOFhjec+4eEwzvU14Oxv3Gt5lB9gEA"
                )
            },
            {
                MasterDuelDeck.MachinaJinzo,
                (
                    DeckName: "Machina Jinzo",
                    DeckHash: "s2Iou1bKHH3gEXNRzhIm4ZMhDDCceWc+CwwbTdQFYw6LfiYY7rSIZPzsvguO1bb3M8CwSrAQq97nv4wJ0h5g/L5JjCEsy5a16+Zy1hJ3bUavAk9mGNbonc8St/gAIwjfCzVkAeEaR0ZWGBa6uYDp9yxD5n7j28yC224yB33awQLCrFfkmK8/tmRx6sxk/M5iwfJ7ugfrPuXlTNv3J7G8OVHAMOPmf6bIOcbMV5mmMdmcmsoI8gcA"
                )
            },
            {
                MasterDuelDeck.BlackwingHybrid,
                (
                    DeckName: "Blackwing Hybrid",
                    DeckHash: "s2KMPvCIeT1zAwsMf+1oZIbhI3teM4Fw8tm7DCD82X0X4yMPEVYQ5l0hx6ASLMR6Mj+GEYRndISywPD7JjGGsCxb1hJ3bUavAk9mGF42+wvjzuDXTF4vC1lBOG7xAUYQvhdqyFIeZ8gMw6HnLzAK3VzA1Ld0HuPvWYbM/ca3mX+zVDAFfdrBUqP+mxGEnTozGX9P92Ddp7yciT1UlgWEt+9PYnlzooDBfu5UFhiecfM/k82pqYz1phsZQGoA"
                )
            }
        };

        /// <summary>
        /// Gets the deck name for the specified Master Duel deck ID.
        /// </summary>
        /// <param name="deckId">The deck ID.</param>
        /// <returns>The deck name.</returns>
        /// <exception cref="KeyNotFoundException">Thrown if the deck ID is not found.</exception>
        public static string GetDeckName(MasterDuelDeck deckId)
        {
            return _decks[deckId].DeckName;
        }

        /// <summary>
        /// Gets the deck hash for the specified Master Duel deck ID.
        /// </summary>
        /// <param name="deckId">The deck ID.</param>
        /// <returns>The deck hash.</returns>
        /// <exception cref="KeyNotFoundException">Thrown if the deck ID is not found.</exception>
        public static string GetDeckHash(MasterDuelDeck deckId)
        {
            return _decks[deckId].DeckHash;
        }

        /// <summary>
        /// Gets a list of available Master Duel deck options for selection.
        /// </summary>
        /// <returns>A list of DeckOption objects containing DeckId and DeckName.</returns>
        public static IEnumerable<DeckOption> GetMasterDuelDeckOptions()
        {
            return _decks.Select(d => new DeckOption { Id = d.Key, Name = d.Value.DeckName });
        }
    }
}
