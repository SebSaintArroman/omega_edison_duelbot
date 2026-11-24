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
        /// The set window as one main-phase move (user, 2026-11-24: "go to set window should be a single thing, rather than evaluating
        /// every card to be set separately, which biases towards setting by having more chances to draw a good line there"). Below it
        /// the search picks a set plan (SetPlanPrefix): every spell/trap it sets, played in a random order so the order never matters.
        /// Monster sets stay their own moves (they compete with the Normal Summon).
        /// </summary>
        public const string SetWindowKey = "setwin";

        /// <summary>A set plan's key: this prefix, then the set moves' keys joined by '|' (a key once per copy set).</summary>
        public const string SetPlanPrefix = "setplan:";

        /// <summary>The spell/trap set moves' key prefix ("4:").</summary>
        public static readonly string SetSpellPrefix = $"{(int)MainAction.SetSpell}:";

        /// <summary>A set plan's set moves (one key per card set).</summary>
        public static List<string> SetPlanCards(string planKey) =>
            planKey.StartsWith(SetPlanPrefix, StringComparison.Ordinal)
                ? planKey[SetPlanPrefix.Length..].Split('|', StringSplitOptions.RemoveEmptyEntries).ToList()
                : new List<string>();

        public static string SetPlanKey(IEnumerable<string> setKeys) => SetPlanPrefix + string.Join("|", setKeys.OrderBy(k => k, StringComparer.Ordinal));

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

        /// <summary>
        /// The idle / battle prompt's options without this answer (a bot packet), to ask our brain again when the search refused it
        /// (user, 2026-11-24: "refused moves shouldn't go straight into fallback, they should just remove that option and try again").
        /// Null when it isn't one of these prompts or the answer isn't among its options.
        /// </summary>
        public static object? WithoutAnswer(object? data, byte[] packet)
        {
            if (packet.Length < 4)
                return null;
            int value = BitConverter.ToInt32(packet, 0);
            int action = value & 0xffff, index = value >> 16;
            // Cards offering this action at this index left out; a card with several activations loses only that one.
            static DynamicCard[]? Drop(DynamicCard[]? cards, int action, int index, out bool removed)
            {
                removed = false;
                if (cards == null)
                    return null;
                var kept = new List<DynamicCard>();
                foreach (var card in cards)
                {
                    if (card.ActionIndex != null && card.ActionIndex.Length > action && card.ActionIndex[action] == index)
                    {
                        removed = true;
                        continue;
                    }
                    kept.Add(card);
                }
                return kept.ToArray();
            }
            static (DynamicCard[]? Cards, int[]? Descriptions) DropActivation(DynamicCard[]? cards, int[]? descriptions, int index, out bool removed)
            {
                removed = false;
                if (cards == null)
                    return (null, descriptions);
                bool parallel = descriptions != null && descriptions.Length == cards.Length;
                var kept = new List<DynamicCard>();
                var keptDescriptions = new List<int>();
                for (int i = 0; i < cards.Length; i++)
                {
                    var card = cards[i];
                    var acts = card.ActionActivateIndex ?? Array.Empty<ActivationCardIndex>();
                    var left = acts.Where(a => a.ActionIndex != index).ToArray();
                    if (left.Length < acts.Length)
                        removed = true;
                    if (acts.Length > 0 && left.Length == 0)
                        continue;
                    card.ActionActivateIndex = left;
                    kept.Add(card);
                    if (parallel)
                        keptDescriptions.Add(descriptions![i]);
                }
                return (kept.ToArray(), parallel ? keptDescriptions.ToArray() : descriptions);
            }
            bool removed;
            switch (data)
            {
                case IdleCmdData idle:
                    switch ((MainAction)action)
                    {
                        case MainAction.Summon: idle.SummonableCards = Drop(idle.SummonableCards, action, index, out removed)!; break;
                        case MainAction.SpSummon: idle.SpecialSummonableCards = Drop(idle.SpecialSummonableCards, action, index, out removed)!; break;
                        case MainAction.Repos: idle.ReposableCards = Drop(idle.ReposableCards, action, index, out removed)!; break;
                        case MainAction.SetMonster: idle.MonsterSetableCards = Drop(idle.MonsterSetableCards, action, index, out removed)!; break;
                        case MainAction.SetSpell: idle.SpellSetableCards = Drop(idle.SpellSetableCards, action, index, out removed)!; break;
                        case MainAction.Activate:
                        {
                            var (cards, descriptions) = DropActivation(idle.ActivableCards, idle.ActivableDescriptions, index, out removed);
                            idle.ActivableCards = cards!;
                            idle.ActivableDescriptions = descriptions!;
                            break;
                        }
                        case MainAction.ToBattlePhase: removed = idle.CanBattlePhase; idle.CanBattlePhase = false; break;
                        case MainAction.ToEndPhase: removed = idle.CanEndPhase; idle.CanEndPhase = false; break;
                        default: return null;
                    }
                    return removed ? idle : null;
                case BattleCmdData battle:
                    switch ((BattleAction)action)
                    {
                        case BattleAction.Attack: battle.AttackableCards = Drop(battle.AttackableCards, action, index, out removed)!; break;
                        case BattleAction.Activate:
                        {
                            var (cards, descriptions) = DropActivation(battle.ActivableCards, battle.ActivableDescriptions, index, out removed);
                            battle.ActivableCards = cards!;
                            battle.ActivableDescriptions = descriptions!;
                            break;
                        }
                        case BattleAction.ToMainPhaseTwo: removed = battle.CanMainPhaseTwo; battle.CanMainPhaseTwo = false; break;
                        case BattleAction.ToEndPhase: removed = battle.CanEndPhase; battle.CanEndPhase = false; break;
                        default: return null;
                    }
                    return removed ? battle : null;
                default:
                    return null;
            }
        }
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
        /// Keys of a replayed game's options that mean a real option in another zone (a card the replay put elsewhere: user, 2026-11-24,
        /// Brain Control's Card Trooper), each to the real key. Only one-to-one matches by LooseKey among the keys that don't already
        /// match exactly: two copies of a card in different zones can't be told apart, and stay as they are.
        /// </summary>
        /// <summary>The cards of these pick keys, each as a one-card key ("card:K"; "cards:K1+K2" gives both); other keys ("cancel") give none.</summary>
        public static IEnumerable<string> PickCardKeys(IEnumerable<string> pickKeys) =>
            pickKeys.SelectMany(k => k.StartsWith("card:") ? new[] { k }
                : k.StartsWith("cards:") ? k[6..].Split('+').Select(c => "card:" + c) : Array.Empty<string>()).Distinct();

        public static Dictionary<string, string> ToLiveKeys(IReadOnlyCollection<string> simKeys, IReadOnlyCollection<string> liveKeys)
        {
            var map = new Dictionary<string, string>();
            var simLeft = simKeys.Where(k => !liveKeys.Contains(k)).Distinct().ToList();
            if (simLeft.Count == 0)
                return map;
            var liveLeft = liveKeys.Where(k => !simKeys.Contains(k)).Distinct().ToList();
            foreach (var group in simLeft.GroupBy(LooseKey))
            {
                var real = liveLeft.Where(k => LooseKey(k) == group.Key).Take(2).ToList();
                if (group.Count() == 1 && real.Count == 1)
                    map[group.First()] = real[0];
            }
            return map;
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
