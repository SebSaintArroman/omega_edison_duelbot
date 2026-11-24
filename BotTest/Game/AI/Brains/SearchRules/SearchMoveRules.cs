using BotTest.Game.AI.Search;
using DuelSim.Engine;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>An answer the play search may give at a prompt, and why it's forced (null: a free choice).</summary>
    internal sealed record MoveOption(string Key, string Label, byte[] Packet, string? MandatoryReason = null);

    /// <summary>
    /// The card rules as the play search sees them: which of the moves on offer the rules allow, and which they force. The
    /// search lists the moves (Search/Mcts.OptionsFor) without knowing any card; everything card- or board-specific is here:
    /// the tags' meaning (TagRules), the cards' own rules (CardRules.Definitions), and the card-rule context built from a
    /// search position (BaseContext). One instance per search (Mcts).
    /// </summary>
    internal sealed class SearchMoveRules
    {
        private readonly SearchView _view;
        private readonly TagRules.BotStrategicState _state;
        private readonly int _plannedSetCardId;
        private readonly bool _viewTurnActivationsCount;
        private readonly Func<int, long, bool> _effectUsed;
        private readonly int _rootTurn;
        private readonly Func<bool, DuelPhase, SideSummary, bool>? _backrowDoomRule;
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DuelHost, ThreatenedState> _threatHistories = new();

        /// <param name="plannedSetCardId">The card a routine of the brain's plan sets (it's exempt from the set rules), or 0.</param>
        /// <param name="viewTurnActivationsCount">The view's record of this turn's activations still applies (the search didn't replay
        /// the real turn from its start).</param>
        /// <param name="effectUsed">Whether our effect (card, description) was used this turn before the search's root.</param>
        public SearchMoveRules(SearchView view, TagRules.BotStrategicState state, int plannedSetCardId, bool viewTurnActivationsCount,
            Func<int, long, bool> effectUsed, int rootTurn, Func<bool, DuelPhase, SideSummary, bool>? backrowDoomRule)
        {
            _view = view;
            _state = state;
            _plannedSetCardId = plannedSetCardId;
            _viewTurnActivationsCount = viewTurnActivationsCount;
            _effectUsed = effectUsed;
            _rootTurn = rootTurn;
            _backrowDoomRule = backrowDoomRule;
        }

        // ------------------------------------------------------------------ the card-rule context of a search position

        /// <summary>
        /// The card-rule context's base from a search position (or, without one, the decision's view): our deck and hand, their
        /// hand size, both life points. Each prompt adds what its rules read.
        /// </summary>
        private CardRuleRegistry.Context BaseContext(SideSummary? own, SideSummary? theirs, int ownLp, int theirLp) =>
            new(own?.DeckCodes ?? _view.OwnDeck, own?.HandCodes ?? _view.OwnHand, theirs?.Hand ?? 0, ownLp, theirLp, false,
                PlannedSetCardId: _plannedSetCardId);

        private static bool CanWinThisTurn(DuelHost host)
        {
            var own = host.Summary(0);
            var enemy = host.Summary(1);
            var attackers = own.Monsters.Where(monster => monster.FaceUp && monster.AttackPosition)
                .Select(monster => monster.Attack);
            var defenders = enemy.Monsters.Select(monster => new BoardAssessment.Defender(
                monster.FaceUp ? monster.AttackPosition ? monster.Attack : monster.Defense : BoardAssessment.AssumedFaceDownDefense,
                monster.FaceUp && monster.AttackPosition, !monster.FaceUp, monster.Code));
            return BoardAssessment.TurnsToWin(attackers, defenders, enemy.LifePoints) == 1;
        }

        private static int[] TargetedEnemyGraveCards(DuelHost host, int viewer)
        {
            var field = host.FieldView(viewer);
            var grave = field.EnemySide.Grave ?? Array.Empty<int>();
            var targeted = new HashSet<int>();
            void CheckZones(CardArray[] zones, CardLocation location)
            {
                for (int sequence = 0; sequence < zones.Length; sequence++)
                {
                    if ((zones[sequence].Cards?.Length ?? 0) == 0) continue;
                    if (host.CardAt(viewer, 1, (int)location, sequence) is not { } card) continue;
                    foreach (var target in (card.CurrentChainTargets ?? Array.Empty<CardCoordinate>())
                                 .Concat(card.TargetCards ?? Array.Empty<CardCoordinate>()))
                        if (target.Controller == 1 && target.Location == (byte)CardLocation.Grave && target.Sequence < grave.Length)
                            targeted.Add(grave[target.Sequence]);
                }
            }
            CheckZones(field.EnemySide.MonsterZones ?? Array.Empty<CardArray>(), CardLocation.MonsterZone);
            CheckZones(field.EnemySide.SpellZones ?? Array.Empty<CardArray>(), CardLocation.SpellZone);
            return targeted.ToArray();
        }

        /// <summary>Whether our Dark Grepher's discard effect is still to come this turn (for Reinforcement's Gardna search).</summary>
        private bool GrepherAvailable(DuelHost? host, SideSummary? own)
        {
            const int grepher = DarkGrepher;
            return !(_viewTurnActivationsCount && (_view.ActivatedThisTurn.Any(id => SearchHost.Is(id, grepher)) ||
                    _effectUsed(grepher, grepher * 16L + 1))) &&
                host?.ActivatedInTurn(0, host.Turn).Any(id => SearchHost.Is(id, grepher)) != true &&
                ((own?.HandCodes ?? _view.OwnHand).Any(id => SearchHost.Is(id, grepher)) ||
                 (own?.Monsters.Any(m => SearchHost.Is(m.Code, grepher)) ??
                  _view.OwnMonsters.Any(m => SearchHost.Is(m.Code, grepher))));
        }

        /// <summary>The threats on their board (ThreatenedState), kept per search position so a line's history carries on.</summary>
        public ThreatenedState.Assessment AssessThreats(DuelHost host, SideSummary own, SideSummary enemy)
        {
            if (!enemy.Monsters.Any(card => card.FaceUp) && !own.SpellCodes.Any(card => SearchHost.Is(card.Code, BottomlessTrapHole)))
            {
                if (_threatHistories.TryGetValue(host, out var current))
                    current.Clear();
                return new(Array.Empty<StrategicCard>(), 0, PressuringState.Applies(own, enemy));
            }
            var history = _threatHistories.GetValue(host, _ => _view.ThreatHistory.Copy());
            return history.Evaluate(own.DeckCodes, own.HandCodes, host.StrategicCards(0, false),
                host.StrategicCards(0, true), host.StrategicCards(1, false), enemy.Hand, own.LifePoints, enemy.LifePoints);
        }

        // ------------------------------------------------------------------ main phase

        /// <summary>The rules for one main-phase prompt: Allowed filters each (card, action), Finish forces what must be done.</summary>
        public MainMoves ForMain(Prompt prompt, IdleCmdData idle, bool ourTurn, DuelHost? host, bool setWindowChosen, bool allowSetsBeforePlays,
            bool positionWindowChosen = false, bool setPlanDone = false) =>
            new(this, prompt, idle, ourTurn, host, setWindowChosen, allowSetsBeforePlays, positionWindowChosen, setPlanDone);

        internal sealed class MainMoves
        {
            private readonly SearchMoveRules _rules;
            private readonly Prompt _prompt;
            private readonly IdleCmdData _idle;
            private readonly DuelHost? _host;
            private readonly bool _setWindowChosen, _allowSetsBeforePlays, _positionWindowChosen, _setPlanDone;
            private readonly SideSummary? _own, _theirs;
            private readonly ThreatenedState.Assessment? _threatAssessment;
            private readonly TagRules.BotStrategicState _strategicState;
            private readonly int _phase;
            private readonly DynamicCard[] _repositionable;
            private readonly bool _faceUpPositionChangeAvailable;
            private readonly IEnumerable<DynamicCard> _summons;
            private readonly int[]? _ownMonsterIds;
            private readonly int _opponentLp;
            private readonly CardRuleRegistry.Context? _baseContext;
            private bool? _plagueMayRevive;
            private CardRuleRegistry.Context[]? _threatContexts;
            private IReadOnlyList<BoardAssessment.Defender>? _threats;
            private Dictionary<(int Id, int Attack), bool>? _threatAnswers;

            public MainMoves(SearchMoveRules rules, Prompt prompt, IdleCmdData idle, bool ourTurn, DuelHost? host, bool setWindowChosen, bool allowSetsBeforePlays,
                bool positionWindowChosen = false, bool setPlanDone = false)
            {
                _setPlanDone = setPlanDone && ourTurn && prompt.Player == 0;
                _rules = rules;
                _prompt = prompt;
                _idle = idle;
                _host = host;
                _setWindowChosen = setWindowChosen;
                _positionWindowChosen = positionWindowChosen;
                _allowSetsBeforePlays = allowSetsBeforePlays;
                bool checkRules = ourTurn && prompt.Player == 0;
                _own = checkRules ? host?.Summary(0) : null;
                _theirs = checkRules ? host?.Summary(1) : null;
                _threatAssessment = host != null && _own != null && _theirs != null ? rules.AssessThreats(host, _own, _theirs) : null;
                _strategicState = rules._state == TagRules.BotStrategicState.Desperate ? rules._state : _threatAssessment?.State ?? rules._state;
                _phase = host?.Phase ?? 0;
                _repositionable = idle.ReposableCards ?? Array.Empty<DynamicCard>();
                _faceUpPositionChangeAvailable = checkRules && _repositionable.Any(c => (c.Position & 0x5) != 0);
                _summons = checkRules ? (idle.SummonableCards ?? Array.Empty<DynamicCard>())
                    .Concat(idle.SpecialSummonableCards ?? Array.Empty<DynamicCard>()) : Enumerable.Empty<DynamicCard>();
                _ownMonsterIds = _own?.Monsters.Select(monster => monster.Code).ToArray();
                _opponentLp = _theirs?.LifePoints ?? rules._view.LifePoints[1];
                int ownLp = host?.LifePoints(0) ?? rules._view.LifePoints[0];
                _baseContext = checkRules ? rules.BaseContext(_own, _theirs, ownLp, _opponentLp) : null;
            }

            private bool AnswersOne(DynamicCard card)
            {
                var key = (card.ID, card.Attack);
                _threatAnswers ??= new();
                if (_threatAnswers.TryGetValue(key, out bool answers))
                    return answers;
                for (int i = 0; i < _threats!.Count; i++)
                    if ((CardRuleRegistry.TagsFor(card.ID, _threatContexts![i]) & TagRules.Tag.AnswersAnyThreat) != 0 ||
                        (SearchHost.Type(card.ID) & 0x1) != 0 && card.Attack > _threats[i].Power)
                        return _threatAnswers[key] = true;
                return _threatAnswers[key] = false;
            }

            /// <summary>
            /// Whether the rules allow this (card, action):
            ///  - the set window: once the line chose to set, only sets and the end of the turn;
            ///  - timing: Flip Summons in Main Phase 1, face-up position changes only once nothing else is left; sets only in the
            ///    set window (unless it's the routine's planned set);
            ///  - the tags: DoNotActivate, DoNotSummon (but for the survival exception), DoNotSet, NonQuickPlaySpell, SetOnly
            ///    (TagRules.ForbidsNormalSummon); attack boosts wait for the damage step.
            /// </summary>
            public bool Allowed(int id, MainAction action)
            {
                var baseContext = _baseContext;
                if (baseContext == null) return true;
                var own = _own;
                var theirs = _theirs;
                var idle = _idle;
                var strategicState = _strategicState;
                // These are mandatory search windows: reposition face-up monsters in MP1; set cards
                // after battle in MP2, or in MP1 when no Battle Phase is available this turn.
                // The set window: sets, face-up position changes (the end of the turn's positions) and the end of the turn.
                if (strategicState != TagRules.BotStrategicState.Desperate && _setWindowChosen &&
                    action is not (MainAction.SetMonster or MainAction.SetSpell or MainAction.Repos or MainAction.ToEndPhase))
                    return false;
                // The position window: once the line changed a face-up monster's position it has committed to what comes next: in Main
                // Phase 1 the battle (more position changes, the Battle Phase or the end of the turn), in Main Phase 2 the end of the
                // turn (more position changes, sets, the end of the turn). User, 2026-11-24: "if it has committed to going to the battle
                // phase next, it should still be allowed to change the battle positions ... decide not to use substitoad's effect, then
                // change it to def, then go to battle"; "end of mp2 should offer the same battle position change window".
                if (_positionWindowChosen && !(action is MainAction.Repos or MainAction.ToEndPhase ||
                                               _phase == (int)DuelPhase.Main1 && action == MainAction.ToBattlePhase ||
                                               _phase == (int)DuelPhase.Main2 && action is MainAction.SetMonster or MainAction.SetSpell))
                    return false;
                if (action == MainAction.Repos)
                {
                    int match = Array.FindIndex(_repositionable, c => SearchHost.Is(c.ID, id));
                    if (match < 0) return false;
                    var card = _repositionable[match];
                    // Flip Summons stay available throughout Main Phase 1. Face-up position changes in either Main Phase; choosing
                    // one opens the position window above (the other plays are declined).
                    if ((card.Position & 0x5) == 0) return _phase == (int)DuelPhase.Main1;
                    if (_phase is not ((int)DuelPhase.Main1 or (int)DuelPhase.Main2)) return false;
                }
                if (action is MainAction.SetMonster or MainAction.SetSpell)
                {
                    bool setWindowPhase = _phase == (int)DuelPhase.Main2 || _phase == (int)DuelPhase.Main1;
                    // In Main Phase 1 (no Battle Phase) face-up position changes come first; in Main Phase 2 sets and position
                    // changes go in either order (both are the end of the turn).
                    if (strategicState != TagRules.BotStrategicState.Desperate && !SetsAnytime(id, action) &&
                        (!setWindowPhase || !_allowSetsBeforePlays && _faceUpPositionChangeAvailable && _phase == (int)DuelPhase.Main1)) return false;
                }
                if (action == MainAction.Activate && SearchHost.Is(id, PlaguespreaderZombie) && own != null && theirs != null)
                    _plagueMayRevive ??= CardRules.PlaguespreaderMayRevive(own, theirs, _host!.ExtraDeckCodes(0), idle.CanBattlePhase);
                bool noOtherAnswer = false;
                bool noHandAnswer = false;
                if (_host != null && own != null && theirs != null)
                {
                    if (_threats == null)
                    {
                        var defenders = theirs.Monsters.Where(m => m.FaceUp)
                            .Select(m => new BoardAssessment.Defender(m.AttackPosition ? m.Attack : m.Defense,
                                m.AttackPosition, false, m.Code)).ToList();
                        _threats = BoardAssessment.Threats(own.Monsters.Where(m => m.FaceUp && m.AttackPosition)
                            .Select(m => m.Attack), defenders);
                        bool opponentHasStardust = theirs.Monsters.Any(monster => SearchHost.Is(monster.Code, StardustDragon));
                        _threatContexts = _threats.Select(threat => baseContext with
                        {
                            ThreatCardId = threat.Id,
                            OpponentHasStardustDragon = opponentHasStardust,
                            ThreatAttackPosition = threat.AttackPosition,
                            ThreatTriggersWhenNormalSummoned = CardRules.ThreatTriggersOnNormalSummon(threat.Id)
                        }).ToArray();
                    }
                    if (_threats.Count > 0)
                    {
                        var availableSummons = _summons.Where(card => !SearchHost.Is(card.ID, id));
                        noOtherAnswer = !availableSummons.Any(card => AnswersOne(card) &&
                            (CardRuleRegistry.TagsFor(card.ID, baseContext) & TagRules.Tag.DoNotSummon) == 0);
                        noHandAnswer = !availableSummons.Concat(idle.ActivableCards ?? Array.Empty<DynamicCard>())
                            .Any(card => card.CurrentCoordinates.Location == (byte)CardLocation.Hand && AnswersOne(card));
                    }
                }
                var ruleContext = baseContext with
                {
                    NoOtherAnswer = noOtherAnswer,
                    OwnMonsterIds = _ownMonsterIds,
                    DrawTrapException = noHandAnswer || (idle.SummonableCards?.Length ?? 0) == 0 &&
                        (idle.MonsterSetableCards?.Length ?? 0) == 0,
                    PlaguespreaderMayRevive = _plagueMayRevive == true
                };
                if (action == MainAction.Activate && SearchHost.Is(id, ReinforcementOfTheArmy))
                {
                    bool usedSummon = _host?.SearchNormalSummonOrSetUsed == true || _rules._view.NormalSummonUsed;
                    ruleContext = ruleContext with
                    {
                        NormalSummonUsed = usedSummon,
                        DarkGrepherAvailable = _rules.GrepherAvailable(_host, own),
                        ReinforcementHasSingleTarget = usedSummon && ruleContext.OwnDeck
                            .Select(SearchHost.Cards.Get)
                            .Where(card => card != null && CardRules.ReinforcementCanSearch(card.Type, card.Race, card.Level))
                            .Select(card => card!.Alias > 0 ? card.Alias : card.Id).Distinct().Take(2).Count() == 1,
                    };
                }
                var cardTags = CardRuleRegistry.TagsFor(id, ruleContext);
                if (action == MainAction.Activate && (cardTags & TagRules.Tag.DoNotActivate) != 0)
                    return false;
                // An attack boost waits for the damage step (unless the card has another effect on offer).
                if (action == MainAction.Activate && (idle.ActivableCards ?? Array.Empty<DynamicCard>())
                    .Any(card => SearchHost.Is(card.ID, id) && CardRules.IsAttackBoost(card.ID, card.CurrentCoordinates.Location)) &&
                    !(idle.ActivableCards ?? Array.Empty<DynamicCard>()).Any(card => SearchHost.Is(card.ID, id) &&
                        !CardRules.IsAttackBoost(card.ID, card.CurrentCoordinates.Location)))
                    return false;
                if (action is MainAction.Summon or MainAction.SpSummon or MainAction.SetMonster &&
                    (cardTags & TagRules.Tag.DoNotSummon) != 0)
                {
                    bool anotherSummonAvailable = _summons
                        .Where(card => !SearchHost.Is(card.ID, id))
                        .Any(card => (CardRuleRegistry.TagsFor(card.ID, ruleContext) & TagRules.Tag.DoNotSummon) == 0);
                    var enemyAttack = theirs?.Monsters.Where(monster => monster.FaceUp && monster.AttackPosition)
                        .Select(monster => monster.Attack) ?? Enumerable.Empty<int>();
                    bool nextTurnLethal = _host != null && BoardAssessment.TurnsToWin(enemyAttack,
                        Array.Empty<BoardAssessment.Defender>(), _host.LifePoints(0)) == 1;
                    bool survivalException = TagRules.DoNotSummonSurvivalException(anotherSummonAvailable,
                        (own?.Monsters.Count ?? 0) > 0, nextTurnLethal);
                    if (TagRules.ForbidsSummonOrSet(cardTags, survivalException))
                        return false;
                }
                if (action is MainAction.SetMonster or MainAction.SetSpell && (cardTags & TagRules.Tag.NeverSet) != 0)
                    return false;
                if (action is MainAction.SetMonster or MainAction.SetSpell && (cardTags & TagRules.Tag.DoNotSet) != 0)
                {
                    if (action == MainAction.SetSpell || own == null || own.Monsters.Count > 0 || theirs == null || _host == null)
                        return false;
                    var blocker = (idle.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                        .FirstOrDefault(card => SearchHost.Is(card.ID, id));
                    if (blocker.ID <= 0 || !TagRules.SetPreventsLethal(blocker,
                        theirs.Monsters.Where(monster => monster.FaceUp && monster.AttackPosition).Select(monster => monster.Attack),
                        _host.LifePoints(0)))
                        return false;
                }
                if (action == MainAction.SetSpell && (cardTags & TagRules.Tag.NonQuickPlaySpell) != 0)
                    return false;
                if (action == MainAction.Summon && (cardTags & TagRules.Tag.SetOnly) != 0)
                {
                    int monsterAttack = (idle.SummonableCards ?? Array.Empty<DynamicCard>())
                        .FirstOrDefault(card => SearchHost.Is(card.ID, id)).Attack;
                    bool hasTuner = own != null && own.HandCodes.Concat(own.Monsters.Where(m => m.FaceUp).Select(m => m.Code))
                        .Any(code => (SearchHost.Type(code) & 0x1000) != 0);
                    var strategicThreats = _threatAssessment?.Threats ?? Array.Empty<StrategicCard>();
                    bool hasAlternativeSynchro = (idle.SpecialSummonableCards ?? Array.Empty<DynamicCard>())
                        .Any(card => (SearchHost.Type(card.ID) & 0x2000) != 0 && strategicThreats.Any(threat =>
                            card.Attack > (threat.AttackPosition ? threat.Attack : threat.Defense) ||
                            (CardRuleRegistry.TagsFor(card.ID, ruleContext with
                            {
                                ThreatCardId = threat.Code,
                                ThreatAttackPosition = threat.AttackPosition,
                                ThreatTriggersWhenNormalSummoned = CardRules.ThreatTriggersOnNormalSummon(threat.Code),
                                OpponentHasStardustDragon = theirs!.Monsters.Any(monster =>
                                    monster.FaceUp && SearchHost.Is(monster.Code, StardustDragon))
                            }) & TagRules.Tag.AnswersAnyThreat) != 0));
                    int projectedDamage = 0;
                    if (idle.CanBattlePhase && own != null && theirs != null)
                    {
                        var defenders = theirs.Monsters.Select(monster => new BoardAssessment.Defender(
                            monster.FaceUp ? monster.AttackPosition ? monster.Attack : monster.Defense : BoardAssessment.AssumedFaceDownDefense,
                            monster.FaceUp && monster.AttackPosition, !monster.FaceUp, monster.Code));
                        var attackers = own.Monsters.Where(monster => monster.FaceUp && monster.AttackPosition)
                            .Select(monster => monster.Attack).Append(monsterAttack);
                        int damageRequired = Math.Max(1, _opponentLp - 200);
                        if (BoardAssessment.TurnsToWin(attackers, defenders, damageRequired, maxTurns: 1) == 1)
                            projectedDamage = damageRequired;
                    }
                    if (TagRules.ForbidsNormalSummon(cardTags, strategicState, hasTuner, hasAlternativeSynchro, _opponentLp, projectedDamage))
                        return false;
                }
                return true;
            }

            /// <summary>
            /// A set the timing rules don't hold back: only the card the brain's routine plans to set. (Not AlwaysSet cards: Trap
            /// Dustshoot waits for the set window like any set; user, 2026-11-23.)
            /// </summary>
            private bool SetsAnytime(int id, MainAction action) =>
                action == MainAction.SetMonster && _rules._plannedSetCardId > 0 && SearchHost.Is(id, _rules._plannedSetCardId);

            /// <summary>
            /// The allowed moves, finished: no Battle Phase once the line is in the set window, no more spell/trap sets once its
            /// set plan is played out, a must-activate tag limits play to eligible activations, and mandatory moves are marked with
            /// their rule.
            /// </summary>
            public List<(SearchActions.Choice<IdleCmdResponse> Choice, string? Reason)> Finish(List<SearchActions.Choice<IdleCmdResponse>> choices)
            {
                var host = _host;
                var strategicState = _strategicState;
                if (_setWindowChosen)
                    choices = choices.Where(c => c.Response.Action != MainAction.ToBattlePhase).ToList();
                // Sets stay on offer beside the plays: the search sees the set window as one move (SearchActions.SetWindowKey) next
                // to them, and monster sets next to the Normal Summon (user, 2026-11-24: "it should be able to see 'go to set window'
                // in that list of options"; "I don't want them deprioritised against the normal summon"). Once the line's set plan
                // is played out, no more spells/traps are set.
                if (_setPlanDone)
                    choices.RemoveAll(c => c.Response.Action == MainAction.SetSpell);
                var ruleContext = _prompt.Player == 0
                    ? _rules.BaseContext(host?.Summary(0), host?.Summary(1), host?.LifePoints(0) ?? _rules._view.LifePoints[0],
                            host?.LifePoints(1) ?? _rules._view.LifePoints[1]) with
                    {
                        CanWinThisTurn = host != null && CanWinThisTurn(host),
                        OwnMonsterIds = host?.Summary(0).Monsters.Select(monster => monster.Code).ToArray(),
                        StrategicState = strategicState,
                    }
                    : null;
                Dictionary<string, string>? mandatoryReasons = null;
                if (ruleContext != null)
                {
                    var mandatorySets = choices.Where(c => c.Response.Action == MainAction.SetSpell &&
                        (CardRuleRegistry.TagsFor(SearchActions.KeyCardId(c.Key), ruleContext) & TagRules.Tag.AlwaysSet) != 0).ToList();
                    var mandatoryActivations = choices.Where(c =>
                    {
                        if (c.Response.Action != MainAction.Activate) return false;
                        int id = SearchActions.KeyCardId(c.Key);
                        var tags = CardRuleRegistry.TagsFor(id, ruleContext);
                        return (tags & TagRules.Tag.ActivateAtFirstOpportunity) != 0 ||
                            (tags & TagRules.Tag.ActivateOnSummon) != 0 && host?.IsSummonWindow(0, id) == true;
                    }).ToList();
                    if (mandatoryActivations.Count > 0)
                    {
                        choices = mandatoryActivations;
                        mandatoryReasons = new();
                        foreach (var choice in mandatoryActivations)
                        {
                            var tags = CardRuleRegistry.TagsFor(SearchActions.KeyCardId(choice.Key), ruleContext);
                            mandatoryReasons[choice.Key] = (tags & TagRules.Tag.ActivateAtFirstOpportunity) != 0
                                ? "activate at the first opportunity" : "activate on summon";
                        }
                    }
                    else if (mandatorySets.Count > 0)
                    {
                        choices.RemoveAll(c => c.Response.Action == MainAction.ToEndPhase);
                        mandatoryReasons = new();
                        foreach (var choice in mandatorySets)
                            mandatoryReasons[choice.Key] = "always set";
                    }
                }
                return choices.Select(c => (c, mandatoryReasons?.GetValueOrDefault(c.Key))).ToList();
            }
        }

        /// <summary>
        /// A step of the brain's plan the rules don't allow in this line: Allure of Darkness while Desperate is only the last card
        /// in hand (its tag's own exception).
        /// </summary>
        public bool PlanStepAllowed(string key, DuelHost host) =>
            !(_state == TagRules.BotStrategicState.Desperate &&
              key.StartsWith($"{(int)MainAction.Activate}:", StringComparison.Ordinal) &&
              SearchHost.Is(SearchActions.KeyCardId(key), AllureOfDarkness) &&
              (host.Summary(0).HandCodes is not { Count: 1 } hand || !SearchHost.Is(hand[0], AllureOfDarkness)));

        // ------------------------------------------------------------------ battle phase

        /// <summary>Ours: no "end the turn" from the Battle Phase while Main Phase 2 is on offer (CSV S10); attack boosts wait for the damage step.</summary>
        public static bool BattleAllowed(Prompt prompt, BattleCmdData battle, SearchActions.Choice<BattleCmdResponse> c) =>
            prompt.Player != 0 || !(c.Key == "end" && battle.CanMainPhaseTwo) &&
            (c.Response.Action != BattleAction.Activate ||
             !(battle.ActivableCards ?? Array.Empty<DynamicCard>()).Any(card =>
                 SearchHost.Is(card.ID, SearchActions.KeyCardId(c.Key)) &&
                 CardRules.IsAttackBoost(card.ID, card.CurrentCoordinates.Location)));

        // ------------------------------------------------------------------ card picks

        /// <summary>
        /// Our search pick a card rule fixes (ForcedSearchPick: Reinforcement of the Army takes Necro Gardna when Dark Grepher needs
        /// a discard): the index to take, or null.
        /// </summary>
        public byte? ForcedSearchPick(Prompt prompt, DuelHost host)
        {
            if (prompt.Player != 0 ||
                prompt.Data is not SelectCardData { SelectionHint: (int)DHint.ATOHAND, Minimum: 1, Maximum: 1, SelectableCards: { } offered } ||
                host.ResolvingChainEffect is not { Player: 0, Code: var id })
                return null;
            var own = host.Summary(0);
            var context = new CardRuleRegistry.Context(own.DeckCodes, own.HandCodes, 0, 0, 0, false,
                DarkGrepherAvailable: GrepherAvailable(host, own));
            if (CardRuleRegistry.ForcedSearchPickFor(id, context, offered.Select(card => card.ID).ToList()) is not int pick)
                return null;
            int index = Array.FindIndex(offered, card => SearchHost.Is(card.ID, pick));
            return index < 0 ? null : (byte)index;
        }

        /// <summary>
        /// Our discard while a card of ours on the field limits it (DiscardPickTag: Dark Grepher takes a GoodDiscard card when one
        /// is offered): only those picks.
        /// </summary>
        public static List<MoveOption> DiscardPicks(Prompt prompt, int hint, DynamicCard[] offered, List<MoveOption> picks, DuelHost? host)
        {
            if (prompt.Player != 0 || hint != (int)DHint.DISCARD || host == null)
                return picks;
            foreach (var monster in host.Summary(0).Monsters)
            {
                var tag = CardRuleRegistry.DiscardPickTagFor(monster.Code);
                if (tag == TagRules.Tag.None || !CardRuleRegistry.AnyCardWithTag(offered.Select(card => card.ID), tag))
                    continue;
                return picks.Where(option => CardRuleRegistry.AnyCardWithTag(new[] { SearchActions.KeyCardId(option.Key) }, tag)).ToList();
            }
            return picks;
        }

        /// <summary>
        /// Our picks for our own resolving effect: its card rule's allowed targets (AllowedTargets), its target priority
        /// (TargetPriority), and an attack boost on the monster battling now.
        /// </summary>
        public List<MoveOption> EffectPicks(Prompt prompt, SelectCardData select, List<MoveOption> picks, DuelHost? host, bool ourTurn)
        {
            if (prompt.Player != 0 || host?.CurrentSearchChain(0) is not { Player: 0, CardId: var currentId })
                return picks;
            var previous = host.PreviousSearchChain(0);
            var ownSide = host.Summary(0);
            var enemySide = host.Summary(1);
            var availableTargets = select.SelectableCards!.Select(card => card.ID).ToList();
            var ruleContext = new CardRuleRegistry.Context(ownSide.DeckCodes, ownSide.HandCodes, enemySide.Hand,
                ownSide.LifePoints, enemySide.LifePoints, false,
                ActiveChainPlayer: previous.Player, ActiveChainCardId: previous.CardId,
                ActiveChainTargetIds: previous.TargetCardIds,
                TargetedOpponentGraveCards: TargetedEnemyGraveCards(host, 0),
                OwnGrave: ownSide.GraveCodes, OpponentGrave: enemySide.GraveCodes,
                OpponentMonsterIds: enemySide.Monsters.Select(monster => monster.Code).ToArray(),
                OpponentHand: enemySide.HandCodes,
                UnknownOpponentHandCards: enemySide.HandCodes.Any(code => SearchHost.Is(code, SearchView.InertCard)),
                IsOurTurn: ourTurn,
                OurFaceUpAttackers: ownSide.Monsters.Count(monster => monster.FaceUp && monster.AttackPosition));
            var allowedTargets = CardRuleRegistry.AllowedTargets(currentId, ruleContext, availableTargets);
            if (CardRuleRegistry.RequiresGoodDiscardDeckSend(currentId) &&
                (DHint)select.SelectionHint == DHint.TOGRAVE &&
                select.SelectableCards!.All(card => card.CurrentCoordinates.Location == (byte)CardLocation.Deck))
            {
                var goodDiscards = allowedTargets.Where(CardRules.IsGoodDarkDiscard).ToArray();
                // Once the effect is resolving, the engine requires a legal selection.
                if (goodDiscards.Length > 0)
                    allowedTargets = goodDiscards;
            }
            if (!availableTargets.All(id => allowedTargets.Any(allowed => SearchHost.Is(id, allowed))))
                picks = picks.Where(option => allowedTargets.Any(id =>
                    SearchHost.Is(SearchActions.KeyCardId(option.Key), id))).ToList();
            if (select.Minimum == 1 && select.Maximum == 1 && picks.Count > 1 &&
                CardRuleRegistry.TargetPriorityFor(currentId) is { } priority)
            {
                bool summonWindow = host.IsSummonWindow(0, currentId);
                int Priority(MoveOption pick) => priority(summonWindow, (DHint)select.SelectionHint,
                    select.SelectableCards![pick.Packet[1]]);
                int best = picks.Max(Priority);
                picks = picks.Where(pick => Priority(pick) == best).ToList();
            }
            if (select.Minimum == 1 && select.Maximum == 1 &&
                (DHint)select.SelectionHint == DHint.FACEUP &&
                host.PendingChainEffects.LastOrDefault() is { Player: 0, Code: var boostId, Location: var source } &&
                CardRules.IsAttackBoost(boostId, source) && host.BattlingCards(0).Own is { } battler)
            {
                var battlingPicks = picks.Where(pick =>
                {
                    var target = select.SelectableCards![pick.Packet[1]].CurrentCoordinates;
                    return target is { Controller: 0, Location: (byte)CardLocation.MonsterZone } &&
                        target.Sequence == battler.CurrentCoordinates.Sequence;
                }).ToList();
                if (battlingPicks.Count > 0)
                    picks = battlingPicks;
            }
            return picks;
        }

        // ------------------------------------------------------------------ chain windows

        /// <summary>
        /// The chain answers the rules allow and force, from every answer on offer ("pass" and one per card):
        ///  - DoNotActivate (unless the game forces a chain);
        ///  - attack boosts: only the smallest set that wins the battle (or a doomed one);
        ///  - the deck treats our backrow as doomed in their End Phase (Treeborn's clear): our set cards go;
        ///  - ActivateAtFirstOpportunity / ActivateOnSummon (in its summon window): only those.
        /// </summary>
        public List<MoveOption> ChainOptions(Prompt prompt, SelectChainData chain, List<MoveOption> list, DuelHost? host, bool ourTurn)
        {
            var cards = chain.SelectableCards ?? Array.Empty<DynamicCard>();
            var ourSide = host?.Summary(0);
            var enemySide = host?.Summary(1);
            var activeLink = host?.CurrentSearchChain(0) ?? new SearchChainLink(-1, 0, Array.Empty<int>());
            var activeTargets = host != null ? TargetedEnemyGraveCards(host, 0) : Array.Empty<int>();
            bool inDamageStep = host?.InDamageStep == true;
            var battlers = prompt.Player == 0 && host != null ? host.BattlingCards(0) : default;
            bool gladiatorWouldBeDestroyed = inDamageStep &&
                battlers is { Own: { } ownBattler, Opponent: { } opponentBattler } &&
                CardRules.GladiatorWouldBeDestroyed(ownBattler, opponentBattler);
            bool deckConsidersBackrowDoomed = prompt.Player == 0 && host != null && ourSide != null &&
                _backrowDoomRule?.Invoke(host.TurnPlayer == 0, (DuelPhase)host.Phase, ourSide) == true;
            bool opponentIsAttacking = host?.IsOpponentAttacking(0) == true;
            bool drawTrapException = opponentIsAttacking && ourSide != null &&
                ourSide.Monsters.Count + ourSide.SpellCodes.Count == 1 &&
                ourSide.HandCodes.Any(code => SearchHost.Is(code, GorzTheEmissaryOfDarkness));
            if (prompt.Player == 0 && host is { TurnPlayer: 0, Phase: (int)DuelPhase.Standby })
            {
                int realTurn = _view.RealTurn + host.Turn - _rootTurn;
                drawTrapException |= _view.OpponentSummonedAndAttackedWithThreatBefore(realTurn) ||
                    host.PreviousTurnSummonedAndAttackedWithThreat;
            }
            var activationContext = BaseContext(ourSide, enemySide, ourSide?.LifePoints ?? _view.LifePoints[0],
                    enemySide?.LifePoints ?? _view.LifePoints[1]) with
            {
                ActiveChainPlayer = activeLink.Player, ActiveChainCardId = activeLink.CardId,
                ActiveChainTargetIds = activeLink.TargetCardIds,
                TargetedOpponentGraveCards = activeTargets,
                OwnGrave = ourSide?.GraveCodes ?? _view.OwnGrave,
                OpponentGrave = enemySide?.GraveCodes ?? _view.EnemyGrave,
                OpponentMonsterIds = enemySide?.Monsters.Select(monster => monster.Code).ToArray() ?? Array.Empty<int>(),
                OpponentHand = enemySide?.HandCodes ?? _view.EnemyKnownHand,
                UnknownOpponentHandCards = enemySide?.HandCodes.Any(code => SearchHost.Is(code, SearchView.InertCard)) ?? false,
                IsOurTurn = ourTurn,
                OurFaceUpAttackers = ourSide?.Monsters.Count(monster => monster.FaceUp && monster.AttackPosition) ?? 0,
                CanWinThisTurn = host != null && CanWinThisTurn(host),
                OwnMonsterIds = ourSide?.Monsters.Select(monster => monster.Code).ToArray(),
                OpponentIsAttacking = opponentIsAttacking,
                InDamageStep = inDamageStep, GladiatorWouldBeDestroyed = gladiatorWouldBeDestroyed,
                DrawTrapException = drawTrapException,
            };
            bool IsDoomed(DynamicCard card)
            {
                var at = card.CurrentCoordinates;
                return at.Location == (byte)CardLocation.Grave
                    ? host?.IsTargetedByOpponent(0, at.Location, at.Sequence) == true
                    : host?.Doomed(0, at.Location, at.Sequence) == true ||
                      deckConsidersBackrowDoomed && at.Location == (byte)CardLocation.SpellZone;
            }
            int IndexOf(MoveOption option) => BitConverter.ToInt32(option.Packet, 0);
            if (prompt.Player == 0 && !chain.Forced)
                list = list.Where(option => IndexOf(option) < 0 ||
                    (CardRuleRegistry.TagsFor(cards[IndexOf(option)].ID, activationContext with { IsDoomed = IsDoomed(cards[IndexOf(option)]) })
                        & TagRules.Tag.DoNotActivate) == 0).ToList();
            if (prompt.Player != 0)
                return list;
            if (!chain.Forced)
            {
                var boosts = list.Select(IndexOf)
                    .Where(index => index >= 0 && CardRules.IsAttackBoost(cards[index].ID, cards[index].CurrentCoordinates.Location)).ToHashSet();
                if (boosts.Count > 0)
                {
                    var allowed = new HashSet<int>();
                    if (host != null && battlers.Own is { } attacker)
                    {
                        var defender = battlers.Opponent;
                        int required = CardRules.RequiredAttackBoost(attacker, defender, host.IsDirectAttack,
                            ourTurn, activationContext.OpponentLifePoints);
                        if (required != int.MaxValue)
                        {
                            int pending = host.PendingChainEffects.Where(effect => effect.Player == 0)
                                .Sum(effect => CardRules.AttackBoost(effect.Code, effect.Location, attacker, defender));
                            allowed = CardRules.MinimumAttackBoosts(boosts.Select(index => (index,
                                CardRules.AttackBoost(cards[index].ID, cards[index].CurrentCoordinates.Location,
                                    attacker, defender))), required - pending);
                        }
                    }
                    list.RemoveAll(option =>
                    {
                        int index = IndexOf(option);
                        return boosts.Contains(index) && !allowed.Contains(index) && !IsDoomed(cards[index]);
                    });
                }
            }
            if (deckConsidersBackrowDoomed && !ourTurn && (DuelPhase?)host?.Phase == DuelPhase.End)
            {
                var clearing = list.Where(option => option.Key.StartsWith("chain:", StringComparison.Ordinal))
                    .Where(option =>
                    {
                        var card = cards[IndexOf(option)];
                        return card.CurrentCoordinates is { Controller: 0, Location: (byte)CardLocation.SpellZone } &&
                            (card.Position & ((byte)CardPosition.FaceUpAttack | (byte)CardPosition.FaceUpDefence)) == 0;
                    }).ToList();
                if (clearing.Count > 0)
                    return clearing.Select(option => option with { MandatoryReason = "clear for Treeborn" }).ToList();
            }
            var ruleContext = BaseContext(ourSide, host?.Summary(1), host?.LifePoints(0) ?? _view.LifePoints[0],
                    host?.LifePoints(1) ?? _view.LifePoints[1]) with
            {
                OwnHand = host?.Summary(0).HandCodes ?? _view.OwnHand,
                ActiveChainPlayer = activeLink.Player, ActiveChainCardId = activeLink.CardId,
                ActiveChainTargetIds = activeLink.TargetCardIds, TargetedOpponentGraveCards = activeTargets,
                OwnGrave = activationContext.OwnGrave, OpponentGrave = activationContext.OpponentGrave,
                OpponentMonsterIds = activationContext.OpponentMonsterIds, OpponentHand = activationContext.OpponentHand,
                UnknownOpponentHandCards = activationContext.UnknownOpponentHandCards,
                IsOurTurn = ourTurn, OurFaceUpAttackers = activationContext.OurFaceUpAttackers,
                CanWinThisTurn = activationContext.CanWinThisTurn,
                OwnMonsterIds = activationContext.OwnMonsterIds,
            };
            var mandatory = list.Where(option => option.Key.StartsWith("chain:", StringComparison.Ordinal) &&
                ((CardRuleRegistry.TagsFor(SearchActions.KeyCardId(option.Key), ruleContext) & TagRules.Tag.ActivateAtFirstOpportunity) != 0 ||
                 (CardRuleRegistry.TagsFor(SearchActions.KeyCardId(option.Key), ruleContext) & TagRules.Tag.ActivateOnSummon) != 0 &&
                 cards.Any(card =>
                     option.Key.Contains(SearchActions.CardKey(card), StringComparison.Ordinal) &&
                     card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone &&
                     host?.IsSummonWindow(0, card.ID) == true))).ToList();
            if (mandatory.Count > 0)
                return mandatory.Select(option => option with
                {
                    MandatoryReason = (CardRuleRegistry.TagsFor(SearchActions.KeyCardId(option.Key), ruleContext) &
                        TagRules.Tag.ActivateAtFirstOpportunity) != 0 ? "activate at the first opportunity" : "activate on summon"
                }).ToList();
            return list;
        }

        // ------------------------------------------------------------------ yes / no and options

        /// <summary>
        /// Our "use this effect?" answer when the tags decide it: DoNotActivate or DeclineEffectOnSummon (in its summon window): no;
        /// ActivateAtFirstOpportunity, or ActivateOnSummon on the field in its summon window: yes. Null: a free choice.
        /// </summary>
        public MoveOption? EffectAnswer(Prompt prompt, SelectEffectYnData effect, DuelHost? host)
        {
            if (prompt.Player != 0)
                return null;
            var ownSide = host?.Summary(0);
            var enemySide = host?.Summary(1);
            var effectContext = BaseContext(ownSide, enemySide, host?.LifePoints(0) ?? _view.LifePoints[0],
                    host?.LifePoints(1) ?? _view.LifePoints[1]) with
            {
                CanWinThisTurn = host != null && CanWinThisTurn(host),
                OwnMonsterIds = ownSide?.Monsters.Select(monster => monster.Code).ToArray(),
            };
            var effectTags = CardRuleRegistry.TagsFor(effect.EffectCard.ID, effectContext);
            if ((effectTags & TagRules.Tag.DoNotActivate) != 0)
                return new("no", "decline the effect", new byte[] { 0 }, "do not activate");
            if ((effectTags & TagRules.Tag.DeclineEffectOnSummon) != 0 && host?.IsSummonWindow(0, effect.EffectCard.ID) == true)
                return new("no", "decline the effect", new byte[] { 0 }, "decline the effect on summon");
            if ((effectTags & TagRules.Tag.ActivateAtFirstOpportunity) != 0)
                return new("yes", $"activate {OmegaBotWrapper.Logging.CardNames.Get(effect.EffectCard.ID)}",
                    new byte[] { 1 }, "activate at the first opportunity");
            if ((effectTags & TagRules.Tag.ActivateOnSummon) != 0 &&
                effect.EffectCard.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone &&
                host?.IsSummonWindow(0, effect.EffectCard.ID) == true)
                return new("yes", $"activate {OmegaBotWrapper.Logging.CardNames.Get(effect.EffectCard.ID)}",
                    new byte[] { 1 }, "activate on summon");
            return null;
        }

        /// <summary>Our option pick for our own resolving effect when its card rule decides it (SelectOptionIndex), or null.</summary>
        public static int? OptionIndex(Prompt prompt, SelectOptionData option, DuelHost? host)
        {
            if (prompt.Player != 0 || host?.CurrentSearchChain(0) is not { Player: 0, CardId: var effectCardId })
                return null;
            var ownSide = host.Summary(0);
            var enemySide = host.Summary(1);
            var optionContext = new CardRuleRegistry.Context(ownSide.DeckCodes, ownSide.HandCodes,
                enemySide.Hand, ownSide.LifePoints, enemySide.LifePoints, false);
            return CardRuleRegistry.OptionIndexFor(effectCardId, optionContext, option.Options?.Length ?? 0) is int index &&
                   index >= 0 && index < (option.Options?.Length ?? 0) ? index : null;
        }
    }
}
