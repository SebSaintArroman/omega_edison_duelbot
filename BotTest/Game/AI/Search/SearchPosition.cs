using System.Globalization;
using System.Text;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Search
{
    /// <summary>One card of a rebuilt position. Player 0 is always the searching bot.</summary>
    internal sealed class PlacedCard
    {
        public int Code;
        public int Owner;
        public int Controller;
        /// <summary>Engine location (LOCATION_*: 1 deck, 2 hand, 4 monster zone, 8 spell/trap zone, 0x10 GY, 0x20 banished, 0x40 extra).</summary>
        public uint Location;
        public int Sequence;
        /// <summary>Engine position (POS_*: 1 face-up attack, 2 face-down attack, 4 face-up defence, 8 face-down defence).</summary>
        public uint Position;
        public bool KnownDeckTop;
        /// <summary>Already attacked this turn (it can't attack again in the rebuilt turn).</summary>
        public bool Attacked;
        /// <summary>Index (in SearchPosition.Cards) of the monster this card is equipped to, or -1.</summary>
        public int EquippedTo = -1;
        /// <summary>Indexes of the cards this card targets (continuous traps like Call of the Haunted).</summary>
        public List<int> Targets { get; } = new();
        public List<(int Type, int Count)> Counters { get; } = new();
        /// <summary>False for an Extra Deck monster that wasn't properly summoned (it can't be revived or taken by Goyo).</summary>
        public bool Proper = true;
        /// <summary>Ours, arrived or changed this turn (ViewCard.ThisTurn): no position change; a set trap / quick-play can't be activated.</summary>
        public bool ThisTurn;
        /// <summary>Ours: can't attack in the rebuilt turn (its own effect used this turn said so).</summary>
        public bool NoAttack;
        /// <summary>Base ATK / DEF as the real board shows them (-1: unknown), see ViewCard.BaseAttack.</summary>
        public int BaseAttack = -1, BaseDefense = -1;
        /// <summary>Current ATK / DEF as the real board shows them (-1: unknown), see ViewCard.Attack.</summary>
        public int Attack = -1, Defense = -1;
        /// <summary>Theirs by ownership, ours until the End Phase (Brain Control): placed with its real owner and a control effect that
        /// ends with the turn (without one the engine sends it straight home).</summary>
        public bool BorrowedUntilEnd;
        /// <summary>Controlled by a player who doesn't own it (Goyo Guardian's prize, Brain Control): held by a control effect, for
        /// good unless BorrowedUntilEnd.</summary>
        public bool TakenControl;
        /// <summary>Theirs: battled one of ours this turn (ViewCard.Battled).</summary>
        public bool Battled;
        /// <summary>Ours: destroyed in our next End Phase (Jinzo revived by Jinzo - Returner), as the real card's effect does.</summary>
        public bool DestroyAtOwnersEnd;
        /// <summary>End Phases (counting this turn's) until its battle position may change again (0: free): Lyla's lock (ViewCard.PositionLockEnds).</summary>
        public int PositionLockEndPhases;
    }

    /// <summary>
    /// A complete position (the bot's view plus sampled hidden cards) as a puzzle script for the rules engine. The engine
    /// starts it at turn 1 of player 0 (us), Main Phase 1. What a puzzle can't carry is approximated: an already used
    /// Normal Summon, monsters that already attacked and a Battle Phase already behind us are blocked by effects that
    /// last until the End Phase.
    /// </summary>
    internal sealed class SearchPosition
    {
        public const uint LocDeck = 0x01, LocHand = 0x02, LocMZone = 0x04, LocSZone = 0x08, LocGrave = 0x10,
            LocRemoved = 0x20, LocExtra = 0x40;
        public const uint PosFaceUpAttack = 0x1, PosFaceDownAttack = 0x2, PosFaceUpDefense = 0x4, PosFaceDownDefense = 0x8,
            PosFaceUp = 0x5, PosFaceDown = 0xa;


        internal static readonly string BlindRevealSetup = $$"""

            local blank={{SearchView.InertCard}}
            local restrictions={EFFECT_CANNOT_SUMMON,EFFECT_CANNOT_FLIP_SUMMON,EFFECT_CANNOT_SPECIAL_SUMMON,
                                EFFECT_CANNOT_MSET,EFFECT_CANNOT_SSET}
            local get_top=Duel.GetDecktopGroup
            function _st_blank_card(c)
                if _st_known_deck[c] or _st_blanks[c] then return end
                c:Recreate(blank,nil,nil,0,nil,nil,nil,nil,nil,nil,nil,nil,true)
                _st_blanks[c]=true
                for _,code in ipairs(restrictions) do
                    local e=Effect.CreateEffect(c)
                    e:SetType(EFFECT_TYPE_SINGLE)
                    e:SetProperty(EFFECT_FLAG_CANNOT_DISABLE|EFFECT_FLAG_UNCOPYABLE)
                    e:SetCode(code)
                    e:SetValue(1)
                    c:RegisterEffect(e,true)
                end
            end
            function _st_blank_top(p,n)
                if _st_hide_new and p==0 and n>0 then
                    for c in aux.Next(get_top(p,n)) do _st_blank_card(c) end
                end
            end
            local discard_deck=Duel.DiscardDeck
            Duel.DiscardDeck=function(p,n,r)
                if _st_hide_new and p==0 and Duel.IsPlayerCanDiscardDeck(p,0) then _st_blank_top(p,n) end
                return discard_deck(p,n,r)
            end
            Duel.GetDecktopGroup=function(p,n)
                local g=get_top(p,n)
                if _st_hide_new and p==0 then _st_blind_groups[g]=true end
                return g
            end
            for _,name in ipairs({"SendtoGrave","Remove","SendtoHand"}) do
                local move=Duel[name]
                Duel[name]=function(g,...)
                    if _st_hide_new and _st_blind_groups[g] then
                        for c in aux.Next(g) do
                            if c:IsLocation(LOCATION_DECK) and c:IsControler(0) then _st_blank_card(c) end
                        end
                        _st_blind_groups[g]=nil
                    end
                    return move(g,...)
                end
            end
            local shuffle_deck=Duel.ShuffleDeck
            Duel.ShuffleDeck=function(p,...)
                if p==0 then _st_known_deck={} end
                return shuffle_deck(p,...)
            end

            """;

        public int[] LifePoints { get; } = { 8000, 8000 };
        public List<PlacedCard> Cards { get; } = new();
        public bool NormalSummonUsed { get; set; }
        /// <summary>The Battle Phase is behind us (deciding in Main Phase 2): no Battle Phase in the rebuilt turn.</summary>
        public bool BattlePhaseDone { get; set; }

        /// <summary>Cards a replay must draw, in order (DeckBotBrain.MidChain): each draw effect of ours in the rebuilt turn first moves the
        /// next of these to the top of the deck, so a shuffle before the draw (Pot of Avarice) still draws what the real game drew.</summary>
        public List<int> ForcedDraws { get; } = new();

        /// <summary>Cards a random pick takes first when they're among its choices (a replay's real random outcomes: Thestalos's discard).</summary>
        public List<int> RandomPicks { get; } = new();

        /// <summary>
        /// Turns the engine skips before play starts (EFFECT_SKIP_TURN: a skipped turn only runs its resets), for a replay from the start
        /// of a real turn (TurnReplay): 1 = their turn 2 is the rebuilt turn (with its draw), 2 = our turn 3 is (with its draw; turn 1
        /// never draws). 0 = the rebuilt turn is turn 1, ours, as for every search from our own main / battle decisions.
        /// </summary>
        public int SkipTurns { get; set; }

        /// <summary>Our monster zone whose monster alone may attack in the rebuilt turn (-1: none): Sirocco's restriction (SearchView.OnlyAttackerZone).</summary>
        public int OnlyAttackerZone { get; set; } = -1;

        /// <summary>The engine turn the line's real turn is played on.</summary>
        public int RebuiltTurn => 1 + SkipTurns;

        public PlacedCard Add(int code, int controller, uint location, int sequence, uint position, int? owner = null)
        {
            var card = new PlacedCard
            {
                Code = code, Owner = owner ?? controller, Controller = controller, Location = location,
                Sequence = sequence, Position = position,
            };
            Cards.Add(card);
            return card;
        }

        public string ToLua(ulong flags)
        {
            var lua = new StringBuilder(8192);
            string N(ulong v) => v.ToString(CultureInfo.InvariantCulture);
            lua.Append("Debug.ReloadFieldBegin(").Append(N(flags)).Append(")\n");
            for (int p = 0; p < 2; p++)
                lua.Append("Debug.SetPlayerInfo(").Append(p).Append(',').Append(Math.Max(1, LifePoints[p])).Append(",0,1)\n");
            lua.Append("local c={}\n");
            for (int i = 0; i < Cards.Count; i++)
            {
                var card = Cards[i];
                bool proc = card.Proper && card.Location is LocMZone or LocGrave or LocRemoved or LocSZone;
                lua.Append("c[").Append(i + 1).Append("]=Debug.AddCard(").Append(card.Code).Append(',').Append(card.Owner).Append(',')
                    .Append(card.Controller).Append(',').Append(card.Location).Append(',').Append(card.Sequence).Append(',')
                    .Append(card.Position).Append(',').Append(proc ? "true" : "false").Append(")\n");
            }
            for (int i = 0; i < Cards.Count; i++)
            {
                var card = Cards[i];
                if (card.EquippedTo >= 0)
                    lua.Append("if c[").Append(i + 1).Append("] and c[").Append(card.EquippedTo + 1).Append("] then Debug.PreEquip(c[")
                        .Append(i + 1).Append("],c[").Append(card.EquippedTo + 1).Append("]) ")
                        // A union equipped by its own effect also gets its union state (equip limit + union status); a bare
                        // PreEquip leaves an equip the engine destroys at once as illegal (round-robin entry 6: Gearframe
                        // vanished from every rebuilt board). With the state, unequipping it is on offer too.
                        .Append("if c[").Append(i + 1).Append("]:IsHasEffect(EFFECT_UNION_LIMIT) then aux.SetUnionState(c[")
                        .Append(i + 1).Append("]) end end\n");
                foreach (int target in card.Targets)
                    lua.Append("if c[").Append(i + 1).Append("] and c[").Append(target + 1).Append("] then Debug.PreSetTarget(c[")
                        .Append(i + 1).Append("],c[").Append(target + 1).Append("]) end\n");
                foreach (var (type, count) in card.Counters)
                    lua.Append("if c[").Append(i + 1).Append("] then Debug.PreAddCounter(c[").Append(i + 1).Append("],")
                        .Append(type).Append(',').Append(count).Append(") end\n");
            }
            lua.Append("Debug.ReloadFieldEnd()\n");
            lua.Append("_st_hide_new=false _st_known_deck={} _st_blanks={} _st_blind_groups=setmetatable({},{__mode='k'})\n");
            for (int i = 0; i < Cards.Count; i++)
                if (Cards[i].KnownDeckTop)
                    lua.Append("if c[").Append(i + 1).Append("] then _st_known_deck[c[").Append(i + 1).Append("]]=true end\n");
                else if (Cards[i] is { Controller: 0, Location: LocHand, Code: SearchView.InertCard })
                    lua.Append("if c[").Append(i + 1).Append("] then _st_blank_card(c[").Append(i + 1).Append("]) end\n");
            // The engine's normal turn draw does not call the Lua Duel.Draw wrapper.
            lua.Append("do local e=Effect.GlobalEffect() e:SetType(EFFECT_TYPE_FIELD|EFFECT_TYPE_CONTINUOUS) e:SetCode(EVENT_DRAW) ")
                .Append("e:SetOperation(function(e,tp,eg,ep) if _st_hide_new and ep==0 then for c in aux.Next(eg) do _st_blank_card(c) end end end) ")
                .Append("Duel.RegisterEffect(e,0) end\n");
            // Monsters controlled by the player who doesn't own them: a control effect keeps them where they are (without one the
            // engine sends a card home to its owner). Borrowed ones (C1, user 2026-11-16: Brain Control) go back at the End Phase, as
            // the card's own effect does it; taken ones (Goyo Guardian) stay.
            for (int i = 0; i < Cards.Count; i++)
                if (Cards[i].TakenControl || Cards[i].BorrowedUntilEnd)
                    lua.Append($"if c[{i + 1}] then local e=Effect.CreateEffect(c[{i + 1}]) e:SetType(EFFECT_TYPE_SINGLE) e:SetCode(EFFECT_SET_CONTROL) ")
                        .Append($"e:SetValue({Cards[i].Controller}) e:SetReset(RESET_EVENT+RESETS_STANDARD{(Cards[i].BorrowedUntilEnd ? "+RESET_PHASE+PHASE_END" : "")}) ")
                        .Append($"c[{i + 1}]:RegisterEffect(e) end\n");
            // Temporary monsters keep their fate (user, 2026-11-18: "the jinzo ... dies next turn ... by the effect of jinzo returner"): a Jinzo
            // revived by Jinzo - Returner is destroyed in its controller's End Phase (Returner's own delayed effect, which a rebuilt card lacks),
            // and our Darius shuffles the monster it revived (its card target) back into the deck when it leaves the field (Darius' own
            // leave-field handler, registered when it revived and lost in a rebuild).
            for (int i = 0; i < Cards.Count; i++)
            {
                string c = $"c[{i + 1}]";
                // A position lock that outlives the rebuild (Lyla's "cannot change its battle position until the end of your next turn"): counted in
                // End Phases, the skipped turns' ones included (their resets fire too).
                if (Cards[i].PositionLockEndPhases > 0 && Cards[i].Location == LocMZone)
                    lua.Append($"if {c} then local e=Effect.CreateEffect({c}) e:SetType(EFFECT_TYPE_SINGLE) e:SetCode(EFFECT_CANNOT_CHANGE_POSITION) ")
                        .Append($"e:SetReset(RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END,{Cards[i].PositionLockEndPhases + SkipTurns}) {c}:RegisterEffect(e) end\n");
                if (Cards[i].DestroyAtOwnersEnd && Cards[i].Location == LocMZone)
                    lua.Append($"if {c} then local e=Effect.CreateEffect({c}) e:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS) e:SetRange(LOCATION_MZONE) ")
                        .Append($"e:SetCode(EVENT_PHASE+PHASE_END) e:SetCountLimit(1) e:SetCondition(function(e) return Duel.IsTurnPlayer(e:GetHandler():GetControler()) end) ")
                        .Append($"e:SetOperation(function(e) Duel.Destroy(e:GetHandler(),REASON_EFFECT) end) e:SetReset(RESET_EVENT+RESETS_STANDARD) {c}:RegisterEffect(e) end\n");
                if (Cards[i].Location == LocMZone && Cards[i].Targets.Count > 0 && (Cards[i].Code == GladiatorBeastDarius || SearchHost.CanonicalId(Cards[i].Code) == GladiatorBeastDarius))
                    lua.Append($"if {c} and c{GladiatorBeastDarius} and c{GladiatorBeastDarius}.regop then local e=Effect.CreateEffect({c}) e:SetType(EFFECT_TYPE_SINGLE+EFFECT_TYPE_CONTINUOUS) ")
                        .Append($"e:SetCode(EVENT_LEAVE_FIELD_P) e:SetOperation(c{GladiatorBeastDarius}.regop) e:SetReset(RESET_EVENT+RESETS_STANDARD) {c}:RegisterEffect(e) end\n");
            }
            // Sirocco the Dawn's restriction this turn: our other monsters, those summoned later too, can't attack (its own field effect, by the
            // target's field id, reset at the End Phase; the skipped turns' End Phases count).
            int onlyAttacker = OnlyAttackerZone < 0 ? -1 : Cards.FindIndex(x => x.Controller == 0 && x.Location == LocMZone && x.Sequence == OnlyAttackerZone);
            if (onlyAttacker >= 0)
                lua.Append($"do local tc=c[{onlyAttacker + 1}] if tc then local e=Effect.GlobalEffect() e:SetType(EFFECT_TYPE_FIELD) e:SetCode(EFFECT_CANNOT_ATTACK) ")
                    .Append("e:SetProperty(EFFECT_FLAG_OATH) e:SetTargetRange(LOCATION_MZONE,0) local fid=tc:GetFieldID() e:SetTarget(function(e,c) return c:GetFieldID()~=fid end) ")
                    .Append($"e:SetReset(RESET_PHASE+PHASE_END,{1 + SkipTurns}) Duel.RegisterEffect(e,0) end end\n");
            // The stand-in for unknown cards (SearchView.InertCard, Bubble Blaster) must never do anything: no copy can be activated (a safety
            // net: one Zombie TeleDAD list plays Bubbleman; the old stand-in, Jar Robber, answered the Pot of Greed every deck plays now).
            lua.Append("do local e=Effect.GlobalEffect() e:SetType(EFFECT_TYPE_FIELD) e:SetProperty(EFFECT_FLAG_PLAYER_TARGET) e:SetCode(EFFECT_CANNOT_ACTIVATE) ")
                .Append("e:SetTargetRange(1,1) e:SetValue(function(e,re,tp) return re:GetHandler():IsCode(").Append(SearchView.InertCard).Append(") end) Duel.RegisterEffect(e,0) end\n");
            // The rebuilt turn (1, or later after skipped turns: SkipTurns): the hacks below hold only while the line is in it.
            lua.Append("_st_turn=").Append(RebuiltTurn).Append('\n');
            // Skipped turns: player 0's turn 1, and with 2 also player 1's turn 2 (reset at each skipped turn's End: count 2 outlives turn 1).
            for (int p = 0; p < Math.Min(2, SkipTurns); p++)
                lua.Append("do local e=Effect.GlobalEffect() e:SetType(EFFECT_TYPE_FIELD) e:SetProperty(EFFECT_FLAG_PLAYER_TARGET) e:SetCode(EFFECT_SKIP_TURN) ")
                    .Append("e:SetTargetRange(1,0) e:SetReset(RESET_PHASE+PHASE_END,").Append(p + 1).Append(") Duel.RegisterEffect(e,").Append(p).Append(") end\n");
            // Cards that got where they are this turn (summoned, set, sent to the graveyard, banished): a rebuilt card has no turn
            // id, so "except the turn it was sent to the GY" (aux.exccon: Skill Successor) and "the turn it was summoned" checks
            // read it as an old card (seen 2026-11-15: a mid-chain replay offered Skill Successor's GY effect the turn it was sent).
            // GetTurnID answers the rebuilt turn for them while the line is in that turn; the engine's own id once it moves on.
            lua.Append("if not _st_orig then _st_orig=Card.GetTurnID Card.GetTurnID=function(c) if _st_cards and _st_cards[c] and Duel.GetTurnCount()==_st_turn then return _st_turn end return _st_orig(c) end end\n");
            lua.Append("_st_cards={}\n");
            // A replay's draws: before each of our draw effects in the rebuilt turn, the card the real game drew goes to the top of the
            // deck (a shuffle first, as Pot of Avarice does, would otherwise draw at random; user, 2026-11-15). Only these cards, once each.
            lua.Append("_st_draws={").Append(string.Join(",", ForcedDraws)).Append("}\n");
            // A replay's random picks (seen 2026-11-18: Thestalos discarded another card of ours in the line): the cards that really went, first.
            lua.Append("_st_random={").Append(string.Join(",", RandomPicks)).Append("}\n");
            lua.Append("if not _st_rs then _st_rs=Group.RandomSelect Group.RandomSelect=function(g,p,n,...) ")
                .Append("if _st_random and #_st_random>0 and Duel.GetTurnCount()==_st_turn then local out=Group.CreateGroup() ")
                .Append("for i=1,#_st_random do if out:GetCount()<n then local tc=g:Filter(function(c) return c:IsCode(_st_random[i]) and not out:IsContains(c) end,nil):GetFirst() ")
                .Append("if tc then out:AddCard(tc) _st_random[i]=0 end end end ")
                .Append("if out:GetCount()==n then return out end end return _st_rs(g,p,n,...) end end\n");
            lua.Append("if not _st_draw then _st_draw=Duel.Draw Duel.Draw=function(p,n,r) ")
                .Append("if p==0 and _st_draws and #_st_draws>0 and Duel.GetTurnCount()==_st_turn then ")
                // Cards no longer in the deck (a search took them first) are dropped, so they don't hold up the cards behind them.
                .Append("local keep={} for _,id in ipairs(_st_draws) do if Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_DECK,0,1,nil,id) then table.insert(keep,id) end end _st_draws=keep ")
                .Append("local k=math.min(n,#_st_draws) for i=k,1,-1 do local tc=Duel.GetMatchingGroup(Card.IsCode,0,LOCATION_DECK,0,nil,_st_draws[i]):GetFirst() ")
                .Append("if tc then Duel.MoveSequence(tc,0) end end for i=1,k do table.remove(_st_draws,1) end end ")
                .Append("if _st_hide_new and p==0 and Duel.IsPlayerCanDraw(p,n) then _st_blank_top(p,n) end ")
                .Append("return _st_draw(p,n,r) end end\n");
            // Our monsters that attacked this turn battled: a Gladiator Beast's tag-out checks GetBattledGroupCount() > 0, which a
            // rebuilt card reads as 0 (seen 2026-11-15: a mid-chain replay after Laquari's attack never got its tag-out window).
            lua.Append("if not _st_bgc then _st_bgc=Card.GetBattledGroupCount Card.GetBattledGroupCount=function(c) local n=_st_bgc(c) ")
                .Append("if n==0 and _st_battled and _st_battled[c] and Duel.GetTurnCount()==_st_turn then return 1 end return n end end\n");
            lua.Append("_st_battled={}\n");
            // A Gladiator Beast summoned by a Gladiator's effect carries its own flag (Laquari: 2100 ATK); the rebuilt one doesn't,
            // so its base stats come out wrong (seen 2026-11-15: Gorz into a 2100 Laquari dealt 600, the lines 900). Restored when
            // the real base ATK / DEF differ from the rebuilt card's.
            for (int i = 0; i < Cards.Count; i++)
            {
                var card = Cards[i];
                if (card.Location != LocMZone || card.BaseAttack < 0 && card.BaseDefense < 0)
                    continue;
                string c = $"c[{i + 1}]";
                string differs = (card.BaseAttack >= 0 ? $"{c}:GetBaseAttack()~={card.BaseAttack}" : "false") + " or " +
                                 (card.BaseDefense >= 0 ? $"{c}:GetBaseDefense()~={card.BaseDefense}" : "false");
                lua.Append($"if {c} and {c}:IsFaceup() and {c}:IsSetCard(SET_GLADIATOR) and ({differs}) then ")
                    .Append($"{c}:RegisterFlagEffect({c}:GetOriginalCode(),RESET_EVENT+RESETS_STANDARD,0,1) end\n");
                // A token's stats come from the effect that made it (Gorz's Emissary of Darkness Token: the damage taken); the rebuilt
                // one is 0/0 (seen 2026-11-15: Ryko into an 1800-DEF token cost 1600 LP the lines never saw). Set them as they are.
                foreach (var (value, code) in new[] { (card.BaseAttack, "EFFECT_SET_BASE_ATTACK"), (card.BaseDefense, "EFFECT_SET_BASE_DEFENSE") })
                    if (value >= 0)
                        lua.Append($"if {c} and {c}:IsType(TYPE_TOKEN) then local e=Effect.CreateEffect({c}) e:SetType(EFFECT_TYPE_SINGLE) ")
                            .Append($"e:SetCode({code}) e:SetValue({value}) e:SetReset(RESET_EVENT+RESETS_STANDARD) {c}:RegisterEffect(e) end\n");
            }
            for (int i = 0; i < Cards.Count; i++)
                if ((Cards[i].Attacked || Cards[i].Battled) && Cards[i].Location == LocMZone)
                    lua.Append("if c[").Append(i + 1).Append("] then _st_battled[c[").Append(i + 1).Append("]]=true end\n");
            for (int i = 0; i < Cards.Count; i++)
                if (Cards[i].ThisTurn)
                    lua.Append("if c[").Append(i + 1).Append("] then _st_cards[c[").Append(i + 1).Append("]]=true end\n");

            // What the puzzle can't carry over from the real turn.
            const string untilEnd = "RESET_PHASE+PHASE_END";
            void PlayerBlock(string code) =>
                lua.Append("do local e=Effect.GlobalEffect() e:SetType(EFFECT_TYPE_FIELD) e:SetProperty(EFFECT_FLAG_PLAYER_TARGET) ")
                    .Append("e:SetCode(").Append(code).Append(") e:SetTargetRange(1,0) e:SetReset(").Append(untilEnd)
                    .Append(") Duel.RegisterEffect(e,0) end\n");
            if (NormalSummonUsed)
            {
                PlayerBlock("EFFECT_CANNOT_SUMMON");
                PlayerBlock("EFFECT_CANNOT_MSET");
            }
            if (BattlePhaseDone)
                PlayerBlock("EFFECT_CANNOT_BP");
            // Ours, this turn (CSV S1 follow-up, 2026-11-15: the rebuilt turn offered moves the real one didn't): a monster summoned,
            // flipped, repositioned or that attacked can't change position again; a trap or quick-play spell set this turn can't be
            // activated until next turn. Effects, not card statuses: the engine clears statuses at the rebuilt turn's Draw Phase.
            for (int i = 0; i < Cards.Count; i++)
            {
                var card = Cards[i];
                if (!(card.ThisTurn || card.Attacked) || card.Controller != 0)
                    continue;
                string c = $"c[{i + 1}]";
                if (card.Location == LocMZone)
                    lua.Append("if ").Append(c).Append(" then local e=Effect.CreateEffect(").Append(c)
                        .Append(") e:SetType(EFFECT_TYPE_SINGLE) e:SetCode(EFFECT_CANNOT_CHANGE_POSITION) e:SetReset(RESET_EVENT+RESETS_STANDARD+")
                        .Append(untilEnd).Append(") ").Append(c).Append(":RegisterEffect(e) end\n");
                else if (card.Location == LocSZone && card.ThisTurn)
                    lua.Append("if ").Append(c).Append(" and ").Append(c).Append(":IsFacedown() and (").Append(c).Append(":IsType(TYPE_TRAP) or ")
                        .Append(c).Append(":IsType(TYPE_QUICKPLAY)) then local e=Effect.CreateEffect(").Append(c)
                        .Append(") e:SetType(EFFECT_TYPE_SINGLE) e:SetCode(EFFECT_CANNOT_TRIGGER) e:SetReset(RESET_EVENT+RESETS_STANDARD+")
                        .Append(untilEnd).Append(") ").Append(c).Append(":RegisterEffect(e) end\n");
            }
            for (int i = 0; i < Cards.Count; i++)
            {
                if (!Cards[i].Attacked && !Cards[i].NoAttack)
                    continue;
                lua.Append("if c[").Append(i + 1).Append("] then local e=Effect.CreateEffect(c[").Append(i + 1)
                    .Append("]) e:SetType(EFFECT_TYPE_SINGLE) e:SetCode(EFFECT_CANNOT_ATTACK) e:SetReset(RESET_EVENT+RESETS_STANDARD+")
                    .Append(untilEnd).Append(") c[").Append(i + 1).Append("]:RegisterEffect(e) end\n");
            }
            // Current ATK / DEF changed by effects that resolved before the rebuild (manual game 2026-11-16: Card Trooper was 1900 for
            // real and 400 in the lines, Kristya 1400 after Gale's halving and 2800 in the lines). The difference to what the rebuilt
            // card shows now (its equips and continuous effects are already in that) is added back: a boost ends with the turn, a
            // drop stays.
            for (int i = 0; i < Cards.Count; i++)
            {
                var card = Cards[i];
                if (card.Location != LocMZone || card.Attack < 0 && card.Defense < 0)
                    continue;
                string c = $"c[{i + 1}]";
                foreach (var (value, get, code) in new[] { (card.Attack, "GetAttack", "EFFECT_UPDATE_ATTACK"), (card.Defense, "GetDefense", "EFFECT_UPDATE_DEFENSE") })
                    if (value >= 0)
                        lua.Append($"if {c} and {c}:IsFaceup() and {c}:IsType(TYPE_MONSTER) then local d={value}-{c}:{get}() if d~=0 then ")
                            .Append($"local e=Effect.CreateEffect({c}) e:SetType(EFFECT_TYPE_SINGLE) e:SetCode({code}) e:SetValue(d) ")
                            .Append($"if d>0 then e:SetReset(RESET_EVENT+RESETS_STANDARD+{untilEnd}) else e:SetReset(RESET_EVENT+RESETS_STANDARD) end ")
                            .Append($"{c}:RegisterEffect(e) end end\n");
            }
            return lua.ToString();
        }
    }
}
