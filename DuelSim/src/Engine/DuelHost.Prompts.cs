using DuelSim.Data;
using DuelSim.Native;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace DuelSim.Engine;

/// <summary>
/// Turns the engine's select messages (vendor/core-src/playerop.cpp) into Omega bot prompts, and the bots' response
/// packets (OmegaBotWrapper/BotResponseWriter.cs) back into engine responses.
/// </summary>
internal sealed partial class DuelHost
{
    // Multi-pick answers to MSG_SELECT_UNSELECT_CARD: the engine asks one card at a time, the bot answers all at once.
    private readonly List<(int Code, (byte, byte, uint) Key)>?[] _unselectQueue = new List<(int, (byte, byte, uint))>?[2];
    private readonly bool[] _unselectPicked = new bool[2];
    // The card a player last chose to use: Omega reports it as the CardID of the zone selection that follows.
    private readonly int[] _lastChosenCode = new int[2];

    /// <summary>Omega reports descriptions the classic way (card id * 16 + string index); EDOPro uses id &lt;&lt; 20 | index.</summary>
    private static int OmegaDesc(ulong desc) => desc >= (1UL << 20) ? (int)((desc >> 20) * 16 + (desc & 0xfffff)) : (int)desc;

    private int Hint(int player) => OmegaDesc(_selectHint[player]);

    private DynamicCard Offered(int viewer, int code, LocInfo at, int[]? actionIndex = null, ActivationCardIndex[]? activations = null)
    {
        // Xyz materials come as (xyz card's location | OVERLAY, xyz sequence, material index in Position).
        var card = at.Location == 0 ? null
            : (at.Location & Ocg.LocOverlay) != 0 ? QueryCard(at.Controller, at.Location, at.Sequence, at.Position)
            : QueryCard(at.Controller, at.Location, at.Sequence);
        return ToDynamic(viewer, at, card, code, offered: true, actionIndex, activations);
    }

    private Prompt? BuildPrompt(byte id, MessageReader r)
    {
        switch (id)
        {
            case Msg.SelectIdleCmd: return IdleCmd(r);
            case Msg.SelectBattleCmd: return BattleCmd(r);
            case Msg.SelectEffectYn: return EffectYesNo(r);
            case Msg.SelectYesNo: return YesNo(r);
            case Msg.SelectOption: return Option(r);
            case Msg.SelectCard: return SelectCard(r);
            case Msg.SelectTribute: return SelectTribute(r);
            case Msg.SelectSum: return SelectSum(r);
            case Msg.SelectUnselectCard: return SelectUnselect(r);
            case Msg.SelectChain: return SelectChain(r);
            case Msg.SelectPlace: return SelectPlace(r, disfield: false);
            case Msg.SelectDisfield: return SelectPlace(r, disfield: true);
            case Msg.SelectPosition: return SelectPosition(r);
            case Msg.SelectCounter: return SelectCounter(r);
            case Msg.SortCard: return SortCard(r);
            case Msg.SortChain:
            {
                int player = r.U8();
                return new Prompt { Player = player, State = BotState.SortCard, MessageId = id, Data = new SortCardData(), Translate = _ => null, Auto = new byte[] { 0xff }, Summary = "trigger order (default)" };
            }
            case Msg.AnnounceRace: return AnnounceRace(r);
            case Msg.AnnounceAttrib: return AnnounceAttribute(r);
            case Msg.AnnounceCard: return AnnounceCard(r);
            case Msg.AnnounceNumber: return AnnounceNumber(r);
            case Msg.RockPaperScissors:
            {
                int player = r.U8();
                return new Prompt { Player = player, State = BotState.RockPaperScissors, MessageId = id, Data = new object(), Translate = _ => null, Auto = Responses.Int32(_rng.Next(1, 4)), Summary = "rock paper scissors" };
            }
            default:
                return null;
        }
    }

    // ---------------------------------------------------------------- main / battle phase

