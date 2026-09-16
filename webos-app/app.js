// NimbusFX — app nativa WebOS. Nessun framework: pacchetto locale (index.html/app.css/app.js)
// installato sulla TV via ares-package, che parla con il backend Send2Plex esistente solo tramite
// fetch()/<video src> su HTTP semplice (API JSON in Services/TvApiEndpoints.cs + endpoint /stream
// già esistente e validato in docs/piano-streaming-diretto.md). Nessuna logica di business qui:
// solo navigazione a telecomando (frecce + tasto Indietro) sopra dati che il backend già espone.

const CFG_KEY = 'nimbusfx.tv.config';

function getConfig() {
  try { return JSON.parse(localStorage.getItem(CFG_KEY)) || {}; }
  catch { return {}; }
}

function saveConfig(cfg) {
  localStorage.setItem(CFG_KEY, JSON.stringify(cfg));
}

function apiBase(hostOverride) {
  const host = hostOverride || getConfig().host || '';
  return host ? `http://${host}` : '';
}

function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  }[c]));
}

function formatSize(bytes) {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let size = Number(bytes) || 0, unit = 0;
  while (size >= 1024 && unit < units.length - 1) { size /= 1024; unit++; }
  return `${size.toFixed(size >= 10 || unit === 0 ? 0 : 1)} ${units[unit]}`;
}

// Provider debrid: un solo punto di conversione invece di ternari "realDebrid ? ... : ..." sparsi
// nel file (docs/piano-premiumize-libreria.md, terzo provider aggiunto dopo i primi due) — mirror
// lato JS di Models/DebridProviderNames.cs sul backend.
const PROVIDER_CYCLE = ['allDebrid', 'realDebrid', 'premiumize'];
function providerLabel(p) {
  return p === 'realDebrid' ? 'Real-Debrid' : p === 'premiumize' ? 'Premiumize' : 'AllDebrid';
}
function providerIconFile(p) {
  return p === 'realDebrid' ? 'realdebrid' : p === 'premiumize' ? 'premiumizeme' : 'alldebrid';
}
function nextProvider(p) {
  return PROVIDER_CYCLE[(PROVIDER_CYCLE.indexOf(p) + 1) % PROVIDER_CYCLE.length];
}
function normalizeProvider(p) {
  return PROVIDER_CYCLE.includes(p) ? p : 'allDebrid';
}

// ---------------------------------------------------------------
// Navigazione tra view + gestione del tasto "Indietro" del telecomando
// ---------------------------------------------------------------

const VIEWS = ['setup', 'home', 'category', 'library', 'plex-library', 'downloads', 'search', 'search-detail', 'search-results', 'version-list', 'files', 'player'];
let stack = [];

function showView(name) {
  VIEWS.forEach(v => document.getElementById('view-' + v).classList.toggle('hidden', v !== name));

  // Poll di stato download (barra di progresso) gestiti centralmente qui, non nei singoli punti
  // di ingresso — bug reale corretto: partivano solo alla PRIMA apertura di una vista (es.
  // openCandidate), non al ritorno con "Indietro" da più in profondità nella navigazione (es. dopo
  // aver avviato un download dalla lista versioni e poi tornato alla pagina di dettaglio) — così
  // funzionano indipendentemente da come si arriva o si ritorna su una vista.
  if (name === 'search-detail' && currentDetailTmdbId != null) startDetailDownloadsPolling(currentDetailTmdbId);
  else stopDetailDownloadsPolling();

  if (name === 'downloads') startDownloadsPolling();
  else stopDownloadsPolling();

  focusFirstIn(name);
}

// Tmdbid del titolo attualmente aperto in search-detail (impostato da openCandidate PRIMA di
// pushView, così showView() lo trova già pronto) — letto da showView per sapere quale titolo
// interrogare quando questa vista torna visibile, in avanti o all'indietro.
let currentDetailTmdbId = null;

function pushView(name) {
  stack.push(name);
  showView(name);
}

function popView() {
  stack.pop();
  const prev = stack[stack.length - 1] || 'home';
  showView(prev);
  if (prev === 'home') { loadContinueWatching(); loadWatchlist(); }
  return prev;
}

// Le view popolate in modo asincrono (search-detail/search-results/files) chiamano pushView()
// PRIMA che i dati arrivino: focusFirstIn() scatta quindi su una griglia ancora vuota e trova solo
// "← Indietro" nella topbar (bug reale segnalato, stesso pattern già risolto una volta per il
// player con data-default-focus). Fix: dopo aver popolato il contenitore, marcare il primo tile
// utile e richiamare focusFirstIn — che a quel punto lo trova e lo preferisce.
function focusFirstAfterRender(viewName, container) {
  const first = container && container.querySelector('[tabindex]');
  if (first) first.dataset.defaultFocus = 'true';
  focusFirstIn(viewName);
}

function focusFirstIn(name) {
  requestAnimationFrame(() => {
    const view = document.getElementById('view-' + name);
    // Senza questa preferenza esplicita, il primo Invio premuto entrando in riproduzione poteva
    // finire su un controllo sbagliato invece di mettere in pausa (bug reale segnalato: "premo
    // invio ed esce dalla riproduzione, non si mette in pausa come tutti i player classici").
    // Play/pausa deve essere l'azione di default su Invio, come in qualunque player TV — per
    // questo #player-timeline (che gestisce Invio->togglePlayPause quando non si sta facendo
    // scrub, vedi il blocco dedicato più sotto) porta data-default-focus, non i singoli pulsanti.
    // Filtrato per isFocusable: da quando il player ha DUE elementi con data-default-focus
    // (#player-timeline e, quando visibile, "Riproduci ora" dell'overlay prossimo episodio),
    // querySelector prenderebbe sempre il primo nel DOM anche se nascosto — .focus() su un
    // elemento display:none non sposta il focus da nessuna parte, bug reale trovato aggiungendo
    // l'overlay "prossimo episodio".
    const defaultCandidates = Array.from(view.querySelectorAll('[data-default-focus]')).filter(isFocusable);
    const first = defaultCandidates[0] || Array.from(view.querySelectorAll('[tabindex]')).find(isFocusable);
    if (!first) return;
    if (first.tagName === 'INPUT') setPseudoFocus(first);
    else { setPseudoFocus(null); first.focus(); }
  });
}

function handleBack() {
  // Rail a icone aperta: Indietro la richiude tornando al contenuto invece di uscire dalla view —
  // coerente con Destra, che fa la stessa cosa (vedi moveRailFocus). Cercata dentro la view
  // corrente, non con un querySelector globale: ogni view ha la sua copia (vedi initIconRails),
  // tutte nel DOM insieme anche se nascoste — un querySelector globale potrebbe trovare quella
  // sbagliata.
  const currentView = document.querySelector('.view:not(.hidden)');
  const openRail = currentView && currentView.querySelector('.icon-rail');
  if (openRail && openRail.contains(document.activeElement)) { closeIconRail(true); return; }

  // Se si sta scrivendo in un campo (focus reale, tastiera aperta), Indietro annulla la
  // digitazione e richiude la tastiera invece di uscire dalla view — resta la selezione visibile
  // sul campo, pronta a riaprire la tastiera con un altro Enter.
  if (document.activeElement && document.activeElement.tagName === 'INPUT') {
    const input = document.activeElement;
    input.blur();
    setPseudoFocus(input);
    return;
  }

  const current = stack[stack.length - 1];
  if (current === 'player') {
    stopPlayer();
    popView();
    return;
  }
  if (stack.length > 1) {
    popView();
    return;
  }
  // Siamo alla radice (libreria, o la schermata di setup al primo avvio): lascia che sia il
  // sistema webOS a gestire l'uscita/minimizzazione dell'app.
  if (window.webOSSystem && typeof window.webOSSystem.platformBack === 'function') {
    window.webOSSystem.platformBack();
  }
}

// ---------------------------------------------------------------
// Navigazione a frecce (D-pad) — focus reale sugli elementi via tabindex, spostato via JS in
// base al numero di colonne della view corrente (data-cols sul contenitore).
// ---------------------------------------------------------------

// Costruisce le "righe" di navigazione della view corrente: la topbar (se presente) è sempre
// un'unica riga, il contenitore [data-cols] viene spezzato in righe da N elementi. Necessario per
// poter risalire con Su dalla griglia alla topbar — con un solo indice lineare (colonna, senza
// concetto di riga) risalire oltre la prima riga della griglia non è mai possibile.
// Esclude elementi disabilitati o nascosti (es. le "altre versioni" di un episodio prima che
// l'utente prema "Mostra altre…") — altrimenti il focus può finire su qualcosa di invisibile.
function isFocusable(el) {
  return !el.disabled && el.offsetParent !== null;
}

function getRows(view) {
  const rows = [];
  const used = new Set();

  const topbarItems = Array.from(view.querySelectorAll('.topbar [tabindex]'));
  if (topbarItems.length) { rows.push(topbarItems); topbarItems.forEach(el => used.add(el)); }

  // Rail a icone (Home): esclusa dal modello a righe generico qui sotto — vive fuori da Su/Giù
  // "normali", con la sua navigazione verticale dedicata (vedi moveRailFocus/apri-chiudiIconRail).
  // Senza questa esclusione ogni bottone della rail finirebbe come riga isolata da un elemento nel
  // ramo "tutto il resto" più sotto, inserita ovunque capiti nell'ordine DOM.
  const iconRailItems = Array.from(view.querySelectorAll('.icon-rail [tabindex]'));
  iconRailItems.forEach(el => used.add(el));

  // Riga "libera" fuori da topbar/griglia (il campo di ricerca + il bottone "Cerca").
  const searchRow = view.querySelector('.search-row');
  if (searchRow) {
    const items = Array.from(searchRow.querySelectorAll('[tabindex]'));
    rows.push(items);
    items.forEach(el => used.add(el));
  }

  // Tutto il resto, in vero ordine DOM — SOSTITUISCE il vecchio ordine "prima tutti i .rail, poi
  // il resto isolato", che ignorava la posizione visiva reale quando un elemento isolato sta SOPRA
  // un rail/gruppo nel DOM (es. pulsante "Prossimo episodio"/Watchlist sopra la rail stagioni
  // nella pagina di dettaglio): Su/Giù saltava comunque al rail per primo. Bug reale, trovato
  // aggiungendo il pulsante Watchlist sopra la rail stagioni. Un elemento dentro un .rail, un
  // .controls-row (i pulsanti del player) o un [data-cols] porta con sé l'intero gruppo come riga
  // (o più righe, per la griglia); un elemento "libero" (es. la timeline del player, i campi della
  // schermata di setup) diventa una riga a sé — stesso risultato di prima per player/griglie/rail,
  // ma derivato genericamente dall'ordine del DOM invece che da casi speciali per ogni view.
  Array.from(view.querySelectorAll('[tabindex]')).forEach(el => {
    if (used.has(el) || !isFocusable(el)) return;

    const rail = el.closest('.rail');
    if (rail) {
      const items = Array.from(rail.querySelectorAll('[tabindex]')).filter(isFocusable);
      if (items.length) { rows.push(items); items.forEach(i => used.add(i)); }
      return;
    }

    const controlsRow = el.closest('.controls-row');
    if (controlsRow) {
      const items = Array.from(controlsRow.querySelectorAll('[tabindex]')).filter(isFocusable);
      if (items.length) { rows.push(items); items.forEach(i => used.add(i)); }
      return;
    }

    // Riga di un risultato (view-version-list) + la sua icona provider affiancata (vedi
    // renderVersionRows): raggruppate come UNA riga (Sinistra/Destra passa dall'una all'altra),
    // altrimenti Su/Giù si fermerebbe due volte per ogni risultato invece di una.
    const resultRow = el.closest('.result-row-wrap');
    if (resultRow) {
      const items = Array.from(resultRow.querySelectorAll('[tabindex]')).filter(isFocusable);
      if (items.length) { rows.push(items); items.forEach(i => used.add(i)); }
      return;
    }

    const grid = el.closest('[data-cols]');
    if (grid) {
      const items = Array.from(grid.querySelectorAll('[tabindex]')).filter(isFocusable);
      const cols = parseInt(grid.dataset.cols || '1', 10);
      for (let i = 0; i < items.length; i += cols) rows.push(items.slice(i, i + cols));
      items.forEach(i => used.add(i));
      return;
    }

    rows.push([el]);
    used.add(el);
  });

  return rows;
}

// webOS apre la tastiera su schermo nell'istante in cui un <input> riceve il focus REALE — anche
// se ci si arriva solo "di passaggio" con le frecce, non con un'attivazione esplicita. Per i campi
// di testo la navigazione a frecce quindi non chiama mai .focus() per davvero: mostra solo un
// "finto focus" via classe CSS, e solo Enter promuove quella selezione a focus reale (aprendo la
// tastiera solo quando l'utente lo intende davvero).
let pseudoFocusedInput = null;

function currentActive() {
  return pseudoFocusedInput || document.activeElement;
}

function setPseudoFocus(input) {
  if (pseudoFocusedInput && pseudoFocusedInput !== input) pseudoFocusedInput.classList.remove('fake-focus');
  pseudoFocusedInput = input;
  if (input) input.classList.add('fake-focus');
}

function moveFocus(direction) {
  const view = document.querySelector('.view:not(.hidden)');
  const iconRail = view.querySelector('.icon-rail');
  const active = currentActive();

  // Dentro la rail a icone le frecce hanno un significato diverso dal modello a righe generico
  // sotto (Su/Giù scorrono le voci della rail, non le righe della pagina) — stesso principio già
  // usato per la timeline del player, intercettato PRIMA di calcolare le righe.
  if (iconRail && active && iconRail.contains(active)) {
    moveRailFocus(iconRail, direction);
    return;
  }

  const rows = getRows(view);
  if (rows.length === 0) return;

  let rIdx = rows.findIndex(r => r.includes(active));
  let cIdx = rIdx === -1 ? 0 : rows[rIdx].indexOf(active);
  if (rIdx === -1) { rows[0][0].focus(); return; }

  if (direction === 'left') {
    // Sinistra sul bordo sinistro del contenuto apre la rail invece di restare fermi — stesso
    // punto d'ingresso da qualunque riga (ricerca o un rail di poster), non solo dalla prima.
    if (cIdx === 0 && iconRail) { openIconRail(iconRail); return; }
    cIdx = Math.max(0, cIdx - 1);
  }
  else if (direction === 'right') cIdx = Math.min(rows[rIdx].length - 1, cIdx + 1);
  else if (direction === 'up') rIdx = Math.max(0, rIdx - 1);
  else if (direction === 'down') rIdx = Math.min(rows.length - 1, rIdx + 1);

  const targetRow = rows[rIdx];
  const target = targetRow[Math.min(cIdx, targetRow.length - 1)];

  if (target.tagName === 'INPUT') {
    if (document.activeElement && document.activeElement.tagName === 'INPUT') document.activeElement.blur();
    setPseudoFocus(target);
  } else {
    setPseudoFocus(null);
    target.focus();
  }
  // Esplicito invece di fare affidamento solo sullo scroll-on-focus del browser: più affidabile
  // sul motore datato di webOS 22 (Chromium 87) dentro un rail orizzontale scorrevole.
  target.scrollIntoView({ block: 'nearest', inline: 'nearest' });
}

// ---------------------------------------------------------------
// Rail a icone (Home, restyle 2026-09-13): sostituisce la vecchia barra in alto + riga di
// categorie. A riposo è solo CSS (:focus-within in app.css) — qui c'è solo la parte che DEVE
// essere JS: spostare il focus reale dentro/fuori dalla rail e ricordare da dove si veniva, così
// Destra/Indietro tornano esattamente al poster o al campo da cui si era entrati invece di
// ricadere sul primo elemento della view.
// ---------------------------------------------------------------

let railReturnFocus = null;

function openIconRail(rail) {
  const items = Array.from(rail.querySelectorAll('.rail-item')).filter(isFocusable);
  if (!items.length) return;
  railReturnFocus = currentActive();
  setPseudoFocus(null);
  items[0].focus();
}

function closeIconRail(restoreFocus) {
  const target = restoreFocus && railReturnFocus && isFocusable(railReturnFocus) ? railReturnFocus : null;
  railReturnFocus = null;
  if (target) target.focus();
  else focusFirstIn(stack[stack.length - 1]); // fallback: la view è cambiata sotto i piedi (raro)
}

function moveRailFocus(rail, direction) {
  const items = Array.from(rail.querySelectorAll('.rail-item')).filter(isFocusable);
  const idx = items.indexOf(document.activeElement);
  if (direction === 'up') { if (idx > 0) items[idx - 1].focus(); }
  else if (direction === 'down') { if (idx < items.length - 1) items[idx + 1].focus(); }
  else if (direction === 'right') closeIconRail(true);
  // 'left' qui non fa nulla: si è già al bordo sinistro, non c'è altro dove entrare.
}

// Anteprima al focus sui poster di Home (Continua a guardare/Watchlist): delegata su focusin/
// focusout invece di un listener per tile, così sopravvive ai re-render dei rail (loadWatchlist/
// loadContinueWatchingRail ricreano i bottoni ad ogni chiamata) senza doverli riagganciare ogni
// volta. Il ritardo di 550ms è l'unica vera protezione contro lo scorrimento veloce a frecce: senza,
// ogni poster attraversato durante lo scroll lampeggerebbe. Le pagine Categoria/Genere hanno
// l'overview TMDB già disponibile (renderPosterTiles), ma qui in Home Continua a guardare/
// Watchlist non la portano (né l'endpoint history né watchlist la restituiscono oggi) — l'anteprima
// resta quindi solo visiva (sollevamento+scala), niente sinossi finta.
let posterPeekTimer = null;
document.addEventListener('focusin', (e) => {
  const tile = e.target.closest && e.target.closest('.poster-tile');
  if (!tile) return;
  clearTimeout(posterPeekTimer);
  posterPeekTimer = setTimeout(() => tile.classList.add('peek-active'), 550);
});
document.addEventListener('focusout', (e) => {
  const tile = e.target.closest && e.target.closest('.poster-tile');
  if (!tile) return;
  clearTimeout(posterPeekTimer);
  tile.classList.remove('peek-active');
});

document.addEventListener('keydown', (e) => {
  // Nel player i comandi non devono mai agire su un overlay invisibile: qualunque tasto lo
  // rimostra e rimanda giù il timer, PRIMA di interpretare il tasto stesso (altrimenti la prima
  // pressione dopo che si è nascosto sposterebbe il focus "alla cieca").
  if (stack[stack.length - 1] === 'player') showPlayerOverlay();

  // 461 è il codice documentato del tasto "Indietro" del telecomando LG; alcune build espongono
  // anche key === 'Back'/'GoBack'. disableBackHistoryAPI in appinfo.json lascia questa gestione
  // completamente manuale, invece di far navigare la cronologia del browser di sistema.
  if (e.keyCode === 461 || e.key === 'Back' || e.key === 'GoBack') {
    e.preventDefault();
    // Pillole "Salta sigla"/"Prossimo episodio" (index.html, #player-corner-pills): con l'autofocus
    // richiesto dall'utente, il primo Indietro premuto dopo che una pillola è comparsa deve
    // annullarla/allontanarsi da lì, non uscire dal player intero — stesso comportamento delle app
    // TV Netflix reali (Indietro chiude la card "Prossimo episodio" prima di uscire dal player).
    if (stack[stack.length - 1] === 'player') {
      const active = currentActive();
      if (active === document.getElementById('next-episode-pill')) { hideNextEpisodePill(); return; }
      if (active === document.getElementById('skip-intro-pill')) { document.getElementById('player-timeline').focus(); return; }
    }
    handleBack();
    return;
  }

  // Sulla timeline del player, Sinistra/Destra scorrono (scrub) invece di spostare il focus tra i
  // controlli — l'unico punto della UI dove le frecce hanno un significato diverso dalla
  // navigazione a righe, per questo va intercettato PRIMA dello switch generico sotto.
  if (stack[stack.length - 1] === 'player' && currentActive() === document.getElementById('player-timeline')) {
    if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
      e.preventDefault();
      scrubBy(e.key === 'ArrowLeft' ? -1 : 1);
      return;
    }
    if (e.key === 'Enter') {
      e.preventDefault();
      if (scrubActive) commitScrub(); else togglePlayPause();
      return;
    }
  }

  switch (e.key) {
    case 'ArrowLeft': e.preventDefault(); moveFocus('left'); break;
    case 'ArrowRight': e.preventDefault(); moveFocus('right'); break;
    case 'ArrowUp': e.preventDefault(); moveFocus('up'); break;
    case 'ArrowDown': e.preventDefault(); moveFocus('down'); break;
    case 'Enter':
      // Un campo di testo "finto-selezionato" (vedi setPseudoFocus) diventa focus reale solo ora,
      // su azione esplicita — è il momento giusto per far comparire la tastiera di webOS. Un
      // bottone con focus reale si attiva già da solo nativamente su Enter, non serve altro qui.
      if (pseudoFocusedInput) {
        e.preventDefault();
        const input = pseudoFocusedInput;
        input.classList.remove('fake-focus');
        pseudoFocusedInput = null;
        input.focus();
      }
      break;
  }
});

// ---------------------------------------------------------------
// Setup (IP/porta del PC Send2Plex sulla rete locale)
// ---------------------------------------------------------------

async function testConnection(host) {
  const statusEl = document.getElementById('setup-status');
  statusEl.textContent = 'Verifico…';
  statusEl.classList.remove('error');
  try {
    const res = await fetch(`${apiBase(host)}/api/tv/ping`);
    if (!res.ok) throw new Error('HTTP ' + res.status);
    statusEl.textContent = '✅ Connesso';
    return true;
  } catch (err) {
    statusEl.textContent = `❌ Impossibile raggiungere ${host} (${err.message})`;
    statusEl.classList.add('error');
    return false;
  }
}

// Individua l'IP locale della TV senza alcuna API "networking" dedicata (le webapp webOS non ne
// hanno): sfrutta il fatto che la raccolta di candidati ICE di WebRTC rivela l'indirizzo LAN del
// dispositivo anche senza una connessione P2P vera (tecnica nota, funziona già su Chromium da
// prima della versione 87 di webOS 22). Da lì si deduce la subnet /24 e si prova /api/tv/ping su
// ogni host — non è mDNS/SSDP vero, ma non richiede nessun permesso nativo aggiuntivo.
function getLocalIp() {
  return new Promise((resolve) => {
    try {
      const pc = new RTCPeerConnection({ iceServers: [] });
      pc.createDataChannel('');
      let done = false;
      const finish = (ip) => { if (!done) { done = true; try { pc.close(); } catch {} resolve(ip); } };
      pc.onicecandidate = (e) => {
        if (!e.candidate) { finish(null); return; }
        const m = /(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})/.exec(e.candidate.candidate);
        if (m && !m[1].startsWith('0.') && !m[1].startsWith('127.')) finish(m[1]);
      };
      pc.createOffer().then(offer => pc.setLocalDescription(offer)).catch(() => finish(null));
      setTimeout(() => finish(null), 2500);
    } catch {
      resolve(null);
    }
  });
}

function pingHost(host, timeoutMs) {
  return new Promise((resolve) => {
    const controller = new AbortController();
    const timer = setTimeout(() => { controller.abort(); resolve(null); }, timeoutMs);
    fetch(`http://${host}/api/tv/ping`, { signal: controller.signal })
      .then(r => r.ok ? r.json() : null)
      .then(data => { clearTimeout(timer); resolve(data && data.ok ? host : null); })
      .catch(() => { clearTimeout(timer); resolve(null); });
  });
}

const SCAN_PORT = 5075; // porta fissa di Send2Plex (Program.cs: UseUrls "http://0.0.0.0:5075")

async function scanLocalNetwork(onProgress) {
  const localIp = await getLocalIp();
  if (!localIp) return null;
  const parts = localIp.split('.');
  if (parts.length !== 4) return null;
  const base = parts.slice(0, 3).join('.');

  // Prova prima l'host della TV stessa e i primi indirizzi tipici di un PC in DHCP, poi il resto
  // della subnet in ordine — pura euristica per trovare il PC prima nel caso comune.
  const order = Array.from({ length: 254 }, (_, i) => i + 1)
    .filter(n => n !== Number(parts[3]));

  const BATCH = 24;
  for (let i = 0; i < order.length; i += BATCH) {
    const batch = order.slice(i, i + BATCH);
    onProgress?.(Math.min(i + BATCH, order.length), order.length);
    const results = await Promise.all(batch.map(n => pingHost(`${base}.${n}:${SCAN_PORT}`, 700)));
    const found = results.find(Boolean);
    if (found) return found;
  }
  return null;
}

// Stato dei 5 "select" della schermata Impostazioni (BUG REALE, vedi index.html: un <select>
// nativo poteva bloccare l'app col telecomando) — ora righe di chip invece di <select>, quindi il
// valore corrente vive in queste variabili invece che in un .value nativo, e va ridisegnato
// esplicitamente (renderSetupChips) ogni volta che cambia da codice (mai dal click su un chip,
// quello si aggiorna già da sé tramite buildFilterChips).
let setupQuality = 'alta';
let setupPlaybackMode = 'auto';
let setupPreferredQuality = '';
let setupPreferredLanguage = '';
let setupPreferredProvider = 'allDebrid';

