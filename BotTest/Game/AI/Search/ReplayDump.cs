using System.Text.Json;
using BotTest.Game.AI.Brains;
using OmegaBotWrapper.Enum;

namespace BotTest.Game.AI.Search
{
    /// <summary>
    /// A live turn's replay input written to a file when its lines miss (user, 2026-11-25: "yes" to a tool that re-runs a live turn's replay):
    /// the turn-start snapshot, every checkpoint of ours with the board we saw and the answer we sent, and the decision being searched (its
    /// state, options and key). DuelSim replay-check loads it and runs the same search offline, so a replay fix can be checked without a duel.
    /// Only what the replay and the search's root check use is kept (SearchView.Clone's fields, less the threat history and turn record).
    /// </summary>
    internal static class ReplayDump
    {
        internal sealed class File
        {
            public string Deck { get; set; } = "";
            public string? Log { get; set; }
            public int Decision { get; set; }
            public string Note { get; set; } = "";
            public int Turn { get; set; }
            public bool Ours { get; set; }
            public bool BlankNextDraw { get; set; }
            /// <summary>The searched decision (null: an end-of-turn audit, every checkpoint answered).</summary>
            public BotState? RootState { get; set; }
            public string? RootKey { get; set; }
            public List<string> Options { get; set; } = new();
            public ViewDto Start { get; set; } = new();
            public List<CheckpointDto> Checkpoints { get; set; } = new();
            /// <summary>A mid-chain replan (from the last searched decision to a chain window) instead of a replay from the turn's start.</summary>
            public MidChainDto? MidChain { get; set; }
        }

        /// <summary>
        /// A mid-chain replan's input (MidChainSearchAsync): the anchor decision's board as the search loads it, our real answers since, their
        /// cards revealed since, the window's key, and what we'd used this turn.
        /// </summary>
        internal sealed class MidChainDto
        {
            public ViewDto View { get; set; } = new();
            public BotState AnchorState { get; set; }
            public List<StepDto> Ours { get; set; } = new();
            public List<RevealDto> Theirs { get; set; } = new();
            public string TargetKey { get; set; } = "";
            public List<int> OurHandNow { get; set; } = new();
            public List<int[]> OurFieldNow { get; set; } = new();
            public List<int> Activated { get; set; } = new();
            public List<string> UsedEffects { get; set; } = new();
            public string BrainKey { get; set; } = "";
        }

        internal sealed class StepDto
        {
            public BotState State { get; set; }
            public string Key { get; set; } = "";
            public string Label { get; set; } = "";
            public string? Exact { get; set; }
        }

        internal sealed class RevealDto
        {
            public int Code { get; set; }
            public bool FromHand { get; set; }
            public bool Monster { get; set; }
            public int Zone { get; set; }
            public int AfterOurAnswers { get; set; }
            public List<int[]>? Targets { get; set; }
        }

        public static MidChainDto MidChainOf(SearchView view, ReplayScript script, IEnumerable<int> activated, IEnumerable<string> usedEffects, string brainKey) => new()
        {
            View = ViewDto.Of(view), AnchorState = script.AnchorState,
            Ours = script.Ours.Select(s => new StepDto { State = s.State, Key = s.Key, Label = s.Label, Exact = s.Exact }).ToList(),
            Theirs = script.Theirs.Select(r => new RevealDto
            {
                Code = r.Code, FromHand = r.FromHand, Monster = r.Monster, Zone = r.Zone, AfterOurAnswers = r.AfterOurAnswers,
                Targets = r.Targets?.Select(t => new[] { t.Controller, t.Location, t.Sequence }).ToList(),
            }).ToList(),
            TargetKey = script.TargetKey, OurHandNow = script.OurHandNow.ToList(),
            OurFieldNow = script.OurFieldNow.Select(f => new[] { f.Location, f.Sequence, f.Code }).ToList(),
            Activated = activated.ToList(), UsedEffects = usedEffects.ToList(), BrainKey = brainKey,
        };

