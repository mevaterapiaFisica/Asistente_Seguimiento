// ── State ─────────────────────────────────────────────────────────────────────

const state = {
  activeTab: 'followup',
  homeData: null,

  followup: {
    centerFilter: null,   // null = all
    stageFilter: null,    // null = all stages
    searchQuery: '',
    activeCenter: null,   // center whose stage was clicked
    activeStage: null     // stage code clicked
  },

  agenda: {
    availableDates: [],
    selectedDate: null,
    centerFilter: null,
    activeMachine: null,
    slots: [],
    loading: false
  },

  tomographAgenda: {
    availableDates: [],
    selectedDate: null,
    centerFilter: null,
    activeTomograph: null,
    slots: [],
    loading: false
  },

  configData: null
};

// ── Utilities ─────────────────────────────────────────────────────────────────

function todayStr() {
  const d = new Date();
  return `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}`;
}
function pad(n) { return String(n).padStart(2, '0'); }

function delayClass(days, expected, isLongWait) {
  if (isLongWait) return 'long-wait';
  if (days < expected) return 'on-time';
  if (days === expected) return 'at-limit';
  return 'delayed';
}

function delayDot(days, expected, isLongWait) {
  const cls = delayClass(days, expected, isLongWait);
  const col = cls === 'long-wait' ? 'dot-gray' : cls === 'on-time' ? 'dot-green' : cls === 'at-limit' ? 'dot-yellow' : 'dot-red';
  return `<span class="dot ${col}"></span>`;
}

function freeMinutes(cap, scrapedCount) {
  const workMin = (Number(cap.workingHours) - Number(cap.reservedSpecialHours)) * 60;
  return Math.max(0, workMin - scrapedCount * cap.standardSlotMinutes);
}

function formatMinutes(min) {
  const h = Math.floor(min / 60), m = min % 60;
  return h === 0 ? `${m}min` : m === 0 ? `${h}h` : `${h}h ${m}min`;
}

function capacityClass(freeSlots, totalSlots) {
  const r = freeSlots / Math.max(totalSlots, 1);
  return r > 0.3 ? 'cap-ok' : r > 0.1 ? 'cap-warn' : 'cap-full';
}

function makePill(label, active, onClick) {
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = `pill-btn${active ? ' active' : ''}`;
  btn.textContent = label;
  btn.addEventListener('click', onClick);
  return btn;
}

function hcTag(patientId) {
  const isHc = patientId && /^\d{1,3}-\d{4,7}-\d{1,3}$/.test(patientId);
  return isHc
    ? `<span class="hc-tag">HC ${patientId}</span>`
    : `<span class="hc-tag muted-italic">HC: No asignada</span>`;
}

function groupByStage(patients, stageOrder) {
  const map = new Map();
  patients.forEach(p => {
    if (!map.has(p.stageCode)) map.set(p.stageCode, []);
    map.get(p.stageCode).push(p);
  });
  return [...map.entries()].sort((a, b) =>
    (stageOrder[a[0]] ?? 999) - (stageOrder[b[0]] ?? 999));
}

// ── Tabs ──────────────────────────────────────────────────────────────────────

function wireTabs() {
  document.querySelectorAll('nav.tabs .tab-button[data-tab]').forEach(btn => {
    btn.addEventListener('click', () => {
      state.activeTab = btn.dataset.tab;
      document.querySelectorAll('nav.tabs .tab-button[data-tab]').forEach(b =>
        b.classList.toggle('active', b === btn));
      document.querySelectorAll('.tab-panel').forEach(p =>
        p.classList.toggle('active', p.id === `tab-${state.activeTab}`));
      if (state.activeTab === 'agenda') refreshAgendaView();
      if (state.activeTab === 'tomograph') refreshTomographAgendaView();
      if (state.activeTab === 'config') loadConfigData();
    });
  });
}

// ── Actions ───────────────────────────────────────────────────────────────────

function wireActions() {
  document.getElementById('runScrapingTest').addEventListener('click', runScrapingTest);
  document.getElementById('runAgendaTest').addEventListener('click', runAgendaTest);
  document.getElementById('runTomographTest').addEventListener('click', runTomographTest);

  // Dropdown scrape menu
  const menuBtn = document.getElementById('scrapMenuBtn');
  const menu = document.getElementById('scrapMenu');
  menuBtn.addEventListener('click', e => {
    menu.hidden = !menu.hidden;
    e.stopPropagation();
  });
  document.addEventListener('click', () => { menu.hidden = true; });
  document.getElementById('actionUpdateSitramed').addEventListener('click', actionUpdateSitramed);
  document.getElementById('actionUpdateAria').addEventListener('click', actionUpdateAria);
  document.getElementById('actionUpdateAll').addEventListener('click', actionUpdateAll);

  document.getElementById('agendaDateSelect').addEventListener('change', e => {
    state.agenda.selectedDate = e.target.value || null;
    state.agenda.activeMachine = null;
    loadAgendaForSelectedDate();
  });
  document.getElementById('tomographDateSelect').addEventListener('change', e => {
    state.tomographAgenda.selectedDate = e.target.value || null;
    state.tomographAgenda.activeTomograph = null;
    loadTomographAgendaForSelectedDate();
  });
  document.getElementById('saveConfigBtn').addEventListener('click', saveConfig);
  document.getElementById('patientSearch').addEventListener('input', e => {
    state.followup.searchQuery = e.target.value.trim().toLowerCase();
    renderFollowupDetail();
  });
}

// ── API wrappers ──────────────────────────────────────────────────────────────

async function runScrapingTest() {
  const t = document.getElementById('scrapingTestResult');
  t.textContent = 'Probando...'; t.className = 'test-result';
  try {
    const r = await (await fetch('/api/scraping/test', { method: 'POST' })).json();
    t.textContent = r.success ? `${r.message} | ${r.pageTitle} | html ${r.htmlLength}` : r.message;
    t.classList.toggle('error', !r.success);
  } catch (e) { t.textContent = `Error: ${e.message}`; t.classList.add('error'); }
}