    private Prompt IdleCmd(MessageReader r)
    {
        int player = r.U8();
        var keys = new List<(int Code, LocInfo At)>[6];
        for (int category = 0; category < 5; category++)
        {
            keys[category] = new();
            uint count = r.U32();
            for (int i = 0; i < count; i++)
            {
                int code = r.I32();
                byte con = r.U8(), loc = r.U8();
                uint seq = category == (int)MainAction.Repos ? r.U8() : r.U32();
                keys[category].Add((code, new LocInfo(con, loc, seq, 0)));
            }
        }
        keys[5] = new();
        var descriptions = new List<int>();
        uint activateCount = r.U32();
        for (int i = 0; i < activateCount; i++)
        {
            int code = r.I32();
            byte con = r.U8(), loc = r.U8();
            uint seq = r.U32();
            ulong desc = r.U64();
            r.U8();
            keys[5].Add((code, new LocInfo(con, loc, seq, 0)));
            descriptions.Add(OmegaDesc(desc));
        }
        bool toBattle = r.U8() != 0, toEnd = r.U8() != 0, canShuffle = r.U8() != 0;

        // One ActionIndex array per card, shared by every list the card is in (as Omega does).
        var actionIndex = new Dictionary<(byte, byte, uint), int[]>();
        var activations = new Dictionary<(byte, byte, uint), List<ActivationCardIndex>>();
        int[] IndexFor((byte, byte, uint) key) => actionIndex.TryGetValue(key, out var a) ? a : actionIndex[key] = new int[ActionSlots];
        for (int category = 0; category < 6; category++)
        {
            for (int i = 0; i < keys[category].Count; i++)
            {
                var key = keys[category][i].At.Key;
                var slots = IndexFor(key);
                if (category < 5 || !activations.ContainsKey(key))
                    slots[category] = i;
                if (category == 5)
                {
                    if (!activations.TryGetValue(key, out var list))
                        activations[key] = list = new();
                    list.Add(new ActivationCardIndex { ActionDescription = descriptions[i], ActionIndex = i });
                }
            }
        }
        DynamicCard[] Cards(int category) => keys[category]
            .Select(k => Offered(player, k.Code, k.At, IndexFor(k.At.Key), activations.TryGetValue(k.At.Key, out var list) ? list.ToArray() : null))
            .ToArray();

        var data = new IdleCmdData
        {
            SummonableCards = Cards(0),
            SpecialSummonableCards = Cards(1),
            ReposableCards = Cards(2),
            MonsterSetableCards = Cards(3),
            SpellSetableCards = Cards(4),
            ActivableCards = Cards(5),
            ActivableDescriptions = descriptions.ToArray(),
            CanBattlePhase = toBattle,
            CanEndPhase = toEnd,
            CanShuffleHand = canShuffle,
        };
        var fallbacks = new List<byte[]>();
        if (toEnd) fallbacks.Add(Responses.Int32((int)MainAction.ToEndPhase));
        if (toBattle) fallbacks.Add(Responses.Int32((int)MainAction.ToBattlePhase));
        return new Prompt
        {
            Player = player, State = BotState.SelectIdleCmd, MessageId = Msg.SelectIdleCmd, Data = data, Fallbacks = fallbacks,
            Summary = "main phase",
            Translate = packet =>
            {
                int value = Responses.ReadInt(packet);
                int action = value & 0xffff, index = value >> 16;
                if (action <= 5 && index < keys[action].Count)
                    _lastChosenCode[player] = keys[action][index].Code;
                return Responses.Int32(value);
            },
        };
    }

