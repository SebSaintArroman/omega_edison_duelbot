using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Search
{
    /// <summary>One answer of ours in the real game since the last searched decision: the prompt's kind and the answer as a key.</summary>
    internal sealed record ReplayStep(BotState State, string Key, string Label);

    /// <summary>
    /// A card of theirs we saw come into play since the last searched decision, and when: after <see cref="AfterOurAnswers"/>
    /// of our answers. Placed where it really was in every line (their set zone, or their hand) and played at that point.
    /// </summary>
    internal sealed record ReplayReveal(int Code, bool FromHand, bool Monster, int Zone, int AfterOurAnswers)
    {
        /// <summary>What it targeted, as the bot saw it (controller 0 = ours): their pick in the replay (seen 2026-11-15: their
        /// Compulsory Evacuation Device bounced Darius in the real game, a random monster in the replay).</summary>
        public IReadOnlyList<(int Controller, int Location, int Sequence)>? Targets { get; init; }
    }

    /// <summary>
    /// The way from the last searched (rebuildable) decision of our turn to a chain window: the search can't load an open chain,
    /// so each line rebuilds that decision's board and plays the real game's moves up to the window, then searches from there
    /// (user, 2026-11-15: "mid-chain planning is very important"). <see cref="TargetKey"/> is the window as the bot sees it
    /// (DecisionKey): a line that gets somewhere else is thrown away.
    /// </summary>
    internal sealed record ReplayScript(BotState AnchorState, IReadOnlyList<ReplayStep> Ours, IReadOnlyList<ReplayReveal> Theirs, string TargetKey)
    {
        /// <summary>Our hand and field cards now (field: location, zone, code): their picks of our cards in the replay take what really
        /// left (seen 2026-11-15: Machina Fortress's discard took Waboku from our hand; the replay discarded at random).</summary>
        public IReadOnlyList<int> OurHandNow { get; init; } = Array.Empty<int>();
        public IReadOnlySet<(int Location, int Sequence, int Code)> OurFieldNow { get; init; } = new HashSet<(int, int, int)>();
    }

    /// <summary>Our real answers as keys, and those keys back as answers to the same prompt in a line of the search.</summary>
    internal static class ReplayKeys
    {
        private static string Loose(DynamicCard card) => SearchActions.LooseKey($"card:{SearchActions.CardKey(card)}")[5..];

        private static DynamicCard[]? PickCards(object data) => data switch
        {
            SelectCardData d => d.SelectableCards,
            SelectTributeData d => d.SelectableCards,
            SelectUnselectCardData d => d.SelectableCards,
            _ => null,
        };

        /// <summary>The real answer (the bytes the wrapper sent) as a replay key, or null when it can't be read.</summary>
        public static ReplayStep? Of(BotState state, object? data, byte[] response)
        {
            try
            {
                switch (data)
                {
                    case IdleCmdData idle:
                    {
                        var c = SearchActions.Idle(idle).FirstOrDefault(x => SearchActions.Packet(x.Response).AsSpan().SequenceEqual(response));
                        return c == null ? Raw(state, response) : new ReplayStep(state, SearchActions.LooseKey(c.Key), c.Label);
                    }
                    case BattleCmdData battle:
                    {
                        var c = SearchActions.Battle(battle, anyOrder: true).FirstOrDefault(x => SearchActions.Packet(x.Response).AsSpan().SequenceEqual(response));
                        // Omega attacks with the first attacker by zone whatever index we send (server attack order): that one.
                        if (c != null && c.Key.StartsWith("attack:"))
                            c = SearchActions.Battle(battle).FirstOrDefault(x => x.Key.StartsWith("attack:")) ?? c;
                        return c == null ? Raw(state, response) : new ReplayStep(state, SearchActions.LooseKey(c.Key), c.Label);
                    }
                    case SelectChainData chain:
                    {
                        int i = BitConverter.ToInt32(response, 0);
                        var cards = chain.SelectableCards ?? Array.Empty<DynamicCard>();
                        if (i < 0 || i >= cards.Length)
                            return new ReplayStep(state, "pass", "don't chain");
                        var descs = chain.Descriptions ?? Array.Empty<int>();
                        return new ReplayStep(state, SearchActions.LooseKey($"chain:{SearchActions.CardKey(cards[i])}:{(i < descs.Length ? descs[i] : 0)}"),
                            $"chain {OmegaBotWrapper.Logging.CardNames.Get(cards[i].ID)}");
                    }
                    case SelectPositionData when response.Length >= 1:
                        // As the search's tree keys a position (Mcts.OptionsFor).
                        return new ReplayStep(state, $"pos:{response[0]}", $"position {(CardPosition)response[0]}");
                    case SelectOptionData option:
                    {
                        int i = BitConverter.ToInt32(response, 0);
                        var options = option.Options ?? Array.Empty<int>();
                        return i >= 0 && i < options.Length ? new ReplayStep(state, $"option:{options[i]}", $"option {i + 1}") : Raw(state, response);
                    }
                }
                if (data != null && PickCards(data) is { } pickFrom && response.Length >= 1)
                {
                    int n = response[0];
                    if (n == 0)
                        return new ReplayStep(state, "pick:", "cancel");
                    var keys = new List<string>();
                    for (int k = 1; k <= n && k < response.Length; k++)
                        if (response[k] < pickFrom.Length)
                            keys.Add(Loose(pickFrom[response[k]]));
                    return new ReplayStep(state, "pick:" + string.Join('+', keys.OrderBy(x => x, StringComparer.Ordinal)),
                        "pick " + string.Join(" + ", Enumerable.Range(1, Math.Min(n, response.Length - 1)).Where(k => response[k] < pickFrom.Length)
                            .Select(k => pickFrom[response[k]].ID > 0 ? OmegaBotWrapper.Logging.CardNames.Get(pickFrom[response[k]].ID) : "a face-down card")));
                }
                return Raw(state, response);
            }
            catch
            {
                return Raw(state, response);
            }
        }

        private static ReplayStep Raw(BotState state, byte[] response) => new(state, "raw:" + Convert.ToHexString(response), state.ToString());

        /// <summary>The answer to this prompt of a line that replays <paramref name="step"/>, or null when it isn't on offer.</summary>
        public static byte[]? Packet(object data, ReplayStep step)
        {
            string key = step.Key;
            if (key.StartsWith("raw:"))
                return Convert.FromHexString(key[4..]);
            switch (data)
            {
                case IdleCmdData idle:
                {
                    var idleChoices = SearchActions.Idle(idle).ToList();
                    var m = idleChoices.FirstOrDefault(c => SearchActions.LooseKey(c.Key) == key) ??
                            (SearchActions.SameMoveIn(key, idleChoices.Select(c => c.Key)) is { } same ? idleChoices.First(c => c.Key == same) : null);
                    return m != null ? SearchActions.Packet(m.Response) : null;
                }
                case BattleCmdData battle:
                    return SearchActions.Battle(battle, anyOrder: true).FirstOrDefault(c => SearchActions.LooseKey(c.Key) == key) is { } b ? SearchActions.Packet(b.Response) : null;
                case SelectChainData chain:
                {
                    if (key == "pass")
                        return chain.Forced ? null : BitConverter.GetBytes(-1);
                    var cards = chain.SelectableCards ?? Array.Empty<DynamicCard>();
                    var descs = chain.Descriptions ?? Array.Empty<int>();
                    for (int i = 0; i < cards.Length; i++)
                        if (SearchActions.LooseKey($"chain:{SearchActions.CardKey(cards[i])}:{(i < descs.Length ? descs[i] : 0)}") == key)
                            return BitConverter.GetBytes(i);
                    return null;
                }
                case SelectOptionData option:
                {
                    var options = option.Options ?? Array.Empty<int>();
                    for (int i = 0; i < options.Length; i++)
                        if ($"option:{options[i]}" == key)
                            return BitConverter.GetBytes(i);
                    return null;
                }
                case SelectPositionData position when key.StartsWith("pos:") && byte.TryParse(key.AsSpan(4), out byte p):
                    return (position.Positions ?? Array.Empty<byte>()).Contains(p) ? new[] { p } : null;
            }
            if (PickCards(data) is { } cardsToPick && key.StartsWith("pick:"))
            {
                if (key == "pick:")
                    return new byte[] { 0 };
                var wanted = key[5..].Split('+');
                var used = new HashSet<int>();
                var chosen = new List<byte>();
                foreach (string w in wanted)
                {
                    int found = -1;
                    for (int i = 0; i < cardsToPick.Length; i++)
                        if (!used.Contains(i) && Loose(cardsToPick[i]) == w)
                        {
                            found = i;
                            break;
                        }
                    if (found < 0)
                        return null;
                    used.Add(found);
                    chosen.Add((byte)found);
                }
                return new[] { (byte)chosen.Count }.Concat(chosen).ToArray();
            }
            return null;
        }

        /// <summary>Their card pick that takes the targets this revealed card really took, or null (not those cards on offer).</summary>
        public static byte[]? TheirTargets(object data, ReplayReveal reveal)
        {
            if (reveal.Targets is not { Count: > 0 } targets || PickCards(data) is not { } cards)
                return null;
            var chosen = new List<byte>();
            foreach (var (controller, location, sequence) in targets)
            {
                // Their prompt sees the board from their side: our cards are their opponent's.
                int at = Array.FindIndex(cards, c => c.CurrentCoordinates.Controller == 1 - controller &&
                                                     c.CurrentCoordinates.Location == location && c.CurrentCoordinates.Sequence == sequence);
                if (at < 0)
                    return null;
                chosen.Add((byte)at);
            }
            return new[] { (byte)chosen.Count }.Concat(chosen).ToArray();
        }

        /// <summary>
        /// Their pick among our cards, as the real game came out: our hand cards it offers beyond what our hand holds now, our field
        /// cards no longer where they are. Null when that doesn't make up a valid pick (then their policy picks).
        /// </summary>
        public static byte[]? TheirPickFromOutcome(object data, ReplayScript script)
        {
            if (PickCards(data) is not { } cards)
                return null;
            int min = data switch { SelectCardData d => d.Minimum, SelectTributeData d => d.Minimum, SelectUnselectCardData d => Math.Max(1, d.Minimum), _ => 1 };
            var handNow = script.OurHandNow.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            var handOffered = cards.Where(c => c.CurrentCoordinates.Controller == 1 && c.CurrentCoordinates.Location == (byte)CardLocation.Hand)
                .GroupBy(c => c.ID).ToDictionary(g => g.Key, g => g.Count());
            var chosen = new List<byte>();
            for (int i = 0; i < cards.Length && chosen.Count < min; i++)
            {
                var at = cards[i].CurrentCoordinates;
                if (at.Controller != 1) // their prompt: our cards are their opponent's
                    continue;
                bool gone = at.Location == (byte)CardLocation.Hand
                    ? handOffered.GetValueOrDefault(cards[i].ID) > handNow.GetValueOrDefault(cards[i].ID)
                    : at.Location is (byte)CardLocation.MonsterZone or (byte)CardLocation.SpellZone && !script.OurFieldNow.Contains((at.Location, at.Sequence, cards[i].ID));
                if (!gone)
                    continue;
                chosen.Add((byte)i);
                if (at.Location == (byte)CardLocation.Hand)
                    handOffered[cards[i].ID]--;
            }
            return chosen.Count == 0 || chosen.Count < min ? null : new[] { (byte)chosen.Count }.Concat(chosen).ToArray();
        }

        /// <summary>Their answer that plays this revealed card at a chain prompt of theirs, or null when it isn't offered there.</summary>
        public static byte[]? Reveal(SelectChainData chain, ReplayReveal reveal)
        {
            var cards = chain.SelectableCards ?? Array.Empty<DynamicCard>();
            for (int i = 0; i < cards.Length; i++)
            {
                var at = cards[i].CurrentCoordinates;
                bool code = cards[i].ID == reveal.Code || SearchHost.CanonicalId(cards[i].ID) == SearchHost.CanonicalId(reveal.Code);
                bool place = reveal.FromHand ? at.Location == (byte)CardLocation.Hand
                    : at.Location == (byte)(reveal.Monster ? CardLocation.MonsterZone : CardLocation.SpellZone);
                if (code && place)
                    return BitConverter.GetBytes(i);
            }
            return null;
        }
    }
}