// 3 nuove preferenze (piano-webos-skip-marker-plex.md, punto 8) — tabella dichiarativa invece di
// ripetere a mano lo stesso pattern chip in 3 punti diversi (renderSetupChips/openSettingsFromRail/
// setup-save) per ciascun campo, solo per questi 3 nuovi (i 5 sopra restano come sono, nessun
// rischio di regressione su ciò che già funziona). toChip/fromChip convertono tra il valore "vero"
// (bool/number, quello letto da getConfig() a runtime e mandato al server) e il valore stringa dei
// chip (buildFilterChips confronta sempre stringhe).
const NEW_TV_PREFS = [
  {
    key: 'autoNextEpisodeEnabled', chipId: 'setup-auto-next-episode', default: true,
    options: [{ value: 'on', label: 'Attivo' }, { value: 'off', label: 'Disattivo' }],
    toChip: v => (v === false ? 'off' : 'on'),
    fromChip: v => v !== 'off'
  },
  {
    key: 'nextEpisodeCountdownSeconds', chipId: 'setup-next-episode-countdown', default: 10,
    options: [5, 10, 15, 20].map(v => ({ value: String(v), label: `${v}s` })),
    toChip: v => String(v || 10),
    fromChip: v => parseInt(v, 10)
  },
  {
    key: 'plexMarkersEnabled', chipId: 'setup-plex-markers', default: true,
    options: [{ value: 'on', label: 'Attivo' }, { value: 'off', label: 'Disattivo' }],
    toChip: v => (v === false ? 'off' : 'on'),
    fromChip: v => v !== 'off'
  }
];
// { [key]: valore-stringa del chip attivo }. Inizializzato subito con i default (non un oggetto
// vuoto): al primissimo avvio dell'app (nessun host mai configurato) init() disegna questa
// schermata chiamando renderSetupChips() direttamente, MAI passando da openSettingsFromRail() —
// senza questo, i 3 chip nuovi apparirebbero senza alcuna opzione evidenziata (bug reale trovato
// verificando in browser: "Attivo"/"Disattivo" restavano entrambi spenti al primo avvio).
let setupNewPrefs = {};
NEW_TV_PREFS.forEach(f => { setupNewPrefs[f.key] = f.toChip(f.default); });

// Tab di Impostazioni (restyle 2026-09-14, richiesta utente) — stesso identico meccanismo chip
// di sopra: "quale tab è attivo" è un valore scelto tra opzioni, esattamente il caso d'uso di
// buildFilterChips, qui applicato alla barra tab stessa invece che a un filtro. Ogni pannello ha
// già il proprio "data-tab" nell'HTML (index.html), showSetupTab si limita a mostrare quello e
// nascondere gli altri — i campi dentro un pannello nascosto escono da soli dal modello a righe di
// getRows() (isFocusable controlla offsetParent, null quando il pannello ha .hidden), nessuna
// modifica lì serve.
const SETUP_TABS = [
  { value: 'connection', label: 'Connessione' },
  { value: 'streaming', label: 'Streaming' },
  { value: 'search', label: 'Ricerca' },
  { value: 'next-episode', label: 'Prossimo episodio' }
];
let setupActiveTab = 'connection';

// focusTab: true SOLO quando lo switch parte da un click reale sulla barra (vedi
// renderSetupTabChips sotto) — buildFilterChips ricrea da zero i bottoni della barra a ogni
// render, quindi il chip appena cliccato perde il focus DOM (stesso comportamento già presente
// per le altre righe di filtro in questa schermata, qui però più percepibile perché cambia anche
// tutto il pannello sotto): lo si porta di nuovo sul nuovo chip attivo così Su/Giù/Sinistra/Destra
// continuano a funzionare da lì. MAI true quando la chiamata arriva da renderSetupChips (sync in
// background dopo uno scan, apertura iniziale della schermata): rubrerebbe il focus da sotto
// l'utente, es. mentre sta ancora scrivendo nel campo host.
function showSetupTab(tab, { focusTab = false } = {}) {
  setupActiveTab = tab;
  document.querySelectorAll('.setup-tab-panel').forEach(panel => {
    panel.classList.toggle('hidden', panel.dataset.tab !== tab);
  });
  renderSetupTabChips();
  if (focusTab) document.querySelector('#setup-tabs .tile-primary')?.focus();
}

function renderSetupTabChips() {
  buildFilterChips('setup-tabs', SETUP_TABS, () => setupActiveTab, v => showSetupTab(v, { focusTab: true }));
}

function renderSetupChips() {
  renderSetupTabChips();
  buildFilterChips('setup-quality', [
    { value: 'alta', label: 'Alta' },
    { value: 'media', label: 'Media (cap 1080p)' },
    { value: 'bassa', label: 'Bassa (cap 720p)' }
  ], () => setupQuality, v => { setupQuality = v; });

  // Etichette accorciate (bug reale segnalato dall'utente 2026-09-14, con screenshot: le vecchie
  // etichette con la spiegazione tra parentesi rendevano questa l'unica riga della pagina più larga
  // dei 900px di .setup-box — 1372px di contenuto contro 964px visibili — quindi l'ultimo chip
  // finiva tagliato/fuori schermo di default, senza alcun indizio che ce ne fosse un terzo). La
  // spiegazione si è spostata nell'hint sotto, stesso pattern già usato per le righe più in basso in
  // questa pagina (Qualità/Lingua torrent, Provider).
  buildFilterChips('setup-playback-mode', [
    { value: 'auto', label: 'Automatica' },
    { value: 'direct', label: 'Sempre diretta' },
    { value: 'transcode', label: 'Sempre trascodifica' }
  ], () => setupPlaybackMode, v => { setupPlaybackMode = v; });

  buildFilterChips('setup-preferred-quality', [
    { value: '', label: 'Nessuna preferenza' },
    { value: '4K', label: '4K' },
    { value: '1080p', label: '1080p' },
    { value: '720p', label: '720p' },
    { value: 'SD', label: 'SD' }
  ], () => setupPreferredQuality, v => { setupPreferredQuality = v; });

  buildFilterChips('setup-preferred-language', [
    { value: '', label: 'Nessuna preferenza' },
    { value: 'ITA', label: 'ITA' },
    { value: 'MULTI', label: 'MULTI' },
    { value: 'SUB ITA', label: 'SUB ITA' },
    { value: 'ENG', label: 'ENG' }
  ], () => setupPreferredLanguage, v => { setupPreferredLanguage = v; });

  buildFilterChips('setup-preferred-provider', [
    { value: 'allDebrid', label: 'AllDebrid', icon: 'assets/provider/alldebrid.png' },
    { value: 'realDebrid', label: 'Real-Debrid', icon: 'assets/provider/realdebrid.png' },
    { value: 'premiumize', label: 'Premiumize', icon: 'assets/provider/premiumizeme.png' }
  ], () => setupPreferredProvider, v => { setupPreferredProvider = v; });

  NEW_TV_PREFS.forEach(f => {
    buildFilterChips(f.chipId, f.options, () => setupNewPrefs[f.key], v => { setupNewPrefs[f.key] = v; });
  });
}

// Preferenze (qualità streaming, qualità/lingua torrent) recuperabili dal server (richiesta
// utente): un reinstall dell'app WebOS a volte svuota il localStorage della TV, ma queste non sono
// segrete/per-dispositivo come l'host — ha senso condividerle. Sincronizzate qui appena l'host è
// raggiungibile (scan riuscito, o riapertura di Impostazioni con un host già noto), non appena
// l'utente digita a mano un indirizzo mai provato prima (lì tocca comunque premere "Salva e
// connetti", che le sincronizza anch'esso prima di leggere i valori dai select).
async function syncPreferencesFromServer(host) {
  try {
    const res = await fetch(`${apiBase(host)}/api/tv/preferences`);
    if (!res.ok) return;
    const prefs = await res.json();
    if (prefs.quality) setupQuality = prefs.quality;
    if (prefs.playbackMode) setupPlaybackMode = prefs.playbackMode;
    if (prefs.preferredQuality !== undefined && prefs.preferredQuality !== null) setupPreferredQuality = prefs.preferredQuality;
    if (prefs.preferredLanguage !== undefined && prefs.preferredLanguage !== null) setupPreferredLanguage = prefs.preferredLanguage;
    if (prefs.preferredProvider) setupPreferredProvider = prefs.preferredProvider;
    NEW_TV_PREFS.forEach(f => {
      const raw = prefs[f.key];
      if (raw !== undefined && raw !== null) setupNewPrefs[f.key] = f.toChip(raw);
    });
    renderSetupChips();
  } catch { /* best-effort: se il fetch fallisce restano i valori già nei chip */ }
}

function savePreferencesToServer(host, prefs) {
  fetch(`${apiBase(host)}/api/tv/preferences`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(prefs)
  }).catch(() => {}); // best-effort: non deve bloccare il salvataggio locale se il server non risponde
}

document.getElementById('setup-scan').addEventListener('click', async () => {
  const statusEl = document.getElementById('setup-status');
  const btn = document.getElementById('setup-scan');
  btn.disabled = true;
  statusEl.classList.remove('error');
  statusEl.textContent = '🔎 Cerco Send2Plex sulla rete locale…';
  try {
    const found = await scanLocalNetwork((done, total) => {
      statusEl.textContent = `🔎 Cerco sulla rete locale… (${done}/${total})`;
    });
    if (found) {
      document.getElementById('setup-host').value = found;
      statusEl.textContent = `✅ Trovato: ${found}`;
      await syncPreferencesFromServer(found);
    } else {
      statusEl.textContent = '❌ Nessun server trovato automaticamente — inserisci l\'indirizzo a mano.';
      statusEl.classList.add('error');
    }
  } finally {
    btn.disabled = false;
  }
});

// BUG REALE (segnalato dall'utente 2026-09-13: "non vengono salvate le preferenze provider/
// lingua/risoluzione"): qui c'era un `await syncPreferencesFromServer(host)` PRIMA di leggere i
// valori dai select — quindi ogni salvataggio si sovrascriveva da solo con le preferenze VECCHIE
// già sul server, prima ancora di leggere/salvare quelle appena scelte dall'utente nella
// schermata. Il caso che quella sincronizzazione voleva coprire (host mai usato prima su questo
// dispositivo, es. dopo un reinstall) è già coperto da syncPreferencesFromServer chiamato
// nell'handler "Cerca sulla rete locale" sopra e all'apertura di Impostazioni (home-settings
// sotto) — qui basta salvare quello che l'utente ha davanti, mai andarlo a sovrascrivere prima.
document.getElementById('setup-save').addEventListener('click', async () => {
  const host = document.getElementById('setup-host').value.trim();
  if (!host) return;
  const ok = await testConnection(host);
  if (!ok) return;
  const quality = setupQuality;
  const playbackMode = setupPlaybackMode;
  const preferredQuality = setupPreferredQuality;
  const preferredLanguage = setupPreferredLanguage;
  const preferredProvider = setupPreferredProvider;
  const newPrefsPatch = {};
  NEW_TV_PREFS.forEach(f => { newPrefsPatch[f.key] = f.fromChip(setupNewPrefs[f.key]); });
  saveConfig({ host, quality, playbackMode, preferredQuality, preferredLanguage, preferredProvider, ...newPrefsPatch });
  savePreferencesToServer(host, { quality, playbackMode, preferredQuality, preferredLanguage, preferredProvider, ...newPrefsPatch });
  stack = ['home'];
  showView('home');
  loadContinueWatching();
  loadWatchlist();
});

// Estratta dal vecchio handler del bottone home-settings (era l'unico punto d'ingresso a
// Impostazioni): ora la rail a icone la richiama da qualunque view tramite railNavigate.
async function openSettingsFromRail() {
  const cfg = getConfig();
  document.getElementById('setup-host').value = cfg.host || '';
  // Prima i valori locali come base, POI la sincronizzazione dal server (che li sovrascrive se
  // presenti) — nell'ordine opposto il fetch verrebbe subito vanificato dalle righe sotto.
  setupQuality = cfg.quality || 'alta';
  setupPlaybackMode = cfg.playbackMode || 'auto';
  setupPreferredQuality = cfg.preferredQuality || '';
  setupPreferredLanguage = cfg.preferredLanguage || '';
  setupPreferredProvider = cfg.preferredProvider || 'allDebrid';
  NEW_TV_PREFS.forEach(f => { setupNewPrefs[f.key] = f.toChip(cfg[f.key] !== undefined ? cfg[f.key] : f.default); });
  renderSetupChips();
  document.getElementById('setup-status').textContent = '';
  pushView('setup');
  if (cfg.host) await syncPreferencesFromServer(cfg.host);
}

// ---------------------------------------------------------------
// Home (ricerca + scaffali di tendenza, stessa fonte di Home.razor)
// ---------------------------------------------------------------

document.getElementById('home-search-form').addEventListener('submit', (e) => {
  e.preventDefault();
  const q = document.getElementById('home-search-input').value.trim();
  if (q) openSearchFor(q);
});

// Punto d'ingresso comune alla view di ricerca, sia da un titolo digitato (Home) sia dal click
// su un poster di tendenza (che riusa il titolo come query testuale — TmdbTrendingItem non porta
// un tmdbId, stessa scelta già fatta da Home.razor con "Cerca questo titolo").
function openSearchFor(query) {
  pushView('search');
  document.getElementById('search-input').value = query;
  runSearch(query);
}

// Rail "scoperta" per genere/servizio di streaming (Fase 4): film e serie mescolati nella STESSA
// riga (due chiamate a /discover in parallelo, una per mediaType, poi concatenate) — a differenza
// dei rail di tendenza porta il tmdbId, quindi il click apre subito la pagina di dettaglio (stesso
// comportamento dei tile Watchlist) invece di una ricerca testuale per nome.
// Fabbrica le tile poster per un rail "con tmdbId" (discover/tmdb-list, mai i vecchi trending che
// non lo portano) — condivisa da loadMixedDiscoverRail e loadListRail, stesso identico markup e
// click-handler (apre subito il dettaglio, niente ricerca testuale).
function renderPosterTiles(el, items) {
  el.innerHTML = '';
  if (items.length === 0) {
    el.innerHTML = '<p class="hint">Nessun dato disponibile.</p>';
    return;
  }
  items.forEach(i => {
    const btn = document.createElement('button');
    btn.className = 'tile poster-tile';
    btn.tabIndex = 0;
    btn.innerHTML = `${i.posterUrl ? `<img src="${i.posterUrl}" alt="" />` : ''}
      <div class="poster-caption">${escapeHtml(i.title)}${i.year ? `<span class="poster-year">${escapeHtml(i.year)}</span>` : ''}</div>`;
    btn.addEventListener('click', () => openCandidate({ tmdbId: i.tmdbId, mediaType: i.mediaType, title: i.title, year: i.year, posterUrl: i.posterUrl, overview: i.overview, isAnime: false }));
    el.appendChild(btn);
  });
}

async function loadMixedDiscoverRail(containerId, movieParams, tvParams) {
  const el = document.getElementById(containerId);
  el.innerHTML = '<p class="hint">Carico…</p>';
  try {
    const [movies, shows] = await Promise.all([
      fetch(`${apiBase()}/api/tv/discover?mediaType=movie&${movieParams}`).then(r => r.json()),
      fetch(`${apiBase()}/api/tv/discover?mediaType=tv&${tvParams}`).then(r => r.json())
    ]);
    const items = [];
    const max = Math.max(movies.length, shows.length);
    for (let i = 0; i < max; i++) {
      if (movies[i]) items.push(movies[i]);
      if (shows[i]) items.push(shows[i]);
    }
    renderPosterTiles(el, items);
  } catch (err) {
    el.innerHTML = `<p class="hint error">Errore: ${escapeHtml(err.message)}</p>`;
  }
}

// Rail per una singola lista editoriale TMDB (Fase 6: popular/top_rated/now_playing/upcoming per i
// film, popular/top_rated/on_the_air/airing_today per le serie) — stessa forma "con tmdbId" delle
// rail di scoperta, click diretto al dettaglio.
async function loadListRail(mediaType, list, containerId) {
  const el = document.getElementById(containerId);
  el.innerHTML = '<p class="hint">Carico…</p>';
  try {
    const items = await fetch(`${apiBase()}/api/tv/tmdb-list?mediaType=${mediaType}&list=${list}`).then(r => r.json());
    renderPosterTiles(el, Array.isArray(items) ? items : []);
  } catch (err) {
    el.innerHTML = `<p class="hint error">Errore: ${escapeHtml(err.message)}</p>`;
  }
}

// ID genere TMDB fissi (noti e stabili, niente endpoint /genre/list): differiscono tra film e serie
// per alcuni generi (es. "Azione" è 28 nei film ma "Azione & Avventura" è 10759 nelle serie), qui
// scelti per coprire lo stesso tema in entrambi. ID provider (condivisi tra film e serie in TMDB):
// Netflix=8, Prime Video=119.

// ---------------------------------------------------------------
// Pagine Categoria (Fase 4, ridisegnato dopo test reale sulla TV): Tendenza/Generi/Streaming
// vivono qui invece che come righe aggiuntive in home — una home con hero + 9 rail era troppo
// lunga da scorrere a frecce (titoli di sezione che uscivano dallo schermo, poco spazio verticale
// residuo). Ogni categoria è una pagina a sé con solo i propri rail, tanto spazio quanto serve.
// ---------------------------------------------------------------

let categoryRailCounter = 0;

// Crea heading+rail dentro "container" e torna l'id del rail, da passare a loadTrendingRail /
// loadMixedDiscoverRail (che si aspettano un id da cercare con getElementById, stesso contratto
// già usato per i rail statici della vecchia home — nessuna modifica a quelle due funzioni).
// iconSrc opzionale: icona ufficiale del servizio (richiesta utente, al posto dell'emoji colorata)
// — messa PRIMA del testo nello stesso <h3>, dimensionata via .rail-heading-icon in app.css.
function buildRailSection(container, headingText, iconSrc) {
  const heading = document.createElement('h3');
  heading.className = 'rail-heading';
  if (iconSrc) {
    const icon = document.createElement('img');
    icon.className = 'rail-heading-icon';
    icon.src = iconSrc;
    icon.alt = '';
    heading.appendChild(icon);
  }
  heading.appendChild(document.createTextNode(headingText));
  const rail = document.createElement('div');
  rail.className = 'rail';
  rail.id = `category-rail-${categoryRailCounter++}`;
  container.appendChild(heading);
  container.appendChild(rail);
  return rail.id;
}

const CATEGORY_TITLES = { movies: '🎬 Film', tv: '📺 Serie TV', genres: '🎭 Generi', streaming: '📡 In streaming' };

function openCategory(category) {
  pushView('category');
  document.getElementById('category-title').textContent = CATEGORY_TITLES[category] || '';
  const body = document.getElementById('category-body');
  body.innerHTML = '';

  if (category === 'movies') {
    loadTrendingRail('movie', buildRailSection(body, '🎬 Film di tendenza'));
    loadListRail('movie', 'now_playing', buildRailSection(body, '🎦 Al cinema ora'));
    loadListRail('movie', 'top_rated', buildRailSection(body, '⭐ I più votati'));
    loadListRail('movie', 'upcoming', buildRailSection(body, '📅 In arrivo'));
  } else if (category === 'tv') {
    loadTrendingRail('tv', buildRailSection(body, '📺 Serie di tendenza'));
    loadListRail('tv', 'airing_today', buildRailSection(body, '📡 In onda oggi'));
    loadListRail('tv', 'top_rated', buildRailSection(body, '⭐ Le più votate'));
    loadListRail('tv', 'on_the_air', buildRailSection(body, '🔴 In corso'));
  } else if (category === 'genres') {
    loadMixedDiscoverRail(buildRailSection(body, '🚀 Fantascienza & Fantasy'), 'genreId=878', 'genreId=10765');
    loadMixedDiscoverRail(buildRailSection(body, '😂 Commedia'), 'genreId=35', 'genreId=35');
    loadMixedDiscoverRail(buildRailSection(body, '💥 Azione & Avventura'), 'genreId=28', 'genreId=10759');
  } else if (category === 'streaming') {
    loadMixedDiscoverRail(buildRailSection(body, 'Netflix', 'assets/streaming/netflix.png'), 'providerId=8', 'providerId=8');
    loadMixedDiscoverRail(buildRailSection(body, 'Prime Video', 'assets/streaming/Primevideo.png'), 'providerId=119', 'providerId=119');
    loadMixedDiscoverRail(buildRailSection(body, 'Disney+', 'assets/streaming/Disney.png'), 'providerId=337', 'providerId=337');
    loadMixedDiscoverRail(buildRailSection(body, 'Apple TV+', 'assets/streaming/Appletv.png'), 'providerId=350', 'providerId=350');
    loadMixedDiscoverRail(buildRailSection(body, 'Paramount+', 'assets/streaming/Paramount.png'), 'providerId=531', 'providerId=531');
    loadMixedDiscoverRail(buildRailSection(body, 'HBO Max', 'assets/streaming/hbomax.png'), 'providerId=1899', 'providerId=1899');
  }

  focusFirstAfterRender('category', body);
}

document.getElementById('category-back').addEventListener('click', handleBack);

// ---------------------------------------------------------------
// Rail a icone — markup condiviso, clonato in ogni view che porta la classe "view-with-icon-rail"
// (vedi index.html) invece di scriverlo a mano una volta per pagina. Un solo posto da cui
// aggiungere/rinominare una voce di menu; apri/chiudi/moveRailFocus (sopra) già ragionano per
// "la rail dentro la view corrente", quindi funzionano automaticamente su ogni copia.
// ---------------------------------------------------------------

function buildIconRail() {
  const nav = document.createElement('nav');
  nav.className = 'icon-rail';
  nav.innerHTML = `
    <div class="rail-brand"><img class="rail-brand-mark" src="assets/logo.png" alt="" /><span class="rail-label rail-wordmark">NimbusFX</span></div>
    <button class="tile tile-small rail-item" data-rail-action="home" tabindex="0"><span class="rail-ic">🏠</span><span class="rail-label">Home</span></button>
    <button class="tile tile-small rail-item" data-rail-action="category:movies" tabindex="0"><span class="rail-ic">🎬</span><span class="rail-label">Film</span></button>
    <button class="tile tile-small rail-item" data-rail-action="category:tv" tabindex="0"><span class="rail-ic">📺</span><span class="rail-label">Serie TV</span></button>
    <button class="tile tile-small rail-item" data-rail-action="category:genres" tabindex="0"><span class="rail-ic">🎭</span><span class="rail-label">Generi</span></button>
    <button class="tile tile-small rail-item" data-rail-action="plex-library" tabindex="0"><span class="rail-ic">📚</span><span class="rail-label">Libreria</span></button>
    <button class="tile tile-small rail-item" data-rail-action="category:streaming" tabindex="0"><span class="rail-ic">📡</span><span class="rail-label">In streaming</span></button>
    <div class="rail-sep"></div>
    <button class="tile tile-small rail-item" data-rail-action="library" tabindex="0"><span class="rail-ic">📥</span><span class="rail-label">Providers</span></button>
    <button class="tile tile-small rail-item" data-rail-action="downloads" tabindex="0"><span class="rail-ic">⬇️</span><span class="rail-label">Download</span></button>
    <button class="tile tile-small rail-item" data-rail-action="settings" tabindex="0"><span class="rail-ic">⚙️</span><span class="rail-label">Impostazioni</span></button>`;
  return nav;
}

function initIconRails() {
  document.querySelectorAll('.view-with-icon-rail').forEach(view => view.appendChild(buildIconRail()));
}
initIconRails();

// Un salto dalla rail è un nuovo punto di partenza, non un altro passo della stessa esplorazione:
// se la sezione scelta usa lo stesso contenitore condiviso della vista attuale (es. "category",
// riscritto ogni volta da openCategory — mai due copie sullo stesso #category-body) o è la stessa
// vista già aperta, sostituisce la cima dello stack invece di impilarne un'altra sopra. Senza
// questo, "Indietro" dopo un salto rail ritroverebbe la vista già sovrascritta dal salto stesso,
// non quella da cui si era partiti davvero.
async function railNavigate(action) {
  railReturnFocus = null; // si sta cambiando sezione: nessun ritorno sensato a cui puntare dopo
  const top = () => stack[stack.length - 1];

  if (action === 'home') {
    stack = ['home'];
    showView('home');
    loadContinueWatching();
    loadWatchlist();
    return;
  }
  if (action.startsWith('category:')) {
    if (top() === 'category') stack.pop();
    openCategory(action.slice('category:'.length));
    return;
  }
  if (action === 'plex-library') {
    if (top() === 'plex-library') stack.pop();
    pushView('plex-library');
    loadPlexLibrary();
    return;
  }
  if (action === 'library') {
    if (top() === 'library') stack.pop();
    setLibraryProvider(normalizeProvider(getConfig().preferredProvider));
    pushView('library');
    loadMagnets();
    return;
  }
  if (action === 'downloads') {
    if (top() === 'downloads') stack.pop();
    pushView('downloads');
    return;
  }
  if (action === 'settings') {
    if (top() === 'setup') stack.pop();
    await openSettingsFromRail();
  }
}

document.addEventListener('click', (e) => {
  const btn = e.target.closest('.rail-item[data-rail-action]');
  if (btn) railNavigate(btn.dataset.railAction);
});

