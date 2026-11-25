using System.Reflection;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// The brain's numbers follow its strategic state (StateTuning; user, 2026-11-25): each [Tunable] property with a value for some state
    /// (<c>Key@State</c> in the tuning file) switches to that state's value, along the state's chain, whenever the state changes, and back
    /// to its base value when it leaves. Rule probabilities and hold costs read the state themselves (RuleGates); the shared static numbers
    /// follow the bot's ambient state, set here too.
    /// </summary>
    public abstract partial class DeckBotBrain : IStateTunable
    {
        // Properties with per-state values: their base value and their value per state (index: the state; null: none).
        private readonly Dictionary<PropertyInfo, (object? Base, object?[] States)> _stateProps = new();
        private TagRules.BotStrategicState _stateApplied;

        void IStateTunable.SetStateValue(PropertyInfo property, TagRules.BotStrategicState state, object value)
        {
            if (!_stateProps.TryGetValue(property, out var entry))
                _stateProps[property] = entry = (property.GetValue(this), new object?[StateTuning.StateCount]);
            entry.States[(int)state] = value;
            ApplyStateProperties(_stateApplied);
        }

        /// <summary>At the start of every decision and when the state is assessed: the brain's numbers, and (the real bot's) the static
        /// numbers of its flow, switched to the current strategic state.</summary>
        private void ApplyStateTuning()
        {
            var state = StrategicState;
            if (!IsSearchTwin)
                StateTuning.SetBotState(state);
            if (state != _stateApplied)
                ApplyStateProperties(state);
        }

        private void ApplyStateProperties(TagRules.BotStrategicState state)
        {
            _stateApplied = state;
            foreach (var (property, (baseValue, states)) in _stateProps)
            {
                object? value = baseValue;
                foreach (var s in StateTuning.Chain(state))
                    if (states[(int)s] is { } stateValue)
                    {
                        value = stateValue;
                        break;
                    }
                property.SetValue(this, value);
            }
        }
    }
}