async function runAgendaTest() {
  const t = document.getElementById('agendaTestResult');
  t.textContent = 'Probando...'; t.className = 'test-result';
  const machine = document.getElementById('agendaTestMachine').value;
  const date = document.getElementById('agendaTestDate').value;
  try {
    const q = new URLSearchParams();
    if (machine) q.set('machine', machine);
    if (date) q.set('date', date);
    const r = await (await fetch(`/api/scraping/test-agenda?${q}`, { method: 'POST' })).json();
    t.textContent = r.success
      ? `${r.message} | ${r.pageTitle} | html ${r.htmlLength} | dom ${r.agendaDomRows ?? 0}` : r.message;
    t.classList.toggle('error', !r.success);
  } catch (e) { t.textContent = `Error: ${e.message}`; t.classList.add('error'); }
}

async function runTomographTest() {
  const t = document.getElementById('tomographTestResult');
  t.textContent = 'Probando...'; t.className = 'test-result';
  const centerName = document.getElementById('tomographTestCenter').value;
  const date = document.getElementById('tomographTestDate').value;
  try {
    const q = new URLSearchParams();
    if (centerName) q.set('centerName', centerName);
    if (date) q.set('date', date);
    const r = await (await fetch(`/api/scraping/test-tomograph?${q}`, { method: 'POST' })).json();
    const rows = (r.rawRowSamples ?? []).join('\n');
    t.textContent = r.success
      ? `${r.message}\nURL: ${r.url}\nHTML: ${r.htmlLength} bytes\n\nFilas crudas:\n${rows || '(ninguna)'}\n\nHtmlPreview:\n${r.htmlPreview ?? ''}`
      : r.message;
    t.classList.toggle('error', !r.success);
  } catch (e) { t.textContent = `Error: ${e.message}`; t.classList.add('error'); }
}

async function actionUpdateSitramed() {
  document.getElementById('scrapMenu').hidden = true;
  const btn = document.getElementById('scrapMenuBtn');
  btn.disabled = true;
  document.getElementById('generatedAt').textContent = 'Actualizando...';
  try {
    const resp = await fetch('/api/home/refresh-no-aria', { method: 'POST' });
    if (!resp.ok) { document.getElementById('generatedAt').textContent = `Error ${resp.status}`; return; }
    renderHome(await resp.json());
    const days = state.configData?.upcomingScrapeDays ?? 15;
    fetch(`/api/agenda/scrape-upcoming?days=${days}`, { method: 'POST' })
      .then(() => loadAvailableDates()).catch(() => {});
    fetch(`/api/tomograph-agenda/scrape-upcoming?days=${days}`, { method: 'POST' })
      .then(() => loadAvailableTomographDates()).catch(() => {});
  } catch (e) {
    document.getElementById('generatedAt').textContent = `Error: ${e.message}`;
  } finally { btn.disabled = false; }
}

async function actionUpdateAria() {
  document.getElementById('scrapMenu').hidden = true;
  const btn = document.getElementById('scrapMenuBtn');
  btn.disabled = true;
  document.getElementById('generatedAt').textContent = 'Consultando ARIA...';
  try {
    const resp = await fetch('/api/aria/run-query', { method: 'POST' });
    const r = await resp.json();
    if (resp.ok) {
      const homeResp = await fetch('/api/home');
      if (homeResp.ok) renderHome(await homeResp.json());
      else document.getElementById('generatedAt').textContent = `ARIA: ${r.withActivePlan} con plan`;
    } else {
      document.getElementById('generatedAt').textContent = r.error ?? `Error ${resp.status}`;
    }
  } catch (e) {
    document.getElementById('generatedAt').textContent = `Error: ${e.message}`;
  } finally { btn.disabled = false; }
}

async function actionUpdateAll() {
  document.getElementById('scrapMenu').hidden = true;
  const btn = document.getElementById('scrapMenuBtn');
  btn.disabled = true;
  document.getElementById('generatedAt').textContent = 'Consultando ARIA...';
  try {
    await fetch('/api/aria/run-query', { method: 'POST' });
    document.getElementById('generatedAt').textContent = 'Actualizando Sitramed...';
    const resp = await fetch('/api/home/refresh', { method: 'POST' });
    if (!resp.ok) { document.getElementById('generatedAt').textContent = `Error ${resp.status}`; return; }
    renderHome(await resp.json());
    const days = state.configData?.upcomingScrapeDays ?? 15;
    fetch(`/api/agenda/scrape-upcoming?days=${days}`, { method: 'POST' })
      .then(() => loadAvailableDates()).catch(() => {});
    fetch(`/api/tomograph-agenda/scrape-upcoming?days=${days}`, { method: 'POST' })
      .then(() => loadAvailableTomographDates()).catch(() => {});
  } catch (e) {
    document.getElementById('generatedAt').textContent = `Error: ${e.message}`;
  } finally { btn.disabled = false; }
}

// ── Home load / render ────────────────────────────────────────────────────────

async function loadHome() {
  const resp = await fetch('/api/home');
  if (!resp.ok) {
    document.getElementById('generatedAt').textContent = `Error ${resp.status}`;
    return;
  }
  renderHome(await resp.json());
}

function renderHome(data) {
  state.homeData = data;
  document.getElementById('generatedAt').textContent =
    new Date(data.generatedAtUtc).toLocaleString();
  buildFollowUpFilters(data);
  renderFollowUp();
  populateAgendaTestControls(data);
}

// ── Seguimiento tab ───────────────────────────────────────────────────────────

