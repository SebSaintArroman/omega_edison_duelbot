const deckSelect = document.getElementById("deckSelect");
const startButton = document.getElementById("startButton");
const clearLogsButton = document.getElementById("clearLogsButton");
const statusBadge = document.getElementById("statusBadge");
const statusDetail = document.getElementById("statusDetail");
const deckCarousel = document.getElementById("deckCarousel");
const carouselTitle = document.getElementById("carouselTitle");
const carouselPrevButton = document.getElementById("carouselPrevButton");
const carouselNextButton = document.getElementById("carouselNextButton");
const terminalPanel = document.getElementById("terminalPanel");
const terminal = document.getElementById("terminal");
const followToggle = document.getElementById("followToggle");
const chatterToggle = document.getElementById("chatterToggle");
const thinkingToggle = document.getElementById("thinkingToggle");
const jumpButton = document.getElementById("jumpButton");
const collapseButton = document.getElementById("collapseButton");
const commentForm = document.getElementById("commentForm");
const commentText = document.getElementById("commentText");
const commentButton = document.getElementById("commentButton");
const commentStatus = document.getElementById("commentStatus");
const commentList = document.getElementById("commentList");
const commentCount = document.getElementById("commentCount");
const cardPreview = document.getElementById("cardPreview");
const cardPreviewImage = document.getElementById("cardPreviewImage");

const RANDOM_DECK_ID = "random";
const MAX_TERMINAL_LINES = 4000;
const DEFAULT_DECK_ART = "/assets/omega.jpg";
const DECK_ASSETS = {
  Vayu: "/assets/vayu.jpg",
  MachinaControl: "/assets/machina-control.jpg",
  MachinaJinzo: "/assets/machina-jinzo.jpg",
  HeroFrog: "/assets/hero-frog.jpg",
  Blackwing: "/assets/blackwing.jpg",
  BlackwingHybrid: "/assets/blackwing-hybrid.jpg",
  GladiatorControl: "/assets/gladiator-control.jpg",
  random: "/assets/random.jpg"
};

let format = null; // Master Duel only
let selectedDeckId = "";
let isSelectionDirty = false;
let previewHideTimeout = null;
let terminalLast = 0;
let autoEnabled = true; // auto-start: the bot fills waiting Omega bot slots by itself

function storageGet(key) {
  try { return window.localStorage.getItem(key); } catch { return null; }
}

function storageSet(key, value) {
  try { window.localStorage.setItem(key, value); } catch { /* storage unavailable */ }
}

async function fetchJson(url, options) {
  const response = await fetch(url, {
    headers: { "Content-Type": "application/json" },
    ...options
  });
  const payload = await response.json();
  if (!response.ok) {
    throw new Error(payload.message || "The operation failed.");
  }
  return payload;
}

function getDeckAsset(deck) {
  return DECK_ASSETS[deck?.assetDeckId || deck?.id] || DEFAULT_DECK_ART;
}

function applyImageWithFallback(image, src, fallback, alt) {
  image.onerror = () => {
    image.onerror = null;
    if (!image.src.endsWith(fallback)) {
      image.src = fallback;
    }
  };
  image.alt = alt;
  image.src = src;
}

function renderDecks() {
  deckSelect.innerHTML = "";
  if (!format) {
    return;
  }
  for (const deck of format.decks) {
    const option = document.createElement("option");
    option.value = deck.id;
    option.textContent = deck.name;
    deckSelect.appendChild(option);
  }
  const hasDeck = format.decks.some((deck) => deck.id === selectedDeckId);
  deckSelect.value = hasDeck ? selectedDeckId : format.decks[0]?.id || "";
  selectedDeckId = deckSelect.value;
  renderDeckCarousel();
}

function renderDeckCarousel() {
  deckCarousel.innerHTML = "";
  if (!format) {
    return;
  }
  carouselTitle.textContent = `${format.name} Showcase`;
  // "Random deck" first (the art of Question): one of the real decks is picked when the duel starts.
  const decks = [...format.decks].sort((a, b) => (b.id === RANDOM_DECK_ID) - (a.id === RANDOM_DECK_ID));
  for (const deck of decks) {
    const card = document.createElement("button");
    card.type = "button";
    card.className = "deck-card";
    card.dataset.selected = String(deck.id === deckSelect.value);

    const cover = document.createElement("img");
    applyImageWithFallback(cover, getDeckAsset(deck), DEFAULT_DECK_ART, `${deck.name} cover`);

    const content = document.createElement("div");
    content.className = "deck-card-content";
    const label = document.createElement("span");
    label.className = "deck-card-format";
    label.textContent = format.name;
    const title = document.createElement("strong");
    title.className = "deck-card-title";
    title.textContent = deck.name;
    const caption = document.createElement("span");
    caption.className = "deck-card-caption";
    const isRandom = deck.id === RANDOM_DECK_ID;
    caption.textContent = deck.id === deckSelect.value
      ? (isRandom ? "Selected: a deck is drawn when the duel starts." : "Selected for the next duel.")
      : (isRandom ? "Let the duel pick a deck for you." : "Click to select this deck.");
    card.dataset.random = String(isRandom);
    content.append(label, title, caption);
    card.append(cover, content);

    card.addEventListener("click", () => {
      if (deckSelect.value === deck.id) {
        return;
      }
      deckSelect.value = deck.id;
      selectedDeckId = deck.id;
      renderDeckCarousel();
      saveSelection().catch(showError);
    });
    deckCarousel.appendChild(card);
  }
  queueMicrotask(() => {
    deckCarousel.querySelector('[data-selected="true"]')?.scrollIntoView({ behavior: "auto", inline: "center", block: "nearest" });
  });
}