    private Prompt BattleCmd(MessageReader r)
    {
        int player = r.U8();
        var activates = new List<(int Code, LocInfo At, int Desc)>();
        uint activateCount = r.U32();
        for (int i = 0; i < activateCount; i++)
        {
            int code = r.I32();
            byte con = r.U8(), loc = r.U8();
            uint seq = r.U32();
            ulong desc = r.U64();
            r.U8();
            activates.Add((code, new LocInfo(con, loc, seq, 0), OmegaDesc(desc)));
        }
        var attackers = new List<(int Code, LocInfo At, bool Direct)>();
        uint attackCount = r.U32();
        for (int i = 0; i < attackCount; i++)
        {
            int code = r.I32();
            byte con = r.U8(), loc = r.U8(), seq = r.U8();
            bool direct = r.U8() != 0;
            attackers.Add((code, new LocInfo(con, loc, seq, 0), direct));
        }
        bool toMain2 = r.U8() != 0, toEnd = r.U8() != 0;

        var actionIndex = new Dictionary<(byte, byte, uint), int[]>();
        int[] IndexFor((byte, byte, uint) key) => actionIndex.TryGetValue(key, out var a) ? a : actionIndex[key] = new int[ActionSlots];
        var activations = new Dictionary<(byte, byte, uint), List<ActivationCardIndex>>();
        for (int i = 0; i < activates.Count; i++)
        {
            var key = activates[i].At.Key;
            if (!activations.TryGetValue(key, out var list))
            {
                activations[key] = list = new();
                IndexFor(key)[(int)BattleAction.Activate] = i;
            }
            list.Add(new ActivationCardIndex { ActionDescription = activates[i].Desc, ActionIndex = i });
        }
        for (int i = 0; i < attackers.Count; i++)
            IndexFor(attackers[i].At.Key)[(int)BattleAction.Attack] = i;

        ActivationCardIndex[]? Acts(LocInfo at) => activations.TryGetValue(at.Key, out var list) ? list.ToArray() : null;
        var data = new BattleCmdData
        {
            // In zone order, as Omega lists them (its AttackableCards[0] is the lowest zone that can still attack).
            AttackableCards = attackers.OrderBy(a => a.At.Sequence).Select(a =>
            {
                var card = Offered(player, a.Code, a.At, IndexFor(a.At.Key), Acts(a.At));
                card.CanDirectAttack = a.Direct;
                return card;
            }).ToArray(),
            ActivableCards = activates.Select(a => Offered(player, a.Code, a.At, IndexFor(a.At.Key), Acts(a.At))).ToArray(),
            ActivableDescriptions = activates.Select(a => a.Desc).ToArray(),
            CanMainPhaseTwo = toMain2,
            CanEndPhase = toEnd,
        };
        var fallbacks = new List<byte[]>();
        if (toMain2) fallbacks.Add(Responses.Int32((int)BattleAction.ToMainPhaseTwo));
        if (toEnd) fallbacks.Add(Responses.Int32((int)BattleAction.ToEndPhase));
        return new Prompt
        {
            Player = player, State = BotState.SelectBattleCmd, MessageId = Msg.SelectBattleCmd, Data = data, Fallbacks = fallbacks,
            Summary = "battle phase",
            Translate = packet =>
            {
                int value = Responses.ReadInt(packet);
                int action = value & 0xffff, index = value >> 16;
                if (action == (int)BattleAction.Activate && index < activates.Count)
                    _lastChosenCode[player] = activates[index].Code;
                // Omega ignores which attacker a bot declares: it always attacks with the first one in zone order (the lowest
                // zone that can still attack). Reproduced for the players that are bots (user, 2026-11-13: "there shouldn't be
                // any differences"; "the opponent should be allowed to attack in any order when simulating opponent's turns").
                if (action == (int)BattleAction.Attack && attackers.Count > 0 && _zoneOrderAttacks[player])
                {
                    int first = attackers.Select((a, i) => (a.At.Sequence, i)).OrderBy(x => x.Sequence).First().i;
                    value = (first << 16) | action;
                }
                return Responses.Int32(value);
            },
        };
    }

    // ---------------------------------------------------------------- yes / no / options

    private Prompt EffectYesNo(MessageReader r)
    {
        int player = r.U8();
        int code = r.I32();
        var at = r.Loc();
        ulong desc = r.U64();
        var data = new SelectEffectYnData { EffectCard = Offered(player, code, at), Description = OmegaDesc(desc) };
        return new Prompt
        {
            Player = player, State = BotState.SelectEffectYn, MessageId = Msg.SelectEffectYn, Data = data,
            Summary = $"use {CardName(code)}?",
            Fallbacks = { Responses.Int32(1), Responses.Int32(0) },
            Translate = packet =>
            {
                bool yes = packet.Length > 0 && packet[0] != 0;
                if (yes)
                    _lastChosenCode[player] = code;
                return Responses.Int32(yes ? 1 : 0);
            },
        };
    }

    private Prompt YesNo(MessageReader r)
    {
        int player = r.U8();
        ulong desc = r.U64();
        // "Attack directly?" (system string 31, asked when a monster that can attack directly attacks into monsters): Omega never asks
        // the bot and always attacks directly (seen 2026-11-17: Jinzo - Returner committed to Lyla hit directly for 600). The same here,
        // so no line plans an attack on a monster that the real game would make a direct attack.
        bool attackDirectly = desc == 31;
        return new Prompt
        {
            Player = player, State = BotState.SelectYesNo, MessageId = Msg.SelectYesNo,
            Data = new SelectYesNoData { Description = OmegaDesc(desc) },
            Summary = $"yes/no {OmegaDesc(desc)}",
            Fallbacks = { Responses.Int32(1), Responses.Int32(0) },
            Translate = packet => Responses.Int32(attackDirectly || packet.Length > 0 && packet[0] != 0 ? 1 : 0),
        };
    }

