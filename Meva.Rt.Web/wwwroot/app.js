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
  }
};

// ── Utilities ─────────────────────────────────────────────────────────────────

function todayStr() {
  const d = new Date();
  return `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}`;
}
function pad(n) { return String(n).padStart(2, '0'); }

function delayClass(days, expected) {
  if (days < expected) return 'on-time';
  if (days === expected) return 'at-limit';
  return 'delayed';
}

function delayDot(days, expected) {
  const cls = delayClass(days, expected);
  const col = cls === 'on-time' ? 'dot-green' : cls === 'at-limit' ? 'dot-yellow' : 'dot-red';
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
    });
  });
}

// ── Actions ───────────────────────────────────────────────────────────────────

function wireActions() {
  document.getElementById('runScrapingTest').addEventListener('click', runScrapingTest);
  document.getElementById('runAgendaTest').addEventListener('click', runAgendaTest);
  document.getElementById('refreshDashboard').addEventListener('click', refreshDashboard);
  document.getElementById('refreshDashboardNoAria').addEventListener('click', refreshDashboardNoAria);
  document.getElementById('importAria').addEventListener('click', importAriaResults);
  document.getElementById('scrapeUpcomingBtn').addEventListener('click', scrapeUpcoming);
  document.getElementById('agendaDateSelect').addEventListener('change', e => {
    state.agenda.selectedDate = e.target.value || null;
    state.agenda.activeMachine = null;
    loadAgendaForSelectedDate();
  });
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

async function refreshDashboard() {
  const btn = document.getElementById('refreshDashboard');
  btn.disabled = true;
  document.getElementById('generatedAt').textContent = 'Actualizando...';
  try {
    const resp = await fetch('/api/home/refresh', { method: 'POST' });
    if (!resp.ok) { document.getElementById('generatedAt').textContent = `Error ${resp.status}`; return; }
    renderHome(await resp.json());
  } finally { btn.disabled = false; }
}

async function refreshDashboardNoAria() {
  const btn = document.getElementById('refreshDashboardNoAria');
  btn.disabled = true;
  document.getElementById('generatedAt').textContent = 'Actualizando...';
  try {
    const resp = await fetch('/api/home/refresh-no-aria', { method: 'POST' });
    if (!resp.ok) { document.getElementById('generatedAt').textContent = `Error ${resp.status}`; return; }
    renderHome(await resp.json());
  } finally { btn.disabled = false; }
}

async function importAriaResults() {
  const btn = document.getElementById('importAria');
  const st = document.getElementById('importAriaStatus');
  btn.disabled = true; st.className = 'import-result'; st.textContent = 'Importando...';
  try {
    const resp = await fetch('/api/aria/import-results', { method: 'POST' });
    const r = await resp.json();
    st.className = `import-result ${resp.ok ? 'ok' : 'error'}`;
    st.textContent = resp.ok
      ? `${r.importedFile} · ${r.withActivePlan} con plan · ${r.withMachineResolved} con equipo`
      : (r.error ?? 'Error al importar.');
  } catch (e) { st.className = 'import-result error'; st.textContent = `Error: ${e.message}`; }
  finally { btn.disabled = false; }
}

async function scrapeUpcoming() {
  const btn = document.getElementById('scrapeUpcomingBtn');
  const st = document.getElementById('scrapeStatus');
  btn.disabled = true; st.textContent = 'Scraping 15 dias habiles...';
  try {
    const resp = await fetch('/api/agenda/scrape-upcoming?days=15', { method: 'POST' });
    const r = await resp.json();
    st.textContent = resp.ok
      ? `Guardados: ${r.savedDates?.join(', ') ?? r.totalDays + ' dias'}`
      : (r.detail ?? `Error ${resp.status}`);
    if (resp.ok) await loadAvailableDates();
  } catch (e) { st.textContent = `Error: ${e.message}`; }
  finally { btn.disabled = false; }
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
  renderConfig(data);
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
    const pats = patients.filter(p => p.stageCode === def.code);
    const isEmpty = pats.length === 0;
    const avg = isEmpty ? 0 : pats.reduce((s, p) => s + p.daysInStage, 0) / pats.length;
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
      state.followup.stageFilter = null;
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
      const dc = delayClass(p.daysInStage, p.expectedDaysInStage);
      const row = document.createElement('article');
      row.className = `patient-row ${dc}`;
      row.innerHTML =
        delayDot(p.daysInStage, p.expectedDaysInStage) +
        `<div class="patient-main">` +
          `<strong>${p.patientName}</strong>` +
          (p.patientId ? `<span class="hc-tag">HC ${p.patientId}</span>` : '') +
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
      const dc = delayClass(p.daysInStage, p.expectedDaysInStage);
      const row = document.createElement('article');
      row.className = `patient-row ${dc}`;
      row.innerHTML =
        delayDot(p.daysInStage, p.expectedDaysInStage) +
        `<strong>${p.patientName}</strong>` +
        (p.patientId ? `<span class="hc-tag">HC ${p.patientId}</span>` : '') +
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
      const dc = delayClass(p.daysInStage, p.expectedDaysInStage);
      const row = document.createElement('article');
      row.className = `patient-row ${dc}`;
      row.innerHTML =
        delayDot(p.daysInStage, p.expectedDaysInStage) +
        `<div class="patient-main">` +
          `<strong>${p.patientName}</strong>` +
          (p.patientId ? `<span class="hc-tag">HC ${p.patientId}</span>` : '') +
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

function el(tag, cls, text) {
  const e = document.createElement(tag);
  e.className = cls;
  e.textContent = text;
  return e;
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
  // Generate next 20 calendar days
  const upcoming = [];
  const d = new Date();
  for (let i = 1; i <= 20; i++) {
    d.setDate(d.getDate() + 1);
    upcoming.push(`${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}`);
  }
  const all = [...new Set([today, ...state.agenda.availableDates, ...upcoming])].sort();

  const sel = document.getElementById('agendaDateSelect');
  const prev = sel.value;
  sel.innerHTML = '<option value="">-- elegir fecha --</option>';
  all.forEach(date => {
    const opt = document.createElement('option');
    opt.value = date;
    const scraped = state.agenda.availableDates.includes(date);
    opt.textContent = date === today
      ? `${date} (hoy)`
      : scraped ? `${date} ✓` : date;
    if (date === prev) opt.selected = true;
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
    const displayName = slot.patientName && slot.patientName !== '~' && slot.patientName !== '-'
      ? slot.patientName : '<em style="color:var(--muted)">(sin nombre)</em>';
    row.innerHTML =
      `<span class="slot-time">${slot.startTime || '~'}</span>` +
      `<span class="slot-patient">${displayName}</span>` +
      (slot.isEstimated
        ? `<span class="slot-badge rosa">inicio estimado${slot.estimatedFromStage ? ` · ${slot.estimatedFromStage}` : ''}</span>`
        : `<span class="slot-badge celeste">en agenda</span>`);
    panel.appendChild(row);
  });
}

// ── Config tab ────────────────────────────────────────────────────────────────

function renderConfig(data) {
  const stageList = document.getElementById('stageConfigList');
  stageList.innerHTML = '';
  (data.configuration?.stages ?? []).forEach(s => {
    const row = document.createElement('article');
    row.className = 'config-row';
    row.innerHTML = `<strong>${s.code} - ${s.displayName}</strong><span>${s.groupName}</span><small>Referencia ${s.expectedDays} dias | microstatus ${s.sitraMicroStatus}</small>`;
    stageList.appendChild(row);
  });

  const machList = document.getElementById('machineConfigList');
  machList.innerHTML = '';
  (data.configuration?.machineCapacities ?? []).forEach(c => {
    const row = document.createElement('article');
    row.className = 'config-row';
    row.innerHTML = `<strong>${c.machineName}</strong><span>${c.centerName}</span><small>${c.workingHours}hs | turno ${c.standardSlotMinutes}min | reservadas ${c.reservedSpecialHours}hs</small>`;
    machList.appendChild(row);
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
}

// ── Boot ──────────────────────────────────────────────────────────────────────

wireTabs();
wireActions();
loadAvailableDates();
loadHome();