function updateState(state) {
  if (!isSelectionDirty && state.selectedDeckId !== deckSelect.value) {
    selectedDeckId = state.selectedDeckId;
    deckSelect.value = selectedDeckId;
    renderDeckCarousel();
  }
  statusBadge.textContent = state.status;
  statusDetail.textContent = state.statusDetail;
  autoEnabled = state.autoEnabled !== false;
  startButton.textContent = autoEnabled ? "Pause auto-start" : "Resume auto-start";
  startButton.className = autoEnabled ? "button button-secondary" : "button button-primary";
  deckSelect.disabled = state.isBusy;
}

// ---------------------------------------------------------------- terminal

// The terminal gets three kinds of line: a decision ("[12:01:02 T3 Main1] NORMAL SUMMON Raiza ..."), the reasons under
// it ("    because herofrog: ..."), and engine chatter from the wrapper ("[Bot 0] [SelectPlace] ...").
const DECISION_LINE = /^\[(\d{2}:\d{2}:\d{2}(?:\.\d+)?) T(\d+) ([A-Za-z0-9]+)\] (.*)$/;
const REASON_LINE = /^\s+because ([a-z][a-z ]*?): (.*)$/;
const VALIDATOR_LINE = /^\s+\(validator changed it to: (.*)\)$/;
const OFFER_LINE = /^\s+monsters on offer: (.*)$/;
const DETAIL_LINE = /^\s+detail ([a-z ]+): (.*)$/;
// A move's score breakdown, one line per move on offer (the played one first): "breakdown: move|score|lines|loss|lethal|parts|role|
// interval|line" (see addBreakdown).
const BREAKDOWN_LINE = /^\s+breakdown: (.*)$/;
const CHATTER_LINE = /^(OnSelectCard|Selecting defender|No specific defender|No advantageous attack|\[Random deck\]|\[Bot \d+\] \[(?!DuelLog)|\[DECK BOT BRAIN\]|OnBattleCmd|OnIdleCmd|Direct Attack with Card ID|Attack with Card ID|No attackable cards)/;
// ASP.NET's own startup lines ("info: Microsoft.Hosting.Lifetime[14]", "Now listening on ...").
const HOSTING_LINE = /^\s*(warn|info|dbug|fail|crit|trce): Microsoft\.|^\s*(Now listening on|Application started|Hosting environment|Content root path|The ASP\.NET Core developer certificate)/;
const PHASE_NAMES = {
  Draw: "Draw", Standby: "Standby", Main1: "Main 1", Main2: "Main 2", BattleStart: "Battle", BattleStep: "Battle",
  Battle: "Battle", Damage: "Damage", DamageCal: "Damage", End: "End"
};

let currentEntry = null;
let currentTurn = null;
let skippingZone = false; // inside a skipped zone-choice entry (its indented lines are skipped too)

function actionTone(verb) {
  if (/SUMMON|FLIP/.test(verb)) return "summon";
  if (/ACTIVATE|CHAIN/.test(verb)) return "activate";
  if (/ATTACK/.test(verb)) return "attack";
  if (/^SET/.test(verb)) return "set";
  return "quiet";
}

function buildDecision(match) {
  const [, time, turn, phase, action] = match;
  const entry = document.createElement("div");
  entry.className = "entry";

  const head = document.createElement("div");
  head.className = "entry-head";
  const stamp = document.createElement("span");
  stamp.className = "entry-time";
  stamp.textContent = time.slice(0, 8);
  const phaseChip = document.createElement("span");
  phaseChip.className = "entry-phase";
  phaseChip.textContent = PHASE_NAMES[phase] || phase;
  const verbMatch = action.match(/^((?:[A-Z]{2,}|S\/T)(?: (?:[A-Z]{2,}|S\/T))*)\s*(.*)$/);
  const verb = document.createElement("span");
  verb.className = "entry-verb";
  const what = document.createElement("span");
  what.className = "entry-what";
  if (verbMatch) {
    verb.textContent = verbMatch[1].toLowerCase();
    verb.dataset.tone = actionTone(verbMatch[1]);
    renderWithCardCodes(what, verbMatch[2]);
  } else {
    verb.hidden = true;
    renderWithCardCodes(what, action);
  }
  // One summary line per action; click it for the reasons and details (time, cards, options, notes).
  const caret = document.createElement("span");
  caret.className = "entry-caret";
  caret.setAttribute("aria-hidden", "true");
  head.append(caret, stamp, phaseChip, verb, what);
  head.tabIndex = 0;
  head.setAttribute("role", "button");
  head.setAttribute("aria-expanded", "false");
  const toggle = () => {
    const open = entry.dataset.open !== "true";
    entry.dataset.open = open ? "true" : "false";
    head.setAttribute("aria-expanded", open ? "true" : "false");
  };
  head.addEventListener("click", toggle);
  head.addEventListener("keydown", (event) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      toggle();
    }
  });
  entry.append(head);
  if (verbMatch && actionTone(verbMatch[1]) === "quiet") {
    entry.dataset.quiet = "true";
  }
  // "PASS (no chain)": a thin, dark line (still expandable). The turn's draw ("DREW <card>") too: small and informational.
  if (verbMatch && (verbMatch[1] === "PASS" || verbMatch[1] === "DREW")) {
    entry.dataset.pass = "true";
  }
  return { entry, turn: Number(turn) };
}

