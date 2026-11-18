using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace OmegaBotWrapper.Logging
{
    /// <summary>
    /// Turns bot state data and responses into short human-readable text for duel logs.
    /// </summary>
    public static class DecisionDescriber
    {
        public static string Card(DynamicCard card)
        {
            // Revealed hand cards (e.g. Trap Dustshoot) carry their identity only in Alias.
            if (card.ID <= 0 && card.CurrentCoordinates.Location == (byte)CardLocation.Hand && card.Alias > 0)
                return $"revealed {CardNames.Get(card.Alias)} [{card.Attack}/{card.Defense}]";
            if (card.ID <= 0)
                return "?";
            string name = CardNames.Get(card.ID);
            if (card.HasType(CardType.Monster) && (card.Attack != 0 || card.Defense != 0 || card.Level > 0))
                return $"{name} [{card.Attack}/{card.Defense}]";
            return name;
        }

        public static string Hint(int hint) =>
            System.Enum.IsDefined(typeof(DHint), hint) ? ((DHint)hint).ToString() : hint.ToString();

        public static string Position(int position) => position switch
        {
            (int)CardPosition.FaceUpAttack => "face-up ATK",
            (int)CardPosition.FaceDownAttack => "face-down ATK",
            (int)CardPosition.FaceUpDefence => "face-up DEF",
            (int)CardPosition.FaceDownDefence => "face-down DEF",
            _ => $"pos 0x{position:X}"
        };

        public static string Location(CardCoordinate c)
        {
            string side = c.Controller == 0 ? "bot" : "opp";
            string loc = c.Location switch
            {
                (byte)CardLocation.Hand => "hand",
                (byte)CardLocation.MonsterZone => ZoneLabels.Get(CardLocation.MonsterZone, c.Sequence),
                (byte)CardLocation.SpellZone => ZoneLabels.Get(CardLocation.SpellZone, c.Sequence),
                (byte)CardLocation.Grave => "GY",
                (byte)CardLocation.Removed => "banished",
                (byte)CardLocation.Deck => "deck",
                (byte)CardLocation.Extra => "extra",
                _ => $"loc 0x{c.Location:X}"
            };
            return $"{side} {loc}";
        }

        private static string CardAt(DynamicCard card) => $"{Card(card)} ({Location(card.CurrentCoordinates)})";

        private static string List(IEnumerable<DynamicCard>? cards) =>
            cards == null || !cards.Any() ? "-" : string.Join(", ", cards.Select(Card));

        private static string ListIndexed(IReadOnlyList<DynamicCard>? cards)
        {
            if (cards == null || cards.Count == 0)
                return "-";
            return string.Join(", ", cards.Select((card, i) => $"[{i}] {CardAt(card)}"));
        }

        private static string Pick(IReadOnlyList<DynamicCard>? cards, IEnumerable<byte>? indexes)
        {
            if (indexes == null)
                return "(nothing)";
            var picked = indexes.Select(i => cards != null && i < cards.Count ? CardAt(cards[i]) : $"[invalid index {i}]").ToList();
            return picked.Count == 0 ? "(nothing)" : string.Join(", ", picked);
        }

        private static string Activations(IReadOnlyList<DynamicCard>? cards, int[]? descriptions)
        {
            if (cards == null || cards.Count == 0)
                return "-";
            return string.Join(", ", cards.Select((card, i) =>
            {
                string desc = descriptions != null && i < descriptions.Length ? CardNames.DescribeEffect(descriptions[i]) : "";
                return string.IsNullOrEmpty(desc) ? CardAt(card) : $"{CardAt(card)} <{desc}>";
            }));
        }

        /// <summary>Describes the options the bot was offered.</summary>
        public static string DescribeOptions(object? stateData) => stateData switch
        {
            IdleCmdData d =>
                $"Summon: {List(d.SummonableCards)} | SpSummon: {List(d.SpecialSummonableCards)} | Set monster: {List(d.MonsterSetableCards)} | " +
                $"Set S/T: {List(d.SpellSetableCards)} | Change pos: {List(d.ReposableCards)} | Activate: {Activations(d.ActivableCards, d.ActivableDescriptions)} | " +
                $"Battle: {(d.CanBattlePhase ? "yes" : "no")} | End: {(d.CanEndPhase ? "yes" : "no")}",
            BattleCmdData d =>
                $"Attackers: {string.Join(", ", (d.AttackableCards ?? Array.Empty<DynamicCard>()).Select(c => $"{Card(c)}{(c.CanDirectAttack ? " (direct ok)" : "")}{(c.Attacked ? " (attacked)" : "")}"))} | " +
                $"Activate: {Activations(d.ActivableCards, d.ActivableDescriptions)} | M2: {(d.CanMainPhaseTwo ? "yes" : "no")} | End: {(d.CanEndPhase ? "yes" : "no")}",
            SelectChainData d =>
                $"Chain options{(d.Forced ? " (FORCED)" : "")}: {Activations(d.SelectableCards, d.Descriptions)}",
            SelectCardData d =>
                $"Select {d.Minimum}-{d.Maximum} for {Hint(d.SelectionHint)}{(d.Cancelable ? " (cancelable)" : "")}: {ListIndexed(d.SelectableCards)}",
            SelectUnselectCardData d =>
                $"Select/unselect {d.Minimum}-{d.Maximum} for {Hint(d.SelectionHint)}{(d.Cancelable ? " (cancelable)" : "")}: {ListIndexed(d.SelectableCards)}",
            SelectTributeData d =>
                $"Tribute {d.Minimum}-{d.Maximum}: {ListIndexed(d.SelectableCards)}",
            SelectSumData d =>
                $"Select sum {d.Sum}{(d.MustBeExactValue ? " (exact)" : "")} using {d.Minimum}-{d.Maximum} for {Hint(d.SelectionHint)}: {ListIndexed(d.SelectableCards)}" +
                (d.MandatoryCards is { Length: > 0 } ? $" | mandatory: {List(d.MandatoryCards)}" : ""),
            SelectEffectYnData d =>
                $"Use effect? {CardAt(d.EffectCard)} <{CardNames.DescribeEffect(d.Description)}>",
            SelectYesNoData d =>
                $"Yes/No? <{CardNames.DescribeEffect(d.Description)}>",
            SelectOptionData d =>
                $"Options: {string.Join(", ", (d.Options ?? Array.Empty<int>()).Select((o, i) => $"[{i}] {CardNames.DescribeEffect(o)}"))}",
            SelectPositionData d =>
                $"Position for {CardNames.Get(d.CardID)}: {string.Join(", ", (d.Positions ?? Array.Empty<byte>()).Select(p => Position(p)))}",
            SelectPlaceData d =>
                $"Place for {CardNames.Get(d.CardID)}: player {d.Player}, location 0x{d.Location:X}, filter 0x{d.Filter:X8}",
            SelectDisfieldData d =>
                $"Disable zone for {CardNames.Get(d.CardID)}: player {d.Player}, location 0x{d.Location:X}, filter 0x{d.Filter:X8}",
            SelectCounterData d =>
                $"Remove {d.RequiredQuantity} counters (type {d.RequiredType}) from: {List(d.SelectableCards)} (have {string.Join(",", d.Counters ?? Array.Empty<short>())})",
            SortCardData d =>
                $"Sort: {ListIndexed(d.SelectableCards)}",
            AnnounceAttributeData d =>
                $"Announce {d.RequiredCount} attribute(s) from: {string.Join(", ", (d.SelectableOptions ?? Array.Empty<byte>()).Select(a => ((CardAttribute)a).ToString()))}",
            AnnounceRaceData d =>
                $"Announce {d.RequiredCount} type(s) from: {string.Join(", ", (d.SelectableOptions ?? Array.Empty<int>()).Select(r => ((CardRace)r).ToString()))}",
            AnnounceNumberData d =>
                $"Announce number from: {string.Join(", ", d.SelectableOptions ?? Array.Empty<int>())}",
            AnnounceCardData d =>
                $"Announce card (filters: {string.Join(",", d.AppliableFilters ?? Array.Empty<int>())})",
            AnnounceCardFilterData d =>
                $"Announce card with filter (filters: {string.Join(",", d.AppliableFilters ?? Array.Empty<int>())})",
            null => "-",
            _ => stateData.GetType().Name
        };

        /// <summary>Describes what the bot answered.</summary>
        public static string DescribeResponse(object? stateData, object? response)
        {
            switch (stateData, response)
            {
                case (IdleCmdData d, IdleCmdResponse r):
                    return DescribeIdle(d, r);
                case (BattleCmdData d, BattleCmdResponse r):
                    return DescribeBattle(d, r);
                case (SelectChainData d, int r):
                    if (r < 0)
                        return "PASS (no chain)";
                    var chainCards = d.SelectableCards ?? Array.Empty<DynamicCard>();
                    string chainDesc = d.Descriptions != null && r < d.Descriptions.Length ? $" <{CardNames.DescribeEffect(d.Descriptions[r])}>" : "";
                    return r < chainCards.Length ? $"CHAIN {CardAt(chainCards[r])}{chainDesc}" : $"CHAIN [invalid index {r}]";
                case (SelectCardData d, IEnumerable<byte> r):
                    return $"SELECT {Pick(d.SelectableCards, r)}";
                case (SelectUnselectCardData d, IEnumerable<byte> r):
                    return $"SELECT {Pick(d.SelectableCards, r)}";
                case (SelectTributeData d, IEnumerable<byte> r):
                    return $"TRIBUTE {Pick(d.SelectableCards, r)}";
                case (SelectSumData d, IEnumerable<byte> r):
                    return $"SELECT {Pick(d.SelectableCards, r)}";
                case (SortCardData d, IEnumerable<byte> r):
                    return $"ORDER {Pick(d.SelectableCards, r)}";
                case (SelectEffectYnData, bool r):
                    return r ? "YES (use effect)" : "NO (don't use effect)";
                case (SelectYesNoData, bool r):
                    return r ? "YES" : "NO";
                case (SelectOptionData d, int r):
                    return d.Options != null && r >= 0 && r < d.Options.Length ? $"OPTION [{r}] {CardNames.DescribeEffect(d.Options[r])}" : $"OPTION {r}";
                case (SelectPositionData, CardPosition r):
                    return $"POSITION {Position((int)r)}";
                case (SelectPlaceData, int r):
                    return Zone(r);
                case (SelectDisfieldData, int r):
                    return Zone(r);
                case (AnnounceCardData, int r):
                    return $"ANNOUNCE {CardNames.GetWithId(r)}";
                case (AnnounceCardFilterData, int r):
                    return $"ANNOUNCE {CardNames.GetWithId(r)}";
                case (AnnounceNumberData, int r):
                    return $"ANNOUNCE {r}";
                case (_, IEnumerable<CardAttribute> r):
                    return $"ANNOUNCE {string.Join(", ", r)}";
                case (_, IEnumerable<CardRace> r):
                    return $"ANNOUNCE {string.Join(", ", r)}";
                case (_, IEnumerable<short> r):
                    return $"COUNTERS {string.Join(", ", r)}";
                case (_, null):
                    return "(no response)";
                default:
                    return $"{response}";
            }
        }

        private static string Zone(int r) =>
            $"ZONE player {r & 0xFF}, location 0x{(r >> 8) & 0xFF:X}, sequence {(r >> 16) & 0xFF}";

        private static string DescribeIdle(IdleCmdData d, IdleCmdResponse r)
        {
            DynamicCard[]? source = r.Action switch
            {
                MainAction.Summon => d.SummonableCards,
                MainAction.SpSummon => d.SpecialSummonableCards,
                MainAction.Repos => d.ReposableCards,
                MainAction.SetMonster => d.MonsterSetableCards,
                MainAction.SetSpell => d.SpellSetableCards,
                _ => null
            };

            string verb = r.Action switch
            {
                MainAction.Summon => "NORMAL SUMMON",
                MainAction.SpSummon => "SPECIAL SUMMON",
                MainAction.Repos => "CHANGE POSITION",
                MainAction.SetMonster => "SET MONSTER",
                MainAction.SetSpell => "SET S/T",
                MainAction.Activate => "ACTIVATE",
                MainAction.ToBattlePhase => "GO TO BATTLE PHASE",
                MainAction.ToEndPhase => "GO TO END PHASE",
                MainAction.ShuffleHand => "SHUFFLE HAND",
                _ => r.Action.ToString()
            };

            if (source != null)
            {
                foreach (var card in source)
                {
                    if (card.ActionIndex != null && card.ActionIndex.Length > (int)r.Action && card.ActionIndex[(int)r.Action] == r.Index)
                        return $"{verb} {CardAt(card)}";
                }
                return $"{verb} (index {r.Index}, card not matched)";
            }

            if (r.Action == MainAction.Activate)
                return $"{verb} {FindActivation(d.ActivableCards, r.Index)}";

            return verb;
        }

        private static string DescribeBattle(BattleCmdData d, BattleCmdResponse r)
        {
            switch (r.Action)
            {
                case BattleAction.Attack:
                    foreach (var card in d.AttackableCards ?? Array.Empty<DynamicCard>())
                    {
                        if (card.ActionIndex != null && card.ActionIndex.Length > (int)BattleAction.Attack && card.ActionIndex[(int)BattleAction.Attack] == r.Index)
                            return $"ATTACK with {CardAt(card)}";
                    }
                    return $"ATTACK (index {r.Index}, card not matched)";
                case BattleAction.Activate:
                    return $"ACTIVATE {FindActivation(d.ActivableCards, r.Index)}";
                case BattleAction.ToMainPhaseTwo:
                    return "GO TO MAIN PHASE 2";
                case BattleAction.ToEndPhase:
                    return "GO TO END PHASE";
                default:
                    return r.Action.ToString();
            }
        }

        private static string FindActivation(DynamicCard[]? cards, int index)
        {
            foreach (var card in cards ?? Array.Empty<DynamicCard>())
            {
                foreach (var activation in card.ActionActivateIndex ?? Array.Empty<ActivationCardIndex>())
                {
                    if (activation.ActionIndex == index)
                        return $"{CardAt(card)} <{CardNames.DescribeEffect(activation.ActionDescription)}>";
                }
            }
            return $"(index {index}, card not matched)";
        }
    }
}