function buildFollowUpFilters(data) {
  // Incluir todos los centros configurados (no solo los que tienen pacientes ahora)
  const configuredCenters = [...new Set(
    (data.configuration?.machineCapacities ?? []).map(c => c.centerName)
  )];
  const patientCenters = [...new Set((data.patients ?? []).map(p => p.centerName))];
  const centers = [...new Set([...configuredCenters, ...patientCenters])].sort();

  const row = document.getElementById('centerFilterPills');
  row.innerHTML = '';
  row.appendChild(makePill('Todos', state.followup.centerFilter === null, () => {
    state.followup.centerFilter = null; renderFollowUp();
  }));
  centers.forEach(c => row.appendChild(
    makePill(c, state.followup.centerFilter === c, () => {
      state.followup.centerFilter = c; renderFollowUp();
    })
  ));

  buildStageFilterPills(data);
}

function buildStageFilterPills(data) {
  const row = document.getElementById('stageFilterPills');
  if (!row) return;
  row.innerHTML = '';
  row.appendChild(makePill('Todas', state.followup.stageFilter === null, () => {
    state.followup.stageFilter = null;
    state.followup.activeCenter = null;
    state.followup.activeStage = null;
    renderFollowUp();
  }));
  (data.stages ?? []).forEach(s => row.appendChild(
    makePill(s.displayName, state.followup.stageFilter === s.code, () => {
      state.followup.stageFilter = s.code;
      state.followup.activeCenter = null;
      state.followup.activeStage = null;
      renderFollowUp();
    })
  ));
}

function renderFollowUp() {
  if (!state.homeData) return;

  const stageDefs = state.homeData.stages ?? [];

  // Sync centro pills
  document.querySelectorAll('#centerFilterPills .pill-btn').forEach(btn => {
    btn.classList.toggle('active',
      btn.textContent === 'Todos'
        ? state.followup.centerFilter === null
        : btn.textContent === state.followup.centerFilter);
  });

  // Sync etapa pills
  document.querySelectorAll('#stageFilterPills .pill-btn').forEach(btn => {
    const matchingStage = stageDefs.find(s => s.displayName === btn.textContent);
    btn.classList.toggle('active',
      btn.textContent === 'Todas'
        ? state.followup.stageFilter === null
        : matchingStage?.code === state.followup.stageFilter);
  });

  const allPatients = state.homeData.patients ?? [];

  // Mostrar siempre TODOS los centros configurados (no solo los con pacientes)
  const configuredCenters = [...new Set(
    (state.homeData.configuration?.machineCapacities ?? []).map(c => c.centerName)
  )].sort();
  const visibleCenters = state.followup.centerFilter
    ? configuredCenters.filter(c => c === state.followup.centerFilter)
    : configuredCenters;

  const container = document.getElementById('centerCards');
  container.innerHTML = '';

  if (visibleCenters.length === 0) {
    container.innerHTML = '<p class="detail-placeholder">Sin centros configurados.</p>';
  } else {
    const stageOrder = Object.fromEntries(stageDefs.map(s => [s.code, s.sortOrder]));
    visibleCenters.forEach(c => {
      // Las tarjetas siempre muestran TODOS los pacientes del centro (sin filtrar por etapa)
      // El filtro de etapa solo afecta el panel de detalle
      const pats = allPatients.filter(p => p.centerName === c);
      container.appendChild(buildCenterCard(c, pats, stageOrder));
    });
  }

  renderFollowupDetail();
}

function buildCenterCard(centerName, patients, stageOrder) {
  const delayed = patients.filter(p => p.isDelayed).length;
  const card = document.createElement('section');
  card.className = 'center-card';

  card.innerHTML = `
    <div class="center-card-header">
      <span class="center-name">${centerName}</span>
      <span class="center-counts">
        ${patients.length} pac
        ${delayed > 0 ? `<span class="dot dot-red"></span><span class="count-delayed">${delayed} dem.</span>` : ''}
      </span>
    </div>`;

  // Mostrar TODAS las etapas en orden, incluso las que tienen 0 pacientes
  const stageDefs = [...(state.homeData.stages ?? [])].sort(
    (a, b) => (stageOrder[a.code] ?? 999) - (stageOrder[b.code] ?? 999));

  stageDefs.forEach(def => {
    if (state.followup.stageFilter && def.code !== state.followup.stageFilter) return;
    const pats = patients.filter(p => p.stageCode === def.code);
    const isEmpty = pats.length === 0;
    const countable = pats.filter(p => !p.isLongWait);
    const avg = countable.length > 0 ? countable.reduce((s, p) => s + p.daysInStage, 0) / countable.length : 0;
    const hasDelayed = pats.some(p => p.isDelayed);
    const isActive = state.followup.activeCenter === centerName && state.followup.activeStage === def.code;
    const isFiltered = state.followup.stageFilter === def.code;

    const row = document.createElement('div');
    row.className = [
      'stage-row',
      isActive    ? 'active'         : '',
      hasDelayed  ? 'has-delayed'    : '',
      isEmpty     ? 'stage-empty'    : '',
      isFiltered && !isActive ? 'stage-filtered' : ''
    ].filter(Boolean).join(' ');

    row.innerHTML =
      `<span class="stage-row-name">${def.displayName}</span>` +
      `<span class="stage-row-stats">${isEmpty ? '—' : `${pats.length} pac (${avg.toFixed(1)}d)`}</span>` +
      (hasDelayed ? `<span class="dot dot-red"></span>` : '');

    row.addEventListener('click', () => {
      if (state.followup.activeCenter === centerName && state.followup.activeStage === def.code) {
        state.followup.activeCenter = null;
        state.followup.activeStage = null;
      } else {
        state.followup.activeCenter = centerName;
        state.followup.activeStage = def.code;
      }
      renderFollowUp();
    });
    card.appendChild(row);
  });

  return card;
}