function addReason(source, text, validator) {
  if (!currentEntry) {
    return null;
  }
  if (source === "strategic state") {
    // Any state name (the leading word of the note) gets the tag, so states added later show up without a page change.
    const state = text.match(/^([A-Z][A-Za-z]*)\b/);
    if (state) {
      const head = currentEntry.querySelector(".entry-head");
      let icon = head.querySelector(".entry-strategic-state");
      if (!icon) {
        icon = element("span", "entry-strategic-state");
        icon.setAttribute("role", "img");
        head.append(icon);
      }
      icon.dataset.state = state[1].toLowerCase();
      icon.textContent = state[1];
      icon.title = `Strategic state: ${text}`;
      icon.setAttribute("aria-label", icon.title);
      return icon;
    }
  }
  let list = currentEntry.querySelector(".entry-reasons");
  if (!list) {
    list = document.createElement("ul");
    list.className = "entry-reasons";
    currentEntry.append(list);
    currentEntry.dataset.quiet = "false";
  }
  const item = document.createElement("li");
  if (validator) {
    item.dataset.kind = "validator";
    renderWithCardCodes(item, `The server's validator changed it to: ${text}`);
  } else {
    const tag = document.createElement("span");
    tag.className = "reason-source";
    tag.textContent = source;
    if (source === "mcts" || source === "search") {
      tag.dataset.source = "mcts";
      if (/OVERRIDES|REPLACED/.test(text)) {
        item.dataset.kind = "override";
        if (currentEntry.dataset.flag !== "unplanned") currentEntry.dataset.flag = "override";
      }
      if (/UNPLANNED|DESYNC|REPLAY MISS/.test(text)) {
        item.dataset.kind = "unplanned";
        currentEntry.dataset.flag = "unplanned"; // shown on the summary line, so it isn't missed while collapsed
      }
      else if (/still thinking \(|thought [0-9.]+ s in all/.test(text)) item.dataset.kind = "thinking";
    }
    const body = document.createElement("span");
    // A reason written as "headline || label: value || label: value" is laid out as a small table under its headline
    // (user, 2026-11-17: "this is unreadable, please lay it out as a table").
    const rows = text.split(" || ");
    renderWithCardCodes(body, rows[0]);
    if (rows.length > 1) {
      const table = document.createElement("table");
      table.className = "reason-table";
      for (const row of rows.slice(1)) {
        const tr = document.createElement("tr");
        const split = row.indexOf(": ");
        const th = document.createElement("th");
        const td = document.createElement("td");
        th.textContent = split > 0 ? row.slice(0, split) : "";
        renderWithCardCodes(td, split > 0 ? row.slice(split + 2) : row);
        tr.append(th, td);
        table.append(tr);
      }
      body.append(table);
    }
    item.append(tag, body);
    // The search's notes go in their own fold, closed by default (user, 2026-11-20: "hide all the search comments under another
    // expandable section (default unexpanded)"). An override or an unplanned chain still flags the summary line.
    if (source === "mcts" || source === "search") {
      searchNotes(list).append(item);
      return item;
    }
  }
  list.append(item);
  return item;
}

// The current entry's folded "Search" section (inside its reasons, after the brain's own), made on first use.
function searchNotes(list) {
  let fold = list.querySelector(":scope > li.search-fold");
  if (!fold) {
    fold = document.createElement("li");
    fold.className = "search-fold";
    const details = document.createElement("details");
    const summary = document.createElement("summary");
    summary.textContent = "Search notes";
    const count = document.createElement("span");
    count.className = "search-fold-count";
    summary.append(count);
    const inner = document.createElement("ul");
    inner.className = "entry-reasons search-fold-list";
    details.append(summary, inner);
    fold.append(details);
  }
  // Always last in the reasons: the brain's own reasons stay on top.
  list.append(fold);
  const inner = fold.querySelector(".search-fold-list");
  fold.querySelector(".search-fold-count").textContent = ` (${inner.childElementCount + 1})`;
  return inner;
}

// Score breakdowns (user, 2026-11-20: "a complete breakdown of what went into the main decision ... nice and visual in a table";
// "the full 'best' line it's following, with a confidence interval on each score"; "show the second best one to the right of the
// best one, and give me a dropdown to replace viewing the second best one with any of the other options"). One "breakdown:" line
// per move on offer, the played move first, then the others by score. Each card: positive parts, negative parts and the net (card
// units, averaged over the move's lines, against the position at the decision), then the move's best line with a 95% interval on
// each step's score, drawn as a range on one shared scale so the widening deeper down shows.
function signed(value) {
  const rounded = Math.round(value * 100) / 100;
  return `${rounded > 0 ? "+" : rounded < 0 ? "−" : ""}${Math.abs(rounded).toFixed(2)}`;
}

const SUB_PART = " :: ";

function parseBreakdown(payload) {
  const fields = payload.split("|");
  if (fields.length < 6) {
    return null;
  }
  const num = (text) => (Number.isFinite(Number(text)) ? Number(text) : 0);
  const parts = fields[5].split(";").filter(Boolean).map((row) => {
    const at = row.lastIndexOf("=");
    return { name: row.slice(0, at), value: Number(row.slice(at + 1)) };
  }).filter((part) => part.name && Number.isFinite(part.value));
  // "part :: category" names split a score into its rule contributions: sub-rows, not counted in the totals.
  const subRows = {};
  const cardChanges = {};
  for (const part of parts.filter((p) => p.name.includes(SUB_PART))) {
    const at = part.name.indexOf(SUB_PART);
    const parent = part.name.slice(0, at);
    const name = part.name.slice(at + SUB_PART.length);
    const cardPrefix = "Cards changed" + SUB_PART;
    if (name.startsWith(cardPrefix)) {
      (cardChanges[parent] = cardChanges[parent] || []).push({ name: name.slice(cardPrefix.length), value: part.value });
    } else {
      (subRows[parent] = subRows[parent] || []).push({ name, value: part.value });
    }
  }
  const line = (fields[8] || "").split("^").filter(Boolean).map((step) => {
    const f = step.split("~");
    return f.length >= 6
      ? { label: f[0], ours: f[1] === "us", theirTurn: f[2] === "next", score: num(f[3]), interval: num(f[4]), lines: num(f[5]),
          rollout: f[6] === "rollout" }
      : null;
  }).filter(Boolean);
  return {
    move: fields[0], score: num(fields[1]), lines: num(fields[2]), lossRate: num(fields[3]), lethal: num(fields[4]),
    parts: parts.filter((p) => !p.name.includes(SUB_PART)), subRows, cardChanges,
    played: fields.length < 7 || fields[6] === "played", interval: num(fields[7]), line
  };
}

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function renderBreakdownCard(b) {
  const card = element("div", "breakdown-card");
  card.dataset.role = b.played ? "played" : "alternative";
  const title = element("div", "breakdown-title");
  title.append(element("span", "breakdown-role", b.played ? "Played" : "Alternative"));
  const name = element("span", "breakdown-move");
  renderWithCardCodes(name, b.move);
  title.append(name);
  card.append(title);
  card.append(element("div", "breakdown-meta",
    `score ${signed(b.score)} ±${b.interval.toFixed(2)} (95%) · ${b.lines} lines · card units vs. the position now`));

  const positive = b.parts.filter((p) => p.value > 0).sort((x, y) => y.value - x.value);
  const negative = b.parts.filter((p) => p.value < 0).sort((x, y) => x.value - y.value);
  const sum = (list) => list.reduce((total, p) => total + p.value, 0);
  const neutral = b.parts.filter((p) => p.value === 0);
  for (const name of Object.keys(b.subRows)) {
    if (!b.parts.some((part) => part.name === name)) neutral.push({ name, value: 0 });
  }
  const largest = Math.max(0.01, ...b.parts.map((p) => Math.abs(p.value)));

  const sectionHead = (label, tone, total, extra) => {
    const head = element("div", `breakdown-section${extra ? ` ${extra}` : ""}`);
    head.dataset.tone = tone;
    head.append(element("span", "", label), element("span", "breakdown-total", signed(total)));
    return head;
  };
  const section = (label, tone, list) => {
    card.append(sectionHead(label, tone, sum(list)));
    const table = element("table", "breakdown-table");
    const header = element("tr");
    for (const text of ["Rule", "", "Contribution"]) header.append(element("th", "", text));
    table.append(header);
    if (!list.length) {
      const tr = element("tr");
      const td = element("td", "breakdown-none", "none");
      td.colSpan = 3;
      tr.append(td);
      table.append(tr);
    }
    for (const part of list) {
      const tr = element("tr");
      const rule = element("td");
      renderWithCardCodes(rule, part.name);
      const barCell = element("td", "breakdown-bar-cell");
      const bar = element("span", "breakdown-bar");
      bar.dataset.tone = tone;
      bar.style.width = `${part.value === 0 ? 0 : Math.max(2, (Math.abs(part.value) / largest) * 100)}%`;
      barCell.append(bar);
      const value = element("td", "breakdown-value", signed(part.value));
      value.dataset.tone = tone;
      tr.append(rule, barCell, value);
      table.append(tr);
      // Rule contributions appear as folded rows under a score,
      // a click on the part opens them. Each shows the rule's contribution relative to the decision position.
      const subs = (b.subRows[part.name] || []).filter((s) => Math.abs(s.value) >= 0.005)
        .sort((x, y) => Math.abs(y.value) - Math.abs(x.value));
      const changes = b.cardChanges[part.name] || [];
      if (subs.length || changes.length) {
        tr.classList.add("breakdown-has-subs");
        rule.prepend(element("span", "breakdown-sub-caret", "›"));
        const subRows = subs.map((sub) => {
          const subTr = element("tr", "breakdown-sub");
          subTr.hidden = true;
          const subName = element("td", "breakdown-sub-name");
          renderWithCardCodes(subName, sub.name);
          const subBarCell = element("td", "breakdown-bar-cell");
          const subBar = element("span", "breakdown-bar breakdown-sub-bar");
          subBar.dataset.tone = sub.value >= 0 ? "positive" : "negative";
          subBar.style.width = `${Math.max(2, Math.min(100, (Math.abs(sub.value) / largest) * 100))}%`;
          subBarCell.append(subBar);
          const subValue = element("td", "breakdown-value", signed(sub.value));
          subValue.dataset.tone = sub.value >= 0 ? "positive" : "negative";
          subTr.append(subName, subBarCell, subValue);
          table.append(subTr);
          return subTr;
        });
        if (changes.length) {
          const changesRow = element("tr", "breakdown-sub");
          changesRow.hidden = true;
          const cell = element("td", "breakdown-sub-name");
          cell.colSpan = 3;
          const details = element("details");
          details.append(element("summary", "", "Cards changed"));
          details.append(element("div", "", "Average copies per scored line"));
          const list = element("ul");
          for (const change of changes.sort((a, b) => a.name.localeCompare(b.name))) {
            const item = element("li");
            renderWithCardCodes(item, change.name);
            item.append(document.createTextNode(change.value < 0.001 ? " (<0.001)" : ` (${Number(change.value.toFixed(3))})`));
            list.append(item);
          }
          details.append(list);
          cell.append(details);
          changesRow.append(cell);
          table.append(changesRow);
          subRows.push(changesRow);
        }
        tr.addEventListener("click", () => {
          const open = tr.dataset.open !== "true";
          tr.dataset.open = String(open);
          for (const row of subRows) row.hidden = !open;
        });
      }
    }
    card.append(table);
  };
  if (b.parts.length || neutral.length) {
    section("Positive contributions", "positive", positive);
    section("Negative contributions", "negative", negative);
    if (neutral.length) section("No net contribution", "neutral", neutral);
    const net = sum(b.parts);
    card.append(sectionHead("Net contribution", net >= 0 ? "positive" : "negative", net, "breakdown-net"));
  } else {
    card.append(element("div", "breakdown-foot", "No scored lines through this move in this search."));
  }
  if (b.lossRate > 0 || b.lethal > 0) {
    const bits = [];
    if (b.lossRate > 0) bits.push(`${Math.round(b.lossRate * 100)}% of its lines lost outright (scored 0, not in the table)`);
    if (b.lethal > 0) bits.push(`lethal risk from the board left: ${Math.round(b.lethal * 100)}% on average (scales a line's score down)`);
    card.append(element("div", "breakdown-foot", bits.join(" · ")));
  }

  if (b.line.length) {
    card.append(element("div", "breakdown-section breakdown-line-head", "Best line"));
    // The scale covers the scored (tree) steps; rollout steps past the tree's end have no score.
    const scored = b.line.filter((s) => !s.rollout);
    const low = Math.min(0, ...scored.map((s) => s.score - s.interval));
    const high = Math.max(0, ...scored.map((s) => s.score + s.interval));
    const span = Math.max(0.01, high - low);
    const at = (v) => `${((v - low) / span) * 100}%`;
    const table = element("table", "breakdown-line");
    const header = element("tr");
    for (const text of ["#", "Step", "", "Score", "±95%", "Lines"]) header.append(element("th", "", text));
    table.append(header);
    let theirTurnShown = false;
    b.line.forEach((step, index) => {
      if (step.theirTurn && !theirTurnShown) {
        theirTurnShown = true;
        const divider = element("tr", "breakdown-line-turn");
        const cell = element("td", "", "their turn");
        cell.colSpan = 6;
        divider.append(cell);
        table.append(divider);
      }
      const tr = element("tr");
      tr.dataset.side = step.ours ? "us" : "them";
      const label = element("td", "breakdown-step");
      label.append(element("span", "breakdown-who", step.ours ? "us" : "them"));
      renderWithCardCodes(label, ` ${step.label}`);
      if (step.rollout) {
        // Past the search tree: the brain's own play in the line that made the last tree step, unscored.
        tr.dataset.rollout = "true";
        tr.title = "Past the search tree's end: the brain's own play in its line (no score of its own)";
        const note = element("td", "breakdown-rollout-note", "rollout");
        note.colSpan = 4;
        tr.append(element("td", "breakdown-index", String(index + 1)), label, note);
        table.append(tr);
        return;
      }
      const range = element("td", "breakdown-range-cell");
      const track = element("span", "breakdown-range");
      const band = element("span", "breakdown-range-band");
      band.style.left = at(step.score - step.interval);
      band.style.width = `${Math.max(1, ((2 * step.interval) / span) * 100)}%`;
      const dot = element("span", "breakdown-range-dot");
      dot.style.left = at(step.score);
      track.append(band, dot);
      range.append(track);
      tr.append(element("td", "breakdown-index", String(index + 1)), label, range,
        element("td", "breakdown-value", signed(step.score)), element("td", "breakdown-interval", `±${step.interval.toFixed(2)}`),
        element("td", "breakdown-lines", String(step.lines)));
      table.append(tr);
    });
    card.append(table);
  }
  return card;
}

function addBreakdown(payload) {
  if (!currentEntry) {
    return false;
  }
  const b = parseBreakdown(payload);
  if (!b) {
    return false;
  }
  let box = currentEntry.querySelector(":scope > .entry-breakdown");
  if (!box) {
    box = element("section", "entry-breakdown");
    const left = element("div", "breakdown-column breakdown-left");
    // A header row as tall as the dropdown's, so the two cards line up.
    const leftHead = element("div", "breakdown-picker breakdown-picker-static");
    leftHead.append(element("span", "", "Played move"));
    const playedSlot = element("div", "breakdown-slot");
    playedSlot.append(element("div", "breakdown-foot", "No search score for the played move."));
    left.append(leftHead, playedSlot);
    const right = element("div", "breakdown-column breakdown-right");
    const picker = element("label", "breakdown-picker");
    picker.append(element("span", "", "Compare with"));
    const select = element("select");
    picker.append(select);
    const slot = element("div", "breakdown-slot");
    right.append(picker, slot);
    right.hidden = true;
    box.append(left, right);
    box.alternatives = [];
    // The dropdown sits inside the clickable entry: its clicks mustn't fold the entry.
    picker.addEventListener("click", (event) => event.stopPropagation());
    select.addEventListener("change", () => {
      const chosen = box.alternatives[Number(select.value)];
      if (chosen) slot.replaceChildren(renderBreakdownCard(chosen));
    });
    // Under the reasons, above the details.
    currentEntry.insertBefore(box, currentEntry.querySelector(":scope > .entry-more"));
    currentEntry.dataset.quiet = "false";
  }
  if (b.played) {
    box.querySelector(".breakdown-left .breakdown-slot").replaceChildren(renderBreakdownCard(b));
    return true;
  }
  // An alternative: listed in the dropdown by score (they arrive best first); the first one shows to the right.
  const right = box.querySelector(".breakdown-right");
  const select = right.querySelector("select");
  box.alternatives.push(b);
  const option = element("option", "", `${signed(b.score)}  ${b.move}`);
  option.value = String(box.alternatives.length - 1);
  select.append(option);
  if (box.alternatives.length === 1) {
    right.hidden = false;
    select.value = "0";
    right.querySelector(".breakdown-slot").replaceChildren(renderBreakdownCard(b));
  }
  return true;
}

// A "detail <label>: <value>" line of the current decision: into its expandable detail block.
function addDetail(label, text) {
  if (!currentEntry) {
    return false;
  }
  // Hand and field first (above the reasons); the rest (decision time, piles, options) after them.
  const cardsLine = /^(our|their) (hand|field)$/.test(label);
  const blockClass = cardsLine ? "entry-detail entry-cards" : "entry-detail entry-more";
  let block = currentEntry.querySelector(cardsLine ? ".entry-cards" : ".entry-more");
  if (!block) {
    block = document.createElement("dl");
    block.className = blockClass;
    currentEntry.append(block);
  }
  const term = document.createElement("dt");
  term.textContent = label;
  const value = document.createElement("dd");
  renderWithCardCodes(value, text);
  block.append(term, value);
  return true;
}

function buildPlainLine(text) {
  const row = document.createElement("div");
  row.className = "terminal-line";
  if (CHATTER_LINE.test(text) || HOSTING_LINE.test(text)) row.dataset.kind = "chatter";
  else if (/^\[thinking\]/.test(text)) row.dataset.kind = "thinking";
  else if (/error|exception|failed/i.test(text)) row.dataset.kind = "error";
  else row.dataset.kind = "plain";
  renderWithCardCodes(row, text);
  return row;
}

function isNearBottom() {
  return terminal.scrollHeight - terminal.scrollTop - terminal.clientHeight < 48;
}

function appendTerminalLines(lines) {
  if (!lines.length) {
    return;
  }
  const stick = followToggle.checked && isNearBottom();
  const fragment = document.createDocumentFragment();
  for (const line of lines) {
    // Console colour codes (ASP.NET's startup log): strip them.
    const text = line.text.replace(/\u001b\[[0-9;]*m/g, "");
    if (!text.trim()) {
      continue;
    }
    const decision = text.match(DECISION_LINE);
    // Zone choices ("ZONE player 0, location 0x4, sequence 2") say nothing: Omega places cards itself. Skipped with their lines.
    if (decision && /^ZONE /.test(decision[4])) {
      currentEntry = null;
      skippingZone = true;
      continue;
    }
    if (skippingZone && /^\s/.test(text)) {
      continue;
    }
    skippingZone = false;
    if (decision) {
      const { entry, turn } = buildDecision(decision);
      if (turn !== currentTurn) {
        const divider = document.createElement("div");
        divider.className = "turn-divider";
        divider.textContent = `Turn ${turn}`;
        fragment.append(divider);
        currentTurn = turn;
      }
      currentEntry = entry;
      fragment.append(entry);
      continue;
    }
    const offer = text.match(OFFER_LINE);
    if (offer && currentEntry) {
      const note = document.createElement("div");
      note.className = "entry-offer";
      renderWithCardCodes(note, `Monsters on offer: ${offer[1]}`);
      currentEntry.append(note);
      continue;
    }
    const detail = text.match(DETAIL_LINE);
    if (detail && addDetail(detail[1], detail[2])) {
      continue;
    }
    const breakdown = text.match(BREAKDOWN_LINE);
    if (breakdown && addBreakdown(breakdown[1])) {
      continue;
    }
    const reason = text.match(REASON_LINE);
    if (reason && addReason(reason[1], reason[2], false)) {
      continue;
    }
    const validator = text.match(VALIDATOR_LINE);
    if (validator && addReason(null, validator[1], true)) {
      continue;
    }
    const row = buildPlainLine(text);
    if (row.dataset.kind !== "chatter" && row.dataset.kind !== "thinking") {
      currentEntry = null;
    }
    fragment.append(row);
  }
  terminal.append(fragment);
  while (terminal.childElementCount > MAX_TERMINAL_LINES) {
    terminal.firstElementChild.remove();
  }
  if (stick) {
    terminal.scrollTop = terminal.scrollHeight;
  } else if (followToggle.checked) {
    jumpButton.hidden = false;
  }
}

function resetTerminal() {
  terminal.innerHTML = "";
  terminalLast = 0;
  currentEntry = null;
  currentTurn = null;
  jumpButton.hidden = true;
}

// The launcher (desktop shortcut) asks an open page to reload instead of opening a new tab: a new reload token.
let knownReloadToken = null;

async function refreshTerminal() {
  const chunk = await fetchJson(`/api/terminal?after=${terminalLast}`);
  if (knownReloadToken === null) {
    knownReloadToken = chunk.reload || "";
  } else if (chunk.reload && chunk.reload !== knownReloadToken) {
    window.location.reload();
    return;
  }
  if (chunk.last < terminalLast) {
    // The buffer was cleared on the server: start over.
    resetTerminal();
    return;
  }
  appendTerminalLines(chunk.lines);
  terminalLast = chunk.last;
}

function setCollapsed(collapsed) {
  terminalPanel.dataset.collapsed = String(collapsed);
  collapseButton.textContent = collapsed ? "Expand" : "Collapse";
  collapseButton.setAttribute("aria-expanded", String(!collapsed));
  storageSet("aiRoom.terminalCollapsed", collapsed ? "1" : "0");
  if (!collapsed && followToggle.checked) {
    terminal.scrollTop = terminal.scrollHeight;
  }
}

// ---------------------------------------------------------------- card previews (ids in the output)

function renderWithCardCodes(container, message) {
  const codePattern = /\b(\d{6,9})\b/g;
  let cursor = 0;
  let match;
  while ((match = codePattern.exec(message))) {
    if (match.index > cursor) {
      container.append(document.createTextNode(message.slice(cursor, match.index)));
    }
    container.append(createCardCodeElement(match[1]));
    cursor = match.index + match[0].length;
  }
  if (cursor < message.length) {
    container.append(document.createTextNode(message.slice(cursor)));
  }
}

function createCardCodeElement(code) {
  const element = document.createElement("span");
  element.className = "log-code";
  element.textContent = code;
  element.addEventListener("mouseenter", (event) => showCardPreview(code, event));
  element.addEventListener("mousemove", positionCardPreview);
  element.addEventListener("mouseleave", hideCardPreview);
  return element;
}

function showCardPreview(code, event) {
  window.clearTimeout(previewHideTimeout);
  cardPreviewImage.src = `https://ygopro.online/assets/card-images/common/${code}.jpg`;
  cardPreview.dataset.visible = "true";
  cardPreview.setAttribute("aria-hidden", "false");
  positionCardPreview(event);
}

function positionCardPreview(event) {
  const left = Math.min(event.clientX + 18, window.innerWidth - 266);
  const top = Math.min(event.clientY + 18, window.innerHeight - 376);
  cardPreview.style.left = `${Math.max(16, left)}px`;
  cardPreview.style.top = `${Math.max(16, top)}px`;
}

function hideCardPreview() {
  previewHideTimeout = window.setTimeout(() => {
    cardPreview.dataset.visible = "false";
    cardPreview.setAttribute("aria-hidden", "true");
    cardPreviewImage.removeAttribute("src");
  }, 60);
}

// ---------------------------------------------------------------- comments

function renderComments(comments) {
  commentCount.textContent = String(comments.length);
  commentList.innerHTML = "";
  if (!comments.length) {
    const empty = document.createElement("li");
    empty.className = "empty-log";
    empty.textContent = "No comments yet. Write one while a duel runs: it's pinned to the decision the bot is on.";
    commentList.appendChild(empty);
    return;
  }
  // Newest first.
  const sorted = [...comments].sort((a, b) => new Date(b.timestamp) - new Date(a.timestamp));
  for (const comment of sorted) {
    const item = document.createElement("li");
    item.className = "comment-item";

    const meta = document.createElement("div");
    meta.className = "comment-meta";
    const when = document.createElement("time");
    when.dateTime = comment.timestamp;
    const date = new Date(comment.timestamp);
    when.textContent = `${date.toLocaleDateString("en-GB", { day: "numeric", month: "short" })}, ${date.toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit" })}`;
    meta.append(when);
    if (comment.deck) {
      const deck = document.createElement("span");
      deck.className = "comment-chip";
      deck.textContent = comment.deck;
      meta.append(deck);
    }
    if (comment.decision) {
      const decision = document.createElement("span");
      decision.className = "comment-chip comment-chip-mono";
      decision.textContent = `#${String(comment.decision).padStart(3, "0")}`;
      decision.title = [comment.logFile, comment.line ? `line ${comment.line}` : null].filter(Boolean).join(", ");
      meta.append(decision);
    }

    const text = document.createElement("p");
    text.className = "comment-text";
    text.textContent = comment.text;
    item.append(meta, text);
    if (comment.logFile) {
      const file = document.createElement("span");
      file.className = "comment-file";
      file.textContent = comment.line ? `${comment.logFile} · line ${comment.line}` : comment.logFile;
      item.append(file);
    }
    commentList.appendChild(item);
  }
}

async function refreshComments() {
  renderComments(await fetchJson("/api/comments"));
}

async function saveComment() {
  const text = commentText.value.trim();
  if (!text) {
    commentStatus.dataset.state = "error";
    commentStatus.textContent = "Write a comment first.";
    return;
  }
  commentButton.disabled = true;
  try {
    await fetchJson("/api/comments", { method: "POST", body: JSON.stringify({ text }) });
    commentText.value = "";
    commentStatus.dataset.state = "saved";
    commentStatus.textContent = `Saved at ${new Date().toLocaleTimeString("en-GB")}.`;
    await refreshComments();
  } catch (error) {
    commentStatus.dataset.state = "error";
    commentStatus.textContent = error.message;
  } finally {
    commentButton.disabled = false;
  }
}

// ---------------------------------------------------------------- actions

async function loadOptions() {
  const options = await fetchJson("/api/options");
  format = options.formats[0] || null;
  selectedDeckId = options.selectedDeckId;
  renderDecks();
}

async function refreshState() {
  updateState(await fetchJson("/api/state"));
}

async function saveSelection() {
  isSelectionDirty = true;
  try {
    await fetchJson("/api/selection", {
      method: "POST",
      body: JSON.stringify({ format: format?.id || "MasterDuel", deckId: deckSelect.value })
    });
  } finally {
    isSelectionDirty = false;
  }
  await refreshState();
}

async function postAction(url) {
  await fetchJson(url, { method: "POST" });
  await refreshState();
}

function stepCarousel(direction) {
  deckCarousel.scrollBy({ left: Math.max(deckCarousel.clientWidth * 0.72, 240) * direction, behavior: "smooth" });
}

function showError(error) {
  isSelectionDirty = false;
  statusBadge.textContent = "Error";
  statusDetail.textContent = error.message;
}

deckSelect.addEventListener("change", () => {
  selectedDeckId = deckSelect.value;
  renderDeckCarousel();
  saveSelection().catch(showError);
});
startButton.addEventListener("click", () => postAction(`/api/duel/auto?enabled=${!autoEnabled}`).catch(showError));
clearLogsButton.addEventListener("click", () => {
  postAction("/api/logs/clear")
    .then(resetTerminal)
    .catch(showError);
});
collapseButton.addEventListener("click", () => setCollapsed(terminalPanel.dataset.collapsed !== "true"));
followToggle.addEventListener("change", () => {
  if (followToggle.checked) {
    terminal.scrollTop = terminal.scrollHeight;
    jumpButton.hidden = true;
  }
});
chatterToggle.addEventListener("change", () => {
  terminal.dataset.chatter = String(chatterToggle.checked);
  storageSet("aiRoom.showChatter", chatterToggle.checked ? "1" : "0");
});
thinkingToggle.addEventListener("change", () => {
  terminal.dataset.thinking = String(thinkingToggle.checked);
  storageSet("aiRoom.showThinking", thinkingToggle.checked ? "1" : "0");
});
jumpButton.addEventListener("click", () => {
  terminal.scrollTop = terminal.scrollHeight;
  jumpButton.hidden = true;
});
terminal.addEventListener("scroll", () => {
  if (isNearBottom()) jumpButton.hidden = true;
});
commentForm.addEventListener("submit", (event) => {
  event.preventDefault();
  saveComment();
});
commentText.addEventListener("keydown", (event) => {
  if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
    event.preventDefault();
    saveComment();
  }
});
carouselPrevButton.addEventListener("click", () => stepCarousel(-1));
carouselNextButton.addEventListener("click", () => stepCarousel(1));

async function boot() {
  setCollapsed(storageGet("aiRoom.terminalCollapsed") === "1");
  chatterToggle.checked = storageGet("aiRoom.showChatter") === "1";
  terminal.dataset.chatter = String(chatterToggle.checked);
  thinkingToggle.checked = storageGet("aiRoom.showThinking") === "1";
  terminal.dataset.thinking = String(thinkingToggle.checked);
  await loadOptions();
  await refreshState();
  await refreshTerminal().catch(() => {});
  await refreshComments().catch(() => {});
  window.setInterval(() => refreshState().catch(() => {}), 2500);
  window.setInterval(() => refreshTerminal().catch(() => {}), 1000);
}

boot().catch(showError);
