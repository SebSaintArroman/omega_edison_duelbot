using BotTest.Game.AI.Brains;
using DuelSim.Engine;
using OmegaBotWrapper.Enum;

namespace BotTest.Game.AI.Search
{
    /// <summary>
    /// The MCTS in dead time (user, 2026-11-10: "it may as well always be planning while there's dead time"; 2026-11-11: one
    /// conventional MCTS). A background thread that keeps growing one tree whenever the bot isn't deciding: during their
    /// turn, our next turn (the draw dealt in each line unless we know it); during ours, the decision we're at. Each decision
    /// on the clock finds its node in that tree again (MctsTree.Find) and carries on from there, so nothing is thrown away
    /// while the game follows a line the tree has explored. Paused while the bot searches on the clock (the decision has the
    /// cores).
    /// </summary>
    internal sealed class MctsSession : IDisposable
    {
        /// <summary>What to search: a board (the root prompt of its view) and where in which tree its lines go.</summary>
        public sealed class Target
        {
            public required string Key;
            public required string Label;
            public required SearchView View;
            public required MctsTree Tree;
            public required MctsNode Start;
            public required BotState RootState;
            public required BoardEvaluator Evaluator;
            public required SearchSettings Settings;
            public Func<SearchBrain>? Brains;
            public Func<int, MainAction, bool>? MayChoose;
            public Func<IReadOnlyList<PlanStep>, int, IReadOnlyList<string>, SideSummary, double>? Suggestions;
        }

        private readonly object _lock = new();
        private Target? _target;
        private CancellationTokenSource _round = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly ManualResetEventSlim _idle = new(true);
        private int _paused;
        private volatile bool _disposed;
        private int _seed = Environment.TickCount;

        public MctsSession()
        {
            new Thread(Loop) { IsBackground = true, Name = "mcts dead time", Priority = ThreadPriority.BelowNormal }.Start();
        }

        /// <summary>The tree being grown now (to find a decision's node in), or null.</summary>
        public MctsTree? Tree
        {
            get { lock (_lock) return _target?.Tree; }
        }

        public string? Key
        {
            get { lock (_lock) return _target?.Key; }
        }

        /// <summary>Search this board in dead time from now on (the same key again changes nothing). Null stops.</summary>
        public void SetTarget(Target? target)
        {
            lock (_lock)
            {
                if (target?.Key == _target?.Key && target?.Start == _target?.Start)
                    return;
                _target = target;
                _round.Cancel();
            }
            _wake.Set();
        }

        /// <summary>A decision is being searched on the clock: stop (what the round found stays in the tree).</summary>
        public void Pause()
        {
            Interlocked.Increment(ref _paused);
            lock (_lock)
                _round.Cancel();
            _idle.Wait(TimeSpan.FromMilliseconds(300));
        }

        public void Resume()
        {
            if (Interlocked.Decrement(ref _paused) <= 0)
            {
                Interlocked.Exchange(ref _paused, 0);
                _wake.Set();
            }
        }

        public void Dispose()
        {
            _disposed = true;
            lock (_lock)
                _round.Cancel();
            _wake.Set();
        }

        private void Loop()
        {
            while (!_disposed)
            {
                Target? target;
                CancellationToken ct;
                lock (_lock)
                {
                    target = Volatile.Read(ref _paused) == 0 ? _target : null;
                    if (_round.IsCancellationRequested)
                        _round = new CancellationTokenSource();
                    ct = _round.Token;
                }
                if (target == null)
                {
                    _wake.WaitOne(TimeSpan.FromSeconds(1));
                    continue;
                }
                try
                {
                    _idle.Reset();
                    var search = new Mcts(target.View, target.Tree, target.Start, target.RootState, null, Array.Empty<int>(),
                        target.Settings with { BudgetMs = 0, MaxIterations = int.MaxValue }, target.Evaluator, target.Brains)
                    { MayChoose = target.MayChoose, Suggestions = target.Suggestions };
                    var outcome = search.Run(Interlocked.Increment(ref _seed), ct, ThreadPriority.BelowNormal);
                    // A board whose position can't be built: don't spin on it.
                    if (outcome.Iterations > 0 && outcome.Failures == outcome.Iterations && outcome.FirstFailure?.StartsWith("position") == true)
                        lock (_lock)
                            if (_target == target)
                                _target = null;
                }
                catch
                {
                    // A failed round loses only itself; wait a moment before the next.
                    _wake.WaitOne(TimeSpan.FromMilliseconds(500));
                }
                finally
                {
                    _idle.Set();
                }
            }
        }
    }
}