        /// <summary>The mid-chain replan's view and script, as live.</summary>
        public static (SearchView View, ReplayScript Script) MidChainReplay(MidChainDto m) =>
            (m.View.ToView(),
             new ReplayScript(m.AnchorState,
                 m.Ours.Select(s => new ReplayStep(s.State, s.Key, s.Label) { Exact = s.Exact }).ToList(),
                 m.Theirs.Select(r => new ReplayReveal(r.Code, r.FromHand, r.Monster, r.Zone, r.AfterOurAnswers)
                 {
                     Targets = r.Targets?.Where(t => t.Length == 3).Select(t => (t[0], t[1], t[2])).ToList(),
                 }).ToList(),
                 m.TargetKey)
             {
                 OurHandNow = m.OurHandNow,
                 OurFieldNow = m.OurFieldNow.Where(f => f.Length == 3).Select(f => (f[0], f[1], f[2])).ToHashSet(),
             });

        internal sealed class CheckpointDto
        {
            public BotState State { get; set; }
            public int Phase { get; set; }
            public ViewDto View { get; set; } = new();
            public BotState? AnswerState { get; set; }
            public string? AnswerKey { get; set; }
            public string? AnswerLabel { get; set; }
            public string? AnswerExact { get; set; }
            public List<string>? ChainOptions { get; set; }
            public int ChainCard { get; set; }
            public List<int>? ChainTargets { get; set; }
        }

        internal sealed class CardDto
        {
            public int Code { get; set; }
            public int Sequence { get; set; }
            public uint Position { get; set; }
            public int Owner { get; set; }
            public bool Attacked { get; set; }
            public int[]? EquipTarget { get; set; }
            public List<int[]> Targets { get; set; } = new();
            public List<int[]> Counters { get; set; } = new();
            public bool Improper { get; set; }
            public bool ThisTurn { get; set; }
            public bool NoAttack { get; set; }
            public int BaseAttack { get; set; } = -1;
            public int BaseDefense { get; set; } = -1;
            public bool Battled { get; set; }
            public int Attack { get; set; } = -1;
            public int Defense { get; set; } = -1;
            public bool Borrowed { get; set; }
            public bool DestroyAtOwnersEnd { get; set; }
            public int PositionLockEnds { get; set; }
            public int UsedEffectMask { get; set; }
            public int FutureFusionCounter { get; set; } = -1;
            public int FutureFusionCode { get; set; }

            public static CardDto Of(ViewCard c) => new()
            {
                Code = c.Code, Sequence = c.Sequence, Position = c.Position, Owner = c.Owner, Attacked = c.Attacked,
                EquipTarget = c.EquipTarget is { } e ? new[] { e.Controller, e.Location, e.Sequence } : null,
                Targets = c.Targets.Select(t => new[] { t.Controller, t.Location, t.Sequence }).ToList(),
                Counters = c.Counters.Select(t => new[] { t.Type, t.Count }).ToList(),
                Improper = c.Improper, ThisTurn = c.ThisTurn, NoAttack = c.NoAttack, BaseAttack = c.BaseAttack, BaseDefense = c.BaseDefense,
                Battled = c.Battled, Attack = c.Attack, Defense = c.Defense, Borrowed = c.Borrowed, DestroyAtOwnersEnd = c.DestroyAtOwnersEnd,
                PositionLockEnds = c.PositionLockEnds, UsedEffectMask = c.UsedEffectMask,
                FutureFusionCounter = c.FutureFusionCounter, FutureFusionCode = c.FutureFusionCode,
            };

            public ViewCard ToCard() => new(Code, Sequence, Position, Owner, Attacked,
                EquipTarget is { Length: 3 } e ? (e[0], e[1], e[2]) : null,
                Targets.Where(t => t.Length == 3).Select(t => (t[0], t[1], t[2])).ToList(),
                Counters.Where(t => t.Length == 2).Select(t => (t[0], t[1])).ToList(),
                Improper, ThisTurn, NoAttack, BaseAttack, BaseDefense, Battled, Attack, Defense, Borrowed, DestroyAtOwnersEnd,
                PositionLockEnds, UsedEffectMask, FutureFusionCounter, FutureFusionCode);
        }