// "Continua a guardare" (Fase 3, piano-restyle-webos-ux.md) — a differenza dei rail di tendenza
// (caricati una sola volta per apertura pagina categoria) va ricaricata OGNI volta che si torna in
// home: il progresso cambia in continuazione durante l'uso (episodio appena finito, film appena
// iniziato).
// Chiamata da popView() quando si torna alla home e dai due punti di ingresso iniziali (init,
// salvataggio setup) — non ci sono altri modi di raggiungere la home in questa app.
async function loadContinueWatching() {
  loadContinueWatchingRail('movie', 'continue-movies-section', 'continue-movies-rail');
  loadContinueWatchingRail('tv', 'continue-tv-section', 'continue-tv-rail');
}

// Watchlist ("da vedere più tardi") — titoli salvati dalla pagina di dettaglio (vedi
// setupWatchlistButton), indipendente dalla cronologia di visione: qui non c'è un file/posizione,
// solo l'intenzione di guardarlo. Click su un tile riapre la pagina di dettaglio (stesso punto di
// ingresso di un candidato di ricerca), non il player direttamente.
async function loadWatchlist() {
  const section = document.getElementById('watchlist-section');
  const rail = document.getElementById('watchlist-rail');
  try {
    const res = await fetch(`${apiBase()}/api/tv/watchlist`);
    if (!res.ok) throw new Error('HTTP ' + res.status);
    const items = await res.json();
    if (items.length === 0) { section.classList.add('hidden'); rail.innerHTML = ''; return; }

    section.classList.remove('hidden');
    rail.innerHTML = '';
    items.forEach(item => {
      const btn = document.createElement('button');
      btn.className = 'tile poster-tile';
      btn.tabIndex = 0;
      btn.innerHTML = `
        ${item.posterUrl ? `<img src="${item.posterUrl}" alt="" />` : ''}
        <div class="poster-caption">${escapeHtml(item.title)}${item.year ? `<span class="poster-year">${escapeHtml(item.year)}</span>` : ''}</div>`;
      btn.addEventListener('click', () => openCandidate({
        tmdbId: item.tmdbId, mediaType: item.mediaType, title: item.title, year: item.year,
        posterUrl: item.posterUrl, overview: null, isAnime: false
      }));

      const removeBtn = buildRemoveButton(async () => {
        await fetch(`${apiBase()}/api/tv/watchlist?tmdbId=${item.tmdbId}&mediaType=${item.mediaType}`, { method: 'DELETE' });
        await loadWatchlist();
        refocusAfterRemoval(rail);
      });

      rail.appendChild(buildPosterWrap(btn, removeBtn));
    });
  } catch (err) {
    section.classList.add('hidden');
  }
}

async function loadContinueWatchingRail(mediaType, sectionId, railId) {
  const section = document.getElementById(sectionId);
  const rail = document.getElementById(railId);
  try {
    const res = await fetch(`${apiBase()}/api/tv/history/continue-watching?mediaType=${mediaType}`);
    if (!res.ok) throw new Error('HTTP ' + res.status);
    const items = await res.json();
    if (items.length === 0) { section.classList.add('hidden'); rail.innerHTML = ''; return; }

    section.classList.remove('hidden');
    rail.innerHTML = '';
    items.forEach(item => {
      const pct = item.durationSeconds > 0 ? Math.min(100, (item.positionSeconds / item.durationSeconds) * 100) : 0;
      const label = item.mediaType === 'tv' && item.episode
        ? (item.episodeTitle || `S${item.season} · E${item.episode}`)
        : null;
      const btn = document.createElement('button');
      btn.className = 'tile poster-tile';
      btn.tabIndex = 0;
      btn.innerHTML = `
        ${item.posterUrl ? `<img src="${item.posterUrl}" alt="" />` : ''}
        <div class="poster-progress"><div class="poster-progress-fill" style="width:${pct}%"></div></div>
        <div class="poster-caption">${escapeHtml(item.title)}${label ? `<span class="poster-year">${escapeHtml(label)}</span>` : ''}</div>`;
      btn.addEventListener('click', () => resumeFromHistory(item));

      const removeBtn = buildRemoveButton(async () => {
        const query = new URLSearchParams({ tmdbId: item.tmdbId, mediaType: item.mediaType });
        if (item.season != null) query.set('season', item.season);
        if (item.episode != null) query.set('episode', item.episode);
        await fetch(`${apiBase()}/api/tv/history/progress?${query.toString()}`, { method: 'DELETE' });
        await loadContinueWatchingRail(mediaType, sectionId, railId);
        refocusAfterRemoval(rail);
      });

      rail.appendChild(buildPosterWrap(btn, removeBtn));
    });
  } catch (err) {
    section.classList.add('hidden'); // arricchimento facoltativo: nessun errore visibile in home se fallisce
  }
}

// Riapre direttamente il player dal file/posizione salvati — nessuna nuova ricerca, il link
// AllDebrid stabile (fileLink) e il contesto tmdbId/stagione/episodio sono già nella cronologia.
function resumeFromHistory(item) {
  if (!item.fileLink) return;
  // "title" qui è item.title (nome pulito, es. "Lanterns") — mai il titolo composto costruito
  // sotto per lo schermo del player, altrimenti il prossimo reportProgress() lo salverebbe di
  // nuovo come titolo "pulito", reintroducendo la stessa impilazione ad ogni resume successivo.
  currentMediaContext = { tmdbId: item.tmdbId, mediaType: item.mediaType, season: item.season, episode: item.episode, title: item.title };
  // BUG REALE (2026-09-14, segnalato dall'utente): questo è l'unico punto d'ingresso al player che
  // salta del tutto la pagina di dettaglio (openCandidate, che altrove aggiorna
  // currentDetailBackdrop) — senza questa riga lo sfondo TMDB nel player restava quello
  // dell'ultimo titolo visitato via dettaglio (o "none" se la sessione non era mai passata da lì),
  // sbagliato per tutta la visione (compresi i reload dei salti avanti/indietro, che riusano lo
  // stesso sfondo impostato una volta sola). Il backend arricchisce già /history/continue-watching
  // con backdropUrl/posterUrl apposta per questo (vedi TvApiEndpoints.cs), bastava usarli.
  currentDetailBackdrop = item.backdropUrl || item.posterUrl || null;
  const label = item.mediaType === 'tv' && item.episode
    ? ` · ${item.episodeTitle || `S${item.season}E${item.episode}`}`
    : '';
  openPlayer({ name: item.fileName, link: item.fileLink, size: 0, provider: item.provider || 'allDebrid' }, `${item.title}${label}`, item.positionSeconds);
}

// Titolo vero di un episodio (TMDB) invece del solo "S01E01" — usato da "Continua a guardare"
// (lato server, vedi TvApiEndpoints) e qui lato client per "Prossimo episodio" (calcolato senza
// passare dal server, che non conosce ancora quell'episodio in nessuna cronologia).
async function fetchEpisodeTitle(tmdbId, season, episode) {
  const episodes = await fetchSeasonEpisodeTitles(tmdbId, season);
  return episodes.find(e => e.episodeNumber === episode)?.name || null;
}

// Stessa chiamata di fetchEpisodeTitle ma per TUTTA la stagione in un colpo solo — usata dalla
// lista episodi (richiesta utente: titolo vero invece di solo "Episodio N"), dove rifare una
// chiamata per riga sarebbe stato uno spreco enorme di round-trip.
async function fetchSeasonEpisodeTitles(tmdbId, season) {
  try {
    const res = await fetch(`${apiBase()}/api/tv/search/episodes?tmdbId=${tmdbId}&season=${season}`);
    if (!res.ok) return [];
    return await res.json();
  } catch {
    return [];
  }
}

// Poster + pulsante "✕" rimuovi ("Continua a guardare"/Watchlist): un <button> non può contenerne
// un altro validamente, quindi il "✕" è un fratello dentro un div wrapper, posizionato sopra
// l'angolo del poster via CSS (.poster-wrap/.poster-remove), non annidato nel bottone principale.
function buildPosterWrap(posterBtn, removeBtn) {
  const wrap = document.createElement('div');
  wrap.className = 'poster-wrap';
  wrap.appendChild(posterBtn);
  wrap.appendChild(removeBtn);
  return wrap;
}

function buildRemoveButton(onRemove) {
  const btn = document.createElement('button');
  btn.className = 'poster-remove';
  btn.tabIndex = 0;
  btn.textContent = '✕';
  btn.title = 'Rimuovi';
  btn.addEventListener('click', (e) => {
    e.stopPropagation();
    onRemove();
  });
  return btn;
}

// Dopo aver rimosso un elemento e ricostruito la riga, il focus (sul bottone appena distrutto)
// andrebbe perso — lo si riporta sul primo elemento rimasto nella stessa riga, o in mancanza su
// un qualunque elemento focalizzabile della vista, invece di lasciarlo "nel vuoto".
function refocusAfterRemoval(rail) {
  const target = rail.querySelector('[tabindex]') || document.querySelector('.view:not(.hidden) [tabindex]');
  if (target) target.focus();
}

async function loadTrendingRail(mediaType, containerId) {
  const el = document.getElementById(containerId);
  el.innerHTML = '<p class="hint">Carico…</p>';
  try {
    const res = await fetch(`${apiBase()}/api/tv/trending?mediaType=${mediaType}`);
    const items = await res.json();
    el.innerHTML = '';
    if (items.length === 0) {
      el.innerHTML = '<p class="hint">Nessun dato disponibile (TMDB non configurato o nessun risultato).</p>';
      return;
    }
    items.forEach(i => {
      const btn = document.createElement('button');
      btn.className = 'tile poster-tile';
      btn.tabIndex = 0;
      btn.innerHTML = `${i.posterUrl ? `<img src="${i.posterUrl}" alt="" />` : ''}
        <div class="poster-caption">${escapeHtml(i.title)}${i.year ? `<span class="poster-year">${escapeHtml(i.year)}</span>` : ''}</div>`;
      btn.addEventListener('click', () => openSearchFor(i.title));
      el.appendChild(btn);
    });
  } catch (err) {
    el.innerHTML = `<p class="hint error">Errore: ${escapeHtml(err.message)}</p>`;
  }
}

// ---------------------------------------------------------------
// Libreria (magnet pronti su AllDebrid)
// ---------------------------------------------------------------

document.getElementById('library-back').addEventListener('click', handleBack);
document.getElementById('library-refresh').addEventListener('click', loadMagnets);
document.getElementById('downloads-back').addEventListener('click', handleBack);
document.getElementById('downloads-refresh').addEventListener('click', loadDownloads);

// ---------------------------------------------------------------
// Sezione Download (richiesta utente): sola lettura + rimuovi/ferma sulla stessa coda di
// Queue.razor — il download parte dal pulsante sulla card episodio/film e prosegue lato server
// indipendentemente da questa vista, che qui viene solo interrogata a intervalli mentre è aperta.
// ---------------------------------------------------------------

let downloadsPollTimer = null;

function startDownloadsPolling() {
  stopDownloadsPolling();
  loadDownloads();
  downloadsPollTimer = setInterval(loadDownloads, 2000);
}

function stopDownloadsPolling() {
  clearInterval(downloadsPollTimer);
  downloadsPollTimer = null;
}

// "Downloading" copre sia la fase di risoluzione del magnet (upload → wait-ready sul provider, può
// durare minuti prima che parta il vero trasferimento) sia il trasferimento vero — distinte da qui
// in poi con un'unica regola: c'è un vero avanzamento solo se è stata misurata una velocità reale.
function hasRealDownloadProgress(item) {
  return item.status === 'Downloading' && !!item.speed;
}

const DOWNLOAD_STATUS_LABEL = {
  Queued: '⏳ In coda',
  Downloading: '⬇️ In corso',
  Done: '✅ Completato',
  Failed: '❌ Fallito',
  Cancelled: '⏹️ Fermato'
};

async function loadDownloads() {
  const grid = document.getElementById('downloads-grid');
  try {
    const res = await fetch(`${apiBase()}/api/tv/downloads`);
    if (!res.ok) throw new Error('HTTP ' + res.status);
    const items = await res.json();
    renderDownloads(items);
  } catch (err) {
    if (grid.children.length === 0) grid.innerHTML = `<p class="hint error">Errore di connessione: ${escapeHtml(err.message)}.</p>`;
  }
}

function renderDownloads(items) {
  const grid = document.getElementById('downloads-grid');
  if (items.length === 0) {
    grid.innerHTML = '<p class="hint">Nessun download in coda o in corso.</p>';
    return;
  }

  grid.innerHTML = '';
  items.forEach(item => {
    const row = document.createElement('div');
    row.className = 'tile tile-row download-row';

    const pct = Math.max(0, Math.min(100, item.percent || 0));
    // "Downloading" copre ANCHE la fase di risoluzione del magnet (upload → wait-ready, può
    // durare minuti prima che parta il vero trasferimento) — mostrare subito "0% ·  · ETA --" in
    // quella fase è fuorviante (sembra bloccato). Se non c'è ancora un vero avanzamento (nessuna
    // velocità misurata), mostra il messaggio di stato reale (es. "🧲 Verifica disponibilità…").
    const downloading = hasRealDownloadProgress(item);
    const detail = downloading
      ? `${pct.toFixed(0)}% · ${escapeHtml(item.speed || '')} · ETA ${escapeHtml(item.eta || '--')}${item.totalFiles > 1 ? ` · file ${item.currentFileIndex}/${item.totalFiles}` : ''}`
      : escapeHtml(item.message || (item.status === 'Downloading' ? '🔍 Verifica disponibilità…' : ''));

    row.innerHTML = `
      <div style="flex:1; min-width:0;">
        <div class="tile-title">${escapeHtml(item.title)}</div>
        <div class="tile-sub">${DOWNLOAD_STATUS_LABEL[item.status] || item.status} — ${escapeHtml(item.sourceName || '')} · ${item.toTv ? '📺 Serie' : '🎬 Film'}</div>
        ${downloading ? `<div class="dl-progress"><div class="dl-progress-fill" style="width:${Math.max(4, pct)}%"></div></div>` : ''}
        <div class="tile-sub">${detail}</div>
      </div>`;

    const actionBtn = document.createElement('button');
    actionBtn.className = 'tile tile-small result-provider-btn';
    actionBtn.tabIndex = 0;
    actionBtn.textContent = item.status === 'Downloading' ? '⏹️' : '✕';
    actionBtn.title = item.status === 'Downloading' ? 'Ferma' : 'Rimuovi';
    actionBtn.addEventListener('click', async () => {
      actionBtn.disabled = true;
      try {
        if (item.status === 'Downloading') {
          await fetch(`${apiBase()}/api/tv/downloads/${item.id}/stop`, { method: 'POST' });
        } else {
          await fetch(`${apiBase()}/api/tv/downloads/${item.id}`, { method: 'DELETE' });
        }
        loadDownloads();
      } catch { actionBtn.disabled = false; }
    });

    const wrap = document.createElement('div');
    wrap.className = 'result-row-wrap';
    wrap.appendChild(row);
    wrap.appendChild(actionBtn);
    grid.appendChild(wrap);
  });
}
document.getElementById('library-tab-alldebrid').addEventListener('click', () => { setLibraryProvider('allDebrid'); loadMagnets(); });
document.getElementById('library-tab-realdebrid').addEventListener('click', () => { setLibraryProvider('realDebrid'); loadMagnets(); });
document.getElementById('library-tab-premiumize').addEventListener('click', () => { setLibraryProvider('premiumize'); loadMagnets(); });
document.getElementById('files-back').addEventListener('click', handleBack);

// ---------------------------------------------------------------
// Libreria Plex/Premiumize (docs/piano-premiumize-libreria.md) — equivalente webOS di
// Library.razor: sfoglia quello che è già su Plex (film/serie) e le acquisizioni Premiumize
// tracciate. Vista separata da 'library' (quella è la gestione magnet AllDebrid/Real-Debrid/
// Premiumize, tutt'altra cosa).
// ---------------------------------------------------------------

// L'apertura non è più legata a un bottone con id fisso: railNavigate('plex-library') (sopra)
// chiama pushView+loadPlexLibrary direttamente da qualunque view tramite la rail a icone.
document.getElementById('plex-library-back').addEventListener('click', handleBack);
document.getElementById('plex-library-refresh').addEventListener('click', loadPlexLibrary);

let plexLibrarySection = 'plex'; // 'plex' | 'premiumize'
let plexLibraryType = 'movie';   // 'movie' | 'tv' — usato da entrambe le sezioni
let pmBreadcrumb = [];           // sfoglio cartelle Premiumize: pila {id, name} dalla radice

document.getElementById('plex-library-tab-plex').addEventListener('click', () => {
  plexLibrarySection = 'plex';
  document.getElementById('plex-library-tab-plex').classList.add('tile-primary');
  document.getElementById('plex-library-tab-premiumize').classList.remove('tile-primary');
  document.getElementById('plex-library-breadcrumb').classList.add('hidden');
  document.getElementById('plex-library-search-input').classList.remove('hidden');
  loadPlexLibrary();
});
document.getElementById('plex-library-tab-premiumize').addEventListener('click', () => {
  plexLibrarySection = 'premiumize';
  document.getElementById('plex-library-tab-premiumize').classList.add('tile-primary');
  document.getElementById('plex-library-tab-plex').classList.remove('tile-primary');
  pmBreadcrumb = [];
  // La ricerca filtra l'elenco Plex già in memoria (plexLibraryItems) — non ha corrispondenza
  // mentre si sfogliano cartelle Premiumize (righe caricate al volo, una cartella alla volta).
  document.getElementById('plex-library-search-input').value = '';
  document.getElementById('plex-library-search-input').classList.add('hidden');
  loadPlexLibrary();
});
document.getElementById('plex-library-search-input').addEventListener('input', applyPlexLibraryFilters);
document.getElementById('plex-library-type-movie').addEventListener('click', () => {
  plexLibraryType = 'movie';
  document.getElementById('plex-library-type-movie').classList.add('tile-primary');
  document.getElementById('plex-library-type-tv').classList.remove('tile-primary');
  pmBreadcrumb = [];
  loadPlexLibrary();
});
document.getElementById('plex-library-type-tv').addEventListener('click', () => {
  plexLibraryType = 'tv';
  document.getElementById('plex-library-type-tv').classList.add('tile-primary');
  document.getElementById('plex-library-type-movie').classList.remove('tile-primary');
  pmBreadcrumb = [];
  loadPlexLibrary();
});

let plexLibraryItems = [];              // ultimo risultato grezzo di /library/all (tab Plex)
let plexLibrarySort = 'added';          // 'added' | 'name' | 'year' — come Library.razor sul web
let plexLibraryGenre = 'all';

async function loadPlexLibrary() {
  const grid = document.getElementById('plex-library-grid');
  grid.innerHTML = '<p class="hint">Carico…</p>';
  document.getElementById('plex-library-sort-row').classList.toggle('hidden', plexLibrarySection !== 'plex');
  try {
    if (plexLibrarySection === 'plex') {
      document.getElementById('plex-library-breadcrumb').classList.add('hidden');
      const res = await fetch(`${apiBase()}/api/tv/library/all?mediaType=${plexLibraryType}`);
      if (!res.ok) throw new Error('HTTP ' + res.status);
      plexLibraryItems = await res.json();
      plexLibraryGenre = 'all';
      document.getElementById('plex-library-search-input').value = ''; // nuovo elenco: stessa logica del reset del genere sopra
      setupPlexLibraryFilters();
      applyPlexLibraryFilters();
    } else {
      const folderId = pmBreadcrumb.length > 0 ? pmBreadcrumb[pmBreadcrumb.length - 1].id : null;
      const url = folderId
        ? `${apiBase()}/api/tv/library/premiumize/browse?type=${plexLibraryType}&folderId=${encodeURIComponent(folderId)}`
        : `${apiBase()}/api/tv/library/premiumize/browse?type=${plexLibraryType}`;
      const res = await fetch(url);
      const entries = await res.json();
      if (!res.ok) throw new Error(entries.error || ('HTTP ' + res.status));
      renderPremiumizeBrowser(entries);
    }
  } catch (err) {
    grid.innerHTML = `<p class="hint error">Errore di connessione: ${escapeHtml(err.message)}. Controlla IP/porta in ⚙️ Impostazioni.</p>`;
  }
}

function renderPmBreadcrumb() {
  const el = document.getElementById('plex-library-breadcrumb');
  if (plexLibrarySection !== 'premiumize' || pmBreadcrumb.length === 0) {
    el.classList.add('hidden');
    el.innerHTML = '';
    return;
  }
  el.classList.remove('hidden');
  el.innerHTML = '';
  const rootLabel = plexLibraryType === 'tv' ? 'Serie TV' : 'Film';
  const crumbs = [{ id: null, name: rootLabel }, ...pmBreadcrumb.map(c => ({ id: c.id, name: c.name }))];
  crumbs.forEach((crumb, i) => {
    const btn = document.createElement('button');
    btn.className = 'tile tile-small' + (i === crumbs.length - 1 ? ' tile-primary' : '');
    btn.tabIndex = 0;
    btn.textContent = crumb.name;
    btn.disabled = i === crumbs.length - 1;
    btn.addEventListener('click', () => {
      pmBreadcrumb = i === 0 ? [] : pmBreadcrumb.slice(0, i);
      loadPlexLibrary();
    });
    el.appendChild(btn);
  });
}

function renderPremiumizeBrowser(entries) {
  renderPmBreadcrumb();
  const grid = document.getElementById('plex-library-grid');
  grid.innerHTML = '';
  if (entries.length === 0) {
    grid.innerHTML = '<p class="hint">Cartella vuota.</p>';
    return;
  }
  entries.forEach(entry => {
    const btn = document.createElement('button');
    btn.className = 'tile tile-row';
    btn.tabIndex = 0;
    const sizeText = entry.isFolder ? '' : formatSize(entry.size);
    btn.innerHTML = `<div class="tile-title">${entry.isFolder ? '📁' : '🎬'} ${escapeHtml(entry.name)}</div><div class="tile-sub">${sizeText}</div>`;
    btn.addEventListener('click', () => {
      if (entry.isFolder) {
        pmBreadcrumb.push({ id: entry.id, name: entry.name });
        loadPlexLibrary();
      } else if (entry.link) {
        openPlayer({ name: entry.name, link: entry.link, size: entry.size, provider: 'premiumize' }, entry.name, 0);
      }
    });
    grid.appendChild(btn);
  });
}

// Chip Ordina/Genere sopra la griglia Plex (richiesta utente, "come Plex") — ricostruiti ad ogni
// caricamento perché l'elenco generi dipende dal tab Film/Serie corrente (es. "Anime" esiste solo
// tra le serie), stesso principio di CurrentGenres in Library.razor sul web.
function setupPlexLibraryFilters() {
  buildFilterChips(
    'plex-library-sort',
    [
      { value: 'added', label: 'Ultima aggiunta' },
      { value: 'name', label: 'Titolo' },
      { value: 'year', label: 'Anno' }
    ],
    () => plexLibrarySort,
    v => { plexLibrarySort = v; applyPlexLibraryFilters(); }
  );

  const genres = Array.from(new Set(plexLibraryItems.flatMap(i => i.genres || []))).sort((a, b) => a.localeCompare(b));
  buildFilterChips(
    'plex-library-genre',
    [{ value: 'all', label: 'Tutti i generi' }, ...genres.map(g => ({ value: g, label: g }))],
    () => plexLibraryGenre,
    v => { plexLibraryGenre = v; applyPlexLibraryFilters(); }
  );
}

function applyPlexLibraryFilters() {
  let items = plexLibraryGenre === 'all'
    ? plexLibraryItems.slice()
    : plexLibraryItems.filter(i => (i.genres || []).includes(plexLibraryGenre));

  // Ricerca testuale (idee-miglioramento-webos.md, richiesta utente 2026-09-14) — client-side come
  // ordina/genere sopra, stessi dati già scaricati da /library/all.
  const query = (document.getElementById('plex-library-search-input').value || '').trim().toLowerCase();
  if (query) items = items.filter(i => (i.title || '').toLowerCase().includes(query));

  items.sort((a, b) => {
    if (plexLibrarySort === 'name') return a.title.localeCompare(b.title);
    if (plexLibrarySort === 'year') return (b.year || 0) - (a.year || 0);
    return new Date(b.addedAt || 0) - new Date(a.addedAt || 0); // 'added' (default)
  });

  renderPlexLibraryGrid(items);
}

