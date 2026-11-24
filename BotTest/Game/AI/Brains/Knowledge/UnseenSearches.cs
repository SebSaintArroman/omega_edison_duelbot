using BotTest.Game.Field;
using BotTest.Utils;
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
    /// Cards they searched without showing us (a searcher resolved, their hand grew): the deck read counts them.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// Their searchers whose search we don't get to see (the card goes to their hand unrevealed to the bot), and what
        /// each can fetch. Reinforcement of the Army: a Level 4 or lower Warrior.
        /// </summary>
        private static readonly (int Id, string Name, Func<DBCardInfo, bool> Fetches)[] UnseenSearchers =
        {
            (ReinforcementOfTheArmy, "Reinforcement of the Army", info => (info.Type & (int)CardType.Monster) != 0 && (info.Race & (int)CardRace.Warrior) != 0 && info.Level is > 0 and <= 4),
        };

        private EnemyRead? _lastEnemyRead;


        /// <summary>Draw phases they've had so far this duel (turns alternate; the header says whose turn this is).</summary>
        private int TheirDrawsSoFar()
        {
            int turn = _currentHeader.CurrentTurn;
            bool theirsNow = _currentHeader.CurrentPlayer == 1;
            int draws = 0;
            for (int t = 1; t <= turn; t++)
                if (((turn - t) % 2 == 0) == theirsNow)
                    draws++;
            return draws; // their current turn's draw counts: it's done by the time we're asked anything in it
        }

        /// <summary>Counts of their side at one field read, to tell an activated searcher from a discarded one.</summary>
        private sealed record EnemyRead(int Hand, int Deck, int FaceDowns, Dictionary<int, int> Pile, Dictionary<int, int> Field)
        {
            public static EnemyRead Of(CardInfo side)
            {
                var pile = new Dictionary<int, int>();
                foreach (int id in (side.Grave ?? Array.Empty<int>()).Concat(side.Banish ?? Array.Empty<int>()).Where(id => id > 0))
                    pile[id] = pile.GetValueOrDefault(id) + 1;
                var field = new Dictionary<int, int>();
                int faceDowns = 0;
                foreach (var zone in (side.MonsterZones ?? Array.Empty<CardArray>()).Concat(side.SpellZones ?? Array.Empty<CardArray>()))
                {
                    var ids = zone.Cards ?? Array.Empty<int>();
                    if (ids.Length == 0) continue;
                    if (ids[0] > 0) field[ids[0]] = field.GetValueOrDefault(ids[0]) + 1;
                    else faceDowns++;
                }
                return new EnemyRead(side.Hand?.Length ?? 0, side.Main?.Length ?? 0, faceDowns, pile, field);
            }

            /// <summary>Their side at the start of the duel (5 in hand), plus the draws they've had since: all cards out of the deck now.</summary>
            public static EnemyRead AtStart(EnemyRead now, int draws)
            {
                int total = now.Hand + now.Deck + now.FaceDowns + now.Field.Values.Sum() + now.Pile.Values.Sum();
                return new EnemyRead(5 + draws, total - 5 - draws, 0, new Dictionary<int, int>(), new Dictionary<int, int>());
            }

            private static Dictionary<int, int> Minus(Dictionary<int, int> a, Dictionary<int, int> b) =>
                a.Select(kv => (kv.Key, Count: kv.Value - b.GetValueOrDefault(kv.Key))).Where(x => x.Count > 0).ToDictionary(x => x.Key, x => x.Count);

            /// <summary>
            /// Null when the searcher (newly in their pile) was activated between the two reads, else why it looks discarded or
            /// milled. Activated: their hand balances (the searcher out, the fetched card in: cards played from hand
            /// accounted for) and the deck gave at least one card. Discarded: the hand is one short.
            /// </summary>
            public static string? SearchedWith(EnemyRead last, EnemyRead now, int searcherId)
            {
                var pileNew = Minus(now.Pile, last.Pile);
                var pileLost = Minus(last.Pile, now.Pile);
                var fieldNew = Minus(now.Field, last.Field);
                var fieldLost = Minus(last.Field, now.Field);
                int faceDownsLost = Math.Max(0, last.FaceDowns - now.FaceDowns);
                int newFaceDowns = Math.Max(0, now.FaceDowns - last.FaceDowns);
                // Cards that reached their graveyard / banishment not from the face-up field: from hand, deck or a face-down.
                int fromElsewhere = Minus(pileNew, fieldLost).Values.Sum();
                int otherFromHand = Math.Max(0, fromElsewhere - 1 - faceDownsLost); // besides the searcher itself
                int fieldFromHand = Math.Max(0, fieldNew.Values.Sum() - pileLost.Values.Sum() - faceDownsLost);
                int handBalance = now.Hand - last.Hand + fieldFromHand + newFaceDowns + otherFromHand;
                int deckDrop = last.Deck - now.Deck;
                if (handBalance < 0)
                    return $"their hand is {-handBalance} short of what an activation leaves (hand {last.Hand} -> {now.Hand}): discarded";
                if (deckDrop < 1)
                    return "no card left their deck for it";
                return null;
            }
        }

        // Copies of each searcher seen in their graveyard so far (a new copy there = a new search).
        private readonly Dictionary<int, int> _searchersSeen = new();
        private int _searchersTurn = int.MaxValue;

        /// <summary>
        /// A searcher of theirs newly in their graveyard: the card it fetched is one of their lists' cards it can fetch, so
        /// the deck read rates each of those likely in their hand, and a monster they set next likely that card (user,
        /// 2026-11-13, D.D. Warrior Lady from Reinforcement of the Army).
        /// </summary>
        private async Task ObserveUnseenSearchesAsync(DuelBotWrapper bot, CardInfo enemy)
        {
            int turn = _currentHeader.CurrentTurn;
            if (turn < _searchersTurn)
            {
                _searchersSeen.Clear(); // a new duel
                _lastEnemyRead = null;
            }
            _searchersTurn = turn;
            var before = _lastEnemyRead;
            var now = EnemyRead.Of(enemy);
            _lastEnemyRead = now;
            var grave = enemy.Grave ?? Array.Empty<int>();
            foreach (var (id, name, fetches) in UnseenSearchers)
            {
                int count = grave.Count(g => g == id || Search.SearchHost.CanonicalId(g) == id);
                if (count <= _searchersSeen.GetValueOrDefault(id))
                {
                    _searchersSeen[id] = count;
                    continue;
                }
                _searchersSeen[id] = count;
                // Activated, or discarded / milled? (User, 2026-11-13: "reinforcement being discarded should NOT count as a
                // search.") Activated, it leaves the hand and the searched card comes in: the hand doesn't drop for it, and
                // the deck gives a card. Discarded, the hand drops by one more than the cards they played. Milled, the deck
                // gives several cards to the graveyard at once.
                // Already there at our first read (they went first): against the duel's start, a 5-card hand plus their draws
                // (the turn player draws on turn 1 too: seen 2026-11-13, hand 6 / deck 34 / Reinforcement after their turn 1).
                var last = before ?? EnemyRead.AtStart(now, TheirDrawsSoFar());
                if (EnemyRead.SearchedWith(last, now, id) is string notSearched)
                {
                    bot.LogNote($"deck brain: {name} went to their graveyard without a search ({notSearched})");
                    continue;
                }
                var candidates = new List<int>();
                foreach (int card in OpponentDeck.LikelyCards(minWeight: 1e-4))
                    if (await CardDatabase.GetAsync(card) is DBCardInfo info && fetches(info))
                        candidates.Add(card);
                OpponentDeck.ObserveSearch(candidates, name);
                if (OpponentDeck.DescribeSearch() is string described)
                    bot.LogNote($"deck brain: they searched with {name} (unseen): {described}");
            }
        }
    }
}