        internal sealed class ViewDto
        {
            public int[]? Attack { get; set; } // player, attacker code, attacker zone, target code, target zone (-1: direct)
            public int[] LifePoints { get; set; } = new int[2];
            public List<int> EnemyKnownHand { get; set; } = new();
            public List<int> OwnHand { get; set; } = new();
            public List<int> OwnDeck { get; set; } = new();
            public List<int> OwnGrave { get; set; } = new();
            public List<int> OwnBanished { get; set; } = new();
            public List<int> OwnExtra { get; set; } = new();
            public List<CardDto> OwnMonsters { get; set; } = new();
            public List<CardDto> OwnSpells { get; set; } = new();
            public int EnemyHandCount { get; set; }
            public int EnemyExtraCount { get; set; }
            public List<int> EnemyGrave { get; set; } = new();
            public List<int> EnemyBanished { get; set; } = new();
            public Dictionary<int, int> OwnGraveImproper { get; set; } = new();
            public Dictionary<int, int> EnemyGraveImproper { get; set; } = new();
            public List<CardDto> EnemyMonsters { get; set; } = new();
            public List<CardDto> EnemySpells { get; set; } = new();
            public Dictionary<int, List<double[]>> EnemySetMonsterOdds { get; set; } = new();
            public bool NormalSummonUsed { get; set; }
            public bool EnemyNormalSummonUsed { get; set; }
            public bool BattlePhaseDone { get; set; }
            public bool FirstTurnOfDuel { get; set; }
            public int RealTurn { get; set; } = 1;
            public TagRules.BotStrategicState StrategicState { get; set; }
            public int OpponentSummonedAndAttackedWithThreatTurn { get; set; } = -1;
            public bool InBattlePhase { get; set; }
            public int OnlyAttackerZone { get; set; } = -1;
            public List<int> Main2Summoned { get; set; } = new();
            public List<int> ActivatedThisTurn { get; set; } = new();
            public bool LylaEffectUsed { get; set; }
            public int DrawsAtStart { get; set; }
            public List<int> KnownTop { get; set; } = new();
            public List<int> ForcedDraws { get; set; } = new();
            public List<int> RandomPicks { get; set; } = new();
            public Dictionary<int, int> ArrivedThisTurn { get; set; } = new();
            public Dictionary<int, int> EnemyArrivedThisTurn { get; set; } = new();
            public int SkipTurns { get; set; }
            public bool BlankNextDraw { get; set; }
            public List<int> EnemyKnownExtra { get; set; } = new();
            public List<int> EnemyDeckTop { get; set; } = new();
            public List<int> EnemyDeckKnown { get; set; } = new();
            public List<int> EnemyDeckRest { get; set; } = new();
            public int EnemyLikelySetDefense { get; set; } = -1;
            public bool EnemyHonestSuspected { get; set; }

