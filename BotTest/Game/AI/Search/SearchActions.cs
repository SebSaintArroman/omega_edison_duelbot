using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Search
{
    /// <summary>
    /// The choices of a main-phase or battle prompt as stable keys, the same on the real server and in the search's
    /// engine (both give the bot the same IdleCmdData / BattleCmdData shapes), so a line found in the search maps back
    /// to the real response.
    /// </summary>
    internal static class SearchActions
    {
        public sealed record Choice<T>(string Key, string Label, T Response);

        /// <summary>
        /// A card's identity across rebuilt positions: hand cards by name (copies are interchangeable), the rest by
        /// zone. Face-down cards of theirs are id 0 in both views.
        /// </summary>
        public static string CardKey(DynamicCard card)
        {
            var at = card.CurrentCoordinates;
            // Unknown cards: 0 in the search's engine, a negative id on live Omega (-3 for a face-down monster): same key.
            int id = Math.Max(0, card.ID);
            // Hand, Deck and Extra Deck: copies are interchangeable and the order differs between rebuilds (every engine
            // shuffles its own way), so the card alone is the key. Keyed by position, a search pick from the deck didn't
            // replay (seen 2026-11-10: the turn planner lost most of its lines after One for One / Substitoad).
            return at.Location switch
            {
                (byte)CardLocation.Hand => $"{id}@hand",
                (byte)CardLocation.Deck => $"{id}@{at.Controller}.deck",
                (byte)CardLocation.Extra => $"{id}@{at.Controller}.extra",
                _ => $"{id}@{at.Controller}.{at.Location}.{at.Sequence}",
            };
        }

        private static string Name(DynamicCard card) => card.ID > 0 ? CardNames.Get(card.ID) : "a face-down card";

        /// <param name="usedThisTurn">Cards whose field effect we already used this turn (once per turn): not offered again.</param>
        public static List<Choice<IdleCmdResponse>> Idle(IdleCmdData data, IReadOnlyCollection<int>? usedThisTurn = null,
            Func<int, MainAction, bool>? allowed = null)
        {
            var choices = new List<Choice<IdleCmdResponse>>();
            var seen = new HashSet<string>();
            void Add(string key, string label, IdleCmdResponse response)
            {
                if (seen.Add(key))
                    choices.Add(new Choice<IdleCmdResponse>(key, label, response));
            }
            void List(DynamicCard[]? cards, MainAction action, string verb)
            {
                foreach (var card in cards ?? Array.Empty<DynamicCard>())
                    if (card.ActionIndex != null && card.ActionIndex.Length > (int)action && (allowed == null || allowed(card.ID, action)))
                        Add($"{(int)action}:{CardKey(card)}", $"{verb} {Name(card)}", (action, (ushort)card.ActionIndex[(int)action]));
            }
            List(data.SummonableCards, MainAction.Summon, "summon");
            List(data.SpecialSummonableCards, MainAction.SpSummon, "special summon");
            List(data.ReposableCards, MainAction.Repos, "change position of");
            List(data.MonsterSetableCards, MainAction.SetMonster, "set");
            List(data.SpellSetableCards, MainAction.SetSpell, "set");
            foreach (var card in data.ActivableCards ?? Array.Empty<DynamicCard>())
            {
                if (allowed != null && !allowed(card.ID, MainAction.Activate))
                    continue;
                bool onField = card.CurrentCoordinates.Location is (byte)CardLocation.MonsterZone or (byte)CardLocation.SpellZone;
                if (onField && usedThisTurn != null && usedThisTurn.Contains(card.ID))
                    continue;
                foreach (var activation in card.ActionActivateIndex ?? Array.Empty<ActivationCardIndex>())
                    Add($"{(int)MainAction.Activate}:{CardKey(card)}:{activation.ActionDescription}", $"activate {Name(card)}",
                        (MainAction.Activate, (ushort)activation.ActionIndex));
            }
            if (data.CanBattlePhase)
                Add("battle", "go to the Battle Phase", MainAction.ToBattlePhase);
            if (data.CanEndPhase)
                Add("end", "end the turn", MainAction.ToEndPhase);
            return choices;
        }

        /// <summary>
        /// Every action in an idle prompt, as keys only. Decision/transposition keys need the engine's full legal
        /// prompt, not the policy-filtered choices, but do not need labels, responses, or response packets.
        /// Keep this in the same order and with the same duplicate handling as <see cref="Idle"/>.
        /// </summary>
        internal static List<string> IdleOptionKeys(IdleCmdData data)
        {
            var keys = new List<string>();
            var seen = new HashSet<string>();
            void Add(string key)
            {
                if (seen.Add(key))
                    keys.Add(key);
            }
            void List(DynamicCard[]? cards, MainAction action)
            {
                foreach (var card in cards ?? Array.Empty<DynamicCard>())
                    if (card.ActionIndex != null && card.ActionIndex.Length > (int)action)
                        Add($"{(int)action}:{CardKey(card)}");
            }
            List(data.SummonableCards, MainAction.Summon);
            List(data.SpecialSummonableCards, MainAction.SpSummon);
            List(data.ReposableCards, MainAction.Repos);
            List(data.MonsterSetableCards, MainAction.SetMonster);
            List(data.SpellSetableCards, MainAction.SetSpell);
            foreach (var card in data.ActivableCards ?? Array.Empty<DynamicCard>())
                foreach (var activation in card.ActionActivateIndex ?? Array.Empty<ActivationCardIndex>())
                    Add($"{(int)MainAction.Activate}:{CardKey(card)}:{activation.ActionDescription}");
            if (data.CanBattlePhase)
                Add("battle");
            if (data.CanEndPhase)
                Add("end");
            return keys;
        }

        internal static List<string> ChainOptionKeys(SelectChainData chain)
        {
            var cards = chain.SelectableCards ?? Array.Empty<DynamicCard>();
            var descs = chain.Descriptions ?? Array.Empty<int>();
            var keys = new List<string>();
            if (!chain.Forced)
                keys.Add("pass");
            for (int i = 0; i < cards.Length; i++)
                keys.Add($"chain:{CardKey(cards[i])}:{(i < descs.Length ? descs[i] : 0)}");
            return keys.Distinct().ToList();
        }

        internal static DynamicCard AttackerFor(BattleCmdData data, ushort index) =>
            (data.AttackableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card =>
                card.ActionIndex is { Length: > (int)BattleAction.Attack } indexes && indexes[(int)BattleAction.Attack] == index);

        public static List<Choice<BattleCmdResponse>> Battle(BattleCmdData data)
        {
            var choices = new List<Choice<BattleCmdResponse>>();
            var seen = new HashSet<string>();
            void Add(string key, string label, BattleCmdResponse response)
            {
                if (seen.Add(key))
                    choices.Add(new Choice<BattleCmdResponse>(key, label, response));
            }
            var attackers = data.AttackableCards ?? Array.Empty<DynamicCard>();
            foreach (var attacker in attackers)
                if (attacker.ActionIndex is { Length: > (int)BattleAction.Attack } index)
                    Add($"attack:{CardKey(attacker)}", $"attack with {Name(attacker)}", (BattleAction.Attack, (ushort)index[(int)BattleAction.Attack]));
            foreach (var card in data.ActivableCards ?? Array.Empty<DynamicCard>())
                foreach (var activation in card.ActionActivateIndex ?? Array.Empty<ActivationCardIndex>())
                    Add($"bact:{CardKey(card)}:{activation.ActionDescription}", $"activate {Name(card)}",
                        (BattleAction.Activate, (ushort)activation.ActionIndex));
            if (data.CanMainPhaseTwo)
                Add("main2", "go to Main Phase 2", BattleAction.ToMainPhaseTwo);
            if (data.CanEndPhase)
                Add("end", "end the turn", BattleAction.ToEndPhase);
            return choices;
        }

        public sealed record Pick(string Key, string Label, byte[] Packet, IReadOnlyList<int> Indexes);

        /// <summary>Most cards in one multi-pick the search tries (bigger picks are left to the brain).</summary>
        public const int MaxPickSize = 3;

        /// <summary>
        /// The ways to answer a card selection: every set of <paramref name="min"/>..<paramref name="max"/> cards (up to
        /// MaxPickSize; copies of the same card count once), plus cancel when allowed. Keys are stable across rebuilt
        /// positions: "card:&lt;key&gt;" for one card, "cards:&lt;key&gt;+&lt;key&gt;" for several (sorted). With an rng and more than
        /// <paramref name="cap"/> sets, a random <paramref name="cap"/> of them (the search re-deals them every line).
        /// </summary>
        public static List<Pick> CardPicks(DynamicCard[] cards, int min, int max, bool cancelable, Random? rng = null, int cap = int.MaxValue)
        {
            var picks = new List<Pick>();
            int n = cards.Length;
            var seen = new HashSet<string>();
            var cardKeys = cards.Select(CardKey).ToArray();
            var cardNames = cards.Select(Name).ToArray();
            PriorityQueue<(string Key, int[] Indexes, int RandomOrder, int Ordinal), (int RandomOrder, int Ordinal)>? sampled = null;
            int ordinal = 0;

            Pick Materialize(string key, IReadOnlyList<int> indexes)
            {
                var packet = new byte[indexes.Count + 1];
                packet[0] = (byte)indexes.Count;
                for (int i = 0; i < indexes.Count; i++)
                    packet[i + 1] = (byte)indexes[i];
                return new Pick(key, $"pick {string.Join(" + ", indexes.Select(i => cardNames[i]))}", packet,
                    indexes is int[] array ? array : indexes.ToArray());
            }

            void Sample(string key, int[] indexes)
            {
                int randomOrder = rng!.Next();
                int order = ordinal++;
                if (cap <= 0)
                    return;
                var candidate = (key, indexes, randomOrder, order);
                // PriorityQueue is a min-heap. Negative priorities put the worst retained candidate at the top.
                var priority = (-randomOrder, -order);
                if (sampled!.Count < cap)
                    sampled.Enqueue(candidate, priority);
                else
                {
                    var worst = sampled.Peek();
                    if (randomOrder < worst.RandomOrder || randomOrder == worst.RandomOrder && order < worst.Ordinal)
                    {
                        sampled.Dequeue();
                        sampled.Enqueue(candidate, priority);
                    }
                }
            }

            void AddPick(string key, List<int> chosen)
            {
                if (sampled != null)
                {
                    Sample(key, chosen.ToArray());
                    return;
                }
                if (rng != null && picks.Count >= cap)
                {
                    // The old implementation assigned one random key to every pick, then took the lowest cap.
                    // Delay that work until a cap is actually exceeded, so <= cap keeps its order and consumes no RNG.
                    sampled = new();
                    foreach (var pick in picks)
                        Sample(pick.Key, pick.Indexes.ToArray());
                    picks.Clear();
                    Sample(key, chosen.ToArray());
                    return;
                }
                picks.Add(Materialize(key, chosen));
            }

            void Combos(int start, List<int> chosen, int size)
            {
                if (chosen.Count == size)
                {
                    var keys = chosen.Select(i => cardKeys[i]).OrderBy(k => k, StringComparer.Ordinal).ToList();
                    string key = size == 1 ? $"card:{keys[0]}" : $"cards:{string.Join('+', keys)}";
                    if (seen.Add(key))
                        AddPick(key, chosen);
                    return;
                }
                for (int i = start; i < n; i++)
                {
                    chosen.Add(i);
                    Combos(i + 1, chosen, size);
                    chosen.RemoveAt(chosen.Count - 1);
                }
            }
            for (int size = Math.Max(1, min); size <= Math.Min(Math.Min(max, n), MaxPickSize); size++)
                Combos(0, new List<int>(), size);
            if (sampled != null)
                picks = sampled.UnorderedItems.Select(item => item.Element)
                    .OrderBy(candidate => candidate.RandomOrder).ThenBy(candidate => candidate.Ordinal)
                    .Select(candidate => Materialize(candidate.Key, candidate.Indexes)).ToList();
            if (min == 0 || cancelable)
                picks.Add(new Pick("cancel", "cancel", new byte[] { 0 }, Array.Empty<int>()));
            return picks;
        }

        /// <summary>The bot packet for a main/battle choice ([u16 action][u16 index], as BotResponseWriter writes it).</summary>
        public static byte[] Packet(int action, int index) => BitConverter.GetBytes((action & 0xffff) | (index << 16));
        public static byte[] Packet(IdleCmdResponse response) => Packet((int)response.Action, response.Index);
        public static byte[] Packet(BattleCmdResponse response) => Packet((int)response.Action, response.Index);

        /// <summary>The card id in a key ("{action}:{id}@..." or "card:{id}@..."), official id, or 0.</summary>
        public static int KeyCardId(string key)
        {
            int colon = key.IndexOf(':'), at = key.IndexOf('@');
            if (colon < 0 || at <= colon || !int.TryParse(key.AsSpan(colon + 1, at - colon - 1), out int id))
                return 0;
            return SearchHost.CanonicalId(id) is > 0 and var official ? official : id;
        }

        private static readonly System.Text.RegularExpressions.Regex ZoneInKey = new(@"@\d+\.\d+\.\d+", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex CardIdInKey = new(@"\d+(?=@)", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex EffectDescInKey = new(@":(\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// A key without zones, with card ids by the card they are: the same move whatever zone its card is in, and whatever id
        /// variant the server uses (Omega's ids for some cards differ from the engine's: Lyla 22624393 live, 22624373 in the
        /// engine). An activation's effect description is the card id x 16 + the effect's index: remapped the same way.
        /// </summary>
        public static string LooseKey(string key)
        {
            static int Canon(int id) => SearchHost.CanonicalId(id) is > 0 and var c ? c : id;
            string loose = CardIdInKey.Replace(ZoneInKey.Replace(key, "@*"), m => int.TryParse(m.Value, out int id) ? Canon(id).ToString() : m.Value);
            return EffectDescInKey.Replace(loose, m =>
                long.TryParse(m.Groups[1].Value, out long desc) && desc >= 16 ? $":{((long)Canon((int)(desc >> 4)) << 4) | (desc & 15)}" : m.Value);
        }

        /// <summary>The live option a key from the search means: the same key, or else the same move by LooseKey. Null if neither.</summary>
        public static string? MatchKey(string planned, IEnumerable<string> available)
        {
            var list = available as ICollection<string> ?? available.ToList();
            if (list.Contains(planned))
                return planned;
            string loose = LooseKey(planned);
            return list.FirstOrDefault(k => LooseKey(k) == loose) ?? SameMoveIn(planned, list);
        }

        /// <summary>
        /// The one key among these that is the same action on the same card as this one, when one side has no effect description (Omega
        /// reports some activations with none: seen 2026-11-17, Swap Frog's return to hand as "&lt;&gt;", so the live move "…:0" matched no option
        /// of the search's engine, which gives it its real description: no verdict, and every mid-chain replay after it missed). Null when
        /// none or several fit (a card with two effects can't be told apart without its description).
        /// </summary>
        public static string? SameMoveIn(string key, IEnumerable<string> keys)
        {
            if (DescOf(LooseKey(key)) is not { } desc)
                return null;
            string bare = Bare(LooseKey(key));
            var fits = keys.Where(k => DescOf(LooseKey(k)) is { } d && (d == 0 || desc == 0) && Bare(LooseKey(k)) == bare).Distinct().Take(2).ToList();
            return fits.Count == 1 ? fits[0] : null;
        }

        /// <summary>An option as a decision's key holds it: LooseKey without the effect description, which Omega sometimes leaves out (Swap Frog's
        /// return to hand, "&lt;&gt;") while the search's engine gives it: with it the real decision never matched the tree's (a DESYNC, 2026-11-17).</summary>
        public static string DecisionOptionKey(string key) => Bare(LooseKey(key));

        private static long? DescOf(string key) =>
            EffectDescInKey.Match(key) is { Success: true } m && long.TryParse(m.Groups[1].Value, out long d) ? d : null;

        private static string Bare(string key) => EffectDescInKey.Replace(key, "");
    }
}