function renderFollowupDetail() {
  const panel = document.getElementById('followupDetail');
  if (!state.homeData) { panel.innerHTML = ''; return; }

  const allPatients = state.homeData.patients ?? [];
  const stageDefs = state.homeData.stages ?? [];

  // Global search overrides stage selection
  if (state.followup.searchQuery) {
    const q = state.followup.searchQuery;
    const matches = allPatients.filter(p =>
      p.patientName?.toLowerCase().includes(q) || p.patientId?.toLowerCase().includes(q));

    panel.innerHTML = '';
    const title = el('div', 'detail-title',
      `${matches.length} resultado${matches.length !== 1 ? 's' : ''} para "${q}"`);
    panel.appendChild(title);

    if (matches.length === 0) {
      panel.appendChild(el('p', 'detail-placeholder', 'Sin coincidencias.'));
      return;
    }
    matches.forEach(p => {
      const def = stageDefs.find(s => s.code === p.stageCode);
      const dc = delayClass(p.daysInStage, p.expectedDaysInStage, p.isLongWait);
      const row = document.createElement('article');
      row.className = `patient-row ${dc}`;
      const nameHtml = p.sitraMedGuid
        ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${p.sitraMedGuid}/overview" target="_blank" rel="noopener noreferrer"><strong>${p.patientName}</strong></a>`
        : `<strong>${p.patientName}</strong>`;
      row.innerHTML =
        delayDot(p.daysInStage, p.expectedDaysInStage, p.isLongWait) +
        `<div class="patient-main">` +
          nameHtml +
          hcTag(p.patientId) +
          (p.assignedPhysicist && p.stageGroupName !== 'Planificacion' ? `<span class="physicist-tag">(asignado a: ${p.assignedPhysicist})</span>` : '') +
          `<span class="patient-context">${p.centerName} · ${def?.displayName ?? p.stageCode}</span>` +
        `</div>` +
        (p.plannedMachineDisplayName ? `<span class="aria-machine">▸ ${p.plannedMachineDisplayName}</span>` : '') +
        `<span class="days-badge ${dc}">${p.daysInStage}d</span>`;
      panel.appendChild(row);
    });
    return;
  }

  // Stage detail
  if (state.followup.activeCenter && state.followup.activeStage) {
    const def = stageDefs.find(s => s.code === state.followup.activeStage);
    const pats = allPatients.filter(p =>
      p.centerName === state.followup.activeCenter && p.stageCode === state.followup.activeStage);

    panel.innerHTML = '';
    panel.appendChild(el('div', 'detail-title',
      `${state.followup.activeCenter} · ${def?.displayName ?? state.followup.activeStage}`));

    if (pats.length === 0) {
      panel.appendChild(el('p', 'detail-placeholder', 'Sin pacientes.'));
      return;
    }
    pats.forEach(p => {
      const dc = delayClass(p.daysInStage, p.expectedDaysInStage, p.isLongWait);
      const row = document.createElement('article');
      row.className = `patient-row ${dc}`;
      const nameHtml = p.sitraMedGuid
        ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${p.sitraMedGuid}/overview" target="_blank" rel="noopener noreferrer"><strong>${p.patientName}</strong></a>`
        : `<strong>${p.patientName}</strong>`;
      row.innerHTML =
        delayDot(p.daysInStage, p.expectedDaysInStage, p.isLongWait) +
        nameHtml +
        hcTag(p.patientId) +
          (p.assignedPhysicist && p.stageGroupName !== 'Planificacion' ? `<span class="physicist-tag">(asignado a: ${p.assignedPhysicist})</span>` : '') +
        (p.plannedMachineDisplayName ? `<span class="aria-machine">▸ ${p.plannedMachineDisplayName}</span>` : '') +
        `<span class="days-badge ${dc}">${p.daysInStage}d</span>`;
      panel.appendChild(row);
    });
    return;
  }

  // Filtro de etapa activo sin fila específica → muestra todos los pacientes de esa etapa
  if (state.followup.stageFilter) {
    const def = stageDefs.find(s => s.code === state.followup.stageFilter);
    let pats = allPatients.filter(p => p.stageCode === state.followup.stageFilter);
    if (state.followup.centerFilter) {
      pats = pats.filter(p => p.centerName === state.followup.centerFilter);
    }
    pats.sort((a, b) => b.daysInStage - a.daysInStage);

    panel.innerHTML = '';
    panel.appendChild(el('div', 'detail-title',
      `${def?.displayName ?? state.followup.stageFilter} · ${pats.length} pac.`));
    if (pats.length === 0) {
      panel.appendChild(el('p', 'detail-placeholder', 'Sin pacientes en esta etapa.'));
      return;
    }
    pats.forEach(p => {
      const dc = delayClass(p.daysInStage, p.expectedDaysInStage, p.isLongWait);
      const row = document.createElement('article');
      row.className = `patient-row ${dc}`;
      const nameHtml = p.sitraMedGuid
        ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${p.sitraMedGuid}/overview" target="_blank" rel="noopener noreferrer"><strong>${p.patientName}</strong></a>`
        : `<strong>${p.patientName}</strong>`;
      row.innerHTML =
        delayDot(p.daysInStage, p.expectedDaysInStage, p.isLongWait) +
        `<div class="patient-main">` +
          nameHtml +
          hcTag(p.patientId) +
          (p.assignedPhysicist && p.stageGroupName !== 'Planificacion' ? `<span class="physicist-tag">(asignado a: ${p.assignedPhysicist})</span>` : '') +
          `<span class="patient-context">${p.centerName}</span>` +
        `</div>` +
        (p.plannedMachineDisplayName ? `<span class="aria-machine">▸ ${p.plannedMachineDisplayName}</span>` : '') +
        `<span class="days-badge ${dc}">${p.daysInStage}d</span>`;
      panel.appendChild(row);
    });
    return;
  }

  panel.innerHTML = '';
  panel.appendChild(el('p', 'detail-placeholder', 'Seleccione una etapa o busque un paciente.'));
}

function findPatientGuid(name) {
  if (!name || !state.homeData?.patients) return null;
  const p = state.homeData.patients.find(p =>
    p.patientName?.toLowerCase() === name.toLowerCase());
  return p?.sitraMedGuid ?? null;
}

function el(tag, cls, text) {
  const e = document.createElement(tag);
  e.className = cls;
  e.textContent = text;
  return e;
}

function formatDisplayDate(iso) {
  const [y, m, d] = iso.split('-');
  return `${d}-${m}-${y}`;
}

