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

function priorityBadge(priority) {
  if (!priority) return '';
  return `<span class="priority-badge priority-p${priority <= 1 ? 1 : 2}">P${priority}</span>`;
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
      if (state.activeTab === 'derivacion') openDerivacion();
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
  document.getElementById('generatedAt').textContent = 'Iniciando consulta ARIA...';
  try {
    const resp = await fetch('/api/aria/run-query', { method: 'POST' });
    if (resp.status === 202 || resp.status === 409) {
      // 202: arrancó en background. 409: ya corría, engancharse al polling.
      await _pollAriaStatus();
    } else if (resp.ok) {
      // Sin runner exe: importó directo, aplicar
      document.getElementById('generatedAt').textContent = 'Aplicando datos ARIA...';
      const applyResp = await fetch('/api/home/apply-aria', { method: 'POST' });
      if (applyResp.ok) renderHome(await applyResp.json());
    } else {
      const r = await resp.json().catch(() => ({}));
      document.getElementById('generatedAt').textContent = r.error ?? `Error ${resp.status}`;
    }
  } catch (e) {
    document.getElementById('generatedAt').textContent = `Error: ${e.message}`;
  } finally { btn.disabled = false; }
}

async function _pollAriaStatus() {
  while (true) {
    await new Promise(r => setTimeout(r, 4000));
    try {
      const st = await fetch('/api/aria/query-status').then(r => r.json());
      if (st.isRunning) {
        const pct = st.progressPct ?? 0;
        document.getElementById('generatedAt').textContent =
          `Consultando ARIA... ${pct}% (${st.currentPatient}/${st.totalPatients})`;
      } else {
        if (st.lastRunSucceeded) {
          document.getElementById('generatedAt').textContent = 'Aplicando datos ARIA...';
          const applyResp = await fetch('/api/home/apply-aria', { method: 'POST' });
          if (applyResp.ok) renderHome(await applyResp.json());
          else document.getElementById('generatedAt').textContent = 'ARIA actualizado (error al aplicar)';
        } else {
          document.getElementById('generatedAt').textContent = `Error ARIA: ${st.lastError ?? 'desconocido'}`;
        }
        break;
      }
    } catch (e) {
      document.getElementById('generatedAt').textContent = `Error estado: ${e.message}`;
      break;
    }
  }
}

