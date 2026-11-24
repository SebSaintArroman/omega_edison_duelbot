using DuelSim.Native;
using OmegaBotWrapper.Struct;

namespace DuelSim.Engine;

/// <summary>
/// The duel as one player sees it, in Omega's API shapes. Controllers are relative to the viewer (0 = the viewer),
/// and the opponent's hidden cards (hand, deck, face-down, face-down extra/banished) report id 0, as on Omega.
/// The viewer's own deck is hidden too (Omega doesn't reveal deck order); cards offered in a selection are shown.
/// </summary>
internal sealed partial class DuelHost
{
    private const int ActionSlots = 16;

    internal CardQuery? QueryCard(int controller, uint location, uint sequence, uint overlaySequence = 0)
    {
        lock (_core)
        {
            if (_ownsDuel)
                return _duel.Query(controller, location, sequence, Ocg.QueryAll, overlaySequence);
            var cache = _searchCardQueries ??= new();
            var key = (controller, location, sequence, overlaySequence);
            if (!cache.TryGetValue(key, out var card))
                cache[key] = card = _duel.Query(controller, location, sequence, Ocg.QueryAll, overlaySequence);
            return card;
        }
    }

    private List<CardQuery?> QueryList(int controller, uint location, uint flags = Ocg.QueryCode | Ocg.QueryPosition | Ocg.QueryIsPublic | Ocg.QueryOverlayCard)
    {
        lock (_core)
            return _duel.QueryLocation(controller, location, flags);
    }

    private List<int?> QueryCodes(int controller, uint location)
    {
        lock (_core)
        {
            var cache = _searchCodeQueries ??= new();
            var key = (controller, location);
            if (!cache.TryGetValue(key, out var cards))
                cache[key] = cards = _duel.QueryLocationCodes(controller, location);
            return cards;
        }
    }

    private bool IsHidden(int viewer, int controller, uint location, CardQuery card, bool offered)
    {
        if (offered && controller == viewer)
            return false;
        if (controller == viewer)
            return location == Ocg.LocDeck;
        if (card.IsPublic)
            return false;
        // Face-up cards are public; everything else of the opponent's is hidden. The graveyard is always public.
        return location != Ocg.LocGrave && (card.Position & Ocg.PosFaceUp) == 0;
    }

    private CardCoordinate Coord(int viewer, LocInfo at) =>
        new() { Controller = (byte)Relative(viewer, at.Controller), Location = at.Location, Sequence = (byte)Math.Min(at.Sequence, 255u) };

    /// <summary>A card as Omega's DynamicCard. offered = the card is part of a selection shown to the viewer.</summary>
    internal DynamicCard ToDynamic(int viewer, LocInfo at, CardQuery? card, int fallbackCode = 0, bool offered = false,
        int[]? actionIndex = null, ActivationCardIndex[]? activations = null)
    {
        var result = new DynamicCard
        {
            CurrentCoordinates = Coord(viewer, at),
            ActionIndex = actionIndex ?? new int[ActionSlots],
            ActionActivateIndex = activations ?? Array.Empty<ActivationCardIndex>(),
            EquipCards = Array.Empty<CardCoordinate>(),
            OwnTargets = Array.Empty<CardCoordinate>(),
            ChainOnlyOwnTargets = Array.Empty<CardCoordinate>(),
            TargetCards = Array.Empty<CardCoordinate>(),
            CurrentChainTargets = Array.Empty<CardCoordinate>(),
            Overlays = Array.Empty<int>(),
            AdditionalDescs = Array.Empty<int>(),
            AdditionalTexts = Array.Empty<string>(),
            Counters = Array.Empty<ushort[]>(),
        };

        if (card == null)
        {
            // Not a card on the field (e.g. a card picked by code): describe it from the database.
            var record = _cards.Get(fallbackCode);
            result.ID = fallbackCode;
            result.Alias = record?.Alias is > 0 ? record.Alias : fallbackCode;
            if (record != null)
            {
                result.Type = record.Type;
                result.Level = (sbyte)record.Level;
                result.Attribute = (byte)record.Attribute;
                result.Race = (int)record.Race;
                result.Attack = result.BaseAttack = record.Attack;
                result.Defense = result.BaseDefense = record.RealDefense;
                result.LinkMarker = (ushort)record.LinkMarker;
            }
            return result;
        }

        result.Position = (byte)card.Position;
        result.Owner = (byte)Relative(viewer, card.Owner);
        if (IsHidden(viewer, at.Controller, at.Location, card, offered))
            return result;

        result.ID = card.Code;
        result.Alias = card.Alias;
        result.Type = card.Type;
        result.Level = (sbyte)card.Level;
        result.Rank = (sbyte)card.Rank;
        result.LScale = (byte)card.LScale;
        result.RScale = (byte)card.RScale;
        result.LinkMarker = (ushort)card.LinkMarker;
        result.Attribute = (byte)card.Attribute;
        result.Race = (int)card.Race;
        result.Attack = card.Attack;
        result.BaseAttack = card.BaseAttack;
        result.Defense = card.Defense;
        result.BaseDefense = card.BaseDefense;
        result.Reason = (int)card.Reason;
        // Omega always reports Status 0 (checked across all BotTest/duel-logs: never "Disabled", never non-zero).
        // Reporting the engine's real status made the bots ignore every negated monster (Vayu synchros) in battle.
        result.Status = 0;
        result.IsSpecialSummoned = (card.Reason & Ocg.ReasonSpSummon) != 0;
        if (card.ReasonCard is { } reasonCard)
            result.ReasonCard = Coord(viewer, reasonCard);
        if (card.EquipTarget is { } equipTarget)
            result.EquipTarget = Coord(viewer, equipTarget);
        result.TargetCards = card.Targets.Select(t => Coord(viewer, t)).ToArray();
        result.Overlays = card.Overlays.ToArray();
        result.Counters = card.Counters.Select(c => new[] { c.Type, c.Count }).ToArray();
        result.Attacked = at.Location == Ocg.LocMZone && _attackedThisTurn.Contains(at.Key);

        // Chain targets: what this card's activation targets, and which chain links target this card.
        if (_chainTargets.TryGetValue(at.Key, out var targets))
            result.CurrentChainTargets = targets.Select(t => Coord(viewer, t)).ToArray();
        result.ChainOnlyOwnTargets = _chainTargets
            .Where(link => link.Value.Any(t => t.Key == at.Key))
            .Select(link => Coord(viewer, new LocInfo(link.Key.Item1, link.Key.Item2, link.Key.Item3, 0)))
            .ToArray();
        return result;
    }

