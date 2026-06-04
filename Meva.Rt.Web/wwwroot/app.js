// ── State ─────────────────────────────────────────────────────────────────────

const state = {
  activeTab: 'resumen',
  homeData: null,

  followup: {
    centerFilter: null,
    stageFilter: null,
    profesionalFilter: null,
    searchQuery: '',
    activeCenter: null,
    activeStage: null
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

  configData: null,

  resumen: {
    weeklyData: null
  },

  fisica: {
    selectedCenters: new Set(),
    selectedTask: null,
    selectedPhysicist: null
  }
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

function renderTreatmentLabel(item) {
  const label = item.treatmentLabel;
  if (!label) return '';
  // Usar el primer token del label como clave CSS (ej: "IMRT" de "IMRT - estático")
  const cssKey = label.split(/[\s-]/)[0];
  return `<span class="treatment-badge tt-${cssKey}">${label}</span>`;
}

function isExcludedTechnique(p) {
  return p.treatmentTechnique === 'BQT' || p.treatmentTechnique === 'IORT';
}

function isAriaEnabled(centerName) {
  const c = (state.homeData?.centers ?? []).find(x => x.name === centerName);
  return c ? c.ariaEnabled !== false : true;
}

function ariaBadges(p) {
  if (!isAriaEnabled(p.centerName)) return '';
  return p.plannedMachineDisplayName
    ? `<span class="aria-machine">▸ ${p.plannedMachineDisplayName}</span>`
    : '';
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

// ── Professional / Fisica constants ──────────────────────────────────────────

const PROFESSIONAL_STAGE_MAP = {
  Medicos: ['F5', 'F6C'],
  Fisicos: ['F6A', 'F6B', 'F6F', 'F6G', 'F7A', 'F7C']
};
const PHYSICS_STAGES = PROFESSIONAL_STAGE_MAP.Fisicos;

// ── Tabs ──────────────────────────────────────────────────────────────────────

function wireTabs() {
  document.querySelectorAll('nav.tabs .tab-button[data-tab]').forEach(btn => {
    btn.addEventListener('click', () => {
      state.activeTab = btn.dataset.tab;
      document.querySelectorAll('nav.tabs .tab-button[data-tab]').forEach(b =>
        b.classList.toggle('active', b === btn));
      document.querySelectorAll('.tab-panel').forEach(p =>
        p.classList.toggle('active', p.id === `tab-${state.activeTab}`));
      if (state.activeTab === 'resumen') renderResumen();
      if (state.activeTab === 'agenda') refreshAgendaView();
      if (state.activeTab === 'tomograph') refreshTomographAgendaView();
      if (state.activeTab === 'config') loadConfigData();
      if (state.activeTab === 'fisica') renderFisicaView();
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
      document.getElementById('generatedAt').textContent = 'Aplicando datos ARIA...';
      const applyResp = await fetch('/api/home/apply-aria', { method: 'POST' });
      if (applyResp.ok) renderHome(await applyResp.json());
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
  buildFisicaCenterFilter();
  renderFisicaView();
  populateAgendaTestControls(data);
  renderResumen();
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
  buildProfesionalFilterPills();
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

function buildProfesionalFilterPills() {
  const row = document.getElementById('profesionalFilterPills');
  if (!row) return;
  row.innerHTML = '';
  row.appendChild(makePill('Todos', state.followup.profesionalFilter === null, () => {
    state.followup.profesionalFilter = null;
    state.followup.stageFilter = null;
    state.followup.activeCenter = null;
    state.followup.activeStage = null;
    renderFollowUp();
  }));
  ['Medicos', 'Fisicos'].forEach(prof => row.appendChild(
    makePill(prof, state.followup.profesionalFilter === prof, () => {
      state.followup.profesionalFilter = prof;
      state.followup.stageFilter = null;
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

  // Sync profesional pills
  document.querySelectorAll('#profesionalFilterPills .pill-btn').forEach(btn => {
    btn.classList.toggle('active',
      btn.textContent === 'Todos'
        ? state.followup.profesionalFilter === null
        : btn.textContent === state.followup.profesionalFilter);
  });

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

    // Build per-center lookup: Map<centerName, Map<stageCode, StageSummaryItem>>
    const summaryByCenter = new Map();
    (state.homeData.stageSummary ?? []).forEach(s => {
      if (!summaryByCenter.has(s.centerName)) summaryByCenter.set(s.centerName, new Map());
      summaryByCenter.get(s.centerName).set(s.stageCode, s);
    });

    visibleCenters.forEach(c => {
      container.appendChild(buildCenterCard(c, summaryByCenter.get(c) ?? new Map(), stageOrder));
    });
  }

  renderFollowupDetail();
}

function buildCenterCard(centerName, stageSummary, stageOrder) {
  let totalCount = 0, delayedTotal = 0;
  stageSummary.forEach(s => { totalCount += s.patientCount ?? 0; delayedTotal += s.delayedCount ?? 0; });

  const card = document.createElement('section');
  card.className = 'center-card';

  const noAria = !isAriaEnabled(centerName);
  card.innerHTML = `
    <div class="center-card-header">
      <span class="center-name">${centerName}${noAria ? ' <span class="no-aria-badge">Sin ARIA</span>' : ''}</span>
      <span class="center-counts">
        ${totalCount} pac
        ${delayedTotal > 0 ? `<span class="dot dot-red"></span><span class="count-delayed">${delayedTotal} dem.</span>` : ''}
      </span>
    </div>`;

  // Mostrar TODAS las etapas en orden, incluso las que tienen 0 pacientes
  const stageDefs = [...(state.homeData.stages ?? [])].sort(
    (a, b) => (stageOrder[a.code] ?? 999) - (stageOrder[b.code] ?? 999));

  stageDefs.forEach(def => {
    if (state.followup.stageFilter && def.code !== state.followup.stageFilter) return;
    if (state.followup.profesionalFilter &&
        !(PROFESSIONAL_STAGE_MAP[state.followup.profesionalFilter] ?? []).includes(def.code)) return;
    const s = stageSummary.get(def.code);
    const count = s?.patientCount ?? 0;
    const isEmpty = count === 0;
    const avg = s?.averageDaysInStage ?? 0;
    const hasDelayed = (s?.delayedCount ?? 0) > 0;
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
      `<span class="stage-row-stats">${isEmpty ? '—' : `${count} pac (${avg.toFixed(1)}d)`}</span>` +
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
      !isExcludedTechnique(p) &&
      (p.patientName?.toLowerCase().includes(q) || p.patientId?.toLowerCase().includes(q)));

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
          (p.assignedPhysicist && p.stageCode !== 'F6A' ? `<span class="physicist-tag">(asignado a: ${p.assignedPhysicist})</span>` : '') +
          `<span class="patient-context">${p.centerName} · ${def?.displayName ?? p.stageCode}</span>` +
        `</div>` +
        renderTreatmentLabel(p) +
        ariaBadges(p) +
        `<span class="days-badge ${dc}">${p.daysInStage}d</span>`;
      panel.appendChild(row);
    });
    return;
  }

  // Stage detail
  if (state.followup.activeCenter && state.followup.activeStage) {
    const def = stageDefs.find(s => s.code === state.followup.activeStage);
    const pats = allPatients.filter(p =>
      !isExcludedTechnique(p) &&
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
          (p.assignedPhysicist && p.stageCode !== 'F6A' ? `<span class="physicist-tag">(asignado a: ${p.assignedPhysicist})</span>` : '') +
        renderTreatmentLabel(p) +
        ariaBadges(p) +
        `<span class="days-badge ${dc}">${p.daysInStage}d</span>`;
      panel.appendChild(row);
    });
    return;
  }

  // Filtro de etapa activo sin fila específica → muestra todos los pacientes de esa etapa
  if (state.followup.stageFilter) {
    const def = stageDefs.find(s => s.code === state.followup.stageFilter);
    let pats = allPatients.filter(p => !isExcludedTechnique(p) && p.stageCode === state.followup.stageFilter);
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
          (p.assignedPhysicist && p.stageCode !== 'F6A' ? `<span class="physicist-tag">(asignado a: ${p.assignedPhysicist})</span>` : '') +
          `<span class="patient-context">${p.centerName}</span>` +
        `</div>` +
        renderTreatmentLabel(p) +
        ariaBadges(p) +
        `<span class="days-badge ${dc}">${p.daysInStage}d</span>`;
      panel.appendChild(row);
    });
    return;
  }

  // Filtro profesional activo sin etapa específica
  if (state.followup.profesionalFilter && !state.followup.stageFilter) {
    const stageCodes = PROFESSIONAL_STAGE_MAP[state.followup.profesionalFilter] ?? [];
    let pats = allPatients.filter(p => !isExcludedTechnique(p) && stageCodes.includes(p.stageCode));
    if (state.followup.centerFilter) pats = pats.filter(p => p.centerName === state.followup.centerFilter);

    panel.innerHTML = '';
    panel.appendChild(el('div', 'detail-title',
      `${state.followup.profesionalFilter} · ${pats.length} pac.`));
    if (pats.length === 0) {
      panel.appendChild(el('p', 'detail-placeholder', 'Sin pacientes.'));
      return;
    }
    stageCodes.forEach(code => {
      const stagePats = pats.filter(p => p.stageCode === code).sort((a, b) => b.daysInStage - a.daysInStage);
      if (stagePats.length === 0) return;
      const def = stageDefs.find(s => s.code === code);
      panel.appendChild(el('div', 'detail-subtitle', `${def?.displayName ?? code} · ${stagePats.length} pac.`));
      stagePats.forEach(p => {
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
            (p.assignedPhysicist && p.stageCode !== 'F6A' ? `<span class="physicist-tag">(asignado a: ${p.assignedPhysicist})</span>` : '') +
            `<span class="patient-context">${p.centerName}</span>` +
          `</div>` +
          renderTreatmentLabel(p) +
          ariaBadges(p) +
          `<span class="days-badge ${dc}">${p.daysInStage}d</span>`;
        panel.appendChild(row);
      });
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
    const isInferred = slot.isEstimated && slot.estimatedSource === 'center';
    row.className = `slot-row ${slot.isEstimated ? (isInferred ? 'inferred' : 'estimated') : 'in-agenda'}`;
    let displayName;
    if (!slot.patientName || slot.patientName === '~' || slot.patientName === '-') {
      displayName = '<em style="color:var(--muted)">(sin nombre)</em>';
    } else {
      const guid = slot.sitraMedGuid || findPatientGuid(slot.patientName);
      displayName = guid
        ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${guid}/overview" target="_blank" rel="noopener noreferrer">${slot.patientName}</a>`
        : slot.patientName;
    }
    let estimatedBadge = '';
    if (slot.isEstimated) {
      if (isInferred) {
        estimatedBadge = `<span class="slot-badge naranja">equipo inferido${slot.estimatedFromStage ? ` · ${slot.estimatedFromStage}` : ''}</span>`;
      } else {
        estimatedBadge = `<span class="slot-badge rosa">estimado (ARIA)${slot.estimatedFromStage ? ` · ${slot.estimatedFromStage}` : ''}</span>`;
      }
    }
    row.innerHTML =
      `<span class="slot-time">${slot.startTime || '~'}</span>` +
      `<span class="slot-patient">${displayName}</span>` +
      renderTreatmentLabel(slot) +
      (slot.isEstimated ? estimatedBadge : `<span class="slot-badge celeste">en agenda</span>`);
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


// ── Fisica tab ────────────────────────────────────────────────────────────────

function buildFisicaCenterFilter() {
  const row = document.getElementById('fisicaCenterFilterPills');
  if (!row) return;
  const centers = [...new Set(
    (state.homeData?.configuration?.machineCapacities ?? []).map(c => c.centerName)
  )].sort();
  row.innerHTML = '';

  const todosActive = state.fisica.selectedCenters.size === 0;
  row.appendChild(makePill('Todos', todosActive, () => {
    state.fisica.selectedCenters.clear();
    renderFisicaView();
  }));
  centers.forEach(c => {
    row.appendChild(makePill(c, state.fisica.selectedCenters.has(c), () => {
      if (state.fisica.selectedCenters.has(c)) state.fisica.selectedCenters.delete(c);
      else state.fisica.selectedCenters.add(c);
      renderFisicaView();
    }));
  });
}

function renderFisicaView() {
  if (!state.homeData) return;

  // Sync pills
  document.querySelectorAll('#fisicaCenterFilterPills .pill-btn').forEach(btn => {
    btn.classList.toggle('active',
      btn.textContent === 'Todos'
        ? state.fisica.selectedCenters.size === 0
        : state.fisica.selectedCenters.has(btn.textContent));
  });

  const allPatients = state.homeData.patients ?? [];
  const stageDefs = state.homeData.stages ?? [];

  const patients = state.fisica.selectedCenters.size === 0
    ? allPatients
    : allPatients.filter(p => state.fisica.selectedCenters.has(p.centerName));

  const physicsPatients = patients.filter(p => PHYSICS_STAGES.includes(p.stageCode) && !isExcludedTechnique(p));

  // Tareas de Fisica card
  const taskContainer = document.getElementById('fisicaTaskCards');
  taskContainer.innerHTML = '';
  const taskCard = document.createElement('section');
  taskCard.className = 'center-card';
  const delayed = physicsPatients.filter(p => p.isDelayed).length;
  taskCard.innerHTML =
    `<div class="center-card-header">` +
      `<span class="center-name">Tareas de Fisica</span>` +
      `<span class="center-counts">${physicsPatients.length} pac` +
        (delayed > 0 ? `<span class="dot dot-red"></span><span class="count-delayed">${delayed} dem.</span>` : '') +
      `</span>` +
    `</div>`;
  PHYSICS_STAGES.forEach(code => {
    const def = stageDefs.find(s => s.code === code);
    const pats = physicsPatients.filter(p => p.stageCode === code);
    const hasDelayed = pats.some(p => p.isDelayed);
    const isEmpty = pats.length === 0;
    const isSelected = state.fisica.selectedTask === code;
    const row = document.createElement('div');
    row.className = `stage-row${isEmpty ? ' stage-empty' : ''}${isSelected ? ' active' : ''}`;
    row.innerHTML =
      `<span class="stage-row-name">${def?.displayName ?? code}</span>` +
      `<span class="stage-row-stats">${isEmpty ? '—' : `${pats.length} pac`}</span>` +
      (hasDelayed ? `<span class="dot dot-red"></span>` : '');
    if (!isEmpty) {
      row.addEventListener('click', () => {
        state.fisica.selectedTask = state.fisica.selectedTask === code ? null : code;
        state.fisica.selectedPhysicist = null;
        renderFisicaView();
      });
    }
    taskCard.appendChild(row);
  });
  taskContainer.appendChild(taskCard);

  // Fisicos asignados card (below task card)
  const physicistPanel = document.getElementById('fisicaPhysicistPanel');
  physicistPanel.innerHTML = '';

  const physicistMap = new Map();
  physicsPatients.forEach(p => {
    if (p.stageCode === 'F6B' && p.assignedPhysicist) {
      physicistMap.set(p.assignedPhysicist, (physicistMap.get(p.assignedPhysicist) ?? 0) + 1);
    } else if (p.stageCode === 'F6A') {
      physicistMap.set('(sin asignar)', (physicistMap.get('(sin asignar)') ?? 0) + 1);
    }
  });

  const assigned = [...physicistMap.entries()]
    .filter(([k]) => k !== '(sin asignar)')
    .sort((a, b) => b[1] - a[1]);
  const sinAsignar = physicistMap.get('(sin asignar)') ?? 0;

  if (assigned.length > 0 || sinAsignar > 0) {
    const physicistCard = document.createElement('section');
    physicistCard.className = 'center-card';
    physicistCard.innerHTML =
      `<div class="center-card-header">` +
        `<span class="center-name">Fisicos asignados</span>` +
        `<span class="center-counts">${assigned.length} fisico${assigned.length !== 1 ? 's' : ''}</span>` +
      `</div>`;

    assigned.forEach(([name, count]) => {
      const row = document.createElement('div');
      const isSelected = state.fisica.selectedPhysicist === name;
      row.className = `fisica-physicist-row${isSelected ? ' active' : ''}`;
      row.innerHTML =
        `<span class="fisica-physicist-name">${name}</span>` +
        `<span class="fisica-physicist-count">${count} pac</span>`;
      row.addEventListener('click', () => {
        state.fisica.selectedPhysicist = state.fisica.selectedPhysicist === name ? null : name;
        state.fisica.selectedTask = null;
        renderFisicaView();
      });
      physicistCard.appendChild(row);
    });

    if (sinAsignar > 0) {
      const row = document.createElement('div');
      const isSelected = state.fisica.selectedPhysicist === '(sin asignar)';
      row.className = `fisica-physicist-row fisica-unassigned${isSelected ? ' active' : ''}`;
      row.innerHTML =
        `<span class="fisica-physicist-name">(sin asignar)</span>` +
        `<span class="fisica-physicist-count">${sinAsignar} pac</span>`;
      row.addEventListener('click', () => {
        state.fisica.selectedPhysicist = state.fisica.selectedPhysicist === '(sin asignar)' ? null : '(sin asignar)';
        state.fisica.selectedTask = null;
        renderFisicaView();
      });
      physicistCard.appendChild(row);
    }

    physicistPanel.appendChild(physicistCard);
  }

  renderFisicaDetail();
}

function renderFisicaDetail() {
  const panel = document.getElementById('fisicaDetailPanel');
  if (!panel || !state.homeData) return;

  const allPatients = state.homeData.patients ?? [];
  const stageDefs = state.homeData.stages ?? [];

  const physicsPatients = (state.fisica.selectedCenters.size === 0
    ? allPatients
    : allPatients.filter(p => state.fisica.selectedCenters.has(p.centerName))
  ).filter(p => PHYSICS_STAGES.includes(p.stageCode) && !isExcludedTechnique(p));

  if (state.fisica.selectedTask) {
    const code = state.fisica.selectedTask;
    const def = stageDefs.find(s => s.code === code);
    const pats = physicsPatients.filter(p => p.stageCode === code).sort((a, b) => b.daysInStage - a.daysInStage);

    panel.innerHTML = '';
    panel.appendChild(el('div', 'detail-title', `${def?.displayName ?? code} · ${pats.length} pac.`));

    if (pats.length === 0) {
      panel.appendChild(el('p', 'detail-placeholder', 'Sin pacientes en esta tarea.'));
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
          (p.assignedPhysicist && code !== 'F6A' ? `<span class="physicist-tag">(asignado a: ${p.assignedPhysicist})</span>` : '') +
          `<span class="patient-context">${p.centerName}</span>` +
        `</div>` +
        renderTreatmentLabel(p) +
        ariaBadges(p) +
        `<span class="days-badge ${dc}">${p.daysInStage}d</span>`;
      panel.appendChild(row);
    });
    return;
  }

  if (state.fisica.selectedPhysicist) {
    const physicistName = state.fisica.selectedPhysicist;
    let pats;
    if (physicistName === '(sin asignar)') {
      pats = physicsPatients.filter(p => p.stageCode === 'F6A');
    } else {
      pats = physicsPatients.filter(p => p.assignedPhysicist === physicistName && p.stageCode === 'F6B');
    }
    pats.sort((a, b) => b.daysInStage - a.daysInStage);

    panel.innerHTML = '';
    panel.appendChild(el('div', 'detail-title', `${physicistName} · ${pats.length} pac.`));

    if (pats.length === 0) {
      panel.appendChild(el('p', 'detail-placeholder', 'Sin pacientes.'));
      return;
    }

    PHYSICS_STAGES.forEach(code => {
      const stagePats = pats.filter(p => p.stageCode === code);
      if (stagePats.length === 0) return;
      const def = stageDefs.find(s => s.code === code);
      panel.appendChild(el('div', 'detail-subtitle', `${def?.displayName ?? code} · ${stagePats.length} pac.`));
      stagePats.forEach(p => {
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
            `<span class="patient-context">${p.centerName}</span>` +
          `</div>` +
          renderTreatmentLabel(p) +
          ariaBadges(p) +
          `<span class="days-badge ${dc}">${p.daysInStage}d</span>`;
        panel.appendChild(row);
      });
    });
    return;
  }

  panel.innerHTML = '';
  panel.appendChild(el('p', 'detail-placeholder', 'Seleccione una tarea o fisico para ver pacientes.'));
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
  renderCapacityList('machineConfigList', state.configData.machineCapacities ?? [], 'mach', state.configData.machineCapabilities ?? []);

  // Tomograph capacities
  renderCapacityList('tomographConfigList', state.configData.tomographCapacities ?? [], 'tomo', null);
}

function renderCapacityList(containerId, capacities, prefix, machCaps) {
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

    if (machCaps) {
      const mc = machCaps.find(m => m.machineName === c.machineName);
      if (mc) {
        const heb = mc.highEnergyBeams ?? [];
        row.innerHTML +=
          `<details class="cap-details">` +
            `<summary>Capacidades técnicas</summary>` +
            `<div class="cap-checkboxes">` +
              `<label><input type="checkbox" id="cap_${i}_vmat" ${mc.canDoVMAT ? 'checked' : ''}> VMAT</label>` +
              `<label><input type="checkbox" id="cap_${i}_electrons" ${mc.canDoElectrons ? 'checked' : ''}> Electrones</label>` +
              `<label><input type="checkbox" id="cap_${i}_sbrt" ${mc.canDoSBRT ? 'checked' : ''}> SBRT</label>` +
              `<label><input type="checkbox" id="cap_${i}_rc" ${mc.canDoRC ? 'checked' : ''}> RC</label>` +
              `<label><input type="checkbox" id="cap_${i}_tbi" ${mc.canDoTBI ? 'checked' : ''}> TBI</label>` +
              `<label><input type="checkbox" id="cap_${i}_tset" ${mc.canDoTSET ? 'checked' : ''}> TSET</label>` +
              `<label><input type="checkbox" id="cap_${i}_igrt" ${mc.canDoIGRT ? 'checked' : ''}> IGRT</label>` +
              `<span class="cap-label">Alta energía:</span>` +
              `<label><input type="checkbox" id="cap_${i}_10x" ${heb.includes('10X') ? 'checked' : ''}> 10X</label>` +
              `<label><input type="checkbox" id="cap_${i}_15x" ${heb.includes('15X') ? 'checked' : ''}> 15X</label>` +
              `<label><input type="checkbox" id="cap_${i}_18x" ${heb.includes('18X') ? 'checked' : ''}> 18X</label>` +
            `</div>` +
          `</details>`;
      }
    }
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

  // Read machine capabilities (index aligned with machineCapacities order by machineName)
  const machCaps = state.configData.machineCapabilities ?? [];
  (state.configData.machineCapacities ?? []).forEach((c, i) => {
    const mc = machCaps.find(m => m.machineName === c.machineName);
    if (!mc) return;
    const cb = name => document.getElementById(`cap_${i}_${name}`)?.checked ?? false;
    mc.canDoVMAT      = cb('vmat');
    mc.canDoElectrons = cb('electrons');
    mc.canDoSBRT      = cb('sbrt');
    mc.canDoRC        = cb('rc');
    mc.canDoTBI       = cb('tbi');
    mc.canDoTSET      = cb('tset');
    mc.canDoIGRT      = cb('igrt');
    mc.highEnergyBeams = ['10X', '15X', '18X'].filter(e => cb(e.toLowerCase()));
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

// ── Resumen tab ───────────────────────────────────────────────────────────────

const RESUMEN_F6B_ALERT = 8;
const RESUMEN_CRITICAL_STAGES = ['F4', 'F6B', 'F6C', 'F7A'];

function mondayOf(isoDate) {
  const d = new Date(isoDate + 'T00:00:00');
  const dow = d.getDay();
  d.setDate(d.getDate() - (dow === 0 ? 6 : dow - 1));
  return `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}`;
}

function last4Weeks() {
  const mon = mondayOf(todayStr());
  const weeks = [];
  for (let i = 3; i >= 0; i--) {
    const d = new Date(mon + 'T00:00:00');
    d.setDate(d.getDate() - i * 7);
    weeks.push(`${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}`);
  }
  return weeks; // oldest → newest
}

function renderResumen() {
  renderResumenEstado();
  renderResumenAlertas();
  renderResumenTendencias();
}

function renderResumenEstado() {
  const container = document.getElementById('resumenEstado');
  if (!state.homeData) { container.innerHTML = '<p class="detail-placeholder">Sin datos.</p>'; return; }

  const centers = [...new Set(
    (state.homeData.configuration?.machineCapacities ?? []).map(c => c.centerName)
  )].sort();

  const summaryByCenter = new Map();
  (state.homeData.stageSummary ?? []).forEach(s => {
    if (!summaryByCenter.has(s.centerName)) summaryByCenter.set(s.centerName, []);
    summaryByCenter.get(s.centerName).push(s);
  });

  const equipsByCenter = new Map();
  (state.homeData.equipments ?? []).forEach(e => {
    if (!equipsByCenter.has(e.centerName)) equipsByCenter.set(e.centerName, []);
    equipsByCenter.get(e.centerName).push(e);
  });

  container.innerHTML = '';
  centers.forEach(centerName => {
    const stages = summaryByCenter.get(centerName) ?? [];
    const totalInProcess = stages.reduce((s, r) => s + (r.patientCount ?? 0) - (r.longWaitCount ?? 0), 0);
    const totalDelayed  = stages.reduce((s, r) => s + (r.delayedCount ?? 0), 0);
    const equips = equipsByCenter.get(centerName) ?? [];

    let machineHtml = '';
    if (equips.length > 0) {
      machineHtml = '<div class="resumen-machine-sep">';
      equips.forEach(eq => {
        const totalSlots = Math.floor((Number(eq.workingHours) - Number(eq.reservedSpecialHours)) * 60 / eq.standardSlotMinutes);
        const freeSlots  = Math.max(0, totalSlots - eq.agendaPatients);
        const pct = eq.agendaPatients / Math.max(totalSlots, 1);
        const valCls = pct > 0.9 ? 'mv-full' : pct > 0.7 ? 'mv-warn' : '';
        const shortName = eq.displayName.replace(/^[^-]+ - /, '');
        machineHtml +=
          `<div class="resumen-machine-row">` +
            `<span>${shortName}</span>` +
            `<span class="${valCls}">${freeSlots}/${totalSlots}</span>` +
          `</div>`;
      });
      machineHtml += '</div>';
    }

    const card = document.createElement('div');
    card.className = 'resumen-center-card';
    card.innerHTML =
      `<div class="resumen-center-name">${centerName}</div>` +
      `<div class="resumen-stat">` +
        `<span class="resumen-stat-label">En proceso</span>` +
        `<span class="resumen-stat-value">${totalInProcess}</span>` +
      `</div>` +
      `<div class="resumen-stat">` +
        `<span class="resumen-stat-label">Demorados</span>` +
        `<span class="resumen-stat-value ${totalDelayed > 0 ? 'sv-warn' : 'sv-ok'}">${totalDelayed}</span>` +
      `</div>` +
      machineHtml;
    container.appendChild(card);
  });

  if (centers.length === 0)
    container.innerHTML = '<p class="detail-placeholder">Sin centros configurados.</p>';
}

function renderResumenAlertas() {
  const container = document.getElementById('resumenAlertas');
  if (!state.homeData) { container.innerHTML = '<p class="detail-placeholder">Sin datos.</p>'; return; }

  const alertas = [];
  const summary = state.homeData.stageSummary ?? [];

  // Etapas con promedio actual > expectedDays * 1.5
  summary.forEach(s => {
    const exp = s.expectedDays ?? 0;
    if (exp > 0 && (s.patientCount ?? 0) > 0 && s.averageDaysInStage > exp * 1.5)
      alertas.push({
        level: 'error',
        text: `${s.centerName} · ${s.stageCode}: promedio ${s.averageDaysInStage.toFixed(1)}d (esp. ${exp}d)`
      });
  });

  // Centros con F6B acumulado > RESUMEN_F6B_ALERT
  const f6bByCenter = {};
  summary.filter(s => s.stageCode === 'F6B').forEach(s => {
    f6bByCenter[s.centerName] = (f6bByCenter[s.centerName] ?? 0) + (s.patientCount ?? 0);
  });
  Object.entries(f6bByCenter).forEach(([center, count]) => {
    if (count > RESUMEN_F6B_ALERT)
      alertas.push({
        level: 'warn',
        text: `${center}: ${count} pacientes en F6B (umbral ${RESUMEN_F6B_ALERT})`
      });
  });

  // Equipos con ocupacion > 90% hoy (desde equipments del home)
  (state.homeData.equipments ?? []).forEach(eq => {
    const totalSlots = Math.floor((Number(eq.workingHours) - Number(eq.reservedSpecialHours)) * 60 / eq.standardSlotMinutes);
    if (totalSlots === 0) return;
    const pct = eq.agendaPatients / totalSlots;
    if (pct > 0.9) {
      const shortName = eq.displayName.replace(/^[^-]+ - /, '');
      alertas.push({
        level: pct >= 1 ? 'error' : 'warn',
        text: `${shortName} (hoy): ${eq.agendaPatients}/${totalSlots} turnos (${Math.round(pct * 100)}%)`
      });
    }
  });

  container.innerHTML = '';
  if (alertas.length === 0) {
    container.appendChild(el('p', 'detail-placeholder', 'Sin alertas activas.'));
    return;
  }

  const list = document.createElement('div');
  list.className = 'alerta-list';
  alertas.forEach(a => {
    const item = document.createElement('div');
    item.className = `alerta-item alerta-${a.level}`;
    item.innerHTML =
      `<span class="dot ${a.level === 'error' ? 'dot-red' : 'dot-yellow'}"></span>` +
      `<span class="alerta-text">${a.text}</span>`;
    list.appendChild(item);
  });
  container.appendChild(list);
}

async function loadWeeklyStats() {
  try {
    const resp = await fetch('/api/stats/weekly');
    state.resumen.weeklyData = resp.ok ? await resp.json() : [];
  } catch {
    state.resumen.weeklyData = [];
  }
  renderResumenTendencias();
}

function renderResumenTendencias() {
  const container = document.getElementById('resumenTendencias');
  const data = state.resumen.weeklyData;

  if (!data || data.length === 0) {
    container.innerHTML = '<p class="detail-placeholder">Sin datos aun. Se acumularan automaticamente con cada actualizacion del snapshot.</p>';
    return;
  }

  const weeks = last4Weeks();
  const stageDefs = state.homeData?.stages ?? [];

  // Aggregate by (weekStart, stageCode) across all centers and techniques
  const agg = new Map();
  data.forEach(row => {
    const key = `${row.weekStart}|${row.stageCode}`;
    if (!agg.has(key)) agg.set(key, { count: 0, sumDays: 0 });
    const a = agg.get(key);
    a.count += row.count;
    a.sumDays += row.sumDays;
  });

  const wrap = document.createElement('div');
  wrap.className = 'tendencias-wrap';
  const table = document.createElement('table');
  table.className = 'tendencias-table';

  const thead = document.createElement('thead');
  const hrow = document.createElement('tr');
  hrow.innerHTML = '<th>Etapa</th>' + weeks.map(w => {
    const [, m, d] = w.split('-');
    return `<th>${d}/${m}</th>`;
  }).join('');
  thead.appendChild(hrow);
  table.appendChild(thead);

  const tbody = document.createElement('tbody');
  RESUMEN_CRITICAL_STAGES.forEach(code => {
    const def = stageDefs.find(s => s.code === code);
    const expected = def?.expectedDays ?? 0;

    const tr = document.createElement('tr');
    let cells =
      `<td><strong>${code}</strong>` +
      (def ? ` <span class="tend-stage-name">${def.displayName}</span>` : '') +
      `</td>`;

    weeks.forEach(week => {
      const a = agg.get(`${week}|${code}`);
      if (!a || a.count === 0) {
        cells += `<td class="tend-empty">—</td>`;
      } else {
        const avg = a.sumDays / a.count;
        let cls = '';
        if (expected > 0) {
          const r = avg / expected;
          cls = r <= 1 ? 'tend-ok' : r <= 1.5 ? 'tend-warn' : 'tend-bad';
        }
        cells +=
          `<td class="${cls}">${avg.toFixed(1)}d` +
          (expected > 0 ? `<span style="color:var(--muted);font-weight:400;font-size:11px"> /${expected}d</span>` : '') +
          `</td>`;
      }
    });

    tr.innerHTML = cells;
    tbody.appendChild(tr);
  });
  table.appendChild(tbody);
  wrap.appendChild(table);
  container.innerHTML = '';
  container.appendChild(wrap);
}

// ── Boot ──────────────────────────────────────────────────────────────────────

wireTabs();
wireActions();
loadAvailableDates();
loadAvailableTomographDates();
loadHome();
loadWeeklyStats();