async function actionUpdateAll() {
  document.getElementById('scrapMenu').hidden = true;
  const btn = document.getElementById('scrapMenuBtn');
  btn.disabled = true;
  document.getElementById('generatedAt').textContent = 'Iniciando consulta ARIA...';
  try {
    const ariaResp = await fetch('/api/aria/run-query', { method: 'POST' });
    if (ariaResp.status === 202 || ariaResp.status === 409) {
      await _pollAriaStatus();
    }
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
      const nameHtml = priorityBadge(p.priority) + (p.sitraMedGuid
        ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${p.sitraMedGuid}/overview" target="_blank" rel="noopener noreferrer"><strong>${p.patientName}</strong></a>`
        : `<strong>${p.patientName}</strong>`);
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
      const nameHtml = priorityBadge(p.priority) + (p.sitraMedGuid
        ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${p.sitraMedGuid}/overview" target="_blank" rel="noopener noreferrer"><strong>${p.patientName}</strong></a>`
        : `<strong>${p.patientName}</strong>`);
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
      const nameHtml = priorityBadge(p.priority) + (p.sitraMedGuid
        ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${p.sitraMedGuid}/overview" target="_blank" rel="noopener noreferrer"><strong>${p.patientName}</strong></a>`
        : `<strong>${p.patientName}</strong>`);
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
      displayName = priorityBadge(slot.priority) + (guid
        ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${guid}/overview" target="_blank" rel="noopener noreferrer">${slot.patientName}</a>`
        : slot.patientName);
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
      displayName = priorityBadge(slot.priority) + (guid
        ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${guid}/overview" target="_blank" rel="noopener noreferrer">${slot.patientName}</a>`
        : slot.patientName);
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
      const nameHtml = priorityBadge(p.priority) + (p.sitraMedGuid
        ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${p.sitraMedGuid}/overview" target="_blank" rel="noopener noreferrer"><strong>${p.patientName}</strong></a>`
        : `<strong>${p.patientName}</strong>`);
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

// ══════════════════════════════════════════════════════════════════════════════
// ── Derivacion ────────────────────────────────────────────────────────────────
// ══════════════════════════════════════════════════════════════════════════════

const deriv = {
  equipoFallido: null,
  fechaInicio: null,
  fechaFin: null,
  incluirPlanificacion: false,
  agendaSlots: {},
  pacientes: [],
  asignaciones: {},
  seleccionado: null
};

let _derivWired = false;

async function openDerivacion() {
  if (!_derivWired) {
    document.getElementById('derivCalcularBtn').addEventListener('click', _calcularDerivacion);
    document.getElementById('derivExportarBtn').addEventListener('click', () => exportarDerivacion(false));
    document.getElementById('derivExportarIgualBtn').addEventListener('click', () => exportarDerivacion(true));
    _derivWired = true;
  }
  if (!state.homeData) return;
  if (!state.configData) {
    try { state.configData = await fetch('/api/configuration').then(r => r.json()); } catch (e) {}
  }
  _renderDerivConfig();
}

function _renderDerivConfig() {
  const machines = state.homeData?.configuration?.machines || [];
  const today = todayStr();
  if (!deriv.fechaInicio) deriv.fechaInicio = today;
  if (!deriv.fechaFin) deriv.fechaFin = today;

  const sel = document.getElementById('derivEquipoSelect');
  const prev = deriv.equipoFallido || sel.value;
  sel.innerHTML = '<option value="">Seleccionar equipo...</option>';
  const byCentre = {};
  machines.forEach(m => (byCentre[m.centerName] = byCentre[m.centerName] || []).push(m));
  Object.entries(byCentre).sort(([a],[b]) => a.localeCompare(b)).forEach(([center, ml]) => {
    const og = document.createElement('optgroup');
    og.label = center;
    ml.sort((a,b) => a.displayName.localeCompare(b.displayName)).forEach(m => {
      const opt = document.createElement('option');
      opt.value = m.displayName;
      opt.textContent = m.displayName;
      if (m.displayName === prev) opt.selected = true;
      og.appendChild(opt);
    });
    sel.appendChild(og);
  });

  document.getElementById('derivFechaInicio').value = deriv.fechaInicio;
  document.getElementById('derivFechaFin').value = deriv.fechaFin;
  document.getElementById('derivIncluirPlanif').checked = deriv.incluirPlanificacion;

  if (deriv.pacientes.length > 0) _renderDerivResult();
}

async function _calcularDerivacion() {
  const equipo = document.getElementById('derivEquipoSelect').value;
  const fi = document.getElementById('derivFechaInicio').value;
  const ff = document.getElementById('derivFechaFin').value;
  const incluir = document.getElementById('derivIncluirPlanif').checked;

  if (!equipo) { alert('Seleccioná un equipo'); return; }
  if (!fi || !ff || fi > ff) { alert('Fechas inválidas (inicio debe ser ≤ fin)'); return; }

  deriv.equipoFallido = equipo;
  deriv.fechaInicio = fi;
  deriv.fechaFin = ff;
  deriv.incluirPlanificacion = incluir;
  deriv.asignaciones = {};
  deriv.seleccionado = null;
  deriv.pacientes = [];
  deriv.agendaSlots = {};

  const btn = document.getElementById('derivCalcularBtn');
  btn.disabled = true; btn.textContent = 'Calculando…';

  try {
    const dates = _weekdaysBetween(fi, ff);
    const results = await Promise.all(
      dates.map(d => fetch(`/api/agenda?date=${d}`).then(r => r.ok ? r.json() : []).catch(() => []))
    );
    dates.forEach((d, i) => {
      const raw = results[i];
      deriv.agendaSlots[d] = Array.isArray(raw) ? raw : (raw?.slots || []);
    });
    deriv.pacientes = _buildPacientesAfectados(equipo, dates, incluir);
    _renderDerivResult();
  } finally {
    btn.disabled = false; btn.textContent = 'Calcular derivacion';
  }
}

function _buildPacientesAfectados(equipo, dates, incluirPlanif) {
  const pacientes = [];

  // FUENTE A — turnos reales en agenda
  const patMap = new Map();
  for (const date of dates) {
    for (const slot of (deriv.agendaSlots[date] || [])) {
      if (slot.machineName !== equipo || slot.isEstimated) continue;
      const mk = slot.sitraMedGuid || `_n_${slot.patientName}`;
      if (!patMap.has(mk)) {
        patMap.set(mk, {
          key: `a_${mk}`, source: 'agenda',
          nombre: slot.patientName || '—', sitraMedGuid: slot.sitraMedGuid || null, hc: null, priority: slot.priority || null,
          fechasTurno: [], horarios: [],
          treatmentLabel: slot.treatmentLabel || slot.treatmentTechnique || slot.treatment || '—',
          etapaDisplay: null, sortOrder: -1, fracciones: null
        });
      }
      const entry = patMap.get(mk);
      entry.fechasTurno.push(date);
      if (slot.startTime) entry.horarios.push(`${date} ${slot.startTime}`);
    }
  }
  for (const p of patMap.values()) {
    p.fechasTurno = [...new Set(p.fechasTurno)].sort();
    pacientes.push(p);
  }
  pacientes.sort((a,b) => (a.fechasTurno[0]||'').localeCompare(b.fechasTurno[0]||''));

  // FUENTE B — pacientes en planificacion con equipo asignado en ARIA
  if (incluirPlanif) {
    const stages = state.homeData?.stages || [];
    const f6aSort = (stages.find(s => s.code === 'F6A') || { sortOrder: 60 }).sortOrder;
    const stageByCode = Object.fromEntries(stages.map(s => [s.code, s]));
    const fuenteB = (state.homeData?.patients || [])
      .filter(p => p.plannedMachineDisplayName === equipo &&
                   (stageByCode[p.stageCode]?.sortOrder ?? 0) >= f6aSort)
      .map(p => ({
        key: `b_${p.patientId}`, source: 'planificacion',
        nombre: p.patientName || '—', sitraMedGuid: p.sitraMedGuid || null, hc: p.patientId, priority: p.priority || null,
        fechasTurno: null,
        treatmentLabel: p.treatmentLabel || p.treatmentTechnique || '—',
        etapaDisplay: p.stageDisplayName || p.stageCode,
        sortOrder: stageByCode[p.stageCode]?.sortOrder ?? 0,
        fracciones: p.numberOfFractions
      }))
      .sort((a,b) => a.sortOrder - b.sortOrder);
    pacientes.push(...fuenteB);
  }
  return pacientes;
}

function _renderDerivResult() {
  document.getElementById('derivContent').hidden = false;
  document.getElementById('derivBarraResumen').hidden = false;
  _renderDerivPacientes();
  _renderDerivBarra();
  _renderDerivResumenEquipos();
}

const _ESTADO_ORDER = { sin_asignar: 0, derivado: 1, suspendido: 2, atendido: 3 };
function _estadoSort(a, b) {
  const oa = _ESTADO_ORDER[deriv.asignaciones[a.key]?.estado || 'sin_asignar'] ?? 0;
  const ob = _ESTADO_ORDER[deriv.asignaciones[b.key]?.estado || 'sin_asignar'] ?? 0;
  return oa - ob;
}

function _renderDerivPacientes() {
  const container = document.getElementById('derivPacientesList');
  container.innerHTML = '';
  const fa = deriv.pacientes.filter(p => p.source === 'agenda').sort(_estadoSort);
  const fb = deriv.pacientes.filter(p => p.source === 'planificacion').sort(_estadoSort);
  if (!fa.length && !fb.length) {
    container.innerHTML = '<p class="detail-placeholder">No hay pacientes afectados en el período.</p>';
    return;
  }
  const appendSection = (label, list) => {
    if (!list.length) return;
    const h = document.createElement('div');
    h.className = 'deriv-source-header';
    h.innerHTML = `<strong>${label}</strong><span class="badge-count">${list.length}</span>`;
    container.appendChild(h);
    list.forEach(p => container.appendChild(_buildDerivCard(p)));
  };
  appendSection('Con turno en el rango', fa);
  appendSection('En planificacion', fb);
}

// Nombre corto: parte después de " - " o el nombre completo
function _shortName(name) {
  const i = name.lastIndexOf(' - ');
  return i >= 0 ? name.slice(i + 3) : name;
}

// Máquinas compatibles ordenadas: mismo centro primero, luego alfabético
// Retorna objetos { displayName, centerName, compat: { ok, warn, reason } }
function _getSortedCompatMachines(treatmentLabel) {
  const caps = state.configData?.machineCapabilities || [];
  const failedCenter = (state.homeData?.configuration?.machines || [])
    .find(m => m.displayName === deriv.equipoFallido)?.centerName;
  return (state.homeData?.configuration?.machines || [])
    .filter(m => m.displayName !== deriv.equipoFallido)
    .map(m => ({ ...m, compat: _checkCompat(treatmentLabel, caps.find(c => c.machineName === m.displayName)) }))
    .filter(m => m.compat.ok)
    .sort((a,b) => {
      const sa = a.centerName === failedCenter ? 0 : 1;
      const sb = b.centerName === failedCenter ? 0 : 1;
      return sa !== sb ? sa - sb : a.centerName.localeCompare(b.centerName) || a.displayName.localeCompare(b.displayName);
    });
}

function _buildDerivCard(p) {
  const asig = deriv.asignaciones[p.key];
  const estado = asig?.estado || 'sin_asignar';

  // Horario actual (solo fuente agenda)
  // Si hay múltiples días incluir la fecha en cada entrada, si es un solo día solo la hora
  const hasManyDays = new Set((p.horarios || []).map(h => h.split(' ')[0])).size > 1;
  const horariosStr = p.horarios?.length
    ? p.horarios.map(h => {
        const [datePart, timePart] = h.split(' ');
        return hasManyDays ? `${_fmtDate(datePart)} ${timePart||''}`.trim() : (timePart || '');
      }).filter(Boolean).join(' · ')
    : null;

  // Fechas / etapa: no mostrar fechas cuando horariosStr ya las incluye (multi-día)
  const infoStr = hasManyDays
    ? null
    : (p.fechasTurno
      ? p.fechasTurno.map(_fmtDate).join(', ')
      : (p.fracciones ? `${p.etapaDisplay} · ${p.fracciones} fx` : (p.etapaDisplay || '—')));

  const badgeHtml = estado === 'derivado'
    ? `<span class="deriv-badge deriv-badge-ok">→ ${_shortName(asig.equipo)}</span>`
    : estado === 'suspendido'
    ? `<span class="deriv-badge deriv-badge-grey">⊗ Suspendido</span>`
    : estado === 'atendido'
    ? `<span class="deriv-badge deriv-badge-atendido">✓ Ya atendido</span>`
    : `<span class="deriv-badge deriv-badge-none">Sin asignar</span>`;

  const label = p.treatmentLabel || '—';

  // Botones rápidos
  const compatMachines = _getSortedCompatMachines(label);
  const top3 = compatMachines.slice(0, 3);
  const others = compatMachines.slice(3);
  const quickBtns = top3.map(m => {
    const isAssigned = asig?.estado === 'derivado' && asig?.equipo === m.displayName;
    const warnTitle = m.compat?.warn && m.compat?.reason ? ` — ${m.compat.reason}` : '';
    return `<button class="deriv-quick-btn${isAssigned ? ' active' : ''}${m.compat?.warn ? ' warn' : ''}"
              data-machine="${m.displayName}" title="${m.displayName}${warnTitle}">
      ${_shortName(m.displayName)} →${m.compat?.warn ? ' ⚠' : ''}
    </button>`;
  }).join('');
  const othersSelect = others.length
    ? `<select class="deriv-others-sel" data-key="${p.key}">
        <option value="">Otros...</option>
        ${others.map(m => `<option value="${m.displayName}"${asig?.estado === 'derivado' && asig?.equipo === m.displayName ? ' selected' : ''}>${_shortName(m.displayName)}${m.compat?.warn ? ' ⚠' : ''}</option>`).join('')}
       </select>`
    : '';

  const card = document.createElement('div');
  card.className = `deriv-patient-card estado-${estado}`;
  card.dataset.key = p.key;
  card.innerHTML = `
    <div class="deriv-card-header">
      ${priorityBadge(p.priority)}<span class="deriv-card-name">${p.nombre}</span>
      ${p.hc ? `<span class="deriv-card-hc">${p.hc}</span>` : ''}
    </div>
    <div class="deriv-card-row">
      <span class="treatment-badge ${_derivLabelClass(label)}">${label}</span>
      ${infoStr ? `<span class="deriv-card-dates">${infoStr}</span>` : ''}
      ${horariosStr ? `<span class="deriv-card-horario">🕐 ${horariosStr}</span>` : ''}
    </div>
    <div class="deriv-quickbtns">
      ${quickBtns}
      ${othersSelect}
    </div>
    <div class="deriv-card-footer">
      ${badgeHtml}
      <div class="deriv-card-actions">
        <button class="deriv-atendido-btn${estado === 'atendido' ? ' active' : ''}"
                title="${estado === 'atendido' ? 'Quitar ya atendido' : 'Ya atendido hoy'}">✓</button>
        <button class="deriv-suspend-btn${estado === 'suspendido' ? ' active' : ''}"
                title="${estado === 'suspendido' ? 'Quitar suspension' : 'Suspender'}">
          ${estado === 'suspendido' ? '↩' : '⊗'}
        </button>
      </div>
    </div>`;

  // Quick-assign buttons
  card.querySelectorAll('.deriv-quick-btn').forEach(btn => {
    btn.addEventListener('click', e => {
      e.stopPropagation();
      _asignarDerivPaciente(p.key, btn.dataset.machine);
    });
  });

  // Others dropdown
  const sel = card.querySelector('.deriv-others-sel');
  if (sel) {
    sel.addEventListener('change', () => {
      if (sel.value) _asignarDerivPaciente(p.key, sel.value);
    });
  }

  card.querySelector('.deriv-suspend-btn').addEventListener('click', e => {
    e.stopPropagation(); _toggleSuspender(p.key);
  });
  card.querySelector('.deriv-atendido-btn').addEventListener('click', e => {
    e.stopPropagation(); _toggleAtendido(p.key);
  });
  return card;
}

function _asignarDerivPaciente(key, machine) {
  const cur = deriv.asignaciones[key];
  if (cur?.estado === 'derivado' && cur?.equipo === machine) delete deriv.asignaciones[key];
  else deriv.asignaciones[key] = { estado: 'derivado', equipo: machine };
  _refreshDerivCard(key);
  _renderDerivResumenEquipos();
  _renderDerivBarra();
}

function _toggleSuspender(key) {
  const cur = deriv.asignaciones[key];
  if (cur?.estado === 'suspendido') delete deriv.asignaciones[key];
  else deriv.asignaciones[key] = { estado: 'suspendido', equipo: null };
  _renderDerivPacientes();
  _renderDerivResumenEquipos();
  _renderDerivBarra();
}

function _toggleAtendido(key) {
  const cur = deriv.asignaciones[key];
  if (cur?.estado === 'atendido') delete deriv.asignaciones[key];
  else deriv.asignaciones[key] = { estado: 'atendido', equipo: null };
  _renderDerivPacientes();
  _renderDerivResumenEquipos();
  _renderDerivBarra();
}

function _refreshDerivCard(key) {
  _renderDerivPacientes();
  _renderDerivResumenEquipos();
  _renderDerivBarra();
}

// Panel derecho: tarjetas de equipos destino con turnos libres y pacientes derivados
function _renderDerivResumenEquipos() {
  const panel = document.getElementById('derivDestinosPanel');
  if (!deriv.equipoFallido || !deriv.pacientes.length) {
    panel.innerHTML = '<p class="detail-placeholder">Calculá la derivacion para ver el resumen de equipos.</p>';
    return;
  }
  const capacities = state.homeData?.configuration?.machineCapacities || [];
  const machines = (state.homeData?.configuration?.machines || [])
    .filter(m => m.displayName !== deriv.equipoFallido);
  const failedCenter = (state.homeData?.configuration?.machines || [])
    .find(m => m.displayName === deriv.equipoFallido)?.centerName;
  const dates = Object.keys(deriv.agendaSlots).sort();

  // Derivados por equipo
  const derivadosPorEquipo = {};
  deriv.pacientes.forEach(p => {
    const asig = deriv.asignaciones[p.key];
    if (asig?.estado === 'derivado') {
      (derivadosPorEquipo[asig.equipo] = derivadosPorEquipo[asig.equipo] || []).push(p);
    }
  });

  // Ordenar: con derivados primero, luego mismo centro, luego alfabético
  const sorted = [...machines].sort((a,b) => {
    const ad = (derivadosPorEquipo[a.displayName]?.length || 0) > 0 ? 0 : 1;
    const bd = (derivadosPorEquipo[b.displayName]?.length || 0) > 0 ? 0 : 1;
    if (ad !== bd) return ad - bd;
    const ac = a.centerName === failedCenter ? 0 : 1;
    const bc = b.centerName === failedCenter ? 0 : 1;
    return ac !== bc ? ac - bc : a.centerName.localeCompare(b.centerName) || a.displayName.localeCompare(b.displayName);
  });

  let html = '<div class="deriv-resumen-grid">';
  for (const m of sorted) {
    const derivados = derivadosPorEquipo[m.displayName] || [];
    const slotsLibres = _calcSlots(m.displayName, dates, capacities);
    const adj = slotsLibres !== null ? slotsLibres - derivados.length : null;
    const sc = adj === null ? '' : adj > 3 ? 'slots-ok' : adj > 0 ? 'slots-warn' : 'slots-full';
    const turnosLabel = adj !== null
      ? `<span class="deriv-slots ${sc}">${Math.max(0, adj)} turno${adj !== 1 ? 's' : ''} disponible${adj !== 1 ? 's' : ''}</span>`
      : '';

    html += `<div class="deriv-equipo-card${derivados.length ? ' has-derivados' : ''}">
      <div class="deriv-equipo-header">
        <span class="deriv-equipo-name">${m.displayName}</span>
        <span class="deriv-equipo-center">${m.centerName === failedCenter ? '★ ' : ''}${m.centerName}</span>
      </div>
      ${turnosLabel}`;

    if (derivados.length) {
      html += '<ul class="deriv-equipo-patients">';
      derivados.forEach(pt => {
        html += `<li>
          <span>${pt.nombre}</span>
          <button class="deriv-unassign-btn" data-key="${pt.key}" title="Quitar derivacion">✕</button>
        </li>`;
      });
      html += '</ul>';
    } else {
      html += '<div class="deriv-equipo-empty">Sin derivaciones</div>';
    }
    html += '</div>';
  }
  html += '</div>';
  panel.innerHTML = html;

  panel.querySelectorAll('.deriv-unassign-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      const key = btn.dataset.key;
      delete deriv.asignaciones[key];
      _refreshDerivCard(key);
      _renderDerivResumenEquipos();
      _renderDerivBarra();
    });
  });
}