function renderPlexLibraryGrid(items) {
  const grid = document.getElementById('plex-library-grid');
  grid.innerHTML = '';
  if (items.length === 0) {
    grid.innerHTML = '<p class="hint">Nessun titolo corrisponde al filtro.</p>';
    return;
  }
  items.forEach(item => {
    const btn = document.createElement('button');
    btn.className = 'tile poster-tile';
    btn.tabIndex = 0;
    const badge = item.isPremiumize ? '☁️' : (item.hasLocalFile ? '💾' : '');
    btn.innerHTML = `${item.thumbUrl ? `<img src="${item.thumbUrl}" alt="" />` : ''}
      ${badge ? `<span class="poster-badge">${badge}</span>` : ''}
      <div class="poster-caption">${escapeHtml(item.title)}${item.year ? `<span class="poster-year">${escapeHtml(String(item.year))}</span>` : ''}</div>`;
    // Richiesta utente: il click deve aprire la VERA pagina di dettaglio (poster/backdrop/generi/
    // cast/stagioni), MAI una ricerca torrent diretta — e lì dentro l'azione "Guarda"/"Prossimo
    // episodio" deve essere PRIMARIA da disco quando il file è già posseduto (richiesta utente
    // 2026-09-13: passando dalla libreria Plex, mai rimandare a scarica/sblocca un torrent), non
    // un pulsante secondario che compare dopo — vedi fetchLocalStatus, usato in renderDetail prima
    // di decidere cosa fa il pulsante principale. Un elemento della libreria Plex non porta un
    // tmdbId (Plex non lo espone), va risolto al volo con una singola chiamata leggera.
    btn.addEventListener('click', () => openLibraryItemDetail(item, plexLibraryType));
    grid.appendChild(btn);
  });

  // getRows() spezza questa griglia in righe da "grid.dataset.cols" elementi (vedi app.js) per far
  // funzionare Sinistra/Destra tra poster affiancati — qui le colonne sono a larghezza fissa
  // (repeat(auto-fill, 220px) in app.css, non un numero scritto a mano), quindi il numero REALE va
  // letto dal layout già calcolato dal browser invece di indovinato: cambia con la larghezza
  // disponibile (rail a icone espansa o no, ecc.), un valore fisso sarebbe sbagliato appena cambia.
  grid.dataset.cols = getComputedStyle(grid).gridTemplateColumns.split(' ').filter(Boolean).length || 1;
}

// Tab AllDebrid/Real-Debrid/Premiumize della Libreria (equivalente webOS di AllDebrid.razor, vedi
// piano-multi-provider-debrid.md / piano-premiumize-libreria.md): cambia quale account viene
// interrogato da /api/tv/magnets*.
let libraryProvider = 'allDebrid';

function setLibraryProvider(provider) {
  libraryProvider = provider;
  document.getElementById('library-tab-alldebrid').classList.toggle('tile-primary', provider === 'allDebrid');
  document.getElementById('library-tab-realdebrid').classList.toggle('tile-primary', provider === 'realDebrid');
  document.getElementById('library-tab-premiumize').classList.toggle('tile-primary', provider === 'premiumize');
}

async function loadMagnets() {
  const grid = document.getElementById('library-grid');
  grid.innerHTML = '<p class="hint">Carico…</p>';
  try {
    const res = await fetch(`${apiBase()}/api/tv/magnets?provider=${libraryProvider}`);
    if (!res.ok) throw new Error('HTTP ' + res.status);
    const magnets = await res.json();
    renderLibrary(magnets);
  } catch (err) {
    grid.innerHTML = `<p class="hint error">Errore di connessione: ${escapeHtml(err.message)}. Controlla IP/porta in ⚙️ Impostazioni.</p>`;
  }
}

function renderLibrary(magnets) {
  const grid = document.getElementById('library-grid');
  grid.innerHTML = '';
  if (magnets.length === 0) {
    grid.innerHTML = `<p class="hint">Nessun magnet su ${providerLabel(libraryProvider)}. Aggiungine uno da Send2Plex sul PC/telefono, poi aggiorna qui.</p>`;
    return;
  }
  magnets.forEach(m => {
    const btn = document.createElement('button');
    btn.className = 'tile' + (m.isReady ? '' : ' tile-disabled');
    btn.tabIndex = 0;
    if (!m.isReady) btn.disabled = true;
    btn.innerHTML = `<div class="tile-title">${escapeHtml(m.filename)}</div>
                      <div class="tile-sub">${formatSize(m.size)} · ${escapeHtml(m.status)}</div>`;
    btn.addEventListener('click', () => { if (m.isReady) openMagnet(m); });
    grid.appendChild(btn);
  });
}

async function openMagnet(m) {
  // Percorso Libreria: nessun tmdbId noto, quindi nessuna cronologia di visione per questo file
  // (a differenza del percorso Cerca, che imposta currentMediaContext prima di arrivare qui).
  currentMediaContext = null;
  try {
    const res = await fetch(`${apiBase()}/api/tv/magnets/${encodeURIComponent(m.id)}/files?provider=${libraryProvider}`);
    if (!res.ok) throw new Error('HTTP ' + res.status);
    const files = await res.json();
    files.forEach(f => { f.provider = libraryProvider; });
    showFilesOrPlayer(files, m.filename);
  } catch (err) {
    alert('Errore recuperando i file: ' + err.message);
  }
}

// Punto di arrivo comune per "libreria" (magnet già pronto) e "cerca" (magnet appena preparato):
// se c'è un solo file salta dritto al player, altrimenti mostra l'elenco per scegliere.
function showFilesOrPlayer(files, title) {
  if (files.length === 0) {
    alert('Nessun file trovato in questo magnet.');
    return;
  }
  if (files.length === 1) {
    openPlayer(files[0], title);
    return;
  }
  pushView('files');
  document.getElementById('files-title').textContent = title;
  // Stessa copertina già scaricata per dettaglio/risultati/versioni (nessuna nuova chiamata) —
  // BUG REALE corretto (2026-09-14): prima questa vista non ne mostrava nessuna.
  document.getElementById('files-backdrop').src = currentDetailBackdrop || '';
  renderFiles(files);
}

function renderFiles(files) {
  const grid = document.getElementById('files-grid');
  grid.innerHTML = '';
  files.forEach(f => {
    const btn = document.createElement('button');
    // Riga a piena larghezza (.tile-row, stesso stile di #version-list-grid) invece della tile
    // quadrata di prima: un nome file grezzo lungo si legge su una riga sola invece di troncare
    // male dentro un riquadro colorato senza immagine.
    btn.className = 'tile tile-row';
    btn.tabIndex = 0;
    btn.innerHTML = `<div class="tile-title">${escapeHtml(f.name)}</div>
                      <div class="result-badges"><span>${escapeHtml(formatSize(f.size))}</span></div>`;
    btn.addEventListener('click', () => openPlayer(f, f.name));
    grid.appendChild(btn);
  });
  focusFirstAfterRender('files', grid);
}

// ---------------------------------------------------------------
// Cerca — TMDB per titolo/poster, Torrentio per i torrent (stesso motore di default di
// Search.razor). Un solo motore, niente selettore multi-sito: Torrentio non richiede altro dopo
// il titolo (niente VPN, niente pagina di dettaglio), il che lo rende l'unico sensato da
// telecomando — digitare è già scomodo di suo, ogni passaggio in più pesa il doppio qui.
// ---------------------------------------------------------------

document.getElementById('search-back').addEventListener('click', handleBack);
document.getElementById('detail-back').addEventListener('click', handleBack);
document.getElementById('results-back').addEventListener('click', handleBack);
document.getElementById('version-list-back').addEventListener('click', handleBack);

// Backdrop TMDB della pagina di dettaglio, riusato tale e quale sulle pagine di lista
// episodi/versioni (nessuna nuova chiamata: stesso URL già scaricato per il dettaglio).
let currentDetailBackdrop = null;

// Contesto del titolo/episodio in corso di selezione — impostato quando si apre una lista versioni
// (film o episodio) dal percorso Cerca, letto da openPlayer per riportare il progresso di visione
// al server (Fase 3, piano-restyle-webos-ux.md). Resta null per il percorso Libreria (nessun
// tmdbId noto, vedi openMagnet) — nessuna cronologia per quei file.
let currentMediaContext = null;

document.getElementById('search-form').addEventListener('submit', (e) => {
  e.preventDefault();
  const q = document.getElementById('search-input').value.trim();
  if (q) runSearch(q);
});

async function runSearch(query) {
  const statusEl = document.getElementById('search-status');
  const grid = document.getElementById('search-grid');
  statusEl.textContent = `🔎 Cerco "${query}"…`;
  grid.innerHTML = '';
  try {
    const res = await fetch(`${apiBase()}/api/tv/search/candidates?q=${encodeURIComponent(query)}`);
    const data = await res.json();
    if (!res.ok) throw new Error(data.error || ('HTTP ' + res.status));
    statusEl.textContent = data.length === 0 ? `❌ Nessun risultato per "${query}".` : '';
    renderCandidates(data);
  } catch (err) {
    statusEl.textContent = '❌ Errore: ' + err.message;
  }
}

function renderCandidates(candidates) {
  const grid = document.getElementById('search-grid');
  grid.innerHTML = '';
  candidates.forEach(c => {
    const btn = document.createElement('button');
    btn.className = 'tile poster-tile';
    btn.tabIndex = 0;
    btn.innerHTML = `
      ${c.posterUrl ? `<img src="${c.posterUrl}" alt="" />` : ''}
      <span class="poster-badge">${c.mediaType === 'tv' ? '📺' : '🎬'}${c.isAnime ? ' ⛩️' : ''}</span>
      <div class="poster-caption">${escapeHtml(c.title)}${c.year ? `<span class="poster-year">${escapeHtml(c.year)}</span>` : ''}</div>`;
    btn.addEventListener('click', () => openCandidate(c));
    grid.appendChild(btn);
  });
}

// Pagina di dettaglio titolo (Fase 2, piano-restyle-webos-ux.md): sostituisce il vecchio salto
// diretto da "Cerca" a stagioni/risultati — poster/backdrop, sinossi, anno/generi/voto, poi
// "▶️ Guarda" (film) o le stagioni (serie) integrate nella STESSA schermata, non più una view a
// sé (era view-search-seasons, rimossa insieme al vecchio endpoint /search/seasons).
// Risolve un elemento della griglia Libreria (Plex, senza tmdbId) al suo candidato TMDB e apre la
// vera pagina di dettaglio — fallback sulla ricerca testuale solo se TMDB non trova nulla (titolo
// troppo sporco/raro), mai come comportamento normale (richiesta utente).
async function openLibraryItemDetail(item, mediaType) {
  try {
    const res = await fetch(`${apiBase()}/api/tv/library/resolve?title=${encodeURIComponent(item.title)}&mediaType=${mediaType}`);
    const data = await res.json();
    if (res.ok && data.found) {
      openCandidate({ tmdbId: data.tmdbId, mediaType: data.mediaType, title: data.title, year: data.year, posterUrl: data.posterUrl, overview: data.overview });
      return;
    }
  } catch { /* rete assente: ricade sulla ricerca sotto */ }
  openSearchFor(item.title);
}

async function openCandidate(c) {
  currentDetailTmdbId = c.tmdbId;
  pushView('search-detail');
  document.getElementById('detail-title').textContent = c.title;
  document.getElementById('detail-status').textContent = 'Carico…';
  document.getElementById('detail-body').classList.add('hidden');
  document.getElementById('detail-action').innerHTML = '';
  document.getElementById('detail-downloads').innerHTML = '';

  try {
    const res = await fetch(`${apiBase()}/api/tv/search/detail?tmdbId=${c.tmdbId}&mediaType=${c.mediaType}`);
    const detail = await res.json();
    if (!res.ok) throw new Error(detail.error || ('HTTP ' + res.status));
    // Solo per le serie: cronologia episodi per calcolare "prossimo episodio" (Fase 3) — in
    // parallelo, non blocca la resa del resto della pagina se il backend risponde lento.
    const watchedEpisodes = c.mediaType === 'tv' ? await fetchWatchedEpisodes(c.tmdbId) : [];
    renderDetail(detail, c, watchedEpisodes);
  } catch (err) {
    document.getElementById('detail-status').textContent = '❌ Errore: ' + err.message;
  }
}

// Trova l'episodio successivo al più recente completato/in corso (Fase 3) — "seasons" ha già
// l'episodeCount di ogni stagione (dalla stessa risposta di /search/detail), non serve altro dal
// server per capire se si passa alla stagione dopo o si resta nella stessa.
function computeNextEpisode(seasons, watchedEpisodes) {
  if (!watchedEpisodes || watchedEpisodes.length === 0) return null;
  const last = watchedEpisodes.slice().sort((a, b) => a.season - b.season || a.episode - b.episode).pop();
  const season = seasons.find(s => s.seasonNumber === last.season);
  if (!season) return null;
  if (last.episode < season.episodeCount) return { season: last.season, episode: last.episode + 1 };
  const nextSeason = seasons.find(s => s.seasonNumber === last.season + 1);
  return nextSeason ? { season: nextSeason.seasonNumber, episode: 1 } : null;
}

// Salta direttamente alla lista versioni di un episodio specifico (usato da "▶ Prossimo
// episodio"), senza passare dalla lista episodi — una sola ricerca mirata (vedi il parametro
// "episode" aggiunto a /search/results apposta per questo caso).
async function continueToEpisode(candidate, season, episode) {
  const statusEl = document.getElementById('detail-status');
  statusEl.textContent = '🔎 Cerco su Torrentio…';
  try {
    const [results, episodeTitle] = await Promise.all([
      fetchTorrentResults({ tmdbId: candidate.tmdbId, mediaType: 'tv', season, episode }),
      fetchEpisodeTitle(candidate.tmdbId, season, episode)
    ]);
    statusEl.textContent = '';
    // Richiesta utente: mostrare sempre sia stagione/episodio SIA il titolo vero, mai l'uno al
    // posto dell'altro (prima, una volta noto il titolo, la sigla S{n}E{n} spariva del tutto).
    const label = episodeTitle ? `S${season}E${episode} · ${episodeTitle}` : `S${season}E${episode}`;
    if (results.length === 0) { statusEl.textContent = `❌ Nessun risultato per ${label}.`; return; }
    openEpisodeVersions(results, `${candidate.title} — ${label}`, candidate.tmdbId, season, episode, candidate.title);
  } catch (err) {
    statusEl.textContent = '❌ Errore: ' + err.message;
  }
}

// "Già presente sul disco" (film o un preciso episodio) — un solo punto per interrogare
// /api/tv/library/status, usato PRIMA di decidere l'azione principale in renderDetail e nella
// riga episodio (richiesta utente 2026-09-13: passando dalla libreria Plex/Serie, lo streaming
// deve partire dal file locale, mai da una ricerca/sblocco torrent) — anche da
// checkLocalFileForVersionList più sotto, per il caso in cui l'utente sia comunque arrivato alla
// lista versioni (es. da una ricerca testuale libera, non dalla libreria).
async function fetchLocalStatus(mediaType, cleanTitle, season, episode) {
  try {
    const params = new URLSearchParams({ mediaType, title: cleanTitle });
    if (season != null) params.set('season', season);
    if (episode != null) params.set('episode', episode);
    const res = await fetch(`${apiBase()}/api/tv/library/status?${params.toString()}`);
    if (!res.ok) return null;
    return await res.json();
  } catch {
    return null;
  }
}

// ---------------------------------------------------------------
// Download su Plex dalla card episodio/film (richiesta utente: MAI dal player) — accoda tramite
// /api/tv/download sulla stessa coda di Queue.razor (DownloadOrchestrator.DownloadFromSearchResultAsync:
// upload magnet → wait-ready → sblocco → salvataggio in Paths.Movies/Tv → refresh Plex →
// cronologia). Il download prosegue lato server indipendentemente da questa vista — si può seguire
// il progresso dalla sezione "⬇️ Download" in home.
// ---------------------------------------------------------------

// ---------------------------------------------------------------
// Notifiche in-app (richiesta utente: avviso quando un download finisce) — poller leggero che
// gira per TUTTA la sessione (avviato una volta in init(), non legato a nessuna view), confronta
// lo stato di ogni elemento della coda con l'ultimo visto e mostra un toast solo sulla transizione
// verso Done/Failed — mai al primo avvistamento di un elemento già concluso (es. app appena
// riaperta con la coda del server ancora popolata da prima), altrimenti si spammerebbero notifiche
// per download già annunciati in una sessione precedente.
// ---------------------------------------------------------------

const downloadNotifyState = new Map();
let downloadNotifyTimer = null;

function startDownloadNotifications() {
  if (downloadNotifyTimer) return;
  checkDownloadNotifications();
  downloadNotifyTimer = setInterval(checkDownloadNotifications, 8000);
}

async function checkDownloadNotifications() {
  try {
    const res = await fetch(`${apiBase()}/api/tv/downloads`);
    if (!res.ok) return;
    const items = await res.json();
    updateHomeDownloadButton(items);

    const seenIds = new Set();
    items.forEach(item => {
      seenIds.add(item.id);
      const prev = downloadNotifyState.get(item.id);
      if (prev !== item.status) {
        downloadNotifyState.set(item.id, item.status);
        if (prev && (item.status === 'Done' || item.status === 'Failed')) {
          showDownloadToast(item, item.status === 'Done');
        }
      }
    });
    // Elementi rimossi dalla coda (rimossi manualmente dalla sezione Download): non servono più
    // in memoria, altrimenti la mappa crescerebbe indefinitamente in una sessione lunga.
    Array.from(downloadNotifyState.keys()).forEach(id => { if (!seenIds.has(id)) downloadNotifyState.delete(id); });
  } catch { /* best-effort: un poll fallito non deve interrompere i successivi */ }
}

// Progresso visibile sul pulsante "Download" della rail a icone (richiesta utente: vederlo anche
// senza entrare nella sezione dedicata) — riusa i dati già scaricati da
// checkDownloadNotifications, zero chiamate di rete aggiuntive. Aggiorna TUTTE le copie della rail
// (initIconRails ne clona una per view, non solo su Home): niente più id univoco da quando la rail
// è diventata globale, si aggancia a data-rail-action come il resto della navigazione.
// Il riempimento va sulla sola icona (.rail-ic), non su tutto il bottone: a riposo la rail è
// larga 88px con la sola icona visibile, un gradiente sul bottone intero non si vedrebbe finché
// non si espande — così il progresso resta leggibile anche collassata. Non usa più
// btn.textContent: sovrascriverebbe gli span icona/etichetta interni ad ogni poll (bug reale,
// causava un testo semplice "⬇️ Download" sempre visibile anche a rail collassata, invece
// dell'etichetta nascosta come le altre voci).
function updateHomeDownloadButton(items) {
  const pending = (items || []).filter(i => i.status === 'Queued' || i.status === 'Downloading');
  const active = pending.filter(hasRealDownloadProgress);

  let label, pct = null;
  if (active.length > 0) {
    pct = Math.max(0, Math.min(100, active[0].percent || 0));
    const extra = pending.length > 1 ? ` (+${pending.length - 1})` : '';
    label = `${pct.toFixed(0)}%${extra}`;
  } else if (pending.length > 0) {
    label = 'Verifica…'; // in coda o in fase di risoluzione del magnet, nessun byte ancora trasferito
  } else {
    label = 'Download';
  }

  document.querySelectorAll('.rail-item[data-rail-action="downloads"]').forEach(btn => {
    const lbl = btn.querySelector('.rail-label');
    const icon = btn.querySelector('.rail-ic');
    if (lbl) lbl.textContent = label;
    if (icon) icon.style.background = pct != null ? `linear-gradient(90deg, var(--accent) ${pct}%, transparent ${pct}%)` : '';
  });
}

// Notifica arricchita (richiesta utente): titolo pulito + poster da TMDB invece del nome file
// grezzo del torrent, quando l'elemento porta un tmdbId (sempre vero per i download avviati dalla
// card episodio/film, vedi enqueueDownload) — ricade sul titolo grezzo se il recupero fallisce o
// se l'elemento non ha un tmdbId (es. accodato in altro modo).
async function showDownloadToast(item, ok) {
  let title = item.title;
  let posterUrl = null;
  if (item.tmdbId) {
    try {
      const mediaType = item.season != null ? 'tv' : 'movie';
      const res = await fetch(`${apiBase()}/api/tv/search/detail?tmdbId=${item.tmdbId}&mediaType=${mediaType}`);
      if (res.ok) {
        const detail = await res.json();
        if (detail.title) title = detail.title;
        posterUrl = detail.posterUrl || null;
      }
    } catch { /* best-effort: resta il titolo grezzo già impostato sopra */ }
  }
  const episodeLabel = item.season != null && item.episode != null
    ? ` · S${String(item.season).padStart(2, '0')}E${String(item.episode).padStart(2, '0')}`
    : '';
  showToast({
    title: `${title}${episodeLabel}`,
    subtitle: ok ? '✅ Download completato' : '❌ Download fallito',
    posterUrl,
    isError: !ok
  });
}

function showToast({ title, subtitle, posterUrl, isError }) {
  const container = document.getElementById('toast-container');
  const toast = document.createElement('div');
  toast.className = 'toast' + (isError ? ' toast-error' : '');
  toast.innerHTML = `
    ${posterUrl ? `<img class="toast-poster" src="${posterUrl}" alt="">` : `<div class="toast-icon">${isError ? '❌' : '⬇️'}</div>`}
    <div class="toast-text">
      <div class="toast-title">${escapeHtml(title)}</div>
      <div class="toast-subtitle">${escapeHtml(subtitle)}</div>
    </div>`;
  container.appendChild(toast);
  setTimeout(() => {
    toast.classList.add('toast-out');
    setTimeout(() => toast.remove(), 320);
  }, 7000);
}

// ---------------------------------------------------------------
// Barra di progresso nella pagina di dettaglio (richiesta utente) — mostra i download attivi
// legati al tmdbId di QUESTO titolo, non solo nella sezione "⬇️ Download" dedicata. Poll separato
// da quello delle notifiche: qui serve solo mentre la pagina di dettaglio è aperta.
// ---------------------------------------------------------------

let detailDownloadsTimer = null;

function stopDetailDownloadsPolling() {
  clearInterval(detailDownloadsTimer);
  detailDownloadsTimer = null;
}

function startDetailDownloadsPolling(tmdbId) {
  stopDetailDownloadsPolling();
  const poll = () => loadDetailDownloads(tmdbId);
  poll();
  detailDownloadsTimer = setInterval(poll, 2000);
}

async function loadDetailDownloads(tmdbId) {
  const container = document.getElementById('detail-downloads');
  try {
    const res = await fetch(`${apiBase()}/api/tv/downloads?tmdbId=${tmdbId}`);
    if (!res.ok) return;
    const items = await res.json();
    const active = items.filter(i => i.status === 'Queued' || i.status === 'Downloading');
    if (active.length === 0) { container.innerHTML = ''; return; }

    container.innerHTML = active.map(item => {
      const pct = Math.max(4, Math.min(100, item.percent || 0));
      const label = item.season != null && item.episode != null
        ? `S${String(item.season).padStart(2, '0')}E${String(item.episode).padStart(2, '0')} — `
        : '';
      const downloading = hasRealDownloadProgress(item);
      const detail = downloading
        ? `${pct.toFixed(0)}% · ${escapeHtml(item.speed || '')} · ETA ${escapeHtml(item.eta || '--')}`
        : escapeHtml(item.message || '🔍 Verifica disponibilità…');
      return `<div class="detail-download-item">
        <div class="tile-title">⬇️ ${label}${escapeHtml(item.title)}</div>
        <div class="tile-sub">${detail}</div>
        <div class="dl-progress"><div class="dl-progress-fill" style="width:${downloading ? pct : 4}%"></div></div>
      </div>`;
    }).join('');
  } catch { /* best-effort */ }
}

// "result" è una riga della lista versioni (title/magnet/siteName + il provider scelto per quella
// riga, vedi renderVersionRows) — MAI una scelta automatica: richiesta utente esplicita, l'utente
// deve vedere qualità/lingua/dimensione prima di decidere cosa scaricare, non ritrovarsi il primo
// risultato preso in automatico (bug segnalato: succedeva con il vecchio pulsante sulla riga
// episodio, che usava sempre "il migliore" senza possibilità di scelta).
// "📚 Acquisisci in libreria": a differenza di enqueueDownload (fire-and-forget sulla coda), qui
// serve il magnetId — prepara il magnet su Premiumize (stesso /prepare usato da selectResult,
// attesa sincrona: un'acquisizione non è un trasferimento di file da minuti come un download
// completo, /prepare ha già aspettato che il transfer sia "finished") e poi registra il tracking.
// Nessun sectionId inviato: il server usa il default configurato in Impostazioni sul PC
// (LibrarySettings.DefaultMovieSectionId/DefaultTvSectionId, webOS non ha un selettore libreria).
async function acquireToLibrary(result, toTv, btnEl) {
  const original = btnEl.textContent;
  btnEl.disabled = true;
  btnEl.textContent = '⏳';
  try {
    const prepRes = await fetch(`${apiBase()}/api/tv/prepare`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title: result.title, magnet: result.magnet, siteName: result.siteName, provider: 'premiumize' })
    });
    const prepData = await prepRes.json();
    if (!prepRes.ok) throw new Error(prepData.error || ('HTTP ' + prepRes.status));

    const tmdbId = currentMediaContext?.tmdbId;
    if (!tmdbId) throw new Error('Titolo non riconosciuto (tmdbId mancante).');

    const acqRes = await fetch(`${apiBase()}/api/tv/acquire`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ magnetId: prepData.magnetId, title: result.title, toTv, tmdbId })
    });
    const acqData = await acqRes.json();
    if (!acqRes.ok) throw new Error(acqData.error || ('HTTP ' + acqRes.status));

    btnEl.textContent = '✅';
  } catch (err) {
    btnEl.textContent = original;
    alert('Errore acquisendo in libreria: ' + err.message);
  } finally {
    btnEl.disabled = false;
  }
}

