using System.Text.RegularExpressions;
using DuelSim.Data;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Effects valued by the cards they can fetch at the decision instead of keyword points (user, 2026-11-24: "special summons should be
    /// valued as the best card it could special summon at that moment, and search valued as the best card it could search ... more fair
    /// than heuristics. leave draw as is"): a search from the deck, a Special Summon from the hand / deck / GY / banishment, a send from
    /// the deck to the GY. Triggers that need a battle or the card's destruction (Shura, Gladiator tag-outs, Dupe Frog) keep their
    /// keyword points: their target isn't a sure thing. Self-summons (Gorz, Cyber Dragon, Dark Armed Dragon) aren't target effects.
    /// The sentence reading is shared by EffectEvaluator (which leaves these sentences out of its points when asked) and BoardEvaluator
    /// (which values them).
    /// </summary>
    internal static class TargetEffects
    {
        private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal enum Kind { Search, Summon, Revive, ReturnBanished, Send }
        [Flags]
        internal enum Zone { None = 0, Hand = 1, Deck = 2, Grave = 4, Banished = 8 }

        /// <summary>One valued effect in a sentence: what it fetches (the filter clause), from where, and its cost clause, if any.</summary>
        internal sealed record Effect(Kind Kind, Zone From, string Filter, string? CostFilter, string? TributeFilter = null);

        private const string Count = @"(?:\d+|a|an|up to \d+)";
        private static readonly Regex Search = R($@"\badd {Count} ([^;]{{1,100}}?) from your deck to your hand");
        private static readonly Regex Send = R($@"\bsend {Count} ([^;]{{1,80}}?) from your deck to the (?:graveyard|gy)\b");
        private static readonly Regex Summon = R($@"\bspecial summon {Count} ([^;]{{1,100}}?) from your (hand, deck,? or (?:graveyard|gy)|hand or deck|hand or (?:graveyard|gy)|deck or (?:graveyard|gy)|hand|deck|graveyard|gy)\b");
        private static readonly Regex TargetRevive = R($@"\btarget(?:ing)? {Count} ([^;]{{1,100}}?) in your (?:graveyard|gy)[^;]*;[^.]*?\bspecial summon (?:that target|it|that monster)\b");
        private static readonly Regex ReturnAll = R(@"\bspecial summon as many of your banished monsters as possible");
        // A trigger that needs a battle or the card's destruction: not a sure thing, so it keeps its keyword points.
        private static readonly Regex Conditional = R(@"\bdestroyed\b|\bsent (?:from the field )?to the (?:graveyard|gy)\b|\bbattle\b|\battacks?\b|\bdamage\b");
        // A cost before the effect's semicolon: "discard 1 DARK monster;", "Send 1 monster from your hand to the GY;".
        private static readonly Regex DiscardCost = R($@"\bdiscard {Count} ([^;]*?);");
        private static readonly Regex HandSendCost = R($@"\bsend {Count} ([^;]*?) from your hand to the (?:graveyard|gy);");
        // "Tribute 1 monster;" / "You can Tribute 1 monster to Special Summon ..." (Substitoad).
        private static readonly Regex TributeCost = R($@"\btribute {Count} ([^;]*?)(?:;|\s+to\s+$)");

        private static readonly Regex Sentence = R(@"[^.]+");

        /// <summary>Whether this sentence (one full stop to the next) is an effect valued by its targets.</summary>
        internal static bool IsValued(string sentence) => Read(sentence) != null;

        /// <summary>Every valued effect in a card's text.</summary>
        internal static List<Effect> Effects(string text) =>
            Sentence.Matches(text).Select(m => Read(m.Value)).Where(e => e != null).Select(e => e!).ToList();

        private static Effect? Read(string sentence)
        {
            if (Conditional.IsMatch(sentence))
                return null;
            Match m;
            Kind kind;
            Zone from;
            if ((m = ReturnAll.Match(sentence)).Success)
                (kind, from) = (Kind.ReturnBanished, Zone.Banished);
            else if ((m = TargetRevive.Match(sentence)).Success)
                (kind, from) = (Kind.Revive, Zone.Grave);
            else if ((m = Summon.Match(sentence)).Success)
            {
                string zones = m.Groups[2].Value.ToLowerInvariant();
                from = (zones.Contains("hand") ? Zone.Hand : 0) | (zones.Contains("deck") ? Zone.Deck : 0) |
                       (zones.Contains("graveyard") || zones.Contains("gy") ? Zone.Grave : 0);
                kind = from == Zone.Grave ? Kind.Revive : Kind.Summon;
            }
            else if ((m = Search.Match(sentence)).Success)
                (kind, from) = (Kind.Search, Zone.Deck);
            else if ((m = Send.Match(sentence)).Success)
                (kind, from) = (Kind.Send, Zone.Deck);
            else
                return null;
            string filter = m.Groups.Count > 1 ? m.Groups[1].Value : "";
            // Only a cost written before the effect counts ("discard 1 DARK monster; send ...").
            string before = sentence[..m.Index];
            var cost = DiscardCost.Match(before) is { Success: true } d ? d : HandSendCost.Match(before);
            var tribute = TributeCost.Match(before.TrimEnd() + " ");
            return new Effect(kind, from, filter, cost.Success ? cost.Groups[1].Value : null, tribute.Success ? tribute.Groups[1].Value : null);
        }

        private static readonly Regex Quoted = R("(except )?\"([^\"]+)\"");
        private static readonly Regex Level = R(@"level (\d+)( or (lower|higher))?");
        private static readonly Regex AtkLimit = R(@"(\d+) or (less|lower|more|higher) (atk|def)|(atk|def) (?:of )?(\d+) or (less|lower|more|higher)");
        private static readonly (string Word, int Attribute)[] AttributeWords =
            { ("earth", 0x01), ("water", 0x02), ("fire", 0x04), ("wind", 0x08), ("light", 0x10), ("dark", 0x20), ("divine", 0x40) };

        /// <summary>Whether a card fits a filter clause ("1 Level 4 or lower \"Lightsworn\" monster", "1 DARK monster", "1 Warrior monster
        /// except \"Gearframe\""): names in quotes, attribute, Type, monster / spell / trap, Normal / Tuner, Level, ATK / DEF limits.</summary>
        internal static bool Fits(string filter, CardRecord card)
        {
            string f = filter.ToLowerInvariant();
            string name = card.Name ?? "";
            var wanted = new List<string>();
            foreach (Match q in Quoted.Matches(filter))
            {
                if (q.Groups[1].Success)
                {
                    if (name.Contains(q.Groups[2].Value, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                else
                    wanted.Add(q.Groups[2].Value);
            }
            if (wanted.Count > 0 && !wanted.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                return false;
            // The rest of the clause, without the quoted names (a "Dark" in a card name isn't the attribute).
            f = Quoted.Replace(f, " ");
            bool isMonster = (card.Type & 0x1) != 0;
            bool monster = Regex.IsMatch(f, @"\bmonsters?\b"), spell = Regex.IsMatch(f, @"\bspells?\b"), trap = Regex.IsMatch(f, @"\btraps?\b");
            if ((monster || spell || trap) && !(monster && isMonster || spell && (card.Type & 0x2) != 0 || trap && (card.Type & 0x4) != 0))
                return false;
            int attribute = AttributeWords.Where(a => Regex.IsMatch(f, $@"\b{a.Word}\b")).Sum(a => a.Attribute);
            if (attribute != 0 && (!isMonster || (card.Attribute & attribute) == 0))
                return false;
            if (Regex.IsMatch(f, @"\bnormal monster") && (card.Type & 0x10) == 0)
                return false;
            if (Regex.IsMatch(f, @"\btuner\b") && (card.Type & 0x1000) == 0)
                return false;
            foreach (var (word, race) in EffectEvaluator.RaceWords)
                if (Regex.IsMatch(f, $@"\b{Regex.Escape(word)}(-type)?\b"))
                {
                    if ((card.Race & (long)race) == 0)
                        return false;
                    break;
                }
            if (Level.Match(f) is { Success: true } level)
            {
                int required = int.Parse(level.Groups[1].Value);
                bool ok = level.Groups[3].Value switch { "lower" => card.Level <= required, "higher" => card.Level >= required, _ => card.Level == required };
                if (!isMonster || !ok)
                    return false;
            }
            if (AtkLimit.Match(f) is { Success: true } limit)
            {
                bool first = limit.Groups[1].Success;
                int value = int.Parse(first ? limit.Groups[1].Value : limit.Groups[5].Value);
                string dir = first ? limit.Groups[2].Value : limit.Groups[6].Value;
                string stat = first ? limit.Groups[3].Value : limit.Groups[4].Value;
                int have = stat == "atk" ? card.Attack : card.RealDefense;
                bool ok = dir is "less" or "lower" ? have <= value : have >= value;
                if (!isMonster || !ok)
                    return false;
            }
            return true;
        }
    }
}
