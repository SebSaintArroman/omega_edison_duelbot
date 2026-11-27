using BotTest.Game.AI.Search;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// The rule gate (user, 2026-11-26: "there should categorically not be any way of the bot ignoring our rules"): every answer of ours,
    /// whatever produced it (a plan's step, the search, the brain, a routine), is checked before it's sent against the search's rule filter
    /// at the real prompt: the turn is replayed from its start to this prompt (TurnStartSearchAsync, a short check run) and the answer must
    /// be on offer after the filter in most of the lines that reach it (SearchOutcome.RuleAllows). The board there is the real one as we
    /// know it: their face-down cards stay unknown, so a step planned in a line where they were already revealed can't pass on that.
    /// An answer the rules leave out is replaced by the search's best among the allowed ones. When no line reaches the prompt (a replay
    /// miss) the answer can't be checked: logged red and flagged with the replay issues.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        [Tunable("The rule gate's check at every prompt (ms): one short run of the search from the turn's start to the real prompt, to read which answers the rules allow there.")]
        public int RuleGateCheckMs { get; set; } = 1000;

        /// <summary>Gate checks made, and answers it replaced (for the bench and the log).</summary>
        public static long RuleGateChecks, RuleGateReplaced, RuleGateUnchecked;

        public async Task<object?> GateAsync(BotState state, object data, object answer, CancellationToken ct = default)
        {
            if (IsSearchTwin || _trackedBot is not { } bot || !SearchHost.Available || _ownTurnParity < 0 || !SearchWanted(bot))
                return null;
            if (Answers(state, data) is not { Count: >= 2 } answers || KeyOf(state, data, answer, answers) is not string key)
                return null;
            var options = answers.Select(a => a.Key).ToList();
            Interlocked.Increment(ref RuleGateChecks);
            // The real prompt, rebuilt: the turn replayed from its start; for our main phase and battle, else the board as it is now (the
            // main-phase search's own root, no replay needed).
            bool main = state is BotState.SelectIdleCmd or BotState.SelectBattleCmd;
            async Task<SearchOutcome?> Check(string prior, bool full)
            {
                SearchOutcome? found = null;
                try { found = await TurnStartSearchAsync(bot, state, options, prior, ct, full ? null : RuleGateCheckMs); }
                catch (Exception ex) { bot.LogNote($"search: rule gate: the check from the turn's start failed ({ex.Message})"); }
                if (found is { RootRuleArrivals: > 0 } || !main)
                    return found;
                bool summonUsed = NormalSummonUsed(_currentHeader), battleDone = (DuelPhase)_currentHeader.CurrentPhase == DuelPhase.Main2;
                if (full)
                {
                    try { return await SearchUntilSureAsync(bot, state, prior, options, options.ToHashSet(), summonUsed, battleDone, state == BotState.SelectBattleCmd, ct); }
                    catch (Exception ex)
                    {
                        bot.LogNote($"search: rule gate: the search on the board as it is failed ({ex.Message})");
                        return null;
                    }
                }
                _extraBudgetMs = RuleGateCheckMs;
                try
                {
                    return await SearchAsync(bot, state, prior, options, summonUsed, battleDone, inBattle: state == BotState.SelectBattleCmd, ct);
                }
                catch (Exception ex)
                {
                    bot.LogNote($"search: rule gate: the check on the board as it is failed ({ex.Message})");
                    return null;
                }
                finally { _extraBudgetMs = null; }
            }
            var check = await Check(key, full: false);
            if (check == null || check.RootRuleArrivals == 0)
            {
                Interlocked.Increment(ref RuleGateUnchecked);
                FlagReplayIssue(bot, $"search: RULE GATE UNCHECKED: no line reached this {state} (replay miss); {Label(answers, key)} goes as it is");
                return null;
            }
            // Another copy of the same card in the same kind of place (two Treeborn Frogs in the graveyard: keys differ by their places) counts
            // as the same answer (DuelSim 2026-11-26: the gate "replaced" a Treeborn Frog pick with the other Treeborn Frog).
            bool Allows(string k) => check.RuleAllows(k) ||
                (check.RootRuleAllowed?.Keys.Any(other => other != k && SameKindOfPlace(other, k) && check.RuleAllows(other)) ?? false);
            string Had(string k) => $"on offer after the rules in {check.RootRuleAllowed?.GetValueOrDefault(k) ?? 0} of {check.RootRuleArrivals} lines, " +
                                    $"the most-offered answer in {check.RootRuleAllowed?.Values.DefaultIfEmpty(0).Max() ?? 0}";
            if (Allows(key))
            {
                bot.LogNote($"search: rule gate: {Label(answers, key)} passes ({Had(key)})");
                return null;
            }
            // Never offered by the line's engine at all (in any copy): not a rule's removal but a key the line doesn't have (DuelSim 2026-11-26:
            // their face-down card, keyed differently in the rebuild, was "left out", and the gate made Dark Armed Dragon destroy itself).
            bool Offered(string k) => check.RootEngineOffered is { } engine &&
                (engine.GetValueOrDefault(k) > 0 || engine.Any(kv => kv.Value > 0 && kv.Key != k && SameKindOfPlace(kv.Key, k)));
            if (!Offered(key))
            {
                Interlocked.Increment(ref RuleGateUnchecked);
                FlagReplayIssue(bot, $"search: RULE GATE UNCHECKED: the line's {state} never offers {Label(answers, key)} ({key}; the engine's answers there: " +
                    $"{string.Join(", ", check.RootEngineOffered?.Keys.Take(8) ?? Enumerable.Empty<string>())}); it goes as it is");
                return null;
            }
            var allowed = options.Where(Allows).ToHashSet();
            if (allowed.Count == 0)
            {
                bot.LogNote($"search: RULE GATE: {Label(answers, key)} is left out by the rules ({Had(key)}), but so is every answer here; it goes as it is");
                return null;
            }
            // The replacement: the search's best among the allowed answers, searched in full (the check's own lines if that fails).
            string prior = allowed.OrderByDescending(k => check.RootRuleAllowed!.GetValueOrDefault(k)).First();
            var fullSearch = await Check(prior, full: true);
            string replacement = (fullSearch != null ? BestByScore(fullSearch, prior, allowed)?.Key : null) ?? BestByScore(check, prior, allowed)?.Key ?? prior;
            Interlocked.Increment(ref RuleGateReplaced);
            bot.LogNote($"search: RULE GATE: {Label(answers, key)} is left out by the rules at this prompt ({Had(key)}); playing {Label(answers, replacement)} instead");
            if (_searchLine != null || _plan != null)
                DropPlan(bot, "its step failed the rule gate");
            return answers.First(a => a.Key == replacement).Answer;
        }

        /// <summary>Two answer keys of the same card in the same kind of place: equal once the place's last index (a graveyard position) is
        /// dropped ("card:12538374@0.16.3" and "card:12538374@0.16.5").</summary>
        private static bool SameKindOfPlace(string a, string b)
        {
            // Hand, Deck and Extra Deck copies are interchangeable (SearchActions.CardKey): the same card there is the same answer.
            static bool Pile(string k) => k.Contains("@hand") || k.Contains(".deck") || k.Contains(".extra");
            if (Pile(a) && Pile(b))
                return SearchActions.KeyCardId(a) == SearchActions.KeyCardId(b);
            return System.Text.RegularExpressions.Regex.Replace(a, @"(@\d\.\d+)\.\d+", "$1") == System.Text.RegularExpressions.Regex.Replace(b, @"(@\d\.\d+)\.\d+", "$1");
        }

        private static string Label(List<(string Key, string Label, object Answer)> answers, string key) =>
            answers.FirstOrDefault(a => a.Key == key).Label ?? key;

        /// <summary>The prompt's answers as the search keys them (its root's move list uses the same keys), each with its label and the answer
        /// to send. Null: a prompt the search doesn't key (zones, sums, announcements).</summary>
        private static List<(string Key, string Label, object Answer)>? Answers(BotState state, object data)
        {
            switch (data)
            {
                case IdleCmdData idle:
                    return SearchActions.Idle(idle).Select(c => (c.Key, c.Label, (object)c.Response)).ToList();
                case BattleCmdData battle:
                    return SearchActions.Battle(battle).Select(c => (c.Key, c.Label, (object)c.Response)).ToList();
                case SelectChainData chain:
                {
                    var list = new List<(string, string, object)>();
                    if (!chain.Forced)
                        list.Add(("pass", "don't chain", (object)(-1)));
                    var cards = chain.SelectableCards ?? Array.Empty<DynamicCard>();
                    var descs = chain.Descriptions ?? Array.Empty<int>();
                    for (int i = 0; i < cards.Length; i++)
                        list.Add(($"chain:{SearchActions.CardKey(cards[i])}:{(i < descs.Length ? descs[i] : 0)}", $"chain {CardNames.Get(cards[i].ID)}", (object)i));
                    return list.GroupBy(a => a.Item1).Select(g => g.First()).ToList();
                }
                case SelectCardData select when (select.SelectableCards?.Length ?? 0) > 0:
                {
                    bool attackTarget = select.SelectionHint == (int)DHint.ATTACKTARGET;
                    return Picks(SearchActions.CardPicks(select.SelectableCards!, attackTarget ? Math.Max(1, select.Minimum) : select.Minimum, select.Maximum, false));
                }
                case SelectTributeData tribute when (tribute.SelectableCards?.Length ?? 0) > 0:
                    return Picks(SearchActions.CardPicks(tribute.SelectableCards!, tribute.Minimum, tribute.Maximum, false));
                case SelectUnselectCardData unselect when (unselect.SelectableCards?.Length ?? 0) > 0:
                    return Picks(SearchActions.CardPicks(unselect.SelectableCards!, Math.Min(unselect.Minimum, 1), 1, unselect.Minimum == 0 || unselect.Cancelable));
                case SelectEffectYnData or SelectYesNoData:
                    return new() { ("yes", "yes", true), ("no", "no", false) };
                case SelectOptionData option:
                    return (option.Options ?? Array.Empty<int>()).Select((o, i) => ($"option:{o}", $"option {o}", (object)i)).ToList();
                case SelectPositionData position:
                    return (position.Positions ?? Array.Empty<byte>()).Select(p => ($"pos:{p}", ((CardPosition)p).ToString(), (object)(CardPosition)p)).ToList();
                default:
                    return null;
            }
        }

        private static List<(string Key, string Label, object Answer)> Picks(List<SearchActions.Pick> picks) =>
            picks.GroupBy(p => p.Key).Select(g => g.First())
                .Select(p => (p.Key, p.Label, (object)(IList<byte>)p.Indexes.Select(i => (byte)i).ToList())).ToList();

        /// <summary>The key of the answer about to be sent. Null when it's none of the keyed answers (a multi-card pick the keys don't list).</summary>
        private static string? KeyOf(BotState state, object data, object answer, List<(string Key, string Label, object Answer)> answers)
        {
            switch (answer)
            {
                case IdleCmdResponse idle:
                    return answers.FirstOrDefault(a => a.Answer is IdleCmdResponse r && r.Action == idle.Action && r.Index == idle.Index).Key;
                case BattleCmdResponse battle:
                    return answers.FirstOrDefault(a => a.Answer is BattleCmdResponse r && r.Action == battle.Action && r.Index == battle.Index).Key;
                case int index when data is SelectChainData:
                    return answers.FirstOrDefault(a => a.Answer is int i && i == (index < 0 ? -1 : index)).Key;
                case int index when data is SelectOptionData:
                    return answers.FirstOrDefault(a => a.Answer is int i && i == index).Key;
                case bool yes:
                    return yes ? "yes" : "no";
                case CardPosition position:
                    return answers.FirstOrDefault(a => a.Answer is CardPosition p && p == position).Key;
                case IList<byte> picked:
                {
                    var set = picked.OrderBy(b => b).ToList();
                    return answers.FirstOrDefault(a => a.Answer is IList<byte> p && p.OrderBy(b => b).SequenceEqual(set)).Key;
                }
                default:
                    return null;
            }
        }
    }
}