async function enqueueDownload(result, toTv, btnEl) {
  const original = btnEl.textContent;
  btnEl.disabled = true;
  btnEl.textContent = '⏳ Avvio…';
  try {
    const provider = result.provider || normalizeProvider(getConfig().preferredProvider);
    const res = await fetch(`${apiBase()}/api/tv/download`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        title: result.title, magnet: result.magnet, siteName: result.siteName, toTv, provider,
        // Se disponibile (arriva sempre da una ricerca già legata a un titolo), permette alla
        // pagina di dettaglio di mostrare la barra di progresso filtrando su questo tmdbId.
        tmdbId: currentMediaContext?.tmdbId, season: currentMediaContext?.season, episode: currentMediaContext?.episode
      })
    });
    const data = await res.json();
    if (!res.ok) throw new Error(data.error || ('HTTP ' + res.status));
    btnEl.textContent = '✅ In download';
  } catch (err) {
    btnEl.textContent = original;
    alert('Errore avviando il download: ' + err.message);
  } finally {
    btnEl.disabled = false;
  }
}

// Pulsante Watchlist (elemento statico in index.html, riusato ad ogni apertura della pagina di
// dettaglio) — .onclick invece di addEventListener: il bottone è nel DOM una volta sola e viene
// riconfigurato ad ogni titolo, con addEventListener i gestori si accumulerebbero visita dopo
// visita invece di essere sostituiti.
async function setupWatchlistButton(candidate) {
  const btn = document.getElementById('detail-watchlist-btn');
  let saved = false;
  try {
    const res = await fetch(`${apiBase()}/api/tv/watchlist/check?tmdbId=${candidate.tmdbId}&mediaType=${candidate.mediaType}`);
    const data = await res.json();
    saved = !!data.saved;
  } catch { /* in dubbio: mostra "aggiungi", l'utente può comunque provare a togglare */ }

  updateWatchlistButtonLabel(btn, saved);
  btn.onclick = async () => {
    try {
      if (saved) {
        await fetch(`${apiBase()}/api/tv/watchlist?tmdbId=${candidate.tmdbId}&mediaType=${candidate.mediaType}`, { method: 'DELETE' });
      } else {
        await fetch(`${apiBase()}/api/tv/watchlist`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ tmdbId: candidate.tmdbId, mediaType: candidate.mediaType, title: candidate.title, year: candidate.year })
        });
      }
      saved = !saved;
      updateWatchlistButtonLabel(btn, saved);
    } catch { /* best-effort: il pulsante resta nello stato precedente se la chiamata fallisce */ }
  };
}

function updateWatchlistButtonLabel(btn, saved) {
  btn.textContent = saved ? '✓ Nella Watchlist' : '☆ Aggiungi alla Watchlist';
  btn.classList.toggle('tile-primary', saved);
}

async function renderDetail(detail, candidate, watchedEpisodes) {
  document.getElementById('detail-status').textContent = '';
  document.getElementById('detail-body').classList.remove('hidden');

  currentDetailBackdrop = detail.backdropUrl || detail.posterUrl || candidate.posterUrl || null;
  document.getElementById('detail-backdrop').src = currentDetailBackdrop || '';
  document.getElementById('detail-poster').src = detail.posterUrl || candidate.posterUrl || '';

  const metaParts = [];
  const year = detail.year || candidate.year;
  if (year) metaParts.push(`<span>${escapeHtml(year)}</span>`);
  // Durata (film: durata totale; serie: durata di un episodio) — richiesta utente, presente nella
  // schermata di riferimento (Stremio) accanto ad anno/voto.
  if (detail.runtimeMinutes) metaParts.push(`<span>${detail.runtimeMinutes} min</span>`);
  if (detail.voteAverage) metaParts.push(`<span class="detail-vote">⭐ ${detail.voteAverage.toFixed(1)}</span>`);
  // Voto IMDb vero (OMDb, opzionale — vedi TvApiEndpoints), distinto dal voto TMDB sopra: badge
  // separato invece di sostituire/mediare i due, sono scale di voto diverse con basi utenti diverse.
  if (detail.imdbRating) metaParts.push(`<span class="detail-imdb">IMDb ${detail.imdbRating.toFixed(1)}</span>`);
  (detail.genres || []).forEach(g => metaParts.push(`<span class="detail-genre">${escapeHtml(g)}</span>`));
  document.getElementById('detail-meta').innerHTML = metaParts.join('');

  document.getElementById('detail-overview').textContent =
    detail.overview || candidate.overview || 'Nessuna sinossi disponibile.';

  // Cast (richiesta utente, presente nella schermata di riferimento) — riga di nomi separata dai
  // generi: i generi sono filtri concettuali (pillole colorate), il cast è testo informativo.
  // detail.cast è ora un array di {name, photoUrl} (piano-webos-skip-marker-plex.md, Fascia 3,
  // punto 7 — prima solo stringhe): qui si usa ancora solo il nome, photoUrl serve al pannello
  // informazioni episodio del punto 8.
  const castEl = document.getElementById('detail-cast');
  if (detail.cast && detail.cast.length > 0) {
    castEl.textContent = detail.cast.map(c => c.name).join(' · ');
    castEl.classList.remove('hidden');
  } else {
    castEl.classList.add('hidden');
  }

  setupWatchlistButton(candidate);

  const actionEl = document.getElementById('detail-action');
  actionEl.innerHTML = '';

  if (candidate.mediaType === 'movie') {
    const title = `${candidate.title}${candidate.year ? ' (' + candidate.year + ')' : ''}`;
    // Richiesta utente (2026-09-13): controllato PRIMA di decidere l'azione principale, non dopo
    // — passando dalla libreria Plex il file è già sul disco, il pulsante deve riprodurlo
    // direttamente invece di rimandare sempre a una ricerca/sblocco torrent.
    const local = await fetchLocalStatus('movie', candidate.title, null, null);
    const btn = document.createElement('button');
    btn.className = 'tile tile-primary';
    btn.tabIndex = 0;
    if (local && local.hasLocalFile) {
      btn.textContent = '💾 Guarda da disco';
      btn.addEventListener('click', () => openLocalPlayer(local.localFilePath, title, { tmdbId: candidate.tmdbId, mediaType: 'movie', season: null, episode: null, title: candidate.title }));
      actionEl.appendChild(btn);
      // Richiesta utente (2026-09-16): il file locale non deve essere l'unica opzione — es. per
      // cercare una versione con audio/sottotitoli diversi, o qualità migliore, senza dover uscire
      // dalla scheda e rifare la ricerca testuale da capo. Riusa la stessa openMovieResults() del
      // ramo "non locale" sotto, nessuna nuova ricerca da scrivere.
      const searchBtn = document.createElement('button');
      searchBtn.className = 'tile tile-small';
      searchBtn.style.marginTop = '12px';
      searchBtn.tabIndex = 0;
      searchBtn.textContent = '🔎 Cerca comunque online';
      searchBtn.addEventListener('click', () => openMovieResults({ tmdbId: candidate.tmdbId, mediaType: 'movie' }, title, candidate.title));
      actionEl.appendChild(searchBtn);
    } else {
      btn.textContent = '▶️ Guarda';
      btn.addEventListener('click', () => openMovieResults({ tmdbId: candidate.tmdbId, mediaType: 'movie' }, title, candidate.title));
      actionEl.appendChild(btn);
      if (local && local.inPlexLibrary) {
        const badge = document.createElement('span');
        badge.className = 'hint';
        badge.textContent = '✅ Già in libreria Plex';
        actionEl.appendChild(badge);
      }
    }
  } else {
    const seasons = detail.seasons || [];
    const nextEpisode = computeNextEpisode(seasons, watchedEpisodes);
    if (nextEpisode) {
      const nextBtn = document.createElement('button');
      nextBtn.className = 'tile tile-primary';
      nextBtn.tabIndex = 0;
      nextBtn.style.marginBottom = '24px';
      nextBtn.textContent = `▶️ Prossimo episodio — S${nextEpisode.season}E${nextEpisode.episode}`;
      actionEl.appendChild(nextBtn);

      // Stesso principio del ramo film sopra: da disco se già posseduto, mai una ricerca torrent
      // per un episodio che è già lì.
      const nextLocal = await fetchLocalStatus('tv', candidate.title, nextEpisode.season, nextEpisode.episode);
      const isLocal = nextLocal && nextLocal.hasLocalFile;
      const icon = isLocal ? '💾' : '▶️';
      if (isLocal) {
        nextBtn.textContent = `💾 Prossimo episodio — S${nextEpisode.season}E${nextEpisode.episode} (da disco)`;
        nextBtn.addEventListener('click', () => openLocalPlayer(nextLocal.localFilePath, `${candidate.title} — S${nextEpisode.season}E${nextEpisode.episode}`, { tmdbId: candidate.tmdbId, mediaType: 'tv', season: nextEpisode.season, episode: nextEpisode.episode, title: candidate.title }));
        // Stesso principio del ramo film sopra: il file locale non deve essere l'unica opzione.
        const searchNextBtn = document.createElement('button');
        searchNextBtn.className = 'tile tile-small';
        searchNextBtn.style.marginBottom = '24px';
        searchNextBtn.tabIndex = 0;
        searchNextBtn.textContent = '🔎 Cerca comunque online';
        searchNextBtn.addEventListener('click', () => continueToEpisode(candidate, nextEpisode.season, nextEpisode.episode));
        actionEl.appendChild(searchNextBtn);
      } else {
        nextBtn.addEventListener('click', () => continueToEpisode(candidate, nextEpisode.season, nextEpisode.episode));
      }
      // Titolo vero aggiunto non appena disponibile (non blocca la resa del pulsante/pagina):
      // il fallback "S{n}E{n}" sopra resta finché la chiamata a TMDB non risponde.
      fetchEpisodeTitle(candidate.tmdbId, nextEpisode.season, nextEpisode.episode).then(name => {
        if (name) nextBtn.textContent = `${icon} Prossimo: S${nextEpisode.season}E${nextEpisode.episode} · ${name}${isLocal ? ' (da disco)' : ''}`;
      });
    }
    if (seasons.length === 0) {
      actionEl.insertAdjacentHTML('beforeend', '<p class="hint">Nessuna stagione trovata su TMDB per questo titolo.</p>');
    } else {
      const rail = document.createElement('div');
      rail.className = 'rail';
      seasons.forEach(s => {
        const sBtn = document.createElement('button');
        sBtn.className = 'tile';
        sBtn.tabIndex = 0;
        sBtn.innerHTML = `<div class="tile-title">${escapeHtml(s.name)}</div>
                           <div class="tile-sub">${s.episodeCount} episodi</div>`;
        sBtn.addEventListener('click', () => openEpisodeList(
          { tmdbId: candidate.tmdbId, mediaType: 'tv', season: s.seasonNumber, episodeCount: s.episodeCount },
          `${candidate.title} — ${s.name}`,
          candidate.title
        ));
        rail.appendChild(sBtn);
      });
      actionEl.appendChild(rail);
    }
  }

  // Fix focus (stesso bug pattern di search-results/files, vedi focusFirstAfterRender): il primo
  // elemento utile è il pulsante "Guarda" o la prima stagione, mai "← Indietro".
  focusFirstAfterRender('search-detail', actionEl);
}

// Solo il fetch, riusato sia dal percorso film (diretto) sia dalla lista episodi (tutte le
// versioni di TUTTI gli episodi della stagione arrivano in un colpo solo, già raggruppate poi
// per episodio lato client — stesso comportamento di prima, cambia solo la resa grafica).
async function fetchTorrentResults(params) {
  const query = new URLSearchParams(
    Object.fromEntries(Object.entries(params).filter(([, v]) => v !== undefined && v !== null))
  );
  const res = await fetch(`${apiBase()}/api/tv/search/results?${query.toString()}`);
  const results = await res.json();
  if (!res.ok) throw new Error(results.error || ('HTTP ' + res.status));
  return results;
}

// L'italiano viene sempre prima (poi MULTI, che di solito lo include; poi i sottotitoli, poi il
// resto), a parità di lingua vince chi pesa di più in GB — di norma il segnale più semplice di
// qualità superiore per uno stesso episodio (richiesta esplicita dell'utente).
const LANGUAGE_RANK = { 'ITA': 0, 'MULTI': 1, 'SUB ITA': 2, 'ENG': 3 };

function bestFirst(a, b) {
  const langDiff = (LANGUAGE_RANK[a.language] ?? 9) - (LANGUAGE_RANK[b.language] ?? 9);
  if (langDiff !== 0) return langDiff;
  return (b.sizeBytes || 0) - (a.sizeBytes || 0);
}

// ---------------------------------------------------------------
// Lista episodi (solo serie) — sostituisce i vecchi rail orizzontali "un episodio = una riga
// scorrevole di tile": con stagioni lunghe (10-24 episodi) la pagina diventava comunque
// chilometrica da scorrere, ed erano proprio i nomi file lunghi a troncare male nei tile a
// larghezza fissa (bug segnalato dall'utente). Qui ogni episodio è UNA riga compatta a piena
// larghezza con la versione migliore già in badge; le altre versioni si vedono solo aprendo
// view-version-list per quel singolo episodio, non tutte insieme.
// ---------------------------------------------------------------

async function openEpisodeList(params, title, cleanTitle) {
  pushView('search-results');
  document.getElementById('results-title').textContent = title;
  document.getElementById('results-backdrop').src = currentDetailBackdrop || '';
  const statusEl = document.getElementById('results-status');
  const grid = document.getElementById('results-grid');
  statusEl.textContent = '🔎 Cerco su Torrentio…';
  grid.innerHTML = '';
  try {
    const [results, watchedEpisodes, ownedEpisodes, episodeTitles] = await Promise.all([
      fetchTorrentResults(params),
      fetchWatchedEpisodes(params.tmdbId),
      fetchOwnedEpisodes(cleanTitle || title),
      fetchSeasonEpisodeTitles(params.tmdbId, params.season)
    ]);
    statusEl.textContent = results.length === 0 ? `❌ Nessun risultato trovato per "${title}".` : '';
    renderEpisodeList(results, title, params.tmdbId, params.season, watchedEpisodes, cleanTitle, ownedEpisodes, episodeTitles);
  } catch (err) {
    statusEl.textContent = '❌ Errore: ' + err.message;
  }
}

// Episodi già presenti su Plex per questa serie (badge "💾", vedi checkLocalFileForVersionList per
// lo streaming diretto da disco quando si apre la versione di un episodio specifico) — stesso
// principio bulk di fetchWatchedEpisodes, fallisce in silenzio: è un arricchimento.
async function fetchOwnedEpisodes(seriesTitle) {
  try {
    const res = await fetch(`${apiBase()}/api/tv/library/episodes?title=${encodeURIComponent(seriesTitle)}`);
    if (!res.ok) return [];
    return await res.json();
  } catch {
    return [];
  }
}

// Cronologia di visione per una serie (Fase 3) — usata sia qui per il badge "✓ Visto" sia nella
// pagina di dettaglio per calcolare il "prossimo episodio". Fallisce in silenzio (torna lista
// vuota): è un arricchimento, non deve bloccare la lista episodi se il backend non risponde.
async function fetchWatchedEpisodes(tmdbId) {
  try {
    const res = await fetch(`${apiBase()}/api/tv/history/show?tmdbId=${tmdbId}`);
    if (!res.ok) return [];
    return await res.json();
  } catch {
    return [];
  }
}

function renderEpisodeList(results, seriesTitle, tmdbId, season, watchedEpisodes, cleanTitle, ownedEpisodes, episodeTitles) {
  const grid = document.getElementById('results-grid');
  grid.innerHTML = '';

  const byEpisode = new Map();
  results.forEach(r => {
    const key = r.episode ?? 0;
    if (!byEpisode.has(key)) byEpisode.set(key, []);
    byEpisode.get(key).push(r);
  });

  const ownedSet = new Set((ownedEpisodes || []).map(o => `${o.season}-${o.episode}`));

  // Episodio 0 = "Altro" (nessun numero riconosciuto nel titolo): in fondo, dopo i veri episodi
  // in ordine crescente, non prima — altrimenti la sezione più interessante scorre via per ultima.
  Array.from(byEpisode.keys()).sort((a, b) => (a || Infinity) - (b || Infinity)).forEach(ep => {
    const group = byEpisode.get(ep).slice().sort(bestFirst);
    const best = group[0];
    // Titolo vero da TMDB (richiesta utente: non solo "Episodio N") — ricade sulla sola sigla se
    // TMDB non ha ancora il titolo per questo episodio (stagione appena uscita, ecc.).
    const episodeName = (episodeTitles || []).find(e => e.episodeNumber === ep)?.name;
    const epLabel = ep > 0 ? `E${ep}${episodeName ? ` · ${episodeName}` : ''}` : 'Altro';
    const watched = (watchedEpisodes || []).some(w => w.season === season && w.episode === ep && w.completed);
    const owned = ownedSet.has(`${season}-${ep}`);

    const btn = document.createElement('button');
    btn.className = 'tile tile-row';
    btn.tabIndex = 0;
    btn.innerHTML = `
      <div>
        <div class="tile-title">${watched ? '✓ ' : ''}${epLabel}</div>
        <div class="tile-sub">${group.length} version${group.length === 1 ? 'e' : 'i'}</div>
      </div>
      <div class="result-badges">
        <span class="${owned ? 'badge-owned' : 'badge-missing'}">${owned ? '💾 In libreria' : '☁️ Non scaricato'}</span>
        <span>${escapeHtml(best.resolution)}</span>
        <span>${escapeHtml(best.language)}</span>
        <span>${escapeHtml(best.sizeText || '?')}</span>
      </div>`;
    // Richiesta utente (2026-09-13): se l'episodio è già in libreria ("💾 In libreria" sopra), il
    // tap deve riprodurlo subito da disco, mai aprire la lista versioni per (ri)scaricarlo.
    btn.addEventListener('click', async () => {
      if (owned) {
        const local = await fetchLocalStatus('tv', cleanTitle || seriesTitle, season, ep);
        if (local && local.hasLocalFile) {
          openLocalPlayer(local.localFilePath, `${seriesTitle} · ${epLabel}`, { tmdbId, mediaType: 'tv', season, episode: ep, title: cleanTitle || seriesTitle });
          return;
        }
      }
      openEpisodeVersions(group, `${seriesTitle} · ${epLabel}`, tmdbId, season, ep, cleanTitle);
    });
    grid.appendChild(btn);
  });

  setupMissingEpisodesButton(byEpisode, ownedSet, tmdbId, season, seriesTitle);
  focusFirstAfterRender('search-results', grid);
}

// Confronto Torrentio/libreria Plex (richiesta utente): episodi trovati su Torrentio ma non ancora
// su Plex. Il pulsante scarica, per ciascuno, il primo risultato che rispetta le preferenze di
// qualità/lingua già impostate dall'utente (stessa scelta di pickPreferredResult usata per
// l'autoplay del prossimo episodio — con lo stesso fallback "il migliore disponibile" se nessun
// risultato rispetta esattamente la preferenza).
function setupMissingEpisodesButton(byEpisode, ownedSet, tmdbId, season, seriesTitle) {
  const btn = document.getElementById('results-download-missing');
  const missing = Array.from(byEpisode.keys()).filter(ep => ep > 0 && !ownedSet.has(`${season}-${ep}`));

  if (missing.length === 0) {
    btn.classList.add('hidden');
    btn.onclick = null;
    return;
  }

  btn.classList.remove('hidden');
  btn.textContent = `⬇️ Episodi mancanti (${missing.length})`;
  btn.onclick = () => downloadMissingEpisodes(byEpisode, missing, tmdbId, season, seriesTitle, btn);
}

async function downloadMissingEpisodes(byEpisode, missing, tmdbId, season, seriesTitle, btnEl) {
  const original = btnEl.textContent;
  btnEl.disabled = true;
  const provider = normalizeProvider(getConfig().preferredProvider);

  let started = 0;
  for (const ep of missing) {
    btnEl.textContent = `⏳ ${started + 1}/${missing.length}…`;
    const pick = pickPreferredResult(byEpisode.get(ep));
    try {
      const res = await fetch(`${apiBase()}/api/tv/download`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          title: pick.title, magnet: pick.magnet, siteName: pick.siteName, toTv: true, provider,
          tmdbId, season, episode: ep
        })
      });
      if (res.ok) started++;
    } catch { /* prosegue con gli altri episodi anche se uno fallisce */ }
  }

  btnEl.textContent = `✅ ${started}/${missing.length} avviati`;
  setTimeout(() => {
    btnEl.textContent = original;
    btnEl.disabled = false;
  }, 4000);
}

// ---------------------------------------------------------------
// Lista versioni (film diretto, o dopo aver scelto un episodio) — righe verticali a piena
// larghezza con filtri Qualità/Lingua applicati sui dati già scaricati (nessuna nuova chiamata:
// i campi resolution/language sono già nella risposta di /search/results).
// ---------------------------------------------------------------

let currentVersionResults = [];
let currentVersionTitle = '';
let versionFilters = { quality: 'all', language: 'all' };

async function openMovieResults(params, title, cleanTitle) {
  currentMediaContext = { tmdbId: params.tmdbId, mediaType: 'movie', season: null, episode: null, title };
  pushView('version-list');
  currentVersionTitle = title;
  document.getElementById('version-list-title').textContent = title;
  document.getElementById('version-list-backdrop').src = currentDetailBackdrop || '';
  document.getElementById('version-filter-quality').innerHTML = '';
  document.getElementById('version-filter-language').innerHTML = '';
  const statusEl = document.getElementById('version-list-status');
  const grid = document.getElementById('version-list-grid');
  statusEl.textContent = '🔎 Cerco su Torrentio…';
  grid.innerHTML = '';
  try {
    const results = await fetchTorrentResults(params);
    statusEl.textContent = results.length === 0 ? `❌ Nessun risultato trovato per "${title}".` : '';
    setupVersionList(results);
    checkLocalFileForVersionList('movie', cleanTitle || title, null, null, title);
  } catch (err) {
    statusEl.textContent = '❌ Errore: ' + err.message;
  }
}

// Nessun fetch: i risultati di questo episodio sono già in memoria (arrivati tutti insieme dalla
// ricerca per stagione in openEpisodeList) — aprire una versione non richiede una nuova chiamata.
function openEpisodeVersions(results, title, tmdbId, season, episode, cleanTitle) {
  // "title" qui è quello mostrato in cima alla pagina (es. "Lanterns — Stagione 1 · Episodio 1"),
  // "cleanTitle" è il nome nudo della serie (es. "Lanterns") — SOLO quest'ultimo va in
  // currentMediaContext, da cui reportProgress() legge cosa salvare nella cronologia. BUG REALE
  // trovato in produzione: usare il titolo composto qui faceva "impilare" le etichette ad ogni
  // resume da "Continua a guardare" (resumeFromHistory ne aggiunge un'altra sopra), es. "Lanterns
  // — Stagione 1 · Episodio 1 · S1E1 · S1E1 · Grandina in Nebraska" invece di solo "Lanterns".
  currentMediaContext = { tmdbId, mediaType: 'tv', season, episode, title: cleanTitle || title };
  pushView('version-list');
  currentVersionTitle = title;
  document.getElementById('version-list-title').textContent = title;
  document.getElementById('version-list-backdrop').src = currentDetailBackdrop || '';
  document.getElementById('version-list-status').textContent = '';
  setupVersionList(results);
  checkLocalFileForVersionList('tv', cleanTitle || title, season, episode, title);
}

// "Già presente sul disco" (richiesta utente): se Send2Plex ha già scaricato questo film/episodio
// in precedenza, offre uno streaming diretto dal file locale invece di dover ricercare/scaricare
// di nuovo un torrent — bypassa completamente sblocco debrid e rete esterna (vedi ResolveLocalPath
// in StreamingService.cs, provider "local"). Best-effort: un errore qui non deve bloccare la
// normale ricerca torrent già in corso.
async function checkLocalFileForVersionList(mediaType, cleanTitle, season, episode, displayTitle) {
  const data = await fetchLocalStatus(mediaType, cleanTitle, season, episode);
  if (!data || !data.hasLocalFile) return;

  const statusEl = document.getElementById('version-list-status');
  const btn = document.createElement('button');
  btn.className = 'tile tile-small tile-primary';
  btn.tabIndex = 0;
  btn.textContent = '💾 Guarda da disco (già scaricato)';
  // currentMediaContext qui è già quello giusto: impostato dal chiamante (openMovieResults/
  // openEpisodeVersions) prima di arrivare a checkLocalFileForVersionList, letto al click (non
  // catturato ora) perché resta lo stesso finché si è su questa pagina.
  btn.addEventListener('click', () => openLocalPlayer(data.localFilePath, displayTitle, currentMediaContext));
  statusEl.textContent = '';
  statusEl.appendChild(btn);
}

// Apre il player direttamente su un file già presente sul disco (provider "local", vedi
// StreamingService.ResolveLocalPath) — niente magnetId/sblocco debrid.
// mediaContext (tmdbId/mediaType/season/episode/title) va sempre passato dal chiamante, che lo ha
// già in mano in tutti i punti da cui si apre: PRIMA azzerava sempre currentMediaContext ("il file
// è già a posto così, niente cronologia") confondendo due cose diverse — non serve tracciare un
// DOWNLOAD (vero, il file c'è già), ma il PROGRESSO di visione va comunque salvato, altrimenti
// "Continua a guardare" non si popola mai per i titoli già posseduti (segnalato dall'utente
// 2026-09-14: "non ha senso non riprendere solo perché il file è locale").
function openLocalPlayer(path, title, mediaContext) {
  currentMediaContext = mediaContext || null;
  openPlayer({ name: title, link: path, size: 0, provider: 'local' }, title, 0);
}