function _renderDerivBarra() {
  const total = deriv.pacientes.length;
  const vals = Object.values(deriv.asignaciones);
  const derivados   = vals.filter(a => a.estado === 'derivado').length;
  const suspendidos = vals.filter(a => a.estado === 'suspendido').length;
  const atendidos   = vals.filter(a => a.estado === 'atendido').length;
  const sinAsignar  = total - derivados - suspendidos - atendidos;
  document.getElementById('derivStatTotal').textContent = `Total: ${total}`;
  document.getElementById('derivStatDerivados').textContent = `Derivados: ${derivados}`;
  document.getElementById('derivStatSuspendidos').textContent = `Suspendidos: ${suspendidos}`;
  document.getElementById('derivStatAtendidos').textContent = `Atendidos: ${atendidos}`;
  document.getElementById('derivStatSinAsignar').innerHTML =
    `Sin asignar: <strong${sinAsignar > 0 ? ' style="color:var(--warn)"' : ''}>${sinAsignar}</strong>`;
  document.getElementById('derivExportarBtn').disabled = sinAsignar > 0;
}

function _checkCompat(label, cap) {
  const l = (label || '').trim();
  if (!l || l === '—') return { ok: true, warn: false, reason: '' };
  if (l === 'BQT' || l === 'IORT') return { ok: false, warn: false, reason: 'No aplica a linac' };
  if (l === 'VMAT' || l === 'SBRT - VMAT' || l === 'RC - VMAT')
    return cap?.canDoVMAT ? { ok: true, warn: false, reason: '' } : { ok: false, warn: false, reason: 'Sin capacidad VMAT' };
  if (l === 'IGRT - VMAT') {
    if (!cap?.canDoVMAT) return { ok: false, warn: false, reason: 'Sin capacidad VMAT' };
    const noIgrt = !cap.canDoIGRT;
    return { ok: true, warn: noIgrt, reason: noIgrt ? 'No hace IGRT' : '' };
  }
  if (l === 'SBRT' || l === 'SBRT - haz SRS')
    return cap?.canDoSBRT ? { ok: true, warn: false, reason: '' } : { ok: false, warn: false, reason: 'Sin capacidad SBRT' };
  if (l === 'RC' || l === 'RC - haz SRS')
    return cap?.canDoRC ? { ok: true, warn: false, reason: '' } : { ok: false, warn: false, reason: 'Sin capacidad RC' };
  if (l === 'TBI')
    return cap?.canDoTBI ? { ok: true, warn: false, reason: '' } : { ok: false, warn: false, reason: 'Sin capacidad TBI' };
  if (l === 'TSET' || l === '3DC e-')
    return cap?.canDoElectrons ? { ok: true, warn: false, reason: '' } : { ok: false, warn: false, reason: 'Sin electrones' };
  if (l === '3DC 10X')
    return cap?.highEnergyBeams?.includes('10X') ? { ok: true, warn: false, reason: '' } : { ok: false, warn: false, reason: 'Sin campo 10X' };
  if (l === '3DC 15X')
    return cap?.highEnergyBeams?.includes('15X') ? { ok: true, warn: false, reason: '' } : { ok: false, warn: false, reason: 'Sin campo 15X' };
  if (l === '3DC 18X')
    return cap?.highEnergyBeams?.includes('18X') ? { ok: true, warn: false, reason: '' } : { ok: false, warn: false, reason: 'Sin campo 18X' };
  if (l === 'IGRT' || l === 'IGRT - estático') {
    const noIgrt = !cap?.canDoIGRT;
    return { ok: true, warn: noIgrt, reason: noIgrt ? 'No hace IGRT' : '' };
  }
  return { ok: true, warn: false, reason: '' };
}