    internal (DynamicCard? Own, DynamicCard? Opponent) BattlingCards(int viewer)
    {
        lock (_core)
        {
            if (_attack is not { } attack)
                return (null, null);
            bool ownAttacks = attack.Attacker.Controller == viewer;
            var own = ownAttacks ? attack.Attacker : attack.Target;
            var opponent = ownAttacks ? attack.Target : attack.Attacker;
            return (own is { } ours ? CardAt(viewer, 0, ours.Location, (int)ours.Sequence) : null,
                opponent is { } theirs ? CardAt(viewer, 1, theirs.Location, (int)theirs.Sequence) : null);
        }
    }

    /// <summary>/field-card-info and /bot-field-card-info: one card, by relative controller and Omega location.</summary>
    public DynamicCard? CardAt(int viewer, int relativeController, int location, int sequence)
    {
        int controller = Absolute(viewer, relativeController);
        uint loc = (uint)location;
        uint seq = (uint)sequence;
        if (loc == 0x100) { loc = Ocg.LocSZone; seq = 5; }      // Omega's FieldZone
        else if (loc == 0x200) { loc = Ocg.LocSZone; seq = seq == 0 ? 0u : 4u; } // PendulumZone (MR5: S1/S5)
        var card = QueryCard(controller, loc, seq);
        if (card == null)
            return null;
        lock (_core)
            return ToDynamic(viewer, new LocInfo((byte)controller, (byte)loc, seq, card.Position), card);
    }

    /// <summary>/field-info and /bot-field-info: both sides from the viewer's point of view.</summary>
    public FieldInfo FieldView(int viewer)
    {
        lock (_core)
        {
            return new FieldInfo
            {
                OwnSide = SideView(viewer, viewer),
                EnemySide = SideView(viewer, 1 - viewer),
            };
        }
    }

    private CardInfo SideView(int viewer, int side)
    {
        int[] Ids(uint location)
        {
            if (!_ownsDuel && (location == Ocg.LocGrave || side == viewer))
                return QueryCodes(side, location).Select(code => code.GetValueOrDefault()).ToArray();
            return QueryList(side, location).Select(card => card == null || IsHidden(viewer, side, location, card, offered: false)
                ? 0 : card.Code).ToArray();
        }

        CardArray[] Zones(uint location) =>
            QueryList(side, location).Select(card =>
            {
                if (card == null)
                    return new CardArray { Cards = Array.Empty<int>() };
                var ids = new int[1 + card.Overlays.Length];
                ids[0] = IsHidden(viewer, side, location, card, offered: false) ? 0 : card.Code;
                card.Overlays.CopyTo(ids, 1);
                return new CardArray { Cards = ids };
            }).ToArray();

        var info = new CardInfo
        {
            LifePoints = _lp[side],
            LinkedZones = 0,
            Main = new int[_duel.QueryCount(side, Ocg.LocDeck)],
            Extra = Ids(Ocg.LocExtra),
            Grave = Ids(Ocg.LocGrave),
            Banish = Ids(Ocg.LocRemoved),
            Hand = Ids(Ocg.LocHand),
            MonsterZones = Zones(Ocg.LocMZone),
            SpellZones = Zones(Ocg.LocSZone),
        };

        if (_attack is { } attack)
        {
            int defender = attack.Target?.Controller ?? 1 - attack.Attacker.Controller;
            info.UnderAttack = defender == side;
            var battling = attack.Attacker.Controller == side ? attack.Attacker
                : attack.Target is { } target && target.Controller == side ? target : (LocInfo?)null;
            if (battling is { } at && QueryCard(at.Controller, at.Location, at.Sequence) is { } card)
            {
                info.HasBattlingMonster = true;
                info.BattlingMonster = ToDynamic(viewer, at, card);
            }
        }
        return info;
    }
}