    private Prompt Option(MessageReader r)
    {
        int player = r.U8();
        int count = r.U8();
        var options = new int[count];
        for (int i = 0; i < count; i++)
            options[i] = OmegaDesc(r.U64());
        return new Prompt
        {
            Player = player, State = BotState.SelectOption, MessageId = Msg.SelectOption,
            Data = new SelectOptionData { Options = options },
            Summary = $"option of {count}",
            Fallbacks = Enumerable.Range(0, count).Select(Responses.Int32).ToList(),
            Translate = packet => Responses.Int32(Responses.ReadInt(packet)),
        };
    }

    // ---------------------------------------------------------------- card selections

    private Prompt SelectCard(MessageReader r)
    {
        int player = r.U8();
        bool cancelable = r.U8() != 0;
        int min = (int)r.U32(), max = (int)r.U32();
        uint count = r.U32();
        var cards = new List<DynamicCard>();
        for (int i = 0; i < count; i++)
        {
            int code = r.I32();
            var at = r.Loc();
            cards.Add(Offered(player, code, at));
        }
        var data = new SelectCardData
        {
            SelectableCards = cards.ToArray(), Minimum = min, Maximum = max, SelectionHint = Hint(player),
            Cancelable = cancelable, SelectableCount = (byte)Math.Min(255, cards.Count),
        };
        return CardListPrompt(player, BotState.SelectCard, Msg.SelectCard, data, cards.Count, min, max, cancelable, "select card");
    }

    private Prompt SelectTribute(MessageReader r)
    {
        int player = r.U8();
        bool cancelable = r.U8() != 0;
        int min = (int)r.U32(), max = (int)r.U32();
        uint count = r.U32();
        var cards = new List<DynamicCard>();
        for (int i = 0; i < count; i++)
        {
            int code = r.I32();
            byte con = r.U8(), loc = r.U8();
            uint seq = r.U32();
            byte releaseParam = r.U8();
            var card = Offered(player, code, new LocInfo(con, loc, seq, 0));
            card.OpParam1 = releaseParam;
            cards.Add(card);
        }
        var data = new SelectTributeData
        {
            SelectableCards = cards.ToArray(), Minimum = min, Maximum = max, SelectionHint = Hint(player),
            Cancelable = cancelable, SelectableCount = (byte)Math.Min(255, cards.Count),
        };
        return CardListPrompt(player, BotState.SelectTribute, Msg.SelectTribute, data, cards.Count, min, max, cancelable, "select tribute");
    }

    private Prompt CardListPrompt(int player, BotState state, byte id, object data, int count, int min, int max, bool cancelable, string summary)
    {
        var fallbacks = new List<byte[]> { Responses.Cards(Enumerable.Range(0, Math.Min(Math.Max(min, 1), count)).ToList()) };
        if (cancelable)
            fallbacks.Add(Responses.Int32(-1));
        return new Prompt
        {
            Player = player, State = state, MessageId = id, Data = data, Fallbacks = fallbacks,
            Summary = $"{summary} ({min}-{max} of {count})",
            Translate = packet =>
            {
                var picks = Responses.ReadByteList(packet).Distinct().Where(i => i < count).ToList();
                return Responses.Cards(picks);
            },
        };
    }