// Toggle di pagina per la lista versioni (Punto UI ibrida, vedi piano-multi-provider-debrid.md):
// inizializzato dal default persistito, cambiabile per questa sola lista — resetta a sua volta
// l'eventuale scelta per-riga già fatta (icona per-risultato in renderVersionRows), stessa
// semplificazione già adottata da Search.razor per non dover tracciare "riga non ancora
// sovrascritta" su un'interfaccia guidata da telecomando.
let versionProvider = 'allDebrid';

function setupVersionList(results) {
  currentVersionResults = results;

  const qualityOrder = ['4K', '1080p', '720p', 'SD', 'Altro'];
  const qualities = qualityOrder.filter(q => results.some(r => r.resolution === q));
  const languageOrder = ['ITA', 'MULTI', 'SUB ITA', 'ENG'];
  const languages = languageOrder.filter(l => results.some(r => r.language === l));

  // Preferenze impostate in ⚙️ Impostazioni (schermata di setup): applicate come filtro di
  // partenza SOLO se quella qualità/lingua esiste davvero tra i risultati di questo film/episodio
  // — altrimenti si ricadrebbe silenziosamente su una lista vuota ("Nessuna versione con questi
  // filtri") invece di mostrare quello che c'è. Restano comunque modificabili subito dopo.
  const cfg = getConfig();
  versionFilters = {
    quality: (cfg.preferredQuality && qualities.includes(cfg.preferredQuality)) ? cfg.preferredQuality : 'all',
    language: (cfg.preferredLanguage && languages.includes(cfg.preferredLanguage)) ? cfg.preferredLanguage : 'all'
  };
  versionProvider = normalizeProvider(cfg.preferredProvider);
  results.forEach(r => { r.provider = versionProvider; });

  buildFilterChips(
    'version-filter-provider',
    [
      { value: 'allDebrid', label: 'AllDebrid', icon: 'assets/provider/alldebrid.png' },
      { value: 'realDebrid', label: 'Real-Debrid', icon: 'assets/provider/realdebrid.png' },
      { value: 'premiumize', label: 'Premiumize', icon: 'assets/provider/premiumizeme.png' }
    ],
    () => versionProvider,
    v => { versionProvider = v; currentVersionResults.forEach(r => { r.provider = v; }); applyVersionFilters(false); }
  );

  buildFilterChips(
    'version-filter-quality',
    [{ value: 'all', label: 'Tutte' }, ...qualities.map(q => ({ value: q, label: q }))],
    () => versionFilters.quality,
    v => { versionFilters.quality = v; applyVersionFilters(false); }
  );

  buildFilterChips(
    'version-filter-language',
    [{ value: 'all', label: 'Tutte le lingue' }, ...languages.map(l => ({ value: l, label: l }))],
    () => versionFilters.language,
    v => { versionFilters.language = v; applyVersionFilters(false); }
  );

  applyVersionFilters(true);
}

// Costruisce i chip UNA sola volta e in seguito aggiorna solo le classi (mai innerHTML) sul click:
// ricreare i bottoni ad ogni tocco perderebbe il focus reale appena impostato su quello premuto,
// costringendo l'utente a ripartire dall'alto della vista per continuare a navigare a frecce.
function buildFilterChips(containerId, options, getActive, onSelect) {
  const container = document.getElementById(containerId);
  container.innerHTML = '';
  options.forEach((opt, i) => {
    const chip = document.createElement('button');
    chip.className = 'tile tile-small' + (getActive() === opt.value ? ' tile-primary' : '');
    chip.tabIndex = 0;
    chip.innerHTML = opt.icon ? `<img class="chip-icon" src="${opt.icon}" alt="">${escapeHtml(opt.label)}` : escapeHtml(opt.label);
    chip.addEventListener('click', () => {
      Array.from(container.children).forEach((c, j) => c.classList.toggle('tile-primary', options[j].value === opt.value));
      onSelect(opt.value);
    });
    container.appendChild(chip);
  });
}

function applyVersionFilters(moveFocus) {
  const filtered = currentVersionResults.filter(r =>
    (versionFilters.quality === 'all' || r.resolution === versionFilters.quality) &&
    (versionFilters.language === 'all' || r.language === versionFilters.language));
  renderVersionRows(filtered, moveFocus);
}

function renderVersionRows(results, moveFocus) {
  const grid = document.getElementById('version-list-grid');
  grid.innerHTML = '';

  if (results.length === 0) {
    grid.innerHTML = '<p class="hint">Nessuna versione con questi filtri.</p>';
    return;
  }

  results.slice().sort(bestFirst).forEach((r, i) => {
    const btn = document.createElement('button');
    btn.className = 'tile tile-row' + (i === 0 ? ' result-best' : '');
    btn.tabIndex = 0;
    btn.innerHTML = `
      <div class="tile-title">${i === 0 ? '★ ' : ''}${escapeHtml(r.title)}</div>
      <div class="result-badges">
        <span>${escapeHtml(r.resolution)}</span>
        <span>${escapeHtml(r.language)}</span>
        <span>${escapeHtml(r.sizeText || '?')}</span>
        <span>👤 ${escapeHtml(r.seedsText || '?')}</span>
      </div>`;
    btn.addEventListener('click', () => selectResult(r, currentVersionTitle));

    // Icona per-risultato (Punto UI ibrida): inizializzata dal toggle di pagina (r.provider
    // impostato in setupVersionList), sovrascrivibile per questo solo file prima di selezionarlo —
    // bottone SIBLING del tile principale (mai annidato: due <button> innestati non sono validi
    // HTML e confonderebbero la navigazione a frecce del telecomando).
    const providerBtn = document.createElement('button');
    providerBtn.className = 'tile tile-small result-provider-btn';
    providerBtn.tabIndex = 0;
    const renderProviderIcon = () => {
      providerBtn.innerHTML = `<img src="assets/provider/${providerIconFile(r.provider)}.png" alt="${providerLabel(r.provider)}">`;
    };
    renderProviderIcon();
    providerBtn.title = 'Provider per questo file (tocca per cambiare)';
    providerBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      r.provider = nextProvider(r.provider);
      renderProviderIcon();
    });

    // "Scarica su Plex" per QUESTO risultato specifico (richiesta utente: mai una scelta
    // automatica — qui l'utente vede già qualità/lingua/dimensione prima di decidere).
    const downloadBtn = document.createElement('button');
    downloadBtn.className = 'tile tile-small result-provider-btn';
    downloadBtn.tabIndex = 0;
    downloadBtn.textContent = '⬇️';
    downloadBtn.title = 'Scarica su Plex';
    downloadBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      enqueueDownload(r, currentMediaContext?.mediaType === 'tv', downloadBtn);
    });

    const wrap = document.createElement('div');
    wrap.className = 'result-row-wrap';
    wrap.appendChild(btn);
    wrap.appendChild(providerBtn);
    wrap.appendChild(downloadBtn);

    // "📚 Acquisisci in libreria" (docs/piano-premiumize-libreria.md, solo Premiumize): visibile
    // solo quando il provider scelto per QUESTA riga è Premiumize, stesso principio del pulsante
    // scarica ma senza passare dalla coda (registra solo il tracking, il file resta nel cloud).
    if (r.provider === 'premiumize') {
      const acquireBtn = document.createElement('button');
      acquireBtn.className = 'tile tile-small result-provider-btn';
      acquireBtn.tabIndex = 0;
      acquireBtn.textContent = '📚';
      acquireBtn.title = 'Acquisisci in libreria (Premiumize, senza occupare spazio sul disco)';
      acquireBtn.addEventListener('click', (e) => {
        e.stopPropagation();
        acquireToLibrary(r, currentMediaContext?.mediaType === 'tv', acquireBtn);
      });
      wrap.appendChild(acquireBtn);
    }

    grid.appendChild(wrap);
  });

  // Solo al primo ingresso nella vista (non ad ogni cambio filtro, altrimenti il focus scapperebbe
  // dal chip appena premuto verso il primo risultato, rendendo scomodo impostare più filtri di
  // seguito): la marcatura data-default-focus serve solo qui.
  if (moveFocus) focusFirstAfterRender('version-list', grid);
}

async function selectResult(result, title) {
  const provider = result.provider || 'allDebrid';
  const statusEl = document.getElementById('version-list-status');
  statusEl.textContent = `🧲 Preparo il magnet su ${providerLabel(provider)} (può richiedere qualche minuto)…`;
  try {
    const res = await fetch(`${apiBase()}/api/tv/prepare`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title: result.title, magnet: result.magnet, siteName: result.siteName, provider })
    });
    const data = await res.json();
    if (!res.ok) throw new Error(data.error || ('HTTP ' + res.status));
    statusEl.textContent = '';
    data.files.forEach(f => { f.provider = provider; });
    showFilesOrPlayer(data.files, title);
  } catch (err) {
    statusEl.textContent = '❌ Errore: ' + err.message;
  }
}

// ---------------------------------------------------------------
// Player — a differenza di Watch.razor (pagina web) qui si parte SEMPRE in trascodifica/MSE, mai
// in "copy" (remux diretto verso <video src>): il tentativo di "copy" prima passava comunque per
// un <video src> puntato a una risposta HTTP che cresce in tempo reale, che si è dimostrato
// inaffidabile sul motore datato di webOS 22 indipendentemente dal fatto che il video venisse
// ricodificato o solo remuxato — è esattamente il bug che ha motivato la costruzione di tutta la
// pipeline MSE. BUG REALE segnalato dall'utente: partiva sempre in "riproduzione diretta", dopo
// ~2s si bloccava e ripartiva in trascodifica — il primissimo tentativo era condannato a fallire
// ogni volta invece di andare subito al percorso (MSE) che funziona davvero. Il seek nativo non
// funziona su uno stream generato al volo: i pulsanti di salto ricaricano lo stream con un nuovo
// `start=`, esattamente come nella pagina web.
// ---------------------------------------------------------------

let currentFile = null;
let currentTitle = '';
// "copy" di default: prova sempre prima la riproduzione diretta senza trascodifica, esattamente
// come fa Moonfin (client Jellyfin per webOS) con la sua strategia "DirectPlay first, transcode
// fallback solo se necessario" — la stessa identica tecnologia di base (HTML5 <video> su Starfish,
// l'unico accesso possibile da un'app web, verificato: non esiste un'API nativa alternativa
// accessibile a app web di terze parti). Il fallback a "transcode" scatta da solo sull'evento
// "error" del player (recoverFromFailure, sotto) se il codec/profilo non è supportato in diretta —
// MA solo se allowAutoFallback è vero: l'utente può forzare "sempre diretta" o "sempre trascodifica"
// da Impostazioni o dal player stesso (pulsante "Modalità"), nel qual caso il fallback automatico
// resta disattivato e si rispetta sempre la scelta esplicita.
let mode = 'copy';
let allowAutoFallback = true;
// Vero quando la sessione corrente sta usando l'HLS nativo del player (Fase 4,
// piano-streaming-multiutente-hls.md) — la playlist servita dichiara già l'intero file da questo
// punto in poi, quindi un salto in avanti/indietro DENTRO questa sessione può restare sul seek
// nativo del player invece di un reload completo (vedi commitScrub). Impostato in loadStream(),
// resettato ad ogni nuovo file in openPlayer().
let usingNativeHls = false;
let baseSeconds = 0;
let reloadToken = 0;
// Motivo dell'ultimo/prossimo (ri)caricamento (2026-09-14) — impostato subito prima di ogni
// chiamata a loadStream() dal punto che la richiede, mandato al server in streamUrl() così finisce
// loggato sulla riga "nuova sessione" di logs/streams/*.log. Serve a distinguere nei log un salto
// utente reale (timeline) da un ricaricamento automatico (watchdog anti-stallo, errore player,
// cambio qualità/modalità/traccia audio) — prima erano indistinguibili dal solo timing.
let reloadReason = 'open';
let streamInfoCache = {};
let playerDuration = null; // durata totale del file (ffprobe, via stream-info) — null se sconosciuta

// Selezione traccia audio (restyle player 2026-09-14, idee-miglioramento-webos.md) — a differenza
// di qualità/modalità NON è una preferenza persistita (getConfig/saveConfig): le tracce disponibili
// e la loro lingua cambiano da file a file, non ha senso "ricordare la traccia 2" globalmente.
let currentAudioIndex = 0;
let currentAudioTracks = []; // popolato da loadAudioTracks(), in parallelo all'avvio della riproduzione

// Marker Plex sigla/titoli di coda (piano-webos-skip-marker-plex.md, Fascia 2) — popolati da
// loadPlexMarkers(), in parallelo a loadAudioTracks(). I due flag sono per-file (azzerati da
// openPlayer): skippedIntroForThisFile evita che la pillola "Salta sigla" riappaia un istante dopo
// il click (il seek potrebbe atterrare un filo prima di endSeconds, il tick successivo altrimenti
// rientrerebbe nel range); nextEpisodeTriggeredForThisFile evita che il trigger "prossimo episodio"
// scatti due volte sullo stesso file (una dal marker "credits", una da "ended" poco dopo).
let currentMarkers = { intro: null, credits: null };
let skippedIntroForThisFile = false;
let nextEpisodeTriggeredForThisFile = false;
// BUG REALE (segnalato dall'utente, 2026-09-14: "Salta sigla" compariva solo la prima volta) —
// loadPlexMarkers() non aveva alcuna protezione da race condition, a differenza di loadStream()
// (che usa loadSequence/thisLoad per lo stesso motivo): se openPlayer() viene richiamata di nuovo
// (episodio successivo, "Prossimo episodio" manuale, ecc.) prima che il fetch precedente sia
// tornato, la risposta VECCHIA poteva arrivare dopo quella nuova e sovrascrivere silenziosamente
// currentMarkers con i dati sbagliati (o null) per l'episodio ora in riproduzione — da quel
// momento "Salta sigla" restava permanentemente senza dati validi. Stesso pattern di
// loadSequence, ma un contatore a parte: i marker devono restare validi per tutta la durata di UN
// episodio, quindi non vanno invalidati da ogni loadStream() (un seek/salto NON cambia episodio).
let markersLoadSequence = 0;

// Dati per il pannello informazioni episodio in pausa (piano-webos-skip-marker-plex.md, Fascia 3,
// punto 8) — popolato da loadEpisodePanelDetail(), stesso identico oggetto già restituito da
// /api/tv/search/detail (sinossi/cast/poster, usato anche dalla pagina di dettaglio e da
// maybeShowNextEpisode). Nullo per i file aperti dalla Libreria (currentMediaContext è null lì).
let currentPanelDetail = null;

// Legge "playbackMode" (auto/direct/transcode) dalle preferenze e imposta mode/allowAutoFallback di
// conseguenza — chiamata all'apertura del player e ogni volta che l'utente cambia impostazione dal
// pulsante "Modalità" nel player stesso.
function applyPlaybackModeSetting() {
  const setting = getConfig().playbackMode || 'auto';
  if (setting === 'transcode') { mode = 'transcode'; allowAutoFallback = false; }
  else if (setting === 'direct') { mode = 'copy'; allowAutoFallback = false; }
  else { mode = 'copy'; allowAutoFallback = true; } // 'auto' (default)
}

function playbackModeLabel(setting) {
  return setting === 'direct' ? 'Sempre diretta' : setting === 'transcode' ? 'Sempre trascodifica' : 'Auto';
}

// Icona sorgente accanto al titolo (restyle 2026-09-14, idee-miglioramento-webos.md) — chiamata
// una sola volta per file da openPlayer (la sorgente non cambia durante la visione, a differenza
// di qualità/modalità/audio che si possono ciclare). "local" è un'emoji semplice (nessuna icona
// dedicata esiste per i file già su disco); i provider debrid riusano le stesse icone PNG già
// mostrate altrove nell'app (Libreria, tab Provider) via providerIconFile, per coerenza visiva.
function updatePlayerSourceBadge() {
  const badge = document.getElementById('player-source-badge');
  if (!badge || !currentFile) return;
  const provider = currentFile.provider || 'allDebrid';
  badge.innerHTML = provider === 'local'
    ? '💾'
    : `<img src="assets/provider/${providerIconFile(provider)}.png" alt="" />`;
}

// Aggiorna il testo dei due pulsanti nel player con i valori correnti — chiamata all'apertura del
// player e dopo ogni ciclo (cyclePlayerQuality/cyclePlayerMode).
function updatePlayerSettingButtons() {
  const cfg = getConfig();
  const qualityLabel = document.querySelector('#player-quality-btn .ctrl-label');
  const modeLabel = document.querySelector('#player-mode-btn .ctrl-label');
  if (qualityLabel) qualityLabel.textContent = `Qualità: ${(cfg.quality || 'alta').charAt(0).toUpperCase()}${(cfg.quality || 'alta').slice(1)}`;
  if (modeLabel) modeLabel.textContent = `Modalità: ${playbackModeLabel(cfg.playbackMode || 'auto')}`;
}

// Salva una preferenza cambiata dal player stesso (non da Impostazioni) sia in locale sia sul
// server, poi ricarica lo stream sulla stessa posizione con il nuovo valore — stesso schema già
// usato da setup-save, solo innescato da un pulsante nel player invece che dal form.
function savePlayerPreference(patch) {
  const cfg = { ...getConfig(), ...patch };
  saveConfig(cfg);
  if (cfg.host) savePreferencesToServer(cfg.host, { quality: cfg.quality, preferredQuality: cfg.preferredQuality, preferredLanguage: cfg.preferredLanguage, playbackMode: cfg.playbackMode });
  updatePlayerSettingButtons();
  streamInfoCache = {}; // la qualità/modalità è cambiata, la cache di stream-info per questo file non vale più
  applyPlaybackModeSetting();
  reloadToken++;
  reloadReason = 'settings-change';
  loadStream();
}

function cyclePlayerQuality() {
  const order = ['alta', 'media', 'bassa'];
  const current = getConfig().quality || 'alta';
  const next = order[(order.indexOf(current) + 1) % order.length];
  savePlayerPreference({ quality: next });
}

function cyclePlayerMode() {
  const order = ['auto', 'direct', 'transcode'];
  const current = getConfig().playbackMode || 'auto';
  const next = order[(order.indexOf(current) + 1) % order.length];
  savePlayerPreference({ playbackMode: next });
}

// Elenco tracce audio del file corrente (/api/tv/audio-tracks, restyle player 2026-09-14) —
// chiamata da openPlayer IN PARALLELO a loadStream(), mai prima: la riproduzione parte comunque
// sulla traccia 0 di default, il selettore compare da solo un attimo dopo se il file ne ha più di
// una. "file" catturato all'inizio invece di rileggere currentFile alla risposta: se nel frattempo
// l'utente ha già aperto un altro file (improbabile ma possibile, es. "Indietro" veloce seguito da
// un altro titolo), la risposta di QUESTA chiamata va scartata invece di sovrascrivere lo stato del
// file nuovo con le tracce di quello vecchio.
async function loadAudioTracks() {
  const file = currentFile;
  try {
    const provider = file.provider || 'allDebrid';
    const res = await fetch(`${apiBase()}/api/tv/audio-tracks?link=${encodeURIComponent(file.link)}&provider=${provider}`);
    const tracks = await res.json();
    if (file !== currentFile || !Array.isArray(tracks)) return;
    currentAudioTracks = tracks;
    updatePlayerAudioButton();
  } catch { /* best-effort: nessun selettore se il probe fallisce, la riproduzione non è coinvolta */ }
}

// Marker Plex sigla/titoli di coda (piano-webos-skip-marker-plex.md, Fascia 2) — chiamata da
// openPlayer IN PARALLELO a loadAudioTracks/loadStream, mai prima: la riproduzione parte comunque,
// le pillole (index.html, #player-corner-pills) compaiono da sole quando/se la finestra del marker
// si apre (vedi updateTimelineUI). Nessun risultato per i file aperti dalla Libreria
// (currentMediaContext è null lì, niente titolo/stagione/episodio da interrogare) o se l'utente ha
// disattivato i marker in Impostazioni — in entrambi i casi currentMarkers resta
// {intro:null,credits:null}, indistinguibile per il resto del codice da "Plex non ha ancora
// processato questo file": stesso comportamento, stesso fallback "+30s".
async function loadPlexMarkers() {
  const thisLoad = ++markersLoadSequence; // scarta una risposta arrivata dopo un openPlayer() più recente
  currentMarkers = { intro: null, credits: null };
  try {
    if (currentMediaContext && getConfig().plexMarkersEnabled !== false) {
      const { mediaType, title, season, episode } = currentMediaContext;
      const params = new URLSearchParams({ mediaType, title });
      if (season != null) params.set('season', season);
      if (episode != null) params.set('episode', episode);
      const res = await fetch(`${apiBase()}/api/tv/library/markers?${params.toString()}`);
      if (thisLoad !== markersLoadSequence) return; // episodio già cambiato di nuovo: risultato ormai stantio
      if (res.ok) currentMarkers = await res.json();
    }
  } catch { /* best-effort, come loadAudioTracks */ }
  if (thisLoad !== markersLoadSequence) return;
  updateSkipFallbackButton(); // SEMPRE eseguito, successo o fallimento: decide se mostrare "+30s"
}

// "+30s": visibile SOLO quando il marker "intro" non è disponibile per questo file — copre sia
// "niente Plex Pass" sia "Plex Pass ma file non ancora processato": in entrambi i casi la pillola
// "Salta sigla" (sopra la timeline) non può comparire, questo pulsante ne prende il posto come
// unico modo di saltare avanti a mano. Deciso una sola volta per file, non ad ogni tick.
function updateSkipFallbackButton() {
  const btn = document.getElementById('player-skip-btn');
  if (btn) btn.classList.toggle('hidden', !!currentMarkers.intro);
}

// Oggi chiamata solo dal pulsante di ripiego "+30s" — i vecchi pulsanti fissi ±10/±30s generici
// erano già stati sostituiti dalla timeline (scrubBy/commitScrub), questo è specifico per il salto
// sigla quando il marker Plex non è disponibile.
function skipForward() {
  const video = document.getElementById('player');
  video.currentTime = Math.min((video.currentTime || 0) + 30, video.duration || Infinity);
}

// ---------------------------------------------------------------
// Pannello informazioni episodio in pausa (piano-webos-skip-marker-plex.md, Fascia 3, punto 8,
// mockup approvato in docs/Design/Player.png) — richiesta utente: prefetch all'apertura del player
// (non al primo pause), mostra/nasconde sincrono con play/pausa senza debounce. Riusa
// /api/tv/search/detail (stesso endpoint già usato dalla pagina di dettaglio e da
// maybeShowNextEpisode), nessuna nuova rotta backend.
// ---------------------------------------------------------------

async function loadEpisodePanelDetail() {
  currentPanelDetail = null;
  if (currentMediaContext) {
    try {
      const { tmdbId, mediaType } = currentMediaContext;
      const res = await fetch(`${apiBase()}/api/tv/search/detail?tmdbId=${tmdbId}&mediaType=${mediaType}`);
      if (res.ok) currentPanelDetail = await res.json();
    } catch { /* best-effort, come loadAudioTracks/loadPlexMarkers */ }
  }
  updateEpisodePanelContent(); // nel caso il pannello sia già visibile (pausa scattata prima che il fetch tornasse)
}

function updateEpisodePanelContent() {
  const d = currentPanelDetail;
  const posterEl = document.getElementById('episode-panel-poster');
  const posterUrl = d?.posterUrl || d?.backdropUrl || '';
  posterEl.src = posterUrl;
  posterEl.classList.toggle('hidden', !posterUrl);

  document.getElementById('episode-panel-title').textContent = d?.title || currentTitle || '';

  const subtitleParts = [];
  if (currentMediaContext?.mediaType === 'tv' && currentMediaContext.season != null) {
    subtitleParts.push(`Stagione ${currentMediaContext.season}`, `Episodio ${currentMediaContext.episode}`);
  } else if (d?.year) {
    subtitleParts.push(d.year);
  }
  if (d?.runtimeMinutes) subtitleParts.push(`${d.runtimeMinutes} min`);
  document.getElementById('episode-panel-subtitle').textContent = subtitleParts.join(' · ');

  document.getElementById('episode-panel-overview').textContent = d?.overview || '';

  const castEl = document.getElementById('episode-panel-cast');
  castEl.innerHTML = '';
  (d?.cast || []).forEach(c => {
    const item = document.createElement('div');
    item.className = 'episode-panel-cast-item';
    item.innerHTML = (c.photoUrl ? `<img class="episode-panel-cast-photo" src="${c.photoUrl}" alt="" />` : '') +
      `<span>${escapeHtml(c.name)}</span>`;
    castEl.appendChild(item);
  });
}

const AUDIO_LANGUAGE_LABELS = {
  ita: 'Italiano', eng: 'Inglese', en: 'Inglese', it: 'Italiano', jpn: 'Giapponese', ja: 'Giapponese',
  fre: 'Francese', fra: 'Francese', fr: 'Francese', ger: 'Tedesco', deu: 'Tedesco', de: 'Tedesco',
  spa: 'Spagnolo', es: 'Spagnolo', por: 'Portoghese', pt: 'Portoghese', rus: 'Russo', ru: 'Russo',
  kor: 'Coreano', ko: 'Coreano', chi: 'Cinese', zho: 'Cinese', zh: 'Cinese'
};