function _calcSlots(machineName, dates, capacities) {
  const cap = capacities.find(c => c.machineName === machineName);
  if (!cap || !cap.standardSlotMinutes) return null;
  const perDay = Math.floor((cap.workingHours - (cap.reservedSpecialHours || 0)) * 60 / cap.standardSlotMinutes);
  const total = perDay * dates.length;
  let occupied = 0;
  for (const d of dates) {
    occupied += (deriv.agendaSlots[d] || []).filter(s => s.machineName === machineName && !s.isEstimated).length;
  }
  return Math.max(0, total - occupied);
}

function exportarDerivacion(incluirSinAsignar) {
  const equipo = deriv.equipoFallido || '—';
  const fi = _fmtDate(deriv.fechaInicio);
  const ff = _fmtDate(deriv.fechaFin);
  const now = new Date().toLocaleString('es-AR');

  const derivados = deriv.pacientes
    .filter(p => deriv.asignaciones[p.key]?.estado === 'derivado')
    .sort((a,b) => {
      const ea = deriv.asignaciones[a.key]?.equipo || '';
      const eb = deriv.asignaciones[b.key]?.equipo || '';
      return ea.localeCompare(eb) || a.nombre.localeCompare(b.nombre);
    });
  const suspendidos = deriv.pacientes.filter(p => deriv.asignaciones[p.key]?.estado === 'suspendido');
  const atendidos   = deriv.pacientes.filter(p => deriv.asignaciones[p.key]?.estado === 'atendido');
  const sinAsignar  = deriv.pacientes.filter(p => !deriv.asignaciones[p.key]);
  const exportList = [...derivados, ...suspendidos, ...atendidos, ...(incluirSinAsignar ? sinAsignar : [])];

  const countByEquipo = {};
  derivados.forEach(p => {
    const eq = deriv.asignaciones[p.key]?.equipo || '—';
    countByEquipo[eq] = (countByEquipo[eq] || 0) + 1;
  });

  const esc = s => String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
  const _exportCaps = state.configData?.machineCapabilities || [];
  const tableRows = exportList.map(p => {
    const asig = deriv.asignaciones[p.key];
    const destino = asig?.estado === 'derivado' ? asig.equipo
      : asig?.estado === 'suspendido' ? 'Suspendido — retoma al reactivar equipo'
      : asig?.estado === 'atendido' ? 'Ya atendido ese día'
      : 'Sin asignar';
    const bg = !asig ? 'background:#fff8e1;'
      : asig.estado === 'suspendido' ? 'background:#f5f5f5;color:#888;'
      : asig.estado === 'atendido' ? 'background:#f0faf0;color:#4caf50;'
      : '';
    const fechas = p.fechasTurno ? p.fechasTurno.map(_fmtDate).join(', ') : (p.etapaDisplay || '—');
    let obs = '';
    if (asig?.estado === 'derivado') {
      const cap = _exportCaps.find(c => c.machineName === asig.equipo);
      const compat = _checkCompat(p.treatmentLabel, cap);
      if (compat.warn && compat.reason) obs = compat.reason;
    }
    return `<tr style="${bg}"><td>${esc(p.nombre)}</td><td>${esc(p.hc||'—')}</td><td>${esc(p.treatmentLabel||'—')}</td><td>${esc(fechas)}</td><td>${esc(destino)}</td><td>${esc(obs)}</td></tr>`;
  }).join('');

  const summaryRows = Object.entries(countByEquipo)
    .sort(([a],[b]) => a.localeCompare(b))
    .map(([eq,n]) => `<tr><td>${esc(eq)}</td><td>${n} paciente${n>1?'s':''}</td></tr>`)
    .join('') || '<tr><td colspan="2" style="color:#999">Sin derivaciones</td></tr>';

  const slug = equipo.toLowerCase().replace(/[^a-z0-9]+/g,'_');
  const ds = (deriv.fechaInicio||'').replace(/-/g,'');

  const html = `<!DOCTYPE html>
<html lang="es"><head><meta charset="utf-8">
<title>Derivacion — ${esc(equipo)}</title>
<style>
body{font-family:"Segoe UI",Arial,sans-serif;margin:32px;color:#1b2f38;font-size:13px;line-height:1.5}
h1{font-size:20px;color:#0f6c74;margin:0 0 4px}
.meta{color:#6b7b83;font-size:12px;margin-bottom:24px}
h2{font-size:14px;color:#0f6c74;margin:24px 0 8px;border-bottom:1px solid #d8cec0;padding-bottom:4px}
table{border-collapse:collapse;width:100%;margin-bottom:20px}
th{background:#f3efe6;border:1px solid #d8cec0;padding:8px 10px;text-align:left;font-size:12px;font-weight:600}
td{border:1px solid #d8cec0;padding:7px 10px;vertical-align:top}
tr:nth-child(even) td{background:#fafaf7}
.note{font-size:11px;color:#6b7b83;margin-top:20px;border-top:1px solid #d8cec0;padding-top:12px}
@media print{body{margin:16px}.note{page-break-before:avoid}}
</style></head><body>
<h1>Plan de Derivacion — ${esc(equipo)}</h1>
<div class="meta">
  Periodo del evento: ${fi}${fi!==ff?' al '+ff:''}<br>
  Generado el: ${now}&nbsp;·&nbsp;Generado por: Meva RT
</div>
<h2>Tabla de derivaciones</h2>
<table><thead><tr><th>Paciente</th><th>HC</th><th>Tecnica</th><th>Turno(s) / Etapa</th><th>Destino</th><th>Observaciones</th></tr></thead>
<tbody>${tableRows||'<tr><td colspan="6" style="color:#999;text-align:center">Sin pacientes</td></tr>'}</tbody></table>
<h2>Carga por equipo destino</h2>
<table style="width:auto"><thead><tr><th>Equipo destino</th><th>Pacientes derivados</th></tr></thead>
<tbody>${summaryRows}</tbody></table>
<div class="note">
  Total: ${exportList.length}&nbsp;|&nbsp;Derivados: ${derivados.length}&nbsp;|&nbsp;Suspendidos: ${suspendidos.length}&nbsp;|&nbsp;Ya atendidos: ${atendidos.length}${incluirSinAsignar&&sinAsignar.length?'&nbsp;|&nbsp;Sin asignar: '+sinAsignar.length:''}<br>
  Este documento es orientativo. Las modificaciones deben realizarse en SitraMed.
</div>
</body></html>`;

  const blob = new Blob([html], { type: 'text/html;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = `derivacion_${slug}_${ds}.html`;
  document.body.appendChild(a); a.click();
  document.body.removeChild(a); URL.revokeObjectURL(url);
}

function _weekdaysBetween(s, e) {
  const days = [];
  const cur = new Date(s + 'T12:00:00');
  const end = new Date(e + 'T12:00:00');
  while (cur <= end) {
    const dow = cur.getDay();
    if (dow > 0 && dow < 6) days.push(cur.toISOString().slice(0,10));
    cur.setDate(cur.getDate() + 1);
  }
  return days;
}

function _fmtDate(s) {
  if (!s) return '—';
  const [y,m,d] = s.split('-');
  return `${d}/${m}/${y}`;
}

function _derivLabelClass(label) {
  if (!label) return '';
  const f = label.split(/[\s\-]/)[0].toUpperCase();
  const m = { VMAT:'VMAT', IMRT:'IMRT', SBRT:'SBRT', IGRT:'IGRT',
              RC:'RC', TBI:'TBI', TSET:'TSET', BQT:'BQT', IORT:'IORT', '3DC':'3DC' };
  return m[f] ? `tt-${m[f]}` : '';
}

// ── Boot ──────────────────────────────────────────────────────────────────────

wireTabs();
wireActions();
loadAvailableDates();
loadAvailableTomographDates();
loadHome();
loadWeeklyStats();