    private Prompt SelectSum(MessageReader r)
    {
        int player = r.U8();
        bool exact = r.U8() == 0;
        int sum = (int)r.U32();
        int min = (int)r.U32(), max = (int)r.U32();
        var must = new List<(DynamicCard Card, int Param)>();
        uint mustCount = r.U32();
        for (int i = 0; i < mustCount; i++)
        {
            int code = r.I32();
            var at = r.Loc();
            int param = r.I32();
            var card = Offered(player, code, at);
            card.OpParam1 = param & 0xffff;
            card.OpParam2 = param >> 16;
            must.Add((card, param));
        }
        var options = new List<(DynamicCard Card, int Param)>();
        uint count = r.U32();
        for (int i = 0; i < count; i++)
        {
            int code = r.I32();
            var at = r.Loc();
            int param = r.I32();
            var card = Offered(player, code, at);
            card.OpParam1 = param & 0xffff;
            card.OpParam2 = param >> 16;
            options.Add((card, param));
        }
        var data = new SelectSumData
        {
            SelectableCards = options.Select(o => o.Card).ToArray(),
            Sum = sum, Minimum = min, Maximum = max, SelectionHint = Hint(player), MustBeExactValue = exact,
            MandatoryCards = must.Select(m => m.Card).ToArray(),
        };
        var fallbacks = new List<byte[]>();
        if (SumSolver.Find(must.Select(m => m.Param).ToList(), options.Select(o => o.Param).ToList(), sum, min, max, exact) is { } solution)
            fallbacks.Add(Responses.Cards(solution));
        return new Prompt
        {
            Player = player, State = BotState.SelectSum, MessageId = Msg.SelectSum, Data = data, Fallbacks = fallbacks,
            Summary = $"select sum {sum} ({min}-{max} of {count}, {mustCount} mandatory)",
            Translate = packet => Responses.Cards(Responses.ReadByteList(packet).Distinct().Where(i => i < options.Count).ToList()),
        };
    }

    private Prompt SelectUnselect(MessageReader r)
    {
        int player = r.U8();
        bool finishable = r.U8() != 0;
        bool cancelable = r.U8() != 0;
        int min = (int)r.U32(), max = (int)r.U32();
        var selectable = new List<(int Code, LocInfo At)>();
        uint count = r.U32();
        for (int i = 0; i < count; i++)
            selectable.Add((r.I32(), r.Loc()));
        uint unselectCount = r.U32();
        for (int i = 0; i < unselectCount; i++)
        {
            r.I32();
            r.Loc();
        }

        // Continue a multi-pick answer the bot already gave.
        if (_unselectQueue[player] is { } queue)
        {
            while (queue.Count > 0)
            {
                var next = queue[0];
                queue.RemoveAt(0);
                int index = selectable.FindIndex(s => s.Code == next.Code && s.At.Key == next.Key);
                if (index >= 0)
                    return AutoPrompt(player, BotState.SelectUnselectCard, Msg.SelectUnselectCard, UnselectPick(index), $"next pick {CardName(next.Code)}");
            }
            _unselectQueue[player] = null;
            if (finishable)
                return AutoPrompt(player, BotState.SelectUnselectCard, Msg.SelectUnselectCard, Responses.Int32(-1), "finish selection");
        }

        var data = new SelectUnselectCardData
        {
            SelectableCards = selectable.Select(s => Offered(player, s.Code, s.At)).ToArray(),
            // Not finishable = the engine needs another pick now, whatever min says.
            Minimum = finishable || cancelable ? Math.Max(0, min - (int)unselectCount) : Math.Max(1, min - (int)unselectCount),
            Maximum = Math.Max(1, max - (int)unselectCount),
            SelectionHint = Hint(player), Cancelable = cancelable || finishable, SelectableCount = (byte)Math.Min(255, selectable.Count),
        };
        var fallbacks = new List<byte[]>();
        if (selectable.Count > 0) fallbacks.Add(UnselectPick(0));
        if (cancelable || finishable) fallbacks.Add(Responses.Int32(-1));
        return new Prompt
        {
            Player = player, State = BotState.SelectUnselectCard, MessageId = Msg.SelectUnselectCard, Data = data, Fallbacks = fallbacks,
            Summary = $"select/unselect ({min}-{max} of {count})",
            Translate = packet =>
            {
                var picks = Responses.ReadByteList(packet).Distinct().Where(i => i < selectable.Count).ToList();
                if (picks.Count == 0)
                    return cancelable || finishable ? Responses.Int32(-1) : null;
                // Omega takes one card per select/unselect answer (the prompt repeats): several at once stalls a real duel.
                if (picks.Count > 1)
                    Warning?.Invoke($"player {player}: answered select/unselect with {picks.Count} cards at once; Omega rejects that (one per prompt)");
                // Only a multi-pick answer leaves a queue (its remaining picks, then finish). A one-card answer (Omega's
                // protocol) leaves none, so the next prompt goes back to the bot instead of being auto-finished.
                _unselectQueue[player] = picks.Count > 1
                    ? picks.Skip(1).Select(i => (selectable[i].Code, selectable[i].At.Key)).ToList()
                    : null;
                return UnselectPick(picks[0]);
            },
        };
    }

