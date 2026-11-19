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
  }
  list.append(item);
  return item;
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

async function refreshTerminal() {
  const chunk = await fetchJson(`/api/terminal?after=${terminalLast}`);
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