// ── Agenda tab ────────────────────────────────────────────────────────────────

async function loadAvailableDates() {
  try {
    const resp = await fetch('/api/agenda/available-dates');
    state.agenda.availableDates = resp.ok ? await resp.json() : [];
  } catch { state.agenda.availableDates = []; }
  populateAgendaDateSelect();
}

function populateAgendaDateSelect() {
  const today = todayStr();
  const futureScraped = state.agenda.availableDates.filter(d => d >= today).sort();
  const lastScraped = futureScraped.length > 0 ? futureScraped[futureScraped.length - 1] : today;

  // Fechas desde hoy hasta el último día scrapeado (inclusive), todas las intermedias incluidas
  const all = [];
  const d = new Date(today + 'T00:00:00');
  const last = new Date(lastScraped + 'T00:00:00');
  while (d <= last) {
    all.push(`${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}`);
    d.setDate(d.getDate() + 1);
  }

  const sel = document.getElementById('agendaDateSelect');
  const prev = sel.value;
  const target = state.agenda.selectedDate || prev;
  sel.innerHTML = '<option value="">-- elegir fecha --</option>';
  all.forEach(date => {
    const opt = document.createElement('option');
    opt.value = date;
    const scraped = state.agenda.availableDates.includes(date);
    const isToday = date === today;
    opt.textContent = isToday ? `${formatDisplayDate(date)} (hoy)` : scraped ? `${formatDisplayDate(date)} ✓` : formatDisplayDate(date);
    if (!scraped && !isToday) { opt.disabled = true; opt.style.color = '#aaa'; }
    if (date === target && !opt.disabled) opt.selected = true;
    sel.appendChild(opt);
  });

  if (!state.agenda.selectedDate && prev) state.agenda.selectedDate = prev;
}

async function loadAgendaForSelectedDate() {
  const date = state.agenda.selectedDate;
  const st = document.getElementById('scrapeStatus');
  if (!date) { state.agenda.slots = []; renderAgenda(); return; }

  const today = todayStr();
  const isFuture = date > today;
  st.textContent = isFuture ? 'Scraping en tiempo real...' : 'Cargando...';
  state.agenda.loading = true;

  try {
    const resp = await fetch(`/api/agenda?date=${date}`);
    if (resp.ok) {
      state.agenda.slots = await resp.json();
      const scraped = state.agenda.slots.filter(s => !s.isEstimated).length;
      const estimated = state.agenda.slots.filter(s => s.isEstimated).length;
      st.textContent = `${scraped} en agenda${estimated > 0 ? ` + ${estimated} estimados` : ''}`;
    } else {
      const err = await resp.json().catch(() => ({}));
      st.textContent = err.detail ?? `Error ${resp.status}`;
      state.agenda.slots = [];
    }
  } catch (e) {
    st.textContent = `Error: ${e.message}`;
    state.agenda.slots = [];
  } finally {
    state.agenda.loading = false;
    renderAgenda();
  }
}

function refreshAgendaView() {
  if (!state.homeData) return;
  buildAgendaCenterFilter();
  if (!state.agenda.selectedDate) state.agenda.selectedDate = todayStr();
  populateAgendaDateSelect();
  if (state.agenda.selectedDate) loadAgendaForSelectedDate();
  else renderAgenda();
}

function buildAgendaCenterFilter() {
  const centers = [...new Set(
    (state.homeData?.configuration?.machineCapacities ?? []).map(c => c.centerName)
  )].sort();
  const row = document.getElementById('agendaCenterFilterPills');
  row.innerHTML = '';
  row.appendChild(makePill('Todos', state.agenda.centerFilter === null, () => {
    state.agenda.centerFilter = null; renderAgenda();
  }));
  centers.forEach(c => row.appendChild(
    makePill(c, state.agenda.centerFilter === c, () => {
      state.agenda.centerFilter = c; renderAgenda();
    })
  ));
}

function renderAgenda() {
  renderAgendaMachineCards();
  renderAgendaDetail();

  document.querySelectorAll('#agendaCenterFilterPills .pill-btn').forEach(btn => {
    btn.classList.toggle('active',
      btn.textContent === 'Todos'
        ? state.agenda.centerFilter === null
        : btn.textContent === state.agenda.centerFilter);
  });
}

function renderAgendaMachineCards() {
  const container = document.getElementById('agendaMachineCards');
  container.innerHTML = '';
  if (!state.homeData) return;

  const caps = (state.homeData.configuration?.machineCapacities ?? [])
    .filter(c => !state.agenda.centerFilter || c.centerName === state.agenda.centerFilter);

  const centerGroups = new Map();
  caps.forEach(c => {
    if (!centerGroups.has(c.centerName)) centerGroups.set(c.centerName, []);
    centerGroups.get(c.centerName).push(c);
  });

  centerGroups.forEach((machines, centerName) => {
    const section = document.createElement('div');
    section.className = 'agenda-center-section';
    section.innerHTML = `<div class="agenda-center-label">${centerName}</div>`;

    const grid = document.createElement('div');
    grid.className = 'agenda-machines-grid';

    machines.forEach(cap => {
      const slots = state.agenda.slots.filter(s => s.machineName === cap.machineName);
      const scraped = slots.filter(s => !s.isEstimated).length;
      const estimated = slots.filter(s => s.isEstimated).length;
      const freeMin = freeMinutes(cap, scraped);
      const totalSlots = Math.floor((Number(cap.workingHours) - Number(cap.reservedSpecialHours)) * 60 / cap.standardSlotMinutes);
      const freeSlots = Math.floor(freeMin / cap.standardSlotMinutes);
      const capCls = capacityClass(freeSlots, totalSlots);
      const isActive = state.agenda.activeMachine === cap.machineName;
      const shortName = cap.machineName.replace(/^[^-]+ - /, '');

      const card = document.createElement('div');
      card.className = `machine-card ${capCls}${isActive ? ' active' : ''}`;
      card.innerHTML =
        `<div class="machine-card-name">${shortName}</div>` +
        `<div class="machine-card-stats">` +
          `<span>${scraped} pac</span>` +
          (estimated > 0 ? `<span class="est-badge">+${estimated} est.</span>` : '') +
        `</div>` +
        `<div class="machine-card-free">${formatMinutes(freeMin)} libre · ${freeSlots} t.</div>`;

      card.addEventListener('click', () => {
        state.agenda.activeMachine = isActive ? null : cap.machineName;
        renderAgenda();
      });
      grid.appendChild(card);
    });

    section.appendChild(grid);
    container.appendChild(section);
  });
}