    private static byte[] UnselectPick(int index)
    {
        var bytes = new byte[8];
        BitConverter.GetBytes(1).CopyTo(bytes, 0);
        BitConverter.GetBytes(index).CopyTo(bytes, 4);
        return bytes;
    }

    private Prompt AutoPrompt(int player, BotState state, byte id, byte[] answer, string summary) =>
        new() { Player = player, State = state, MessageId = id, Data = new object(), Translate = _ => null, Auto = answer, Summary = summary };

    private Prompt SelectChain(MessageReader r)
    {
        int player = r.U8();
        r.U8(); // spe_count
        bool forced = r.U8() != 0;
        r.U32(); r.U32(); // hint timings
        uint count = r.U32();
        var cards = new List<DynamicCard>();
        var codes = new List<int>();
        var descriptions = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int code = r.I32();
            var at = r.Loc();
            ulong desc = r.U64();
            r.U8();
            cards.Add(Offered(player, code, at));
            codes.Add(code);
            descriptions.Add(OmegaDesc(desc));
        }
        if (count == 0 && !forced)
            return AutoPrompt(player, BotState.SelectChain, Msg.SelectChain, Responses.Int32(-1), "nothing to chain");
        return new Prompt
        {
            Player = player, State = BotState.SelectChain, MessageId = Msg.SelectChain,
            Data = new SelectChainData { SelectableCards = cards.ToArray(), Descriptions = descriptions.ToArray(), Forced = forced },
            Summary = $"chain? ({count} options{(forced ? ", forced" : "")})",
            Fallbacks = { Responses.Int32(forced ? 0 : -1) },
            Translate = packet =>
            {
                int index = Responses.ReadInt(packet);
                if (index >= 0 && index < codes.Count)
                    _lastChosenCode[player] = codes[index];
                return Responses.Int32(index);
            },
        };
    }

    // ---------------------------------------------------------------- zones, positions, counters, sorting

    private Prompt SelectPlace(MessageReader r, bool disfield)
    {
        int player = r.U8();
        int count = r.U8();
        uint flag = r.U32();
        uint free = ~flag;
        // Omega offers one (player, location) at a time: own monster zones, own spell zones, then the opponent's.
        var groups = new (int Relative, uint Location, int Mask)[]
        {
            (0, Ocg.LocMZone, (int)(free & 0x7f)),
            (0, Ocg.LocSZone, (int)((free >> 8) & 0xff)),
            (1, Ocg.LocMZone, (int)((free >> 16) & 0x7f)),
            (1, Ocg.LocSZone, (int)((free >> 24) & 0xff)),
        };
        var group = groups.FirstOrDefault(g => g.Mask != 0);
        // Main monster zones the way Omega fills them: it ignores the zone the bot asks for and takes the first free one
        // centre-out (M3, M2, M4, M1, M5), and it attacks in zone order, so the zones decide the attack order (user,
        // 2026-11-12: the simulation must play out exactly as Omega would). Other zones in their own order.
        static IEnumerable<int> ZoneOrder(uint location) => location == Ocg.LocMZone ? new[] { 2, 1, 3, 0, 4, 5, 6, 7 } : Enumerable.Range(0, 8);
        var allZones = groups.SelectMany(g => ZoneOrder(g.Location).Where(s => (g.Mask & (1 << s)) != 0)
            .Select(s => (Player: Absolute(player, g.Relative), Location: (byte)g.Location, Sequence: (byte)s))).ToList();

        byte[] Encode(IEnumerable<(int Player, byte Location, byte Sequence)> zones)
        {
            var picked = zones.Distinct().ToList();
            foreach (var zone in allZones)
            {
                if (picked.Count >= count)
                    break;
                if (!picked.Contains(zone))
                    picked.Add(zone);
            }
            return picked.Take(count).SelectMany(z => new[] { (byte)z.Player, z.Location, z.Sequence }).ToArray();
        }

        var data = new SelectPlaceData
        {
            CardID = _lastChosenCode[player],
            Player = (byte)group.Relative,
            Location = (short)group.Location,
            Filter = group.Mask,
        };
        object boxed = disfield
            ? new SelectDisfieldData { CardID = data.CardID, Player = data.Player, Location = data.Location, Filter = data.Filter }
            : data;
        return new Prompt
        {
            Player = player, State = disfield ? BotState.SelectDisfield : BotState.SelectPlace,
            MessageId = disfield ? Msg.SelectDisfield : Msg.SelectPlace, Data = boxed,
            Summary = $"select zone ({count})",
            Fallbacks = { Encode(Array.Empty<(int, byte, byte)>()) },
            Translate = packet =>
            {
                int value = Responses.ReadInt(packet);
                int relative = value & 0xff, location = (value >> 8) & 0xff, sequence = (value >> 16) & 0xff;
                // A main monster zone: Omega ignores the choice (see ZoneOrder), so the engine gets Omega's zone instead.
                if (location == Ocg.LocMZone && sequence < 5 && count == 1)
                    return Encode(Array.Empty<(int, byte, byte)>());
                return Encode(new[] { (Absolute(player, relative), (byte)location, (byte)sequence) });
            },
        };
    }

    private Prompt SelectPosition(MessageReader r)
    {
        int player = r.U8();
        int code = r.I32();
        int positions = r.U8();
        var options = new[] { 0x1, 0x2, 0x4, 0x8 }.Where(p => (positions & p) != 0).Select(p => (byte)p).ToArray();
        return new Prompt
        {
            Player = player, State = BotState.SelectPosition, MessageId = Msg.SelectPosition,
            Data = new SelectPositionData { CardID = code, Positions = options },
            Summary = $"position for {CardName(code)}",
            Fallbacks = options.Select(p => Responses.Int32(p)).ToList(),
            Translate = packet => Responses.Int32(packet.Length > 0 ? packet[0] : 0),
        };
    }

    private Prompt SelectCounter(MessageReader r)
    {
        int player = r.U8();
        ushort type = r.U16();
        ushort quantity = r.U16();
        uint count = r.U32();
        var cards = new List<DynamicCard>();
        var available = new List<short>();
        for (int i = 0; i < count; i++)
        {
            int code = r.I32();
            byte con = r.U8(), loc = r.U8(), seq = r.U8();
            ushort counters = r.U16();
            cards.Add(Offered(player, code, new LocInfo(con, loc, seq, 0)));
            available.Add((short)counters);
        }
        byte[] Encode(IReadOnlyList<short> amounts) => amounts.SelectMany(a => BitConverter.GetBytes(a)).ToArray();
        var greedy = new short[count];
        int left = quantity;
        for (int i = 0; i < count && left > 0; i++)
        {
            greedy[i] = (short)Math.Min(left, available[i]);
            left -= greedy[i];
        }
        return new Prompt
        {
            Player = player, State = BotState.SelectCounter, MessageId = Msg.SelectCounter,
            Data = new SelectCounterData { RequiredType = (short)type, RequiredQuantity = (short)quantity, SelectableCards = cards.ToArray(), Counters = available.ToArray() },
            Summary = $"remove {quantity} counters",
            Fallbacks = { Encode(greedy) },
            Translate = packet =>
            {
                if (packet.Length < 1)
                    return null;
                var amounts = new short[count];
                for (int i = 0; i < count && 1 + i * 2 + 1 < packet.Length; i++)
                    amounts[i] = BitConverter.ToInt16(packet, 1 + i * 2);
                return Encode(amounts);
            },
        };
    }

    private Prompt SortCard(MessageReader r)
    {
        int player = r.U8();
        uint count = r.U32();
        var cards = new List<DynamicCard>();
        for (int i = 0; i < count; i++)
        {
            int code = r.I32();
            byte con = r.U8();
            uint loc = r.U32(), seq = r.U32();
            cards.Add(Offered(player, code, new LocInfo(con, (byte)loc, seq, 0)));
        }
        return new Prompt
        {
            Player = player, State = BotState.SortCard, MessageId = Msg.SortCard,
            Data = new SortCardData { SelectableCards = cards.ToArray() },
            Summary = $"sort {count} cards",
            Fallbacks = { new byte[] { 0xff } },
            Translate = packet =>
            {
                // Bot: indexes in the desired order. Engine: for each card, its place in that order.
                var order = Responses.ReadByteList(packet);
                if (order.Count != count || order.Distinct().Count() != count)
                    return new byte[] { 0xff };
                var places = new byte[count];
                for (int place = 0; place < order.Count; place++)
                    places[order[place]] = (byte)place;
                return places;
            },
        };
    }

    // ---------------------------------------------------------------- announcements

    private Prompt AnnounceRace(MessageReader r)
    {
        int player = r.U8();
        int count = r.U8();
        ulong available = r.U64();
        var options = Enumerable.Range(0, 32).Select(b => 1 << b).Where(b => (available & (ulong)(uint)b) != 0).ToArray();
        long first = options.Take(count).Aggregate(0L, (acc, b) => acc | (uint)b);
        return new Prompt
        {
            Player = player, State = BotState.AnnounceRace, MessageId = Msg.AnnounceRace,
            Data = new AnnounceRaceData { RequiredCount = count, SelectableOptions = options },
            Summary = "announce race",
            Fallbacks = { Responses.Int64(first) },
            Translate = packet => Responses.Int64((uint)Responses.ReadInt(packet)),
        };
    }

    private Prompt AnnounceAttribute(MessageReader r)
    {
        int player = r.U8();
        int count = r.U8();
        uint available = r.U32();
        var options = Enumerable.Range(0, 8).Select(b => (byte)(1 << b)).Where(b => (available & b) != 0).ToArray();
        int first = options.Take(count).Aggregate(0, (acc, b) => acc | b);
        return new Prompt
        {
            Player = player, State = BotState.AnnounceAttribute, MessageId = Msg.AnnounceAttrib,
            Data = new AnnounceAttributeData { RequiredCount = count, SelectableOptions = options },
            Summary = "announce attribute",
            Fallbacks = { Responses.Int32(first) },
            Translate = packet => Responses.Int32(Responses.ReadInt(packet)),
        };
    }

    private Prompt AnnounceCard(MessageReader r)
    {
        int player = r.U8();
        int count = r.U8();
        var filters = new int[count];
        for (int i = 0; i < count; i++)
            filters[i] = CardFilter.ToOmega(r.U64());
        var fallback = _cards.All.FirstOrDefault(card => CardFilter.Matches(filters, card));
        return new Prompt
        {
            Player = player, State = BotState.AnnounceCard, MessageId = Msg.AnnounceCard,
            Data = new AnnounceCardData { FilterCount = (byte)count, AppliableFilters = filters },
            Summary = "announce card",
            Fallbacks = fallback == null ? new List<byte[]>() : new List<byte[]> { Responses.Int32(fallback.Id) },
            Translate = packet => Responses.Int32(Responses.ReadInt(packet)),
        };
    }

    private Prompt AnnounceNumber(MessageReader r)
    {
        int player = r.U8();
        int count = r.U8();
        var options = new int[count];
        for (int i = 0; i < count; i++)
            options[i] = (int)r.U64();
        return new Prompt
        {
            Player = player, State = BotState.AnnounceNumber, MessageId = Msg.AnnounceNumber,
            Data = new AnnounceNumberData { SelectableOptions = options },
            Summary = $"announce number ({string.Join("/", options)})",
            Fallbacks = { Responses.Int32(0) },
            Translate = packet => Responses.Int32(Responses.ReadInt(packet)),
        };
    }
}