            public static ViewDto Of(SearchView v) => new()
            {
                Attack = v.AttackInProgress is { } a
                    ? new[] { a.Player, a.Attacker.Code, a.Attacker.Zone, a.Target?.Code ?? 0, a.Target?.Zone ?? -1 } : null,
                LifePoints = v.LifePoints.ToArray(),
                EnemyKnownHand = v.EnemyKnownHand.ToList(), OwnHand = v.OwnHand.ToList(), OwnDeck = v.OwnDeck.ToList(), OwnGrave = v.OwnGrave.ToList(),
                OwnBanished = v.OwnBanished.ToList(), OwnExtra = v.OwnExtra.ToList(),
                OwnMonsters = v.OwnMonsters.Select(CardDto.Of).ToList(), OwnSpells = v.OwnSpells.Select(CardDto.Of).ToList(),
                EnemyHandCount = v.EnemyHandCount, EnemyExtraCount = v.EnemyExtraCount,
                EnemyGrave = v.EnemyGrave.ToList(), EnemyBanished = v.EnemyBanished.ToList(),
                OwnGraveImproper = new(v.OwnGraveImproper), EnemyGraveImproper = new(v.EnemyGraveImproper),
                EnemyMonsters = v.EnemyMonsters.Select(CardDto.Of).ToList(), EnemySpells = v.EnemySpells.Select(CardDto.Of).ToList(),
                EnemySetMonsterOdds = v.EnemySetMonsterOdds.ToDictionary(kv => kv.Key, kv => kv.Value.Select(o => new[] { o.Id, o.Chance }).ToList()),
                NormalSummonUsed = v.NormalSummonUsed, EnemyNormalSummonUsed = v.EnemyNormalSummonUsed, BattlePhaseDone = v.BattlePhaseDone, FirstTurnOfDuel = v.FirstTurnOfDuel, RealTurn = v.RealTurn,
                StrategicState = v.StrategicState, OpponentSummonedAndAttackedWithThreatTurn = v.OpponentSummonedAndAttackedWithThreatTurn,
                InBattlePhase = v.InBattlePhase, OnlyAttackerZone = v.OnlyAttackerZone, Main2Summoned = v.Main2Summoned.ToList(),
                ActivatedThisTurn = v.ActivatedThisTurn.ToList(), LylaEffectUsed = v.LylaEffectUsed, DrawsAtStart = v.DrawsAtStart,
                KnownTop = v.KnownTop.ToList(), ForcedDraws = v.ForcedDraws.ToList(), RandomPicks = v.RandomPicks.ToList(),
                ArrivedThisTurn = new(v.ArrivedThisTurn), EnemyArrivedThisTurn = new(v.EnemyArrivedThisTurn),
                SkipTurns = v.SkipTurns, BlankNextDraw = v.BlankNextDraw, EnemyKnownExtra = v.EnemyKnownExtra.ToList(), EnemyDeckTop = v.EnemyDeckTop.ToList(), EnemyDeckKnown = v.EnemyDeckKnown.ToList(), EnemyDeckRest = v.EnemyDeckRest.ToList(),
                EnemyLikelySetDefense = v.EnemyLikelySetDefense, EnemyHonestSuspected = v.EnemyHonestSuspected,
            };

            public SearchView ToView()
            {
                var v = new SearchView
                {
                    AttackInProgress = Attack is { Length: 5 } a
                        ? new SearchView.BattleAttack(a[0], new SearchView.BattleCard(a[1], a[2]), a[4] < 0 ? null : new SearchView.BattleCard(a[3], a[4]))
                        : null,
                    EnemyHandCount = EnemyHandCount, EnemyExtraCount = EnemyExtraCount, NormalSummonUsed = NormalSummonUsed, EnemyNormalSummonUsed = EnemyNormalSummonUsed,
                    BattlePhaseDone = BattlePhaseDone, FirstTurnOfDuel = FirstTurnOfDuel, RealTurn = RealTurn, InBattlePhase = InBattlePhase,
                    StrategicState = StrategicState, OpponentSummonedAndAttackedWithThreatTurn = OpponentSummonedAndAttackedWithThreatTurn,
                    DrawsAtStart = DrawsAtStart, SkipTurns = SkipTurns, BlankNextDraw = BlankNextDraw, OnlyAttackerZone = OnlyAttackerZone,
                    LylaEffectUsed = LylaEffectUsed, EnemyLikelySetDefense = EnemyLikelySetDefense,
                    EnemyHonestSuspected = EnemyHonestSuspected,
                };
                v.LifePoints[0] = LifePoints.ElementAtOrDefault(0);
                v.LifePoints[1] = LifePoints.ElementAtOrDefault(1);
                v.EnemyKnownHand.AddRange(EnemyKnownHand); v.OwnHand.AddRange(OwnHand); v.OwnDeck.AddRange(OwnDeck); v.OwnGrave.AddRange(OwnGrave);
                v.OwnBanished.AddRange(OwnBanished); v.OwnExtra.AddRange(OwnExtra);
                v.OwnMonsters.AddRange(OwnMonsters.Select(c => c.ToCard())); v.OwnSpells.AddRange(OwnSpells.Select(c => c.ToCard()));
                v.EnemyGrave.AddRange(EnemyGrave); v.EnemyBanished.AddRange(EnemyBanished);
                foreach (var kv in OwnGraveImproper) v.OwnGraveImproper[kv.Key] = kv.Value;
                foreach (var kv in EnemyGraveImproper) v.EnemyGraveImproper[kv.Key] = kv.Value;
                v.EnemyMonsters.AddRange(EnemyMonsters.Select(c => c.ToCard())); v.EnemySpells.AddRange(EnemySpells.Select(c => c.ToCard()));
                foreach (var kv in EnemySetMonsterOdds)
                    v.EnemySetMonsterOdds[kv.Key] = kv.Value.Where(o => o.Length == 2).Select(o => ((int)o[0], o[1])).ToList();
                v.Main2Summoned.AddRange(Main2Summoned); v.ActivatedThisTurn.AddRange(ActivatedThisTurn);
                v.KnownTop.AddRange(KnownTop); v.ForcedDraws.AddRange(ForcedDraws); v.RandomPicks.AddRange(RandomPicks);
                foreach (var kv in ArrivedThisTurn) v.ArrivedThisTurn[kv.Key] = kv.Value;
                foreach (var kv in EnemyArrivedThisTurn) v.EnemyArrivedThisTurn[kv.Key] = kv.Value;
                v.EnemyKnownExtra.AddRange(EnemyKnownExtra); v.EnemyDeckTop.AddRange(EnemyDeckTop); v.EnemyDeckKnown.AddRange(EnemyDeckKnown); v.EnemyDeckRest.AddRange(EnemyDeckRest);
                return v;
            }
        }

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