function renderAgendaDetail() {
  const panel = document.getElementById('agendaDetail');

  if (!state.agenda.activeMachine || !state.agenda.selectedDate) {
    panel.innerHTML = '<p class="detail-placeholder">Seleccione un equipo para ver su agenda.</p>';
    return;
  }

  const today = todayStr();
  const isToday = state.agenda.selectedDate === today;
  const machineSlots = state.agenda.slots.filter(s => s.machineName === state.agenda.activeMachine);

  panel.innerHTML = '';
  const shortName = state.agenda.activeMachine.replace(/^[^-]+ - /, '');
  panel.appendChild(el('div', 'detail-title', `${shortName} · ${state.agenda.selectedDate}`));

  if (state.agenda.loading) {
    panel.appendChild(el('p', 'detail-placeholder', 'Cargando...'));
    return;
  }

  const scraped = machineSlots.filter(s => !s.isEstimated);
  const estimated = machineSlots.filter(s => s.isEstimated);

  if (scraped.length === 0 && (isToday || estimated.length === 0)) {
    panel.appendChild(el('p', 'detail-placeholder',
      isToday ? 'Sin turnos en agenda para hoy.' : 'Sin turnos para esta fecha.'));
    if (!isToday && estimated.length === 0) return;
  }

  // Sort scraped by start time, estimated at end
  scraped.sort((a, b) => (a.startTime || 'zzz').localeCompare(b.startTime || 'zzz'));

  const all = [...scraped, ...estimated];
  all.forEach(slot => {
    const row = document.createElement('div');
    row.className = `slot-row ${slot.isEstimated ? 'estimated' : 'in-agenda'}`;
    let displayName;
    if (!slot.patientName || slot.patientName === '~' || slot.patientName === '-') {
      displayName = '<em style="color:var(--muted)">(sin nombre)</em>';
    } else {
      const guid = findPatientGuid(slot.patientName);
      displayName = guid
        ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${guid}/overview" target="_blank" rel="noopener noreferrer">${slot.patientName}</a>`
        : slot.patientName;
    }
    row.innerHTML =
      `<span class="slot-time">${slot.startTime || '~'}</span>` +
      `<span class="slot-patient">${displayName}</span>` +
      (slot.isEstimated
        ? `<span class="slot-badge rosa">inicio estimado${slot.estimatedFromStage ? ` · ${slot.estimatedFromStage}` : ''}</span>`
        : `<span class="slot-badge celeste">en agenda</span>`);
    panel.appendChild(row);
  });
}


function populateAgendaTestControls(data) {
  const sel = document.getElementById('agendaTestMachine');
  if (!sel) return;
  sel.innerHTML = '';
  (data.configuration?.machines ?? []).forEach(m => {
    const opt = document.createElement('option');
    opt.value = m.displayName; opt.textContent = m.displayName;
    sel.appendChild(opt);
  });
  const di = document.getElementById('agendaTestDate');
  if (di && !di.value) di.valueAsDate = new Date();

  const tsel = document.getElementById('tomographTestCenter');
  if (tsel) {
    tsel.innerHTML = '';
    (data.configuration?.tomographCapacities ?? []).forEach(c => {
      const opt = document.createElement('option');
      opt.value = c.centerName; opt.textContent = c.centerName;
      tsel.appendChild(opt);
    });
  }
  const tdi = document.getElementById('tomographTestDate');
  if (tdi && !tdi.value) tdi.valueAsDate = new Date();
}

// ── Tomograph Agenda tab ──────────────────────────────────────────────────────

async function loadAvailableTomographDates() {
  try {
    const resp = await fetch('/api/tomograph-agenda/available-dates');
    state.tomographAgenda.availableDates = resp.ok ? await resp.json() : [];
  } catch { state.tomographAgenda.availableDates = []; }
  populateTomographDateSelect();
}

function populateTomographDateSelect() {
  const today = todayStr();
  const futureScraped = state.tomographAgenda.availableDates.filter(d => d >= today).sort();
  const lastScraped = futureScraped.length > 0 ? futureScraped[futureScraped.length - 1] : today;

  const all = [];
  const d = new Date(today + 'T00:00:00');
  const last = new Date(lastScraped + 'T00:00:00');
  while (d <= last) {
    all.push(`${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}`);
    d.setDate(d.getDate() + 1);
  }

  const sel = document.getElementById('tomographDateSelect');
  const prev = sel.value;
  const target = state.tomographAgenda.selectedDate || prev;
  sel.innerHTML = '<option value="">-- elegir fecha --</option>';
  all.forEach(date => {
    const opt = document.createElement('option');
    opt.value = date;
    const scraped = state.tomographAgenda.availableDates.includes(date);
    const isToday = date === today;
    opt.textContent = isToday ? `${formatDisplayDate(date)} (hoy)` : scraped ? `${formatDisplayDate(date)} ✓` : formatDisplayDate(date);
    if (!scraped && !isToday) { opt.disabled = true; opt.style.color = '#aaa'; }
    if (date === target && !opt.disabled) opt.selected = true;
    sel.appendChild(opt);
  });

  if (!state.tomographAgenda.selectedDate && prev) state.tomographAgenda.selectedDate = prev;
}

function refreshTomographAgendaView() {
  if (!state.homeData) return;
  buildTomographCenterFilter();
  if (!state.tomographAgenda.selectedDate) state.tomographAgenda.selectedDate = todayStr();
  populateTomographDateSelect();
  if (state.tomographAgenda.selectedDate) loadTomographAgendaForSelectedDate();
  else renderTomographAgenda();
}