function audioTrackLabel(track, index) {
  if (!track) return `Traccia ${index + 1}`;
  const lang = (track.language || '').toLowerCase();
  const langLabel = AUDIO_LANGUAGE_LABELS[lang] || track.language || `Traccia ${index + 1}`;
  return track.title ? `${langLabel} · ${track.title}` : langLabel;
}

// Nascosto di default (vedi index.html): compare solo quando il file ha davvero più di una
// traccia, altrimenti ciclarle non avrebbe alcun effetto — un pulsante sempre visibile che non fa
// nulla per la stragrande maggioranza dei file sarebbe solo rumore nell'overlay.
function updatePlayerAudioButton() {
  const btn = document.getElementById('player-audio-btn');
  if (!btn) return;
  if (currentAudioTracks.length < 2) { btn.classList.add('hidden'); return; }
  btn.classList.remove('hidden');
  const label = btn.querySelector('.ctrl-label');
  if (label) label.textContent = `Audio: ${audioTrackLabel(currentAudioTracks[currentAudioIndex], currentAudioIndex)}`;
}

// Non persistita (a differenza di qualità/modalità, vedi savePlayerPreference): la traccia scelta
// vale solo per QUESTA visione, azzerata al prossimo file da openPlayer.
function cyclePlayerAudioTrack() {
  if (currentAudioTracks.length < 2) return;
  currentAudioIndex = (currentAudioIndex + 1) % currentAudioTracks.length;
  updatePlayerAudioButton();
  reloadToken++;
  reloadReason = 'audio-change';
  loadStream();
}

// resumeSeconds: solo dal tile "Continua a guardare" in home, che conosce la posizione salvata —
// il percorso di ricerca normale riparte sempre da 0, anche per un titolo già in parte visto (per
// riprendere quello si passa da "Continua a guardare", non da una ricerca qualsiasi).
function openPlayer(file, title, resumeSeconds) {
  currentFile = file;
  currentTitle = title || file.name;
  applyPlaybackModeSetting(); // imposta mode/allowAutoFallback in base alla preferenza salvata
  baseSeconds = resumeSeconds || 0;
  reloadToken = 0;
  streamInfoCache = {};
  playerDuration = null;
  currentAudioIndex = 0;
  currentAudioTracks = [];
  currentMarkers = { intro: null, credits: null };
  skippedIntroForThisFile = false;
  nextEpisodeTriggeredForThisFile = false;
  currentPanelDetail = null;
  document.getElementById('skip-intro-pill').classList.add('hidden');
  document.getElementById('episode-info-panel').classList.remove('visible');
  hideNextEpisodePill();
  setPlayerBackdrop(currentDetailBackdrop);
  pushView('player');
  document.getElementById('player-title').textContent = currentTitle;
  updatePlayerSourceBadge();
  updatePlayerSettingButtons();
  updatePlayerAudioButton(); // nasconde il pulsante finché loadAudioTracks() non scopre le tracce del nuovo file
  updatePlayerNextEpisodeButton();
  updatePlayerDetailButton();
  updateSkipFallbackButton(); // nasconde "+30s" finché loadPlexMarkers() non sa se c'è un marker "intro"
  reloadReason = 'open';
  loadStream();
  loadAudioTracks(); // in parallelo, non blocca l'avvio della riproduzione (vedi commento sulla funzione)
  loadPlexMarkers(); // in parallelo, stesso principio
  loadEpisodePanelDetail(); // in parallelo, stesso principio — pronto già al primo pause
  showPlayerOverlay();
  startStallWatchdog();
  startProgressReporting();
}

// L'endpoint /stream manda un MP4 frammentato che CRESCE mentre arriva (niente moov completo
// all'inizio, vedi StreamingService.cs) — il demuxer di Chrome desktop lo gestisce bene, ma quello
// più datato di webOS 22 (Chromium 87) può restare bloccato dopo un piccolo intoppo di rete invece
// di riprendersi da solo (osservato in pratica: funziona da PC, si blocca sulla TV). Un evento
// "error" del tag <video> non scatta sempre in questo caso (non è un errore, solo uno stallo), per
// questo serve un controllo attivo invece di limitarsi al solo listener "error" già presente.
let stallCheckTimer = null;
let stallLastTime = 0;
let stallLastAdvanceAt = 0;
let stallEverAdvanced = false; // true dal primo avanzamento in poi, per QUESTO caricamento

// Sorgenti pesanti (4K/HDR, con la pipeline di tone-mapping applicata prima dell'encoder) possono
// impiegare molto più di 8s per produrre il PRIMO fotogramma — un timeout stretto anche in questa
// fase iniziale interrompeva la connessione prima ancora che ffmpeg avesse una vera occasione di
// partire, e il server leggeva quell'interruzione come "l'encoder non si apre", innescando un
// fallback ingiustificato a libx264 software (ancora più lento, quindi ancora più probabile un
// nuovo timeout: un loop che si autoalimenta, osservato in pratica). Concediamo un margine ampio
// SOLO prima del primo avanzamento; una volta che lo streaming ha dimostrato di funzionare, un
// vero stallo a metà va comunque rilevato in tempi ragionevoli.
const STALL_GRACE_FIRST_FRAME_MS = 25000;
const STALL_GRACE_PLAYING_MS = 8000;

function startStallWatchdog() {
  stopStallWatchdog();
  stallCheckTimer = setInterval(() => {
    const video = document.getElementById('player');
    if (video.paused || video.ended || video.seeking) { stallLastAdvanceAt = Date.now(); return; }
    if (video.currentTime > stallLastTime + 0.15) {
      stallLastTime = video.currentTime;
      stallLastAdvanceAt = Date.now();
      stallEverAdvanced = true;
      return;
    }
    const grace = stallEverAdvanced ? STALL_GRACE_PLAYING_MS : STALL_GRACE_FIRST_FRAME_MS;
    if (Date.now() - stallLastAdvanceAt > grace) {
      stallLastAdvanceAt = Date.now(); // non ritentare a raffica se anche il prossimo si blocca
      setStatus('⚠️ Streaming bloccato, ritento…');
      seek(0); // ricarica lo stream dalla stessa posizione assoluta (baseSeconds + currentTime)
    }
  }, 2000);
}

function stopStallWatchdog() {
  clearInterval(stallCheckTimer);
  stallCheckTimer = null;
}

// ---------------------------------------------------------------
// Cronologia di visione (Fase 3, piano-restyle-webos-ux.md) — riportata al server ogni 20s durante
// la riproduzione (più un ultimo invio quando si esce dal player), solo se currentMediaContext è
// noto (percorso Cerca, mai per file aperti dalla Libreria: vedi openMagnet). Persistita lato
// server invece che in localStorage per sopravvivere a un reset dell'app/della TV.
// ---------------------------------------------------------------

let progressReportTimer = null;

function startProgressReporting() {
  stopProgressReporting();
  if (!currentMediaContext) return;
  progressReportTimer = setInterval(reportProgress, 20000);
}

function stopProgressReporting() {
  clearInterval(progressReportTimer);
  progressReportTimer = null;
}

function reportProgress() {
  if (!currentMediaContext || !currentFile) return;
  const video = document.getElementById('player');
  const position = baseSeconds + (video.currentTime || 0);
  if (position <= 0) return; // niente da salvare prima del primo avanzamento reale

  fetch(`${apiBase()}/api/tv/history/progress`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      tmdbId: currentMediaContext.tmdbId,
      mediaType: currentMediaContext.mediaType,
      // Il nome "pulito" (es. "Lanterns"), non currentTitle (usato per il titolo a schermo nel
      // player, spesso composto tipo "Lanterns — Stagione 1 · Episodio 1") — BUG REALE trovato in
      // produzione: salvare il titolo composto faceva "impilare" le etichette ad ogni resume da
      // "Continua a guardare" (che ne aggiunge un'altra sopra), vedi nota in openEpisodeVersions.
      title: currentMediaContext.title || currentTitle,
      season: currentMediaContext.season,
      episode: currentMediaContext.episode,
      positionSeconds: position,
      durationSeconds: playerDuration || 0,
      fileLink: currentFile.link,
      fileName: currentFile.name,
      provider: currentFile.provider || 'allDebrid'
    })
  }).catch(() => {}); // best-effort: un progresso perso non deve interrompere la visione
}

// Il player deve restare a schermo pieno per guardare, non con una barra di controlli sempre
// sopra l'immagine: l'overlay compare quando serve (nuovo stream, tasto premuto, pausa) e sparisce
// da solo dopo qualche secondo — qualunque tasto premuto nel player la fa ricomparire (vedi il
// listener keydown globale) prima ancora di spostare il focus, così non si preme mai "alla cieca".
let overlayHideTimer = null;

function showPlayerOverlay() {
  const overlay = document.getElementById('player-overlay');
  overlay.classList.remove('overlay-hidden');
  clearTimeout(overlayHideTimer);
  const video = document.getElementById('player');
  if (video.paused) return; // in pausa l'overlay resta visibile finché non si riparte
  overlayHideTimer = setTimeout(() => overlay.classList.add('overlay-hidden'), 4000);
}

function streamUrl() {
  const params = new URLSearchParams({
    link: currentFile.link,
    mode,
    quality: getConfig().quality || 'alta',
    start: baseSeconds.toFixed(2),
    r: String(reloadToken),
    // forceEncode segue "mode", non è più sempre 'true': quando mode='copy' (primo tentativo,
    // DirectPlay) deve restare 'false' per lasciare che il server tenti davvero "-c:v copy" — vedi
    // IsVideoCompatible in StreamingService.cs, che con forceEncode=true la ignora sempre a
    // prescindere dal codec. Una volta scattato il fallback a "transcode" (recoverFromFailure,
    // sotto, su un vero errore del player) forceEncode torna 'true': BUG REALE storico
    // (regressione) — IsVideoCompatible sceglieva comunque "-c:v copy" per contenuto H.264 8-bit
    // già compatibile anche quando il client aveva ESPLICITAMENTE chiesto la trascodifica
    // garantita dopo un fallback, riportando il player sul <video src> diretto già scartato.
    forceEncode: mode === 'copy' ? 'false' : 'true',
    provider: currentFile.provider || 'allDebrid',
    audioIndex: String(currentAudioIndex),
    // Motivo dell'ultimo (ri)caricamento (2026-09-14): finisce nel log server sulla riga "nuova
    // sessione", per distinguere un salto utente reale da un ricaricamento automatico (watchdog
    // anti-stallo, cambio qualità, ecc.) — prima erano indistinguibili, si poteva solo indovinare
    // dal timing tra le righe di log.
    reason: reloadReason
  });
  return `${apiBase()}/stream?${params.toString()}`;
}

// Fase 4 (piano-streaming-multiutente-hls.md): stessi parametri di streamUrl(), ma verso
// l'endpoint HLS (redirige alla playlist VOD già completa e chiusa della sessione, vedi
// Services/StreamingService.Hls.cs) — usata al posto del pipe MP4 diretto solo quando il video va
// ricodificato (vedi loadStream), mai per il bypass DirectPlay o il remux "copy" senza MSE, che
// restano invariati sul vecchio endpoint.
function hlsStreamUrl() {
  const params = new URLSearchParams({
    link: currentFile.link,
    mode,
    quality: getConfig().quality || 'alta',
    start: baseSeconds.toFixed(2),
    r: String(reloadToken),
    forceEncode: mode === 'copy' ? 'false' : 'true',
    provider: currentFile.provider || 'allDebrid',
    audioIndex: String(currentAudioIndex),
    reason: reloadReason
  });
  return `${apiBase()}/stream/hls?${params.toString()}`;
}

function setStatus(text) {
  document.getElementById('player-status').textContent = text;
}

// Spinner a schermo intero durante un reload (nuovo file, retry di uno stallo, passaggio
// automatico diretta->trascodifica): copre lo sfondo nero che il <video> mostra comunque appena
// gli si assegna una nuova sorgente (richiesta utente: la transizione sembrava un blocco/crash
// invece di un caricamento). Nascosto sull'evento "playing" del player, vedi sotto — quel segnale
// arriva solo quando la riproduzione è DAVVERO ripartita, non solo quando i primi byte arrivano.
function showLoadingOverlay(text) {
  document.getElementById('player-loading-text').textContent = text;
  document.getElementById('player-loading').classList.add('visible');
}

// Backdrop TMDB dietro lo spinner (richiesta utente, stile Stremio/Prime Video): riusa
// currentDetailBackdrop, già scaricato per le pagine dettaglio/risultati/versioni — nessuna nuova
// chiamata di rete. Impostato una volta sola all'apertura del player (openPlayer), resta invariato
// per tutta la visione anche durante i reload successivi.
function setPlayerBackdrop(url) {
  document.getElementById('player-loading').style.setProperty('--player-backdrop-image', url ? `url("${url}")` : 'none');
}

function hideLoadingOverlay() {
  document.getElementById('player-loading').classList.remove('visible');
}

// Interroga /api/tv/stream-info per sapere PRIMA se il video verrà ricodificato (nel qual caso si
// userà MSE con lo string codec fisso che il server restituisce) o copiato così com'è (nel qual
// caso resta il <video src> semplice — il codec originale non è prevedibile in anticipo). Cache
// per file+modalità: non ha senso rifare la chiamata ad ogni retry sullo stesso file, il risultato
// non cambia (rallenterebbe solo ogni tentativo di recupero).
async function getStreamInfo() {
  const provider = currentFile.provider || 'allDebrid';
  // audioIndex in chiave (restyle streaming locale 2026-09-14): il bypass ffmpeg lato server vale
  // solo per la traccia 0 (vedi StreamingService.cs) — senza questo nella chiave, una risposta
  // "directFile" già in cache per la traccia 0 verrebbe riusata per errore dopo aver cambiato
  // traccia audio con cyclePlayerAudioTrack.
  const key = `${currentFile.link}|${mode}|${provider}|${currentAudioIndex}`;
  if (streamInfoCache[key]) return streamInfoCache[key];
  try {
    const quality = getConfig().quality || 'alta';
    const forceEncode = mode === 'copy' ? 'false' : 'true';
    const res = await fetch(`${apiBase()}/api/tv/stream-info?link=${encodeURIComponent(currentFile.link)}&mode=${mode}&quality=${quality}&forceEncode=${forceEncode}&provider=${provider}&audioIndex=${currentAudioIndex}`);
    const data = await res.json();
    if (!res.ok) throw new Error(data.error || ('HTTP ' + res.status));
    streamInfoCache[key] = data;
    return data;
  } catch {
    return { videoEncoded: false }; // in dubbio, il <video src> diretto resta la scelta più sicura
  }
}

async function loadStream() {
  const loadingText = mode === 'copy' ? 'Riproduzione diretta…' : 'Trascodifica in corso…';
  setStatus(loadingText);
  showLoadingOverlay(loadingText);
  stallLastTime = 0;
  stallLastAdvanceAt = Date.now();
  stallEverAdvanced = false;
  cancelScrub(); // un reload (retry, salto) invalida qualunque scrub ancora in sospeso sul vecchio stream

  const video = document.getElementById('player');
  video.removeAttribute('src');
  video.load();
  updateTimelineUI();

  const thisLoad = ++loadSequence; // scarta risultati di caricamenti superati da un retry più recente
  const info = await getStreamInfo();
  if (thisLoad !== loadSequence) return;

  // Trascodifica "intelligente" per codec (2026-09-14, docs/idee-miglioramento-webos.md): se
  // stream-info torna già videoEncoded=true per una richiesta ancora in modalità 'copy', è perché
  // il probe lato server ha confermato con certezza che il video non è H.264 (vedi
  // GetStreamInfoAsync) — passare subito a 'transcode' evita al player il ciclo sprecato "prova
  // diretta -> errore -> recupero -> ritenta in trascodifica" tipico di ogni file HEVC. Stesso
  // guard di recoverFromFailure: mai forzare 'transcode' se l'utente ha scelto esplicitamente
  // "Sempre diretta" (allowAutoFallback=false), si rispetta sempre la scelta esplicita.
  if (info.videoEncoded && mode === 'copy' && allowAutoFallback) {
    mode = 'transcode';
    setStatus('Trascodifica in corso…');
    showLoadingOverlay('Trascodifica in corso…'); // lo spinner era già comparso con il testo "diretta" prima di sapere del probe
  }

  // Mantiene l'ultima durata nota buona se un retry temporaneo non la riporta (probe fallito):
  // la durata del file non cambia tra un reload e l'altro, non ha senso far sparire la barra.
  if (info.durationSeconds) playerDuration = info.durationSeconds;
  updateTimelineUI();

  if (info.directFile) {
    // Bypass ffmpeg (restyle streaming locale 2026-09-14): il server serve il file così com'è con
    // supporto Range vero, non un flusso generato al volo — niente più kill-and-relaunch di
    // ffmpeg per un salto: il browser fa la sua richiesta Range da solo non appena impostiamo
    // currentTime, esattamente come per un qualunque video scaricato da un sito normale.
    usingNativeHls = false;
    video.src = streamUrl();
    if (baseSeconds > 0) {
      video.addEventListener('loadedmetadata', function () {
        if (thisLoad === loadSequence) video.currentTime = baseSeconds;
      }, { once: true });
    }
    video.play().catch(() => {});
  } else if (info.videoEncoded) {
    // Fase 4 (piano-streaming-multiutente-hls.md): HLS nativo del player invece della pipeline
    // MSE scritta a mano (fetch manuale + SourceBuffer.appendBuffer) — quella era la fonte della
    // maggior parte dei bug di streaming di questa app (buffer che cresce senza limite, corse su
    // seek/cambio traccia, MediaSource.duration mai impostato...). Il server dichiara sempre una
    // playlist VOD già chiusa (durata nota da ffprobe) e aspetta che i segmenti richiesti siano
    // pronti invece di rispondere 404 — vedi Services/StreamingService.Hls.cs — quindi un salto
    // "piccolo" può restare sul seek nativo del player (vedi commitScrub/usingNativeHls), ma un
    // salto GRANDE (oltre quanto ffmpeg ha già codificato in sequenza da questo punto di partenza)
    // va comunque gestito con un reload esplicito, coperto dal watchdog anti-stallo esistente.
    usingNativeHls = true;
    video.src = hlsStreamUrl();
    video.play().catch(() => {});
  } else {
    usingNativeHls = false;
    video.src = streamUrl();
    video.play().catch(() => {});
  }
}
let loadSequence = 0;

// ---------------------------------------------------------------
// Timeline del player — sostituisce i vecchi pulsanti fissi ±10s/±30s. Sinistra/Destra sulla
// timeline (intercettate nel keydown globale, vedi sotto) spostano un bersaglio "scrub" visuale
// SENZA ricaricare subito lo stream: solo dopo un breve debounce di inattività lo stream viene
// davvero ricaricato con un nuovo `start=` (non esiste un vero seek byte-range su uno stream
// generato al volo, vedi piano-restyle-webos-ux.md — la timeline è un'interfaccia elegante sopra
// lo stesso meccanismo di reload già usato dai vecchi pulsanti). Tenendo premuto (pressioni
// ripetute ravvicinate, tipico di un tasto tenuto sul telecomando) il passo accelera, per poter
// scorrere rapidamente file lunghi senza decine di pressioni singole.
let scrubActive = false;
let scrubTarget = 0;
let scrubStep = 0;
let scrubLastDirection = null;
let scrubLastPressAt = 0;
let scrubCommitTimer = null;
const SCRUB_BASE_STEP = 10;
const SCRUB_MAX_STEP = 60;
const SCRUB_ACCEL_WINDOW_MS = 450;
const SCRUB_COMMIT_DEBOUNCE_MS = 600;

function currentAbsoluteTime() {
  const video = document.getElementById('player');
  return baseSeconds + (video.currentTime || 0);
}

function scrubBy(direction) {
  if (!scrubActive) {
    scrubActive = true;
    scrubTarget = currentAbsoluteTime();
    scrubStep = SCRUB_BASE_STEP;
    scrubLastDirection = null;
  }

  const now = Date.now();
  scrubStep = (scrubLastDirection === direction && (now - scrubLastPressAt) < SCRUB_ACCEL_WINDOW_MS)
    ? Math.min(SCRUB_MAX_STEP, scrubStep + SCRUB_BASE_STEP)
    : SCRUB_BASE_STEP;
  scrubLastDirection = direction;
  scrubLastPressAt = now;

  scrubTarget = Math.max(0, scrubTarget + direction * scrubStep);
  if (playerDuration) scrubTarget = Math.min(scrubTarget, playerDuration);
  updateTimelineUI();

  clearTimeout(scrubCommitTimer);
  scrubCommitTimer = setTimeout(commitScrub, SCRUB_COMMIT_DEBOUNCE_MS);
}

function commitScrub() {
  clearTimeout(scrubCommitTimer);
  if (!scrubActive) return;
  scrubActive = false;

  // BUG REALE (skip con la timeline lascia il player in pausa, richiede un tasto play manuale,
  // 2026-09-16): un reload completo (loadStream, nuovo <video src>) per OGNI salto — anche i più
  // piccoli — è quello che causava la pausa. Con HLS la playlist dichiara già l'intero file da
  // questo punto in poi (piano-streaming-multiutente-hls.md, Fase 2), quindi un salto DENTRO la
  // sessione corrente può restare sul seek nativo del player invece del reload.
  //
  // BUG REALE #2, TROVATO SUBITO DOPO (stesso giorno): il primo fix si fidava di QUALUNQUE punto
  // "target >= baseSeconds", senza controllare se fosse già stato davvero codificato. Su un file
  // leggero (H.264 8-bit, ~11x tempo reale) quasi sempre lo è, ma su un file pesante (4K HDR/
  // Dolby Vision con tone-mapping, ~3x — la maggior parte della libreria reale dell'utente) un
  // salto anche modesto oltre il buffer già scaricato mandava il player ad aspettare fino a 30s
  // il segmento lato server (HlsFileAsync), poi ad arrendersi con un errore VERO — molto peggio
  // del semplice reload diretto di prima (confermato nei log: un salto di posizione di ~293s in
  // 49s reali, poi "reason=error-recovery"). Corretto: il seek nativo è sicuro SOLO se il punto
  // richiesto è già dentro (o appena oltre) quanto il player ha già scaricato per davvero
  // (video.buffered) — altrimenti reload diretto, niente attesa lato server che tanto fallirebbe.
  if (usingNativeHls && scrubTarget >= baseSeconds) {
    const video = document.getElementById('player');
    const relativeTarget = scrubTarget - baseSeconds;
    let withinBuffer = false;
    try {
      for (let i = 0; i < video.buffered.length; i++) {
        if (relativeTarget >= video.buffered.start(i) - 1 && relativeTarget <= video.buffered.end(i) + 3) { withinBuffer = true; break; }
      }
    } catch {}
    if (withinBuffer) {
      video.currentTime = relativeTarget;
      updateTimelineUI();
      return;
    }
  }

  baseSeconds = scrubTarget;
  reloadToken++;
  reloadReason = 'seek';
  loadStream();
}

function cancelScrub() {
  clearTimeout(scrubCommitTimer);
  scrubActive = false;
}

function togglePlayPause() {
  const video = document.getElementById('player');
  if (video.paused) video.play(); else video.pause();
}

function updateTimelineUI() {
  const elapsed = scrubActive ? scrubTarget : currentAbsoluteTime();
  document.getElementById('player-time-elapsed').textContent = FormatTimeShort(elapsed);

  const totalEl = document.getElementById('player-time-total');
  const fill = document.getElementById('player-timeline-fill');
  const thumb = document.getElementById('player-timeline-thumb');

  if (playerDuration && playerDuration > 0) {
    totalEl.textContent = FormatTimeShort(playerDuration);
    totalEl.classList.remove('hidden');
    thumb.classList.remove('hidden');
    const pct = Math.min(100, Math.max(0, (elapsed / playerDuration) * 100));
    fill.style.width = pct + '%';
    thumb.style.left = pct + '%';
  } else {
    // Durata sconosciuta (probe fallito): niente barra percentuale ingannevole, solo il tempo
    // trascorso — lo scrub resta comunque utilizzabile (delta relativi, senza limite superiore).
    totalEl.classList.add('hidden');
    thumb.classList.add('hidden');
    fill.style.width = '0%';
  }

  updateSkipIntroPill(elapsed);
  updateNextEpisodeMarkerTrigger(elapsed);
}

// ---------------------------------------------------------------
// Fase 4 (2026-09-16, piano-streaming-multiutente-hls.md): la pipeline Media Source Extensions
// scritta a mano che viveva qui (fetch manuale + SourceBuffer.appendBuffer, ~180 righe) è stata
// rimossa e sostituita dall'HLS nativo del player (vedi hlsStreamUrl()/loadStream()) — è stata la
// fonte della stragrande maggioranza dei bug di streaming di questa sessione (buffer che cresce
// senza limite, corse su seek/cambio traccia, MediaSource.duration mai impostato, potature del
// SourceBuffer che il motore nativo webOS non tollerava). Un tentativo precedente di sostituirla
// con HLS (2026-09-13) era stato abbandonato per bug MAI risolti sulla TV vera; questa volta la
// causa reale è stata isolata lato server (segmenti fMP4 invece di MPEG-TS, playlist sempre
// dichiarata chiusa fin da subito) e confermata su un episodio intero senza stalli.

