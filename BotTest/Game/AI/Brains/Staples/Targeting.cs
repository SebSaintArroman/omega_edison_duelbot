using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Shared target choice for removal and other effects on the opponent's cards (moved from the Vayu brain): which of
    /// their cards to destroy / banish / bounce / return (field, or a revealed hand), and Charge of the Light Brigade's
    /// search. Every deck's removal (Ryko, Lyla, Dark Armed Dragon, Machina Fortress, Brionac...) picks through here.
    /// </summary>
    public abstract partial class DeckBotBrain
    {

        /// <summary>
        /// Removal-target score of an opponent's set card in our Main Phase 1 with attackers out (it could be Mirror Force).
        /// Above most monsters (a 2000 beater is about 4-6), below the biggest threats. Tune from logs.
        /// </summary>
        [Tunable("Removal-target score of an opponent's set card in our Main Phase 1 with attackers out (it could be Mirror Force). Above most monsters (a 2000 beater is about 4-6), below the biggest threats. Tune from logs.")]
        public double SetCardBeforeAttackScore { get; set; } = 7;

        /// <summary>Removal-target score of an opponent's set spell/trap otherwise: one card. Tune from logs.</summary>
        [Tunable("Removal-target score of an opponent's set spell/trap otherwise: one card. Tune from logs.")]
        public double SetCardBaseScore { get; set; } = 1;

        /// <summary>The turn the next removal pick takes their best monster over their set cards (then cleared); -1 for none.</summary>
        protected int MonsterBeforeSetsTurn = -1;

        // Our Dark Armed Dragon's activation whose destroy target comes next: the turn and which use this turn (1 = first).
        private (int Turn, int Use)? _dadTarget;
        private (int Turn, int Count) _dadUses;

        /// <summary>Called when our move activates Dark Armed Dragon: its next destroy prompt is picked by the DAD order.</summary>
        protected void NoteDadActivation(int turn)
        {
            _dadUses = _dadUses.Turn == turn ? (turn, _dadUses.Count + 1) : (turn, 1);
            _dadTarget = (turn, _dadUses.Count);
        }

        /// <summary>Our Dark Armed Dragon activations so far this turn.</summary>
        protected int DadUsesThisTurn => _dadUses.Turn == _currentHeader.CurrentTurn ? _dadUses.Count : 0;

        /// <summary>Dark Armed Dragon's ATK (a monster at least this big is one it can't get over).</summary>
        protected const int DadAttack = 2800;

        /// <summary>Combined-lethal plans: extra cost per set card of theirs the plan leaves standing (a trap there likely stops it).</summary>
        [Tunable("Combined-lethal plans: extra cost per set card of theirs the plan leaves standing (a trap there likely stops it).")]
        public double SetCardLeftCost { get; set; } = 1.5;

        private static readonly System.Text.RegularExpressions.Regex DestroyedByBattleText = new(
            @"destroyed by battle",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Removal-target bonus on our turn for a monster that negates attacks (Fulhelmknight). Tune from logs.</summary>
        [Tunable("Removal-target bonus on our turn for a monster that negates attacks (Fulhelmknight). Tune from logs.")]
        public double AttackNegatorRemovalBonus { get; set; } = 4;

        /// <summary>Banish-target bonus for a monster that rewards its destruction (Card Trooper, floaters). Tune from logs.</summary>
        [Tunable("Banish-target bonus for a monster that rewards its destruction (Card Trooper, floaters). Tune from logs.")]
        public double BanishDeniesRewardBonus { get; set; } = 3;

        /// <summary>
        /// Removal and targeting on the opponent's cards (destroy, target, banish, bounce, return to deck): the best of
        /// theirs, never our own. Null when this prompt isn't one (not enough of their cards on offer).
        /// </summary>
        protected async Task<IList<byte>?> SelectOpponentTargetsAsync(DecisionContext<SelectCardData> context)
        {
            var state = context.StateData;
            var cards = state.SelectableCards ?? Array.Empty<DynamicCard>();
            int count = Math.Max(1, state.Minimum);
            var hint = (DHint)state.SelectionHint;
            var indexed = cards.Select((card, i) => (Card: card, Index: i)).ToList();
            var opponent = indexed.Where(x => x.Card.CurrentCoordinates.Controller != 0).ToList();
            if (!(hint is DHint.DESTROY or DHint.TARGET or DHint.REMOVE or DHint.RTOHAND or DHint.TODECK) || opponent.Count < count || opponent.Count == 0)
                return null;
            void Note(DecisionContext<SelectCardData> c, string message) => StapleNote(c, message);

            // Revealed hand cards have no id, but identical visible stats almost certainly mean the same card.
            // Taking a card they hold only one copy of hurts more than taking one of a pair.
            var copies = opponent
                .GroupBy(x => VisibleSignature(x.Card))
                .ToDictionary(g => g.Key, g => g.Count());
            bool fromHand = opponent.All(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Hand);

            // Revealed cards: judge usefulness from their effect text, checking graveyard conditions against their (public) GY.
            var effectScores = new Dictionary<int, double>();
            if (fromHand)
            {
                var opponentGrave = (await CurrentField.GetBotField()).EnemySide.Grave ?? Array.Empty<int>();
                // Our best blocker decides whether their battle-damage effects (e.g. Airbellum's discard) can land.
                int ourBestBlocker = (await GetBotMonsterCardsAsync())
                    .Select(card => card.IsFaceup() ? card.GetRealPower() : OpponentMemory.FaceDownDefense(card))
                    .DefaultIfEmpty(0).Max();
                var reasons = new List<string>();
                foreach (var x in opponent)
                {
                    var (score, why) = await EffectEvaluator.ScoreAsync(BoardAssessment.KnownId(x.Card), opponentGrave, ourBestBlocker);
                    // Grand Mole-type cards are far stronger than their keywords suggest (especially against us).
                    var (bonus, bonusWhy) = await RemovalValueBonusAsync(BoardAssessment.KnownId(x.Card));
                    var others = opponent.Where(o => o.Index != x.Index).Select(o => BoardAssessment.KnownId(o.Card)).ToList();
                    if (await NearlyLiveBossAsync(BoardAssessment.KnownId(x.Card), opponentGrave, others) is string live)
                    {
                        bonus += NearlyLiveBossBonus;
                        bonusWhy = $"{bonusWhy} +{NearlyLiveBossBonus} ({live})".Trim();
                    }
                    effectScores[x.Index] = score + bonus;
                    reasons.Add($"{why}{(bonus > 0 ? " " + bonusWhy : "")} x{copies[VisibleSignature(x.Card)]}");
                }
                Note(context, $"revealed hand: {string.Join(" | ", reasons)}");
                ObserveRevealedOpponentHand(opponent.Select(x => x.Card));
                ObserveRevealedOpponentCards(context, opponent.Select(x => BoardAssessment.KnownId(x.Card)));
            }

            // Face-up monsters on the field: their effects plus their size, both by the user's tables (user, 2026-11-13: "use
            // that table for all atk and def scoring"): the scorer's printed-ATK part is swapped for the table value of the
            // current ATK (only the stat of its position: DEF in defence, as the search counts it).
            if (!fromHand)
            {
                var enemyGrave = (await CurrentField.GetBotField()).EnemySide.Grave ?? Array.Empty<int>();
                var fieldReasons = new List<string>();
                await RefreshRewardOnDestroyAsync(opponent.Where(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone).Select(x => x.Card));
                foreach (var x in opponent.Where(x => x.Card.IsFaceup() && x.Card.ID > 0 &&
                                                      x.Card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone))
                {
                    // Destroying it with an effect: its own "when destroyed" rewards (Sangan's search) count against it.
                    var (score, why) = await EffectEvaluator.ScoreOnFieldAsync(x.Card.ID, enemyGrave, destroying: hint == DHint.DESTROY);
                    int printed = await CardDatabase.GetAsync(x.Card.ID) is DBCardInfo info ? info.Attack : x.Card.Attack;
                    score += BoardEvaluator.AtkValue(x.Card.IsAttack() ? x.Card.Attack : x.Card.Defense * BoardEvaluator.DefenceShare) - BoardEvaluator.AtkValue(printed);
                    var (bonus, bonusWhy) = await RemovalValueBonusAsync(x.Card.ID);
                    // Banishing denies a destruction reward (Card Trooper's draw, a floater's replacement) that
                    // battle or destruction would hand them: that's what banish removal is for.
                    if (hint == DHint.REMOVE && RewardsDestruction(x.Card))
                    {
                        bonus += BanishDeniesRewardBonus;
                        bonusWhy = $"{bonusWhy} +{BanishDeniesRewardBonus} (banishing denies its destruction reward)".Trim();
                    }
                    // On our turn, a monster that negates attacks (XX-Saber Fulhelmknight) stops one of ours every
                    // turn: weak as an attacker, but a real wall against our clock.
                    if (IsOwnTurn(context.Header) && await CardDatabase.GetAsync(x.Card.ID) is DBCardInfo negInfo &&
                        AttackNegateText.IsMatch(negInfo.CardText ?? ""))
                    {
                        bonus += AttackNegatorRemovalBonus;
                        bonusWhy = $"{bonusWhy} +{AttackNegatorRemovalBonus} (negates our attacks: a wall against our clock)".Trim();
                    }
                    effectScores[x.Index] = score + bonus;
                    fieldReasons.Add($"{why}{(x.Card.Attack != printed ? $" (now {x.Card.Attack} ATK)" : "")}{(bonus > 0 ? " " + bonusWhy : "")}");
                }
                if (fieldReasons.Count > 1)
                    Note(context, $"their monsters: {string.Join(" | ", fieldReasons)}");
            }

            // Face-up spells/traps: only worth hitting if they get in our way, prop up their board or keep Mole
            // coming back (Ultimate Offering with a Mole around); otherwise a set card (maybe Mirror Force) matters more.
            var faceUpSpellValue = new Dictionary<int, int>();
            foreach (var x in opponent.Where(x => x.Card.IsFaceup() && x.Card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone))
            {
                string? matters = await FaceUpSpellMattersAsync(x.Card);
                faceUpSpellValue[x.Index] = matters != null ? 2500 : 300;
                if (matters == null)
                    Note(context, $"{CardNames.Get(x.Card.ID)} is face-up but doesn't get in our way: a set card is the better target");
            }
            // A set spell/trap is at least one card (user, 2026-11-13: "it should score 1 because it's 1 card"; it scored 0, so
            // Ryko took Sangan at 1.0 over it). Raised below when we attack this turn or push next turn.
            foreach (var x in opponent.Where(x => !x.Card.IsFaceup() && x.Card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone))
                effectScores[x.Index] = SetCardBaseScore;

            // Our Main Phase 1 with attackers out: their set cards come first. A set Mirror Force / Torrential /
            // Dimensional Prison is what can wreck our attack (DAD attacking into Mirror Force), and repeatable
            // removal (DAD) can take the monsters after.
            bool attackingSoon = IsOwnTurn(context.Header) && (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 &&
                                 (await GetBotMonsterCardsAsync()).Any(card => card.IsFaceup() && card.IsAttack());
            // A removal pick told to take a monster first (Dark Armed Dragon's first use: a Bottomless / Torrential in their set
            // cards is chained in response anyway, so DAD on a monster takes two cards; user, 2026-11-12).
            bool monsterFirst = MonsterBeforeSetsTurn == context.Header.CurrentTurn &&
                                opponent.Any(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone);
            if (MonsterBeforeSetsTurn == context.Header.CurrentTurn)
                MonsterBeforeSetsTurn = -1;
            if (monsterFirst)
                Note(context, "their set cards can wait: a summon-response trap among them is chained now anyway, so a monster first (two cards for one)");
            // On their turn (Ryko flipped by their attack), with a push of ours coming next turn: their set cards first too, so
            // it doesn't run into them (user, 2026-11-13: "the bot should consider if it thinks it has a push next turn that it
            // needs my backrow gone for").
            string? pushNext = !IsOwnTurn(context.Header) && !monsterFirst ? await PushNextTurnAsync() : null;
            if ((attackingSoon || pushNext != null) && !monsterFirst)
            {
                var setCards = opponent.Where(x => !x.Card.IsFaceup() && x.Card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone).ToList();
                foreach (var x in setCards)
                    effectScores[x.Index] = SetCardBeforeAttackScore;
                if (setCards.Count > 0)
                    Note(context, attackingSoon
                        ? $"we attack this turn: their {setCards.Count} set card(s) first (score {SetCardBeforeAttackScore}), so our attack can't run into Mirror Force or similar"
                        : $"we push next turn ({pushNext}): their {setCards.Count} set card(s) first (score {SetCardBeforeAttackScore})");
            }

            // In their Battle Phase (e.g. Ryko flipped by an attack): normally still the best monster. A monster that
            // still has to attack this turn comes first only when its attack matters: the damage still to come is
            // lethal or leaves us within reach of their board, or it would destroy a monster of ours that isn't
            // expendable. Then the biggest of those.
            var stillToAttack = await StillToAttackThisTurnAsync(context.Header);
            var coming = opponent.Where(x => stillToAttack(x.Card)).ToList();
            bool damageMatters = false;
            var ourMonsters = new List<DynamicCard>();
            if (coming.Count > 0)
            {
                int lp = await CurrentField.GetBotLifePoints();
                int incoming = coming.Sum(x => x.Card.Attack);
                int theirBoard = (await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup()).Sum(card => card.Attack);
                damageMatters = incoming >= lp || lp - incoming <= theirBoard;
                ourMonsters = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && !IsExpendable(card)).ToList();
            }
            bool ProtectsOurs(DynamicCard enemy) =>
                ourMonsters.Any(own => own.IsAttack() ? enemy.Attack >= own.Attack : enemy.Attack > own.Defense);
            bool AttackMatters(DynamicCard enemy) => stillToAttack(enemy) && (damageMatters || ProtectsOurs(enemy));
            var mattering = opponent.Where(x => AttackMatters(x.Card)).ToList();
            if (mattering.Count > 0)
                Note(context, $"their Battle Phase: hit a monster still to attack ({string.Join(", ", mattering.Select(x => $"{CardNames.Get(x.Card.ID)} {x.Card.Attack}"))}): " +
                              (damageMatters ? "the damage still to come matters" : "it would destroy one of our monsters"));
            // Dark Armed Dragon's destroy (user, 2026-11-12): the first use takes a monster (one DAD can't get over, or one whose
            // "destroyed by battle" effect a battle would set off); after that DAD itself attacks over almost anything, so its
            // uses clear the way: monsters bigger than DAD, then set spells/traps and "destroyed by battle" monsters, then the
            // other monsters, face-up spells/traps last.
            var dadGroup = new Dictionary<int, int>();
            if (hint == DHint.DESTROY && _dadTarget is { } dad && dad.Turn == context.Header.CurrentTurn)
            {
                _dadTarget = null;
                int dadAtk = (await GetBotMonsterCardsAsync()).Where(card => card.IsCode(DarkArmedDragon) && card.IsFaceup())
                    .Select(card => card.Attack).DefaultIfEmpty(2800).Max();
                static int Power(DynamicCard card) => card.IsAttack() ? card.Attack : card.Defense;
                var battleTrigger = new HashSet<int>();
                foreach (var x in opponent.Where(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone && x.Card.IsFaceup() && x.Card.ID > 0))
                    if (await CardDatabase.GetAsync(x.Card.ID) is DBCardInfo info && DestroyedByBattleText.IsMatch(info.CardText ?? ""))
                        battleTrigger.Add(x.Index);
                bool anyMonster = opponent.Any(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone);
                foreach (var x in opponent)
                {
                    bool monster = x.Card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone;
                    bool bigger = monster && x.Card.IsFaceup() && Power(x.Card) >= dadAtk;
                    bool trigger = battleTrigger.Contains(x.Index);
                    bool setSpell = !monster && !x.Card.IsFaceup();
                    dadGroup[x.Index] = dad.Use == 1 && anyMonster
                        ? (monster ? (bigger || trigger ? 0 : 1) : 2)
                        : bigger ? 0 : setSpell || trigger ? 1 : monster ? 2 : 3;
                }
                Note(context, $"Dark Armed Dragon's use #{dad.Use} this turn: " +
                              (dad.Use == 1 && anyMonster ? "a monster first (one it can't get over, or a \"destroyed by battle\" one, if any)"
                                                          : "clear the way for its attack: monsters bigger than it, then set cards and \"destroyed by battle\" monsters"));
            }
            var picks = opponent
                .OrderBy(x => dadGroup.TryGetValue(x.Index, out int g) ? g : 0)
                .ThenByDescending(x => fromHand && copies[VisibleSignature(x.Card)] == 1)
                .ThenByDescending(x => AttackMatters(x.Card) ? 1 : 0)
                .ThenByDescending(x => AttackMatters(x.Card) ? x.Card.Attack : 0)
                .ThenByDescending(x => effectScores.TryGetValue(x.Index, out double s) ? s : 0)
                .ThenByDescending(x => faceUpSpellValue.TryGetValue(x.Index, out int v) ? v : OpponentThreat(x.Card))
                // Between set spells/traps: the one most likely to be Mirror Force / Torrential, random on ties (never by zone).
                .ThenByDescending(x => SetCardDanger(x.Card))
                .Take(count)
                .ToList();
            Note(context, $"{hint}: opponent {string.Join(", ", picks.Select(x => DescribePick(x.Card) + (SetCardDanger(x.Card) > 0 ? $" in S{x.Card.CurrentCoordinates.Sequence + 1}" : "")))}");
            // Taken from their hand (Trap Dustshoot to the deck): no longer in it.
            if (fromHand)
                lock (OpponentDeck)
                    foreach (var x in picks)
                        OpponentDeck.CardLeftHand(BoardAssessment.KnownId(x.Card));
            return picks.Select(x => (byte)x.Index).ToList();
        }

        /// <summary>
        /// Why we have a push coming next turn that their backrow could stop, or null: a face-up attacker of ours that isn't
        /// expendable, or a strong monster in hand (StrongMonsterAttack+) to attack with.
        /// </summary>
        protected async Task<string?> PushNextTurnAsync()
        {
            var attacker = (await GetBotMonsterCardsAsync()).FirstOrDefault(card => card.ID > 0 && card.IsFaceup() && !IsExpendable(card) && card.Attack >= StrongMonsterAttack);
            if (attacker.ID > 0)
                return $"{CardNames.Get(attacker.ID)} attacks";
            var inHand = (await GetBotHandCardsAsync()).FirstOrDefault(card => card.HasType(CardType.Monster) && card.Attack >= StrongMonsterAttack);
            return inHand.ID > 0 ? $"{CardNames.Get(inHand.ID)} from hand" : null;
        }

        /// <summary>Removal-score bonus for a hand card whose summon condition is live or nearly so (see NearlyLiveBossAsync).</summary>
        [Tunable("Removal-score bonus for a hand card whose summon condition is live or nearly so (see NearlyLiveBossAsync).")]
        public double NearlyLiveBossBonus { get; set; } = 4;

        private static readonly System.Text.RegularExpressions.Regex ExactlyInGraveyard = new(
            @"exactly (\d|one|two|three|four|five) (\w+)(?:-type)? monsters? in your (?:graveyard|gy)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Why a revealed hand card counts as live: its summon condition ("exactly 4 Fairy monsters in your GY": Archlord
        /// Kristya; "exactly 3 DARK": Dark Armed Dragon) is met or nearly so. Their graveyard, plus the other revealed hand
        /// cards that fit (they can get there), plus one for a battle, reach the number, without the graveyard already
        /// being past it (user, 2026-11-11: Kristya with 3 Fairies in their GY was "nearly live" and the best Dustshoot pick).
        /// Null otherwise.
        /// </summary>
        private static async Task<string?> NearlyLiveBossAsync(int id, IReadOnlyCollection<int> grave, IReadOnlyCollection<int> otherHand)
        {
            if (await CardDatabase.GetAsync(id) is not DBCardInfo info || ExactlyInGraveyard.Match(info.CardText ?? "") is not { Success: true } m)
                return null;
            string word = m.Groups[1].Value.ToLowerInvariant();
            int need = word switch { "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5, _ => int.TryParse(word, out int n) ? n : 0 };
            string kind = m.Groups[2].Value.ToLowerInvariant();
            int attribute = kind switch { "earth" => 1, "water" => 2, "fire" => 4, "wind" => 8, "light" => 16, "dark" => 32, "divine" => 64, _ => 0 };
            int race = kind switch
            {
                "warrior" => 0x1, "spellcaster" => 0x2, "fairy" => 0x4, "fiend" => 0x8, "zombie" => 0x10, "machine" => 0x20,
                "aqua" => 0x40, "pyro" => 0x80, "rock" => 0x100, "dragon" => 0x2000, "beast" => 0x4000, "dinosaur" => 0x10000, _ => 0
            };
            if (need <= 0 || attribute == 0 && race == 0)
                return null;
            async Task<bool> Fits(int card) =>
                await CardDatabase.GetAsync(card) is DBCardInfo c && (c.Type & (int)CardType.Monster) != 0 &&
                (attribute != 0 ? (c.Attribute & attribute) != 0 : (c.Race & race) != 0);
            int inGrave = 0, inHand = 0;
            foreach (int card in grave)
                if (await Fits(card)) inGrave++;
            foreach (int card in otherHand)
                if (await Fits(card)) inHand++;
            if (inGrave > need || inGrave + inHand + 1 < need)
                return null;
            return inGrave == need
                ? $"live: exactly {need} {m.Groups[2].Value} in their graveyard"
                : $"nearly live: {inGrave} of {need} {m.Groups[2].Value} in their graveyard, {inHand} more in hand";
        }

        /// <summary>
        /// Charge of the Light Brigade's search (Ryko or Lyla from the deck): Ryko by default (set, it destroys a card
        /// when attacked). Lyla instead when a set Ryko wouldn't work (their set monster, likely their own Ryko, flips
        /// first; Grand Mole bounces a set monster) and they have a spell/trap for her to destroy. Null if not that prompt.
        /// </summary>
        protected async Task<IList<byte>?> SelectLightswornSearchAsync(DecisionContext<SelectCardData> context)
        {
            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            var indexed = cards.Select((card, i) => (Card: card, Index: i)).ToList();
            if ((DHint)context.StateData.SelectionHint != DHint.ATOHAND || indexed.Count == 0 ||
                !indexed.All(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Deck) ||
                !indexed.Any(x => x.Card.IsCode(RykoLightswornHunter, LylaLightswornSorceress)))
                return null;
            void Note(DecisionContext<SelectCardData> c, string message) => StapleNote(c, message);
            var enemyMonstersNow = await GetEnemyMonsterCardsAsync();
            bool rykoBlocked = enemyMonstersNow.Any(card => !card.IsFaceup()) || BouncerThreat;
            bool lylaHasTarget = (await GetEnemySpellCardsAsync()).Count > 0;
            int want = rykoBlocked && lylaHasTarget && !DeckTooSmallForLyla ? LylaLightswornSorceress : RykoLightswornHunter;
            var pick = indexed.FirstOrDefault(x => x.Card.IsCode(want));
            if (pick.Card.ID <= 0)
                pick = indexed.First(x => x.Card.IsCode(RykoLightswornHunter, LylaLightswornSorceress));
            Note(context, $"search {CardNames.Get(pick.Card.ID)}: " + (pick.Card.IsCode(LylaLightswornSorceress)
                ? "a set Ryko wouldn't work here (their set monster or Grand Mole), and Lyla destroys one of their spells/traps"
                : "set, it destroys a card when attacked"));
            return new List<byte> { (byte)pick.Index };
        }

        protected static int OpponentThreat(DynamicCard card)
        {
            // Revealed hand cards (e.g. Trap Dustshoot): no id is given, but ATK and type are, so take the biggest threat.
            if (card.CurrentCoordinates.Location == (byte)CardLocation.Hand)
                return card.HasType(CardType.Monster)
                    ? card.Attack + (card.HasType(CardType.Effect) ? 500 : 0)
                    : 0;

            // Face-down field cards: only the zone is known (the server leaks real stats/type; don't use them).
            if (BoardAssessment.IsHiddenFromBot(card))
                return card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone ? 1500 : 2000;

            int score = 0;
            if (card.HasType(CardType.Monster))
                score += card.IsFaceup() ? 2000 + card.Attack : 1500;
            else
                score += card.IsFaceup() ? (card.HasType(CardType.Continuous) || card.HasType(CardType.Field) || card.HasType(CardType.Equip) ? 2500 : 500) : 2000;
            return score;
        }

        // Revealed cards carry their identity (Alias), so duplicates are matched by id; stats are a fallback.
        protected static (int Id, int Type, int Atk, int Def, int Level, int Attribute, int Race) VisibleSignature(DynamicCard card) =>
            (BoardAssessment.KnownId(card), card.Type, card.Attack, card.Defense, card.Level, card.Attribute, card.Race);

        protected static string DescribePick(DynamicCard card) =>
            card.ID > 0 ? CardNames.Get(card.ID)
            : BoardAssessment.KnownId(card) is int known and > 0 ? $"revealed {CardNames.GetWithId(known)}"
            : "face-down card";

        /// <summary>Picks <paramref name="count"/> cards in priority order (unlisted last), then by ATK, and logs them.</summary>
        protected IList<byte> OrderedPicks<T>(
            DecisionContext<T> context,
            string label,
            List<(DynamicCard Card, int Index)> indexed,
            int count,
            int[] priority,
            bool preferLowValue)
        {
            var picks = indexed
                .OrderBy(x => StaplePriorityIndex(priority, x.Card))
                .ThenBy(x => preferLowValue ? x.Card.Attack : -x.Card.Attack)
                .Take(count)
                .ToList();
            StapleNote(context, $"{label}: {string.Join(", ", picks.Select(x => DescribePick(x.Card)))}");
            return picks.Select(x => (byte)x.Index).ToList();
        }
    }
}