function buildTomographCenterFilter() {
  const centers = [...new Set(
    (state.homeData?.configuration?.tomographCapacities ?? []).map(c => c.centerName)
  )].sort();
  const row = document.getElementById('tomographCenterFilterPills');
  row.innerHTML = '';
  row.appendChild(makePill('Todos', state.tomographAgenda.centerFilter === null, () => {
    state.tomographAgenda.centerFilter = null; renderTomographAgenda();
  }));
  centers.forEach(c => row.appendChild(
    makePill(c, state.tomographAgenda.centerFilter === c, () => {
      state.tomographAgenda.centerFilter = c; renderTomographAgenda();
    })
  ));
}

async function loadTomographAgendaForSelectedDate() {
  const date = state.tomographAgenda.selectedDate;
  const st = document.getElementById('scrapeTomographStatus');
  if (!date) { state.tomographAgenda.slots = []; renderTomographAgenda(); return; }

  const today = todayStr();
  const isFuture = date > today;
  st.textContent = isFuture ? 'Scraping en tiempo real...' : 'Cargando...';
  state.tomographAgenda.loading = true;

  try {
    const resp = await fetch(`/api/tomograph-agenda?date=${date}`);
    if (resp.ok) {
      state.tomographAgenda.slots = await resp.json();
      st.textContent = `${state.tomographAgenda.slots.length} en agenda`;
    } else {
      const err = await resp.json().catch(() => ({}));
      st.textContent = err.detail ?? `Error ${resp.status}`;
      state.tomographAgenda.slots = [];
    }
  } catch (e) {
    st.textContent = `Error: ${e.message}`;
    state.tomographAgenda.slots = [];
  } finally {
    state.tomographAgenda.loading = false;
    renderTomographAgenda();
  }
}

function renderTomographAgenda() {
  renderTomographMachineCards();
  renderTomographAgendaDetail();

  document.querySelectorAll('#tomographCenterFilterPills .pill-btn').forEach(btn => {
    btn.classList.toggle('active',
      btn.textContent === 'Todos'
        ? state.tomographAgenda.centerFilter === null
        : btn.textContent === state.tomographAgenda.centerFilter);
  });
}

function renderTomographMachineCards() {
  const container = document.getElementById('tomographMachineCards');
  container.innerHTML = '';
  if (!state.homeData) return;

  const caps = (state.homeData.configuration?.tomographCapacities ?? [])
    .filter(c => !state.tomographAgenda.centerFilter || c.centerName === state.tomographAgenda.centerFilter);

  const centerGroups = new Map();
  caps.forEach(c => {
    if (!centerGroups.has(c.centerName)) centerGroups.set(c.centerName, []);
    centerGroups.get(c.centerName).push(c);
  });

  centerGroups.forEach((tomographs, centerName) => {
    const section = document.createElement('div');
    section.className = 'agenda-center-section';
    section.innerHTML = `<div class="agenda-center-label">${centerName}</div>`;

    tomographs.forEach(cap => {
      const slots = state.tomographAgenda.slots.filter(s => s.machineName === cap.machineName);
      const scraped = slots.length;
      const freeMin = freeMinutes(cap, scraped);
      const totalSlots = Math.floor((Number(cap.workingHours) - Number(cap.reservedSpecialHours)) * 60 / cap.standardSlotMinutes);
      const freeSlots = Math.floor(freeMin / cap.standardSlotMinutes);
      const capCls = capacityClass(freeSlots, totalSlots);
      const isActive = state.tomographAgenda.activeTomograph === cap.machineName;
      const shortName = cap.machineName.replace(/^[^-]+ - /, '');

      const card = document.createElement('div');
      card.className = `machine-card ${capCls}${isActive ? ' active' : ''}`;
      card.innerHTML =
        `<div class="machine-card-name">${shortName}</div>` +
        `<div class="machine-card-stats"><span>${scraped} pac</span></div>` +
        `<div class="machine-card-free">${formatMinutes(freeMin)} libre · ${freeSlots} t.</div>`;

      card.addEventListener('click', () => {
        state.tomographAgenda.activeTomograph = isActive ? null : cap.machineName;
        renderTomographAgenda();
      });
      section.appendChild(card);
    });

    container.appendChild(section);
  });
}

function renderTomographAgendaDetail() {
  const panel = document.getElementById('tomographDetail');

  if (!state.tomographAgenda.activeTomograph || !state.tomographAgenda.selectedDate) {
    panel.innerHTML = '<p class="detail-placeholder">Seleccione un tomografo para ver su agenda.</p>';
    return;
  }

  const machineSlots = state.tomographAgenda.slots.filter(s => s.machineName === state.tomographAgenda.activeTomograph);
  panel.innerHTML = '';
  const shortName = state.tomographAgenda.activeTomograph.replace(/^[^-]+ - /, '');
  panel.appendChild(el('div', 'detail-title', `${shortName} · ${state.tomographAgenda.selectedDate}`));

  if (state.tomographAgenda.loading) {
    panel.appendChild(el('p', 'detail-placeholder', 'Cargando...'));
    return;
  }

  if (machineSlots.length === 0) {
    panel.appendChild(el('p', 'detail-placeholder', 'Sin turnos para esta fecha.'));
    return;
  }

  machineSlots.sort((a, b) => (a.startTime || 'zzz').localeCompare(b.startTime || 'zzz'));
  machineSlots.forEach(slot => {
    const row = document.createElement('div');
    row.className = 'slot-row in-agenda';
    let displayName;
    if (!slot.patientName || slot.patientName === '~' || slot.patientName === '-') {
      displayName = '<em style="color:var(--muted)">(sin nombre)</em>';
    } else {
      const guid = findPatientGuid(slot.patientName);
      displayName = guid
        ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${guid}/overview" target="_blank" rel="noopener noreferrer">${slot.patientName}</a>`
        : slot.patientName;
    }
    row.innerHTML =
      `<span class="slot-time">${slot.startTime || '~'}</span>` +
      `<span class="slot-patient">${displayName}</span>` +
      `<span class="slot-badge celeste">${slot.treatment || 'en agenda'}</span>`;
    panel.appendChild(row);
  });
}