// FONDAMENTALE: aggiorna baseSeconds con la posizione ASSOLUTA raggiunta prima di ricaricare —
// senza questo, ogni recovery (error/ended) ripartiva sempre da start=0.00 (bug reale osservato
// in log: 9s poi di nuovo da zero, 20s poi di nuovo da zero, sempre daccapo), che dall'esterno
// sembra un loop bloccato anche se ogni tentativo tecnicamente arrivava un po' più avanti — con
// il player che ripropone sempre i primi secondi del file non si ha mai la sensazione di
// avanzare. seek(0) già faceva bene questo calcolo, qui si riusa la stessa logica.
function recoverFromFailure(reasonText) {
  const video = document.getElementById('player');
  const elapsed = video.currentTime || 0;
  baseSeconds = Math.max(0, baseSeconds + elapsed);

  // allowAutoFallback=false quando l'utente ha forzato esplicitamente "Sempre diretta" o "Sempre
  // trascodifica" (Impostazioni o pulsante "Modalità" nel player): in quel caso non si passa mai
  // automaticamente all'altra modalità, si rispetta sempre la scelta esplicita e si ritenta solo
  // da capo nella stessa modalità.
  if (mode !== 'transcode' && allowAutoFallback) {
    mode = 'transcode';
    setStatus(`${reasonText} Passo alla trascodifica…`);
  } else {
    setStatus(`${reasonText} Ritento da ${FormatTimeShort(baseSeconds)}…`);
  }
  reloadToken++;
  reloadReason = 'error-recovery';
  loadStream();
}

function FormatTimeShort(seconds) {
  const s = Math.max(0, Math.floor(seconds));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  return h > 0
    ? `${h}:${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}`
    : `${m}:${String(sec).padStart(2, '0')}`;
}

document.getElementById('player').addEventListener('error', () => recoverFromFailure('Riproduzione diretta non riuscita.'));

// "playing" (non "loadeddata"/"canplay") è l'unico segnale affidabile che la riproduzione è
// DAVVERO ripartita, non solo che sono arrivati i primi byte — nasconde lo spinner di
// caricamento esattamente quando l'utente rivede il video muoversi, mai prima.
document.getElementById('player').addEventListener('playing', hideLoadingOverlay);

// Alcune fonti (es. rip di scarsa qualità con pacchetti malformati, o più in generale il demuxer
// datato di webOS 22/Chromium 87 alle prese con un MP4 frammentato che cresce mentre arriva) fanno
// terminare ffmpeg "normalmente" (nessun errore HTTP, nessun evento "error") ma con solo pochi
// secondi di contenuto davvero valido per volta — un "ended" con pochi minuti di contenuto è
// quasi certamente prematuro per un film/episodio reale, non la fine vera. Nessun limite fisso di
// tentativi: ora che recoverFromFailure riparte dalla posizione raggiunta (non più da zero), anche
// un'interruzione periodica ripetuta fa comunque avanzare la visione invece di bloccarla — un
// vero loop immobile non è più possibile per questa via.
document.getElementById('player').addEventListener('ended', () => {
  const video = document.getElementById('player');
  if (video.currentTime < 600) {
    recoverFromFailure('Streaming interrotto troppo presto.');
  } else if (!nextEpisodeTriggeredForThisFile && currentMediaContext && currentMediaContext.mediaType === 'tv' && getConfig().autoNextEpisodeEnabled !== false) {
    nextEpisodeTriggeredForThisFile = true; // impostato QUI, prima dell'async: mai un doppio trigger se "ended" e il marker "credits" scattassero a distanza di pochi ms
    maybeShowNextEpisode();
  }
});

// ---------------------------------------------------------------
// "Prossimo episodio" automatico, stile Netflix (richiesta utente) — solo per le serie (mai per i
// film, dove "ended"/il marker "credits" non hanno un seguito da proporre). Scatta o dal marker
// Plex "credits" (updateNextEpisodeMarkerTrigger, con anticipo sui titoli di coda) o dall'evento
// "ended" quando il marker manca — MAI entrambi sullo stesso file, vedi
// nextEpisodeTriggeredForThisFile. La pillola in alto a destra (index.html,
// #player-corner-pills) compare da sola e si autofocalizza; Invio/click su di lei salta subito,
// altrimenti il riempimento la innesca da solo a fine conto alla rovescia. Il tasto Indietro del
// telecomando la annulla (vedi il blocco keydown più sotto) invece di uscire dal player, come su
// una vera app TV Netflix — restyle 2026-09-14, sostituisce il vecchio overlay centrale a schermo
// intero (che nascondeva #player-overlay: qui il video resta visibile e in riproduzione sotto).
// ---------------------------------------------------------------

let nextEpisodeCountdownTimer = null;
let pendingNextEpisode = null; // { tmdbId, seriesTitle, season, episode } mentre la pillola è visibile

function hideNextEpisodePill() {
  clearInterval(nextEpisodeCountdownTimer);
  nextEpisodeCountdownTimer = null;
  pendingNextEpisode = null;
  const pill = document.getElementById('next-episode-pill');
  pill.classList.add('hidden');
  if (currentActive() === pill) document.getElementById('player-timeline').focus();
}

// Calcolato qui (non passato dalla pagina di dettaglio) perché il player può restare aperto per
// molti episodi di fila senza mai tornare al dettaglio: serve conoscere solo season/episode appena
// finiti (già in currentMediaContext) + l'episodeCount della stagione corrente, preso da
// /search/detail (stessa chiamata già usata altrove, qui a fine episodio non è un costo sentito).
// Estratta a parte da maybeShowNextEpisode() per essere riusata anche dal pulsante manuale
// "Prossimo episodio" (piano-webos-skip-marker-plex.md, punto 2), che salta l'overlay col conto
// alla rovescia e passa direttamente a playNextEpisode().
async function resolveNextEpisode() {
  const { tmdbId, season, episode, title } = currentMediaContext;
  if (season == null || episode == null) return null; // contesto incompleto, mai dovrebbe capitare per una serie
  try {
    const res = await fetch(`${apiBase()}/api/tv/search/detail?tmdbId=${tmdbId}&mediaType=tv`);
    if (!res.ok) return null;
    const detail = await res.json();
    const seasons = detail.seasons || [];
    const currentSeason = seasons.find(s => s.seasonNumber === season);
    let next = null;
    if (currentSeason && episode < currentSeason.episodeCount) {
      next = { season, episode: episode + 1 };
    } else {
      const nextSeason = seasons.find(s => s.seasonNumber === season + 1);
      if (nextSeason) next = { season: nextSeason.seasonNumber, episode: 1 };
    }
    if (!next) return null; // ultimo episodio della serie: niente da proporre
    return { tmdbId, title, season: next.season, episode: next.episode };
  } catch { return null; /* nessun risultato se il dettaglio non risponde, non deve bloccare nulla */ }
}

async function maybeShowNextEpisode() {
  const next = await resolveNextEpisode();
  if (!next) return; // ultimo episodio della serie: niente da proporre, resta la schermata di fine
  const episodeTitle = await fetchEpisodeTitle(next.tmdbId, next.season, next.episode);
  showNextEpisodePill(next.tmdbId, next.title, next.season, next.episode, episodeTitle);
}

// Trigger anticipato sul marker Plex "credits" (piano-webos-skip-marker-plex.md, punto 6) —
// chiamata da updateTimelineUI ad ogni tick. Senza marker "credits" disponibile (niente Plex Pass,
// file non processato, o marker disattivati): comportamento identico a prima, scatta solo su
// "ended" — nessuna euristica sul tempo residuo aggiunta, deciso con l'utente: meglio niente che un
// trigger non confermato.
function updateNextEpisodeMarkerTrigger(elapsed) {
  if (!nextEpisodeTriggeredForThisFile && currentMediaContext?.mediaType === 'tv' &&
      getConfig().autoNextEpisodeEnabled !== false && currentMarkers.credits &&
      elapsed >= currentMarkers.credits.startSeconds) {
    nextEpisodeTriggeredForThisFile = true;
    maybeShowNextEpisode();
  }
}

// Pulsante manuale nella barra controlli (sempre presente per le serie, non solo a fine episodio):
// salta subito al prossimo episodio, senza passare dalla pillola con conto alla rovescia — quella
// resta riservata al trigger automatico. Nessuna nuova logica di ricerca: riusa playNextEpisode()
// già esistente, solo con season/episode risolti da resolveNextEpisode() invece che dalla pillola.
async function playNextEpisodeManual() {
  if (!currentMediaContext || currentMediaContext.mediaType !== 'tv') return;
  const next = await resolveNextEpisode();
  if (!next) { setStatus('❌ Nessun episodio successivo trovato.'); return; }
  playNextEpisode(next.tmdbId, next.title, next.season, next.episode);
}

// Visibile solo per le serie TV (mai per i film): stesso pattern di updatePlayerAudioButton, un
// pulsante che non fa mai nulla per un film sarebbe solo rumore nell'overlay.
function updatePlayerNextEpisodeButton() {
  const btn = document.getElementById('player-next-episode-btn');
  if (!btn) return;
  btn.classList.toggle('hidden', !(currentMediaContext && currentMediaContext.mediaType === 'tv'));
}

// Idea utente 2026-09-16: dal player si può tornare alla scheda TMDB per scegliere un altro
// episodio o un'altra fonte, senza uscire dall'app. Nascosto se currentMediaContext non ha un
// tmdbId noto (es. file aperto da Providers via openMagnet(), che lo azzera apposta).
function updatePlayerDetailButton() {
  const btn = document.getElementById('player-detail-btn');
  if (!btn) return;
  btn.classList.toggle('hidden', !(currentMediaContext && currentMediaContext.tmdbId != null));
}

function openDetailFromPlayer() {
  if (!currentMediaContext || currentMediaContext.tmdbId == null) return;
  const { tmdbId, mediaType, title } = currentMediaContext;
  stopPlayer();
  // stack.pop() invece di popView(): la scheda deve SOSTITUIRE "player" in cima allo stack, non
  // impilarsi sopra — altrimenti un successivo "Indietro" dalla scheda tornerebbe al player vuoto
  // invece che a dove si era prima di aprirlo.
  stack.pop();
  openCandidate({ tmdbId, mediaType, title });
}

function showNextEpisodePill(tmdbId, seriesTitle, season, episode, episodeTitle) {
  // Se nel frattempo l'utente è già uscito dal player (Indietro durante il fetch), non fare nulla.
  if (stack[stack.length - 1] !== 'player') return;

  pendingNextEpisode = { tmdbId, seriesTitle, season, episode };
  document.getElementById('next-episode-pill-title').textContent =
    episodeTitle ? `S${season}E${episode} · ${episodeTitle}` : `S${season}E${episode}`;

  const pill = document.getElementById('next-episode-pill');
  const fillEl = document.getElementById('next-episode-pill-fill');
  const alreadyVisible = !pill.classList.contains('hidden');
  fillEl.style.width = '0%';
  pill.classList.remove('hidden');
  // Autofocus SOLO alla comparsa (richiesta utente: l'utente deve trovarla già pronta e premere
  // Invio senza spostarsi) — se era già visibile (countdown riavviato da capo, non dovrebbe
  // succedere in pratica ma per sicurezza) non rubare di nuovo il focus da sotto l'utente.
  if (!alreadyVisible) pill.focus();

  // 10s di default (era 15 fisso, poi testo "Tra Ns") — configurabile in Impostazioni, vedi
  // NEW_TV_PREFS. Il riempimento della pillola sostituisce il vecchio testo: aggiornato ad ogni
  // tick invece che con una transizione CSS a tempo fisso, per restare sincronizzato col conto
  // reale anche se il tab perde/riguadagna performance (setInterval, non requestAnimationFrame).
  const totalSeconds = getConfig().nextEpisodeCountdownSeconds || 10;
  let remaining = totalSeconds;
  clearInterval(nextEpisodeCountdownTimer);
  nextEpisodeCountdownTimer = setInterval(() => {
    remaining--;
    const pct = Math.max(0, Math.min(100, ((totalSeconds - remaining) / totalSeconds) * 100));
    fillEl.style.width = pct + '%';
    if (remaining <= 0) {
      clearInterval(nextEpisodeCountdownTimer);
      nextEpisodeCountdownTimer = null;
      playNextEpisode(tmdbId, seriesTitle, season, episode);
    }
  }, 1000);
}

// Stessa logica di preferenza qualità/lingua di setupVersionList (applicata qui invece che via
// scelta manuale, dato che l'intero senso di "automatico" è non dover scegliere di nuovo la
// versione ad ogni episodio) — ricade su "tutte" se la preferenza non esiste per QUESTO episodio,
// mai una lista vuota.
function pickPreferredResult(results) {
  const cfg = getConfig();
  const qualityOrder = ['4K', '1080p', '720p', 'SD', 'Altro'];
  const languageOrder = ['ITA', 'MULTI', 'SUB ITA', 'ENG'];
  let pool = results;
  if (cfg.preferredQuality && results.some(r => r.resolution === cfg.preferredQuality)) {
    pool = pool.filter(r => r.resolution === cfg.preferredQuality);
  }
  if (cfg.preferredLanguage && pool.some(r => r.language === cfg.preferredLanguage)) {
    pool = pool.filter(r => r.language === cfg.preferredLanguage);
  }
  return pool.slice().sort(bestFirst)[0];
}

async function playNextEpisode(tmdbId, seriesTitle, season, episode) {
  hideNextEpisodePill();

  // BUG REALE (segnalato dall'utente, 2026-09-14): mancava qui il controllo "già sul disco" che
  // esiste già altrove per lo stesso identico caso (vedi il pulsante "Prossimo episodio" nella
  // pagina di dettaglio, openCandidate — "da disco se già posseduto, mai una ricerca torrent per
  // un episodio che è già lì") — playNextEpisode andava sempre dritta su Torrentio anche quando il
  // file era già scaricato, ignorando il file locale.
  setStatus('🔎 Verifico se è già sul disco…');
  const local = await fetchLocalStatus('tv', seriesTitle, season, episode);
  if (stack[stack.length - 1] !== 'player') return; // l'utente è uscito nel frattempo
  if (local && local.hasLocalFile) {
    currentMediaContext = { tmdbId, mediaType: 'tv', season, episode, title: seriesTitle };
    setStatus('');
    const episodeTitle = await fetchEpisodeTitle(tmdbId, season, episode);
    const label = episodeTitle ? `S${season}E${episode} · ${episodeTitle}` : `S${season}E${episode}`;
    openLocalPlayer(local.localFilePath, `${seriesTitle} — ${label}`, currentMediaContext);
    return;
  }

  setStatus('🔎 Cerco il prossimo episodio su Torrentio…');
  try {
    const [results, episodeTitle] = await Promise.all([
      fetchTorrentResults({ tmdbId, mediaType: 'tv', season, episode }),
      fetchEpisodeTitle(tmdbId, season, episode)
    ]);
    if (stack[stack.length - 1] !== 'player') return; // l'utente è uscito nel frattempo
    if (results.length === 0) { setStatus(`❌ Nessun risultato per S${season}E${episode}.`); return; }
    const label = episodeTitle ? `S${season}E${episode} · ${episodeTitle}` : `S${season}E${episode}`;
    const best = pickPreferredResult(results);
    // Nessuna interazione possibile a runtime durante l'autoplay (vedi piano): usa sempre il
    // default/ultimo provider scelto (toggle di pagina della lista versioni), non un valore
    // per-riga — qui non c'è alcuna riga da poter toccare.
    const provider = versionProvider;
    setStatus(`🧲 Preparo il magnet su ${providerLabel(provider)} (può richiedere qualche minuto)…`);
    const res = await fetch(`${apiBase()}/api/tv/prepare`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title: best.title, magnet: best.magnet, siteName: best.siteName, provider })
    });
    const data = await res.json();
    if (!res.ok) throw new Error(data.error || ('HTTP ' + res.status));
    if (stack[stack.length - 1] !== 'player') return;
    currentMediaContext = { tmdbId, mediaType: 'tv', season, episode, title: seriesTitle };
    setStatus('');
    data.files.forEach(f => { f.provider = provider; });
    showFilesOrPlayer(data.files, `${seriesTitle} — ${label}`);
  } catch (err) {
    if (stack[stack.length - 1] === 'player') setStatus('❌ Errore passando al prossimo episodio: ' + err.message);
  }
}

document.getElementById('next-episode-pill').addEventListener('click', () => {
  if (!pendingNextEpisode) return;
  const { tmdbId, seriesTitle, season, episode } = pendingNextEpisode;
  clearInterval(nextEpisodeCountdownTimer);
  nextEpisodeCountdownTimer = null;
  playNextEpisode(tmdbId, seriesTitle, season, episode);
});

// ---------------------------------------------------------------
// "Salta sigla" (piano-webos-skip-marker-plex.md, punto 5) — stessa filosofia della pillola
// "Prossimo episodio" sopra: compare da sola nella finestra del marker "intro", si autofocalizza,
// Invio/click salta subito. Nessun conto alla rovescia qui (saltare non è mai automatico): la
// pillola sparisce da sola quando la finestra del marker si chiude, il riempimento mostra quanto
// tempo resta ancora utile per usarla invece di un countdown che triggera un'azione.
// ---------------------------------------------------------------

function updateSkipIntroPill(elapsed) {
  const pill = document.getElementById('skip-intro-pill');
  const m = currentMarkers.intro;
  const shouldShow = !!m && !skippedIntroForThisFile && elapsed >= m.startSeconds && elapsed < m.endSeconds;
  const wasVisible = !pill.classList.contains('hidden');

  if (shouldShow) {
    const windowLength = m.endSeconds - m.startSeconds;
    const pct = windowLength > 0 ? Math.max(0, Math.min(100, ((m.endSeconds - elapsed) / windowLength) * 100)) : 0;
    document.getElementById('skip-intro-pill-fill').style.width = pct + '%';
    if (!wasVisible) {
      pill.classList.remove('hidden');
      pill.focus(); // autofocus SOLO alla comparsa, come la pillola "Prossimo episodio"
    }
  } else if (wasVisible) {
    pill.classList.add('hidden');
    if (currentActive() === pill) document.getElementById('player-timeline').focus();
  }
}

// BUG REALE STORICO (stallo dopo "Salta sigla", sessione 2026-09-14, risolto prima ancora del
// passaggio a HLS): qui c'era un salto diretto `video.currentTime = m.endSeconds`, l'unico punto
// della UI a muovere il playhead senza passare da loadStream() — con la vecchia pipeline MSE
// (rimossa in Fase 4) il buffer non arrivava mai fin lì, un punto mai bufferizzato con attesa
// silenziosa. Si comporta come ogni altro salto della UI (commitScrub/seek): baseSeconds è una
// posizione ASSOLUTA nel file, non relativa al segmento corrente, quindi si riassegna direttamente
// (non si somma a video.currentTime) e si forza una nuova sessione server allo stesso punto.
document.getElementById('skip-intro-pill').addEventListener('click', () => {
  const m = currentMarkers.intro;
  if (m) {
    const target = Math.max(m.endSeconds, 0);
    baseSeconds = playerDuration ? Math.min(target, playerDuration) : target;
    reloadToken++;
    reloadReason = 'seek';
    loadStream();
  }
  skippedIntroForThisFile = true;
  const pill = document.getElementById('skip-intro-pill');
  pill.classList.add('hidden');
  document.getElementById('player-timeline').focus();
});

// Log play/pausa lato server (sessione 2026-09-14, docs/idee-miglioramento-webos.md): senza
// questo, i log di ffmpeg durante uno stallo non distinguono una pausa volontaria dell'utente da
// un vero blocco in lettura — stessa identica firma (currentTime fermo, buffer pieno, ffmpeg che
// frena) per entrambi i casi. Fire-and-forget: un log perso non deve mai influire sulla
// riproduzione.
function logPlaybackEvent(eventName) {
  if (!currentFile || !currentFile.link) return;
  const video = document.getElementById('player');
  fetch(`${apiBase()}/api/tv/playback-event`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ link: currentFile.link, eventName, positionSeconds: video.currentTime || 0 })
  }).catch(() => {});
}

document.getElementById('player').addEventListener('playing', () => setStatus(''));
document.getElementById('player').addEventListener('timeupdate', updateTimelineUI);
document.getElementById('player').addEventListener('pause', () => {
  showPlayerOverlay();
  document.getElementById('pause-indicator').classList.add('visible');
  document.getElementById('playpause-icon-pause').classList.add('hidden');
  document.getElementById('playpause-icon-play').classList.remove('hidden');
  // Pannello informazioni episodio (piano-webos-skip-marker-plex.md, Fascia 3): stesso identico
  // meccanismo di #pause-indicator qui sopra, zero debounce — richiesta utente esplicita, pausa/
  // riprendi rapidi più volte mostrano/nascondono il pannello ogni volta senza stati intermedi.
  document.getElementById('episode-info-panel').classList.add('visible');
  logPlaybackEvent('pause');
});
document.getElementById('player').addEventListener('play', () => {
  showPlayerOverlay();
  document.getElementById('pause-indicator').classList.remove('visible');
  document.getElementById('playpause-icon-play').classList.add('hidden');
  document.getElementById('playpause-icon-pause').classList.remove('hidden');
  document.getElementById('episode-info-panel').classList.remove('visible');
  logPlaybackEvent('play');
});

// Indicatore play/pausa a inizio timeline (vedi index.html): non ha tabindex, quindi il
// telecomando lo attiva già tramite il blocco Invio/scrub della timeline sopra in questo file —
// questo listener serve solo al click reale del puntatore Magic Remote. Rifocalizza la timeline
// per restare coerenti col modello a righe di getRows()/moveFocus() dopo un click diretto.
document.getElementById('player-timeline-playpause').addEventListener('click', () => {
  togglePlayPause();
  document.getElementById('player-timeline').focus();
});

// Oggi chiamata SOLO dal watchdog anti-stallo (seek(0), vedi startStallWatchdog) — i vecchi
// pulsanti fissi ±10/±30s sono stati sostituiti dalla timeline (scrubBy/commitScrub).
function seek(deltaSeconds) {
  const video = document.getElementById('player');
  const elapsed = video.currentTime || 0;
  baseSeconds = Math.max(0, baseSeconds + elapsed + deltaSeconds);
  reloadToken++;
  reloadReason = 'stall-recovery';
  loadStream();
}

function stopPlayer() {
  clearTimeout(overlayHideTimer);
  cancelScrub();
  stopStallWatchdog();
  hideLoadingOverlay(); // altrimenti resterebbe visibile al prossimo openPlayer(), prima ancora del primo loadStream()
  hideNextEpisodePill(); // altrimenti resterebbe visibile/in conto alla rovescia al prossimo openPlayer()
  reportProgress(); // ultimo salvataggio prima di uscire, altrimenti si perdono gli ultimi <20s
  stopProgressReporting();
  loadSequence++; // invalida qualunque caricamento ancora in corso per il file precedente
  const video = document.getElementById('player');
  video.pause();
  video.removeAttribute('src');
  video.load();
}

document.querySelectorAll('#player-overlay .ctrl').forEach(btn => {
  btn.addEventListener('click', () => {
    const action = btn.dataset.action;
    if (action === 'back') handleBack();
    else if (action === 'cycle-quality') cyclePlayerQuality();
    else if (action === 'cycle-mode') cyclePlayerMode();
    else if (action === 'cycle-audio') cyclePlayerAudioTrack();
    else if (action === 'next-episode') playNextEpisodeManual();
    else if (action === 'skip-forward') skipForward();
    else if (action === 'open-detail') openDetailFromPlayer();
  });
});

// ---------------------------------------------------------------
// Avvio — schermata di apertura (richiesta utente) prima di Home/Setup. Le due durate qui SOTTO
// devono combaciare con le animazioni CSS (.splash-mark/.splash-text/.splash-fade-out in app.css):
// SPLASH_DURATION_MS è il tempo a schermo prima di iniziare l'uscita (l'animazione di ingresso, ~1s
// in tutto, deve essere già finita), SPLASH_EXIT_MS è la durata del fade-out (0.45s in CSS).
// ---------------------------------------------------------------

const SPLASH_DURATION_MS = 1300;
const SPLASH_EXIT_MS = 450;

function init() {
  setTimeout(() => {
    const splash = document.getElementById('view-splash');
    splash.classList.add('splash-exit');
    setTimeout(() => {
      splash.classList.add('hidden'); // "splash" non è in VIEWS: showView() non la tocca mai, va nascosta a mano
      const cfg = getConfig();
      if (cfg.host) {
        stack = ['home'];
        showView('home');
        loadContinueWatching();
        loadWatchlist();
      } else {
        stack = ['setup'];
        showView('setup');
        renderSetupChips(); // primo avvio: mai passato da home-settings, i chip vanno disegnati qui
      }
      startDownloadNotifications(); // gira per tutta la sessione, indipendente dalla view corrente
    }, SPLASH_EXIT_MS);
  }, SPLASH_DURATION_MS);
}

document.addEventListener('DOMContentLoaded', init);