/// <summary>Finds a valid answer to a select-sum prompt (used as the fallback when the bot's answer is rejected).</summary>
internal static class SumSolver
{
    public static List<int>? Find(List<int> mandatory, List<int> options, int target, int min, int max, bool exact)
    {
        int n = Math.Min(options.Count, 16);
        for (int mask = 1; mask < (1 << n); mask++)
        {
            var picked = Enumerable.Range(0, n).Where(i => (mask & (1 << i)) != 0).ToList();
            if (exact && (picked.Count < min || picked.Count > max))
                continue;
            var parameters = mandatory.Concat(picked.Select(i => options[i])).ToList();
            if (exact ? CanMakeExact(parameters, target) : AtLeast(parameters, target))
                return picked;
        }
        return null;
    }

    private static bool CanMakeExact(List<int> parameters, int target)
    {
        var sums = new HashSet<int> { 0 };
        foreach (int p in parameters)
        {
            int a = p & 0xffff, b = p >> 16;
            sums = sums.SelectMany(s => b > 0 ? new[] { s + a, s + b } : new[] { s + a }).ToHashSet();
        }
        return sums.Contains(target);
    }

    private static bool AtLeast(List<int> parameters, int target)
    {
        int sum = 0, smallest = int.MaxValue;
        foreach (int p in parameters)
        {
            int a = p & 0xffff, b = p >> 16;
            int low = b > 0 && b < a ? b : a;
            sum += Math.Max(a, b);
            smallest = Math.Min(smallest, low);
        }
        return sum >= target && sum - smallest < target;
    }
}