        /// <summary>Writes the replay input; returns the file's path, or null when it couldn't be written (never costs the bot its answer).</summary>
        public static string? Save(string directory, string fileName, File dump)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, fileName);
                System.IO.File.WriteAllText(path, JsonSerializer.Serialize(dump, Options));
                return path;
            }
            catch
            {
                return null;
            }
        }

        public static File Make(string deck, string? log, int decision, string note, SearchView start, bool ours, int turn,
            IReadOnlyList<ReplayCheckpoint> checkpoints, bool blankNextDraw, BotState? rootState, string? rootKey, IEnumerable<string>? options) => new()
        {
            Deck = deck, Log = log, Decision = decision, Note = note, Turn = turn, Ours = ours, BlankNextDraw = blankNextDraw,
            RootState = rootState, RootKey = rootKey, Options = options?.ToList() ?? new(),
            Start = ViewDto.Of(start),
            Checkpoints = checkpoints.Select(cp => new CheckpointDto
            {
                State = cp.State, Phase = cp.Phase, View = ViewDto.Of(cp.View),
                AnswerState = cp.Answer?.State, AnswerKey = cp.Answer?.Key, AnswerLabel = cp.Answer?.Label, AnswerExact = cp.Answer?.Exact,
                ChainOptions = cp.ChainOptions?.ToList(), ChainCard = cp.ChainCard, ChainTargets = cp.ChainTargets?.ToList(),
            }).ToList(),
        };

        public static File Load(string path) =>
            JsonSerializer.Deserialize<File>(System.IO.File.ReadAllText(path), Options) ?? throw new InvalidDataException($"not a replay dump: {path}");

        /// <summary>The dump's turn replay, prepared as live (TurnReplay.Prepare on the turn-start snapshot and the checkpoints).</summary>
        public static TurnReplay Replay(File dump)
        {
            var checkpoints = dump.Checkpoints.Select(cp => new ReplayCheckpoint(cp.State, cp.Phase, cp.View.ToView(),
                cp.AnswerKey == null ? null : new ReplayStep(cp.AnswerState ?? cp.State, cp.AnswerKey, cp.AnswerLabel ?? cp.AnswerKey) { Exact = cp.AnswerExact },
                cp.ChainOptions, cp.ChainCard, cp.ChainTargets)).ToList();
            var replay = TurnReplay.Prepare(dump.Start.ToView(), dump.Ours, dump.Turn, checkpoints);
            replay.Start.BlankNextDraw = dump.BlankNextDraw;
            return replay;
        }
    }
}