// ── Config editable ───────────────────────────────────────────────────────────

async function loadConfigData() {
  try {
    const resp = await fetch('/api/configuration');
    if (resp.ok) { state.configData = await resp.json(); renderConfig(); }
  } catch { /* ignore */ }
}

function renderConfig() {
  if (!state.configData) return;

  // General
  const gen = document.getElementById('generalConfigSection');
  gen.innerHTML = '';
  const genRow = document.createElement('article');
  genRow.className = 'config-row';
  genRow.innerHTML =
    `<strong>Dias espera larga (Long Wait)</strong>` +
    `<span>Pacientes con mas dias se muestran en gris</span>` +
    `<input type="number" class="config-input-number" id="cfgLongWait" min="1" value="${state.configData.longWaitThresholdDays ?? 40}">`;
  gen.appendChild(genRow);

  const scrapeRow = document.createElement('article');
  scrapeRow.className = 'config-row';
  scrapeRow.innerHTML =
    `<strong>Dias de scrap de agenda</strong>` +
    `<span>Dias habiles proximos que se scrapearan al actualizar</span>` +
    `<input type="number" class="config-input-number" id="cfgScrapeDays" min="1" max="60" value="${state.configData.upcomingScrapeDays ?? 15}">`;
  gen.appendChild(scrapeRow);

  // Stages
  const stageList = document.getElementById('stageConfigList');
  stageList.innerHTML = '';
  (state.configData.stages ?? []).forEach(s => {
    const row = document.createElement('article');
    row.className = 'config-row';
    row.innerHTML =
      `<strong>${s.code} – ${s.displayName}</strong>` +
      `<span>${s.groupName}</span>` +
      `<label>Dias ref: <input type="number" class="config-input-number" id="cfgStage_${s.code}" min="1" value="${s.expectedDays}"></label>`;
    stageList.appendChild(row);
  });

  // Machine capacities
  renderCapacityList('machineConfigList', state.configData.machineCapacities ?? [], 'mach');

  // Tomograph capacities
  renderCapacityList('tomographConfigList', state.configData.tomographCapacities ?? [], 'tomo');
}

function renderCapacityList(containerId, capacities, prefix) {
  const list = document.getElementById(containerId);
  list.innerHTML = '';
  capacities.forEach((c, i) => {
    const row = document.createElement('article');
    row.className = 'config-row';
    const shortName = c.machineName.replace(/^[^-]+ - /, '');
    row.innerHTML =
      `<strong>${shortName}</strong>` +
      `<span>${c.centerName}</span>` +
      `<label>Hs: <input type="number" class="config-input-number" id="${prefix}_hs_${i}" min="1" step="0.5" value="${c.workingHours}"></label>` +
      `<label>Min turno: <input type="number" class="config-input-number" id="${prefix}_slot_${i}" min="1" value="${c.standardSlotMinutes}"></label>` +
      `<label>Hs reservadas: <input type="number" class="config-input-number" id="${prefix}_res_${i}" min="0" step="0.5" value="${c.reservedSpecialHours}"></label>`;
    list.appendChild(row);
  });
}

async function saveConfig() {
  if (!state.configData) return;
  const btn = document.getElementById('saveConfigBtn');
  const st = document.getElementById('saveConfigStatus');
  btn.disabled = true; st.textContent = 'Guardando...';

  // Read general
  const lwInput = document.getElementById('cfgLongWait');
  if (lwInput) state.configData.longWaitThresholdDays = parseInt(lwInput.value) || state.configData.longWaitThresholdDays;
  const sdInput = document.getElementById('cfgScrapeDays');
  if (sdInput) state.configData.upcomingScrapeDays = parseInt(sdInput.value) || state.configData.upcomingScrapeDays;

  // Read stages
  (state.configData.stages ?? []).forEach(s => {
    const inp = document.getElementById(`cfgStage_${s.code}`);
    if (inp) s.expectedDays = parseInt(inp.value) || s.expectedDays;
  });

  // Read machine capacities
  (state.configData.machineCapacities ?? []).forEach((c, i) => {
    const hs = document.getElementById(`mach_hs_${i}`);
    const slot = document.getElementById(`mach_slot_${i}`);
    const res = document.getElementById(`mach_res_${i}`);
    if (hs)   c.workingHours        = parseFloat(hs.value)   || c.workingHours;
    if (slot) c.standardSlotMinutes = parseInt(slot.value)   || c.standardSlotMinutes;
    if (res)  c.reservedSpecialHours = parseFloat(res.value);
  });

  // Read tomograph capacities
  (state.configData.tomographCapacities ?? []).forEach((c, i) => {
    const hs = document.getElementById(`tomo_hs_${i}`);
    const slot = document.getElementById(`tomo_slot_${i}`);
    const res = document.getElementById(`tomo_res_${i}`);
    if (hs)   c.workingHours        = parseFloat(hs.value)   || c.workingHours;
    if (slot) c.standardSlotMinutes = parseInt(slot.value)   || c.standardSlotMinutes;
    if (res)  c.reservedSpecialHours = parseFloat(res.value);
  });

  try {
    const resp = await fetch('/api/configuration', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(state.configData)
    });
    if (resp.ok) {
      state.configData = await resp.json();
      st.textContent = 'Guardado correctamente.';
    } else {
      st.textContent = `Error ${resp.status}`;
    }
  } catch (e) {
    st.textContent = `Error: ${e.message}`;
  } finally {
    btn.disabled = false;
  }
}

// ── Boot ──────────────────────────────────────────────────────────────────────

wireTabs();
wireActions();
loadAvailableDates();
loadAvailableTomographDates();
loadHome();
