// ── State ─────────────────────────────────────────────────────────────────────

const state = {
  activeTab: 'followup',
  homeData: null,

  followup: {
    centerFilter: null,
    stageGroupFilter: null,
    stageFilter: null,
    profesionalFilter: null,
    activeCenter: null,
    activeStage: null
  },

  agenda: {
    availableDates: [],
    selectedDate: null,
    centerFilter: null,
    activeMachine: null,
    slots: [],
    scrapingErrors: [],
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
    selectedPhysicist: null,
    selectedPatient: null,
    recoWeeklyStats: null
  },

  alertas: {
    weeklyStats: null,
    loaded: false
  },

  especiales: {
    techniqueFilter: null,
    stageFilter: null,
    sort: { col: null, dir: 'asc' }
  },

  inicios: {
    centerFilter: null,
    agendaByDate: {},       // date → slots[] (undefined = not loaded yet)
    futureDates: [],
    missingDates: [],       // future dates sin archivo pre-scrapeado (mostrando advertencia)
    failedDates: new Set(), // fechas que no se pudieron cargar tras reintento
    scrapePending: false,
    retryHandle: null
  },

  pacientes: {
    query: '',
    events: null,
    selected: null          // result object seleccionado en la lista
  }
};

// Reservas activas: Map<patientId, reservation> — accesible globalmente
window.activeReservations = new Map();

// ── Utilities ─────────────────────────────────────────────────────────────────

function todayStr() {
  const d = new Date();
  return `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}`;
}
function pad(n) { return String(n).padStart(2, '0'); }
function esc(s) { return String(s ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }

function delayClass(days, expected, isLongWait) {
  if (isLongWait) return 'long-wait';
  if (days < expected) return 'on-time';
  if (days === expected) return 'at-limit';
  return 'delayed';
}

function daysBadge(p, dc) {
  if (p.postponedUntil && p.stageCode === 'F4') {
    const d = new Date(), todayIso = d.getFullYear() + '-' + String(d.getMonth()+1).padStart(2,'0') + '-' + String(d.getDate()).padStart(2,'0');
    if (p.postponedUntil >= todayIso) {
      const [, mo, da] = p.postponedUntil.split('-');
      return `<span class="days-badge long-wait">Pospuesto por pac. h/${da}-${mo}</span>`;
    }
  }
  return `<span class="days-badge ${dc}">${p.daysInStage}d</span>`;
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

async function _refreshReservations() {
  try {
    const resp = await fetch('/api/reservations');
    if (!resp.ok) return;
    const list = await resp.json();
    window.activeReservations = new Map(list.map(r => [r.patientId, r]));
  } catch {}
}

function _reservationBadge(patientId) {
  const r = window.activeReservations.get(patientId);
  if (!r) return '';
  const [y, mo, d] = r.reservedDate.split('-');
  return `<span class="reservation-badge">Turno ${d}/${mo} ${r.reservedTime}</span>`;
}

function _fmtDateTime(utcStr) {
  if (!utcStr) return '';
  const d = new Date(utcStr);
  return `${pad(d.getDate())}/${pad(d.getMonth()+1)}/${d.getFullYear()} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
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

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function hcTag(patientId) {
  if (!patientId || GUID_RE.test(patientId))
    return `<span class="hc-tag muted-italic">Sin HC</span>`;
  const isHc = /^\d{1,3}-\d{4,7}-\d{1,3}$/.test(patientId);
  return isHc
    ? `<span class="hc-tag">HC ${patientId}</span>`
    : `<span class="hc-tag muted-italic">${patientId}</span>`;
}

function fmtHc(hc) {
  if (!hc || GUID_RE.test(hc)) return 'Sin HC';
  return hc;
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

function isExcludedSlot(slot) {
  const t = (slot.treatmentLabel || '').split(/[\s-]/)[0];
  return t === 'BQT' || t === 'IORT';
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

// ── Fisica reco constants ─────────────────────────────────────────────────────

// resolvePlanningTimeSource() es la fuente única de verdad para decidir
// qué tiempos de referencia usar en toda la aplicación.
// Usada en: herramienta de selección de equipo, panel de alertas.
function resolvePlanningTimeSource(weeklyStats) {
  const CRITICAL_STAGES = ['F6B', 'F6C', 'F6F', 'F6G', 'F7A'];
  const MIN_WEEKS_REQUIRED = 4;
  const weeksWithData = new Set(
    weeklyStats
      .filter(s => CRITICAL_STAGES.includes(s.stageCode))
      .map(s => s.weekStart)
  ).size;
  return {
    source: weeksWithData >= MIN_WEEKS_REQUIRED ? 'weekly_stats' : 'expected_days',
    weeksAvailable: weeksWithData,
    weeksRequired: MIN_WEEKS_REQUIRED
  };
}

const FISICA_TECHNIQUES = [
  { key: '3DC-6X',        label: '3DC - 6X',
    centerEnabled: _caps => true,                                              machineEnabled: _cap => true },
  { key: '3DC-AltaE',     label: '3DC - Alta E',
    centerEnabled: caps => caps.some(c => c.highEnergyBeams?.length > 0),     machineEnabled: cap => cap.highEnergyBeams?.length > 0 },
  { key: '3DC-e',         label: '3DC - e⁻',
    centerEnabled: caps => caps.some(c => c.canDoElectrons),                  machineEnabled: cap => cap.canDoElectrons },
  { key: 'IMRT-estatico', label: 'IMRT estático',
    centerEnabled: _caps => true,                                              machineEnabled: _cap => true },
  { key: 'IMRT-VMAT',     label: 'IMRT VMAT',
    centerEnabled: caps => caps.some(c => c.canDoVMAT),                       machineEnabled: cap => cap.canDoVMAT },
  { key: 'IGRT',          label: 'IGRT',
    centerEnabled: caps => caps.some(c => c.canDoIGRT),                       machineEnabled: cap => cap.canDoIGRT },
  { key: 'SBRT',          label: 'SBRT',
    centerEnabled: caps => caps.some(c => c.canDoSBRT),                       machineEnabled: cap => cap.canDoSBRT },
  { key: 'RC',            label: 'RC',
    centerEnabled: caps => caps.some(c => c.canDoRC),                         machineEnabled: cap => cap.canDoRC },
];

const TECH_HIGHLIGHT_MAP = {
  '3DC':  ['3DC-6X', '3DC-AltaE', '3DC-e'],
  'IMRT': ['IMRT-estatico', 'IMRT-VMAT'],
  'VMAT': ['IMRT-VMAT'],
  'SBRT': ['SBRT'],
  'RC':   ['RC'],
  'IGRT': ['IGRT'],
};

// ── Tabs ──────────────────────────────────────────────────────────────────────

// ── Auth modal ────────────────────────────────────────────────────────────────

function requestAuth(profile, options = {}) {
  return new Promise(resolve => {
    const { title = 'Autenticación requerida' } = options;

    const overlay   = document.getElementById('auth-modal-overlay');
    const titleEl   = document.getElementById('auth-modal-title');
    const passwordIn = document.getElementById('auth-password');
    const errorDiv  = document.getElementById('auth-error');
    const acceptBtn = document.getElementById('auth-accept-btn');
    const cancelBtn = document.getElementById('auth-cancel-btn');

    titleEl.textContent = title;
    passwordIn.value = '';
    errorDiv.hidden = true;
    errorDiv.textContent = '';
    acceptBtn.disabled = true;
    overlay.hidden = false;
    passwordIn.focus();

    function checkReady() {
      acceptBtn.disabled = passwordIn.value.length === 0;
    }

    async function onAccept() {
      acceptBtn.disabled = true;
      errorDiv.hidden = true;
      try {
        const resp = await fetch('/api/auth/verify', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ profile, password: passwordIn.value })
        });
        if (resp.ok) {
          const result = { authenticated: true };
          if (options.returnPassword) result.password = passwordIn.value;
          cleanup();
          resolve(result);
        } else if (resp.status === 429) {
          const data = await resp.json().catch(() => ({}));
          errorDiv.textContent = data.error ?? 'Demasiados intentos. Espere 5 minutos.';
          errorDiv.hidden = false;
          acceptBtn.disabled = false;
        } else {
          errorDiv.textContent = 'Contraseña incorrecta.';
          errorDiv.hidden = false;
          passwordIn.value = '';
          checkReady();
          passwordIn.focus();
        }
      } catch {
        errorDiv.textContent = 'Error de conexión.';
        errorDiv.hidden = false;
        acceptBtn.disabled = false;
      }
    }

    function onCancel() { cleanup(); resolve(null); }

    function onKeydown(e) {
      if (e.key === 'Enter' && !acceptBtn.disabled) onAccept();
      if (e.key === 'Escape') onCancel();
    }

    function cleanup() {
      overlay.hidden = true;
      passwordIn.removeEventListener('input', checkReady);
      acceptBtn.removeEventListener('click', onAccept);
      cancelBtn.removeEventListener('click', onCancel);
      passwordIn.removeEventListener('keydown', onKeydown);
    }

    passwordIn.addEventListener('input', checkReady);
    acceptBtn.addEventListener('click', onAccept);
    cancelBtn.addEventListener('click', onCancel);
    passwordIn.addEventListener('keydown', onKeydown);
  });
}

const NAV_GROUPS = {
  pacientes: { tabs: [
    { id: 'followup',   label: 'Seguimiento' },
    { id: 'especiales', label: 'Técnicas Especiales' },
    { id: 'fisica',     label: 'Física' },
    { id: 'pacientes',  label: 'Buscar' },
  ]},
  agendas: { tabs: [
    { id: 'agenda',     label: 'Equipos' },
    { id: 'tomograph',  label: 'Tomógrafos' },
    { id: 'inicios',    label: 'Inicios' },
    { id: 'derivacion', label: 'Derivación' },
  ]},
  analisis: { tabs: [
    { id: 'alertas',    label: 'Alertas' },
    { id: 'resumen',    label: 'Tendencias' },
  ]},
  admin: { tabs: [
    { id: 'config',     label: 'Configuración' },
  ]},
};

function _renderTabRow(groupId) {
  const row = document.querySelector('.nav-row--tabs');
  row.innerHTML = '';
  for (const t of NAV_GROUPS[groupId].tabs) {
    const btn = document.createElement('button');
    btn.className = 'tab-btn' + (t.id === state.activeTab ? ' active' : '');
    btn.type = 'button';
    btn.dataset.tab = t.id;
    btn.textContent = t.label;
    btn.addEventListener('click', () => activateTab(t.id));
    row.appendChild(btn);
  }
}

function _syncGroupActive(groupId) {
  document.querySelectorAll('.nav-group-btn').forEach(btn =>
    btn.classList.toggle('active', btn.dataset.group === groupId));
}

async function activateGroup(groupId) {
  const prevGroup = document.querySelector('.nav-group-btn.active')?.dataset.group ?? 'pacientes';
  _syncGroupActive(groupId);
  _renderTabRow(groupId);
  const firstTab = NAV_GROUPS[groupId].tabs[0].id;
  const ok = await activateTab(firstTab);
  if (!ok) {
    _syncGroupActive(prevGroup);
    _renderTabRow(prevGroup);
  }
}

async function activateTab(targetTab) {
  if (targetTab === 'config') {
    const auth = await requestAuth('sysadmin', { title: 'Acceso a Configuración' });
    if (!auth || !auth.authenticated) return false;
  }
  state.activeTab = targetTab;
  document.querySelectorAll('.nav-row--tabs .tab-btn').forEach(btn =>
    btn.classList.toggle('active', btn.dataset.tab === targetTab));
  document.querySelectorAll('.tab-panel').forEach(p =>
    p.classList.toggle('active', p.id === `tab-${targetTab}`));
  if (targetTab === 'pacientes') loadPacientesTab();
  if (targetTab === 'inicios') loadIniciosTab();
  if (targetTab === 'resumen') renderResumen();
  if (targetTab === 'alertas') loadAlertasTab();
  if (targetTab === 'agenda') refreshAgendaView();
  if (targetTab === 'tomograph') refreshTomographAgendaView();
  if (targetTab === 'config') loadConfigData();
  if (targetTab === 'fisica') renderFisicaView();
  if (targetTab === 'especiales') renderEspeciales();
  if (targetTab === 'derivacion') openDerivacion();
  return true;
}

function wireTabs() {
  document.querySelectorAll('.nav-group-btn').forEach(btn =>
    btn.addEventListener('click', () => activateGroup(btn.dataset.group)));
  const initGroup = Object.keys(NAV_GROUPS).find(g =>
    NAV_GROUPS[g].tabs.some(t => t.id === state.activeTab)) ?? 'pacientes';
  _syncGroupActive(initGroup);
  _renderTabRow(initGroup);
  document.querySelectorAll('.tab-panel').forEach(p =>
    p.classList.toggle('active', p.id === `tab-${state.activeTab}`));
}

// ── Actions ───────────────────────────────────────────────────────────────────

function wireActions() {
  // Dropdown scrape menu
  const menuBtn = document.getElementById('scrapMenuBtn');
  const menu = document.getElementById('scrapMenu');
  menuBtn.addEventListener('click', e => {
    menu.hidden = !menu.hidden;
    e.stopPropagation();
  });
  document.addEventListener('click', () => { menu.hidden = true; });
  document.getElementById('actionUpdateSitramed').addEventListener('click', async () => {
    const auth = await requestAuth('sysadmin', { title: 'Confirmar actualización' });
    if (!auth || !auth.authenticated) return;
    actionUpdateSitramed();
  });
  document.getElementById('actionUpdateAria').addEventListener('click', async () => {
    const auth = await requestAuth('sysadmin', { title: 'Confirmar actualización' });
    if (!auth || !auth.authenticated) return;
    actionUpdateAria();
  });
  document.getElementById('actionUpdateAll').addEventListener('click', async () => {
    const auth = await requestAuth('sysadmin', { title: 'Confirmar actualización' });
    if (!auth || !auth.authenticated) return;
    actionUpdateAll();
  });

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
  document.getElementById('refreshAlertasBtn').addEventListener('click', () => {
    state.alertas.loaded = false;
    loadAlertasTab();
  });
}

// ── API wrappers ──────────────────────────────────────────────────────────────

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
  buildEspecialesFilters(data);
  renderEspeciales();
  populateAgendaTestControls(data);
  loadAlertasTab();
  _refreshReservations();
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

  buildStageGroupFilterPills(data);
  buildProfesionalFilterPills();
}

function buildStageGroupFilterPills(data) {
  const stages = data.stages ?? [];
  const groupMap = new Map();
  stages.forEach(s => {
    if (!groupMap.has(s.groupName)) groupMap.set(s.groupName, { stages: [], minSort: s.sortOrder ?? 999 });
    const g = groupMap.get(s.groupName);
    g.stages.push(s);
    if ((s.sortOrder ?? 999) < g.minSort) g.minSort = s.sortOrder ?? 999;
  });
  const groups = [...groupMap.entries()].sort((a, b) => a[1].minSort - b[1].minSort);

  const row = document.getElementById('stageGroupFilterPills');
  if (!row) return;
  row.innerHTML = '';
  row.appendChild(makePill('Todas', state.followup.stageGroupFilter === null, () => {
    state.followup.stageGroupFilter = null;
    state.followup.stageFilter = null;
    state.followup.activeCenter = null;
    state.followup.activeStage = null;
    _updateStageSubFilter();
    renderFollowUp();
  }));
  groups.forEach(([groupName, { stages: gs }]) => row.appendChild(
    makePill(groupName, state.followup.stageGroupFilter === groupName, () => {
      state.followup.stageGroupFilter = groupName;
      state.followup.stageFilter = gs.length === 1 ? gs[0].code : null;
      state.followup.activeCenter = null;
      state.followup.activeStage = null;
      _updateStageSubFilter();
      renderFollowUp();
    })
  ));
  _updateStageSubFilter();
}

function _updateStageSubFilter() {
  const stages = state.homeData?.stages ?? [];
  const subRow = document.getElementById('stageSubFilterRow');
  const pillsEl = document.getElementById('stageFilterPills');
  if (!subRow || !pillsEl) return;

  const groupName = state.followup.stageGroupFilter;
  const gs = groupName
    ? stages.filter(s => s.groupName === groupName).sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0))
    : [];

  const showSub = gs.length > 1;
  subRow.classList.toggle('visible', showSub);
  if (!showSub) { pillsEl.innerHTML = ''; return; }

  pillsEl.innerHTML = '';
  pillsEl.appendChild(makePill('Todas', state.followup.stageFilter === null, () => {
    state.followup.stageFilter = null;
    state.followup.activeCenter = null;
    state.followup.activeStage = null;
    renderFollowUp();
  }));
  gs.forEach(s => pillsEl.appendChild(
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
    state.followup.stageGroupFilter = null;
    state.followup.stageFilter = null;
    state.followup.activeCenter = null;
    state.followup.activeStage = null;
    _updateStageSubFilter();
    renderFollowUp();
  }));
  ['Medicos', 'Fisicos'].forEach(prof => row.appendChild(
    makePill(prof, state.followup.profesionalFilter === prof, () => {
      state.followup.profesionalFilter = prof;
      state.followup.stageGroupFilter = null;
      state.followup.stageFilter = null;
      state.followup.activeCenter = null;
      state.followup.activeStage = null;
      _updateStageSubFilter();
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

  // Sync grupo etapa pills
  document.querySelectorAll('#stageGroupFilterPills .pill-btn').forEach(btn => {
    btn.classList.toggle('active',
      btn.textContent === 'Todas'
        ? state.followup.stageGroupFilter === null
        : btn.textContent === state.followup.stageGroupFilter);
  });

  // Sync sub-etapa pills
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

function followUpSort(a, b) {
  const lwa = a.isLongWait ? 1 : 0, lwb = b.isLongWait ? 1 : 0;
  if (lwa !== lwb) return lwa - lwb;
  return b.daysInStage - a.daysInStage;
}

function renderFollowupDetail() {
  const panel = document.getElementById('followupDetail');
  if (!state.homeData) { panel.innerHTML = ''; return; }

  const allPatients = state.homeData.patients ?? [];
  const stageDefs = state.homeData.stages ?? [];

  // Stage detail
  if (state.followup.activeCenter && state.followup.activeStage) {
    const def = stageDefs.find(s => s.code === state.followup.activeStage);
    const pats = allPatients.filter(p =>
      !isExcludedTechnique(p) &&
      p.centerName === state.followup.activeCenter && p.stageCode === state.followup.activeStage)
      .sort(followUpSort);

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
        daysBadge(p, dc);
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
    pats.sort(followUpSort);

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
        daysBadge(p, dc);
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
      const stagePats = pats.filter(p => p.stageCode === code).sort(followUpSort);
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
          daysBadge(p, dc);
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

function _populateDateSelect(stateSlice, elementId) {
  const today = todayStr();
  const futureScraped = stateSlice.availableDates.filter(d => d >= today).sort();
  const lastScraped = futureScraped.length > 0 ? futureScraped[futureScraped.length - 1] : today;

  // Fechas desde hoy hasta el último día scrapeado (inclusive), todas las intermedias incluidas
  const all = [];
  const d = new Date(today + 'T00:00:00');
  const last = new Date(lastScraped + 'T00:00:00');
  while (d <= last) {
    all.push(`${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}`);
    d.setDate(d.getDate() + 1);
  }

  const sel = document.getElementById(elementId);
  const prev = sel.value;
  const target = stateSlice.selectedDate || prev;
  sel.innerHTML = '<option value="">-- elegir fecha --</option>';
  all.forEach(date => {
    const opt = document.createElement('option');
    opt.value = date;
    const scraped = stateSlice.availableDates.includes(date);
    const isToday = date === today;
    opt.textContent = isToday ? `${formatDisplayDate(date)} (hoy)` : scraped ? `${formatDisplayDate(date)} ✓` : formatDisplayDate(date);
    if (!scraped && !isToday) { opt.disabled = true; opt.style.color = '#aaa'; }
    if (date === target && !opt.disabled) opt.selected = true;
    sel.appendChild(opt);
  });

  if (!stateSlice.selectedDate && prev) stateSlice.selectedDate = prev;
}

function populateAgendaDateSelect()    { _populateDateSelect(state.agenda,          'agendaDateSelect'); }
function populateTomographDateSelect() { _populateDateSelect(state.tomographAgenda, 'tomographDateSelect'); }

async function loadAgendaForSelectedDate() {
  const date = state.agenda.selectedDate;
  const st = document.getElementById('scrapeStatus');
  if (!date) { state.agenda.slots = []; renderAgenda(); return; }

  const today = todayStr();
  const isFuture = date > today;
  st.textContent = isFuture ? 'Scraping en tiempo real...' : 'Cargando...';
  state.agenda.loading = true;
  state.agenda.scrapingErrors = [];

  try {
    const resp = await fetch(`/api/agenda?date=${date}`);
    if (resp.ok) {
      const data = await resp.json();
      state.agenda.slots = data.slots ?? data;
      state.agenda.scrapingErrors = data.scrapingErrors ?? [];
      const scraped = state.agenda.slots.filter(s => !s.isEstimated).length;
      const estimated = state.agenda.slots.filter(s => s.isEstimated).length;
      const errCount = state.agenda.scrapingErrors.length;
      st.textContent = `${scraped} en agenda${estimated > 0 ? ` + ${estimated} estimados` : ''}` +
        (errCount > 0 ? ` · ⚠ ${errCount} equipo${errCount > 1 ? 's' : ''} sin datos` : '');
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
      const hasError = state.agenda.scrapingErrors.includes(cap.machineName);
      const slots = state.agenda.slots.filter(s => s.machineName === cap.machineName && !isExcludedSlot(s));
      const scraped = slots.filter(s => !s.isEstimated).length;
      const estimated = slots.filter(s => s.isEstimated).length;
      const freeMin = freeMinutes(cap, scraped);
      const totalSlots = Math.floor((Number(cap.workingHours) - Number(cap.reservedSpecialHours)) * 60 / cap.standardSlotMinutes);
      const freeSlots = Math.floor(freeMin / cap.standardSlotMinutes);
      const capCls = hasError ? 'cap-scrape-error' : capacityClass(freeSlots, totalSlots);
      const isActive = state.agenda.activeMachine === cap.machineName;
      const shortName = cap.machineName.replace(/^[^-]+ - /, '');

      const card = document.createElement('div');
      card.className = `machine-card ${capCls}${isActive ? ' active' : ''}`;
      card.innerHTML =
        `<div class="machine-card-name">${shortName}</div>` +
        (hasError
          ? `<div class="machine-card-scrape-error">⚠ Error de scraping</div>`
          : `<div class="machine-card-stats">` +
              `<span>${scraped} pac</span>` +
              (estimated > 0 ? `<span class="est-badge">+${estimated} est.</span>` : '') +
            `</div>` +
            `<div class="machine-card-free">${formatMinutes(freeMin)} libre · ${freeSlots} t.</div>`);

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

  const scraped = machineSlots.filter(s => !s.isEstimated && !isExcludedSlot(s));
  const estimated = machineSlots.filter(s => s.isEstimated && !isExcludedSlot(s));

  if (scraped.length === 0 && (isToday || estimated.length === 0)) {
    panel.appendChild(el('p', 'detail-placeholder',
      isToday ? 'Sin turnos en agenda para hoy.' : 'Sin turnos para esta fecha.'));
    if (!isToday && estimated.length === 0) return;
  }

  // Ghost slots: reservations matching this machine and date
  const ghostSlots = [];
  window.activeReservations.forEach(r => {
    if (r.reservedDate !== state.agenda.selectedDate) return;
    if (r.machineDisplayName !== state.agenda.activeMachine) return;
    ghostSlots.push({ _ghost: true, startTime: r.reservedTime || '', reservation: r });
  });

  // Real + ghost sorted by time; estimated appended at end
  const realAndGhost = [...scraped, ...ghostSlots]
    .sort((a, b) => (a.startTime || 'zzz').localeCompare(b.startTime || 'zzz'));
  const all = [...realAndGhost, ...estimated];

  all.forEach(slot => {
    const row = document.createElement('div');

    if (slot._ghost) {
      const r = slot.reservation;
      row.className = 'slot-row reserved-ghost';
      row.innerHTML =
        `<span class="slot-time">${r.reservedTime || '~'}</span>` +
        `<span class="slot-patient">${esc(r.patientName)}</span>` +
        `<span class="slot-badge" style="background:var(--color-reservation-bg);color:var(--color-reservation);border:1px solid var(--color-reservation-border)">turno reservado</span>`;
      panel.appendChild(row);
      return;
    }

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

function _addBusinessDays(dateStr, n) {
  if (!n || n <= 0) return dateStr;
  const d = new Date(dateStr + 'T12:00:00');
  let added = 0;
  while (added < Math.ceil(n)) {
    d.setDate(d.getDate() + 1);
    if (d.getDay() > 0 && d.getDay() < 6) added++;
  }
  return d.toISOString().slice(0, 10);
}

function _subtractBusinessDays(dateStr, n) {
  if (!n || n <= 0) return dateStr;
  const d = new Date(dateStr + 'T12:00:00');
  let removed = 0;
  while (removed < n) {
    d.setDate(d.getDate() - 1);
    if (d.getDay() > 0 && d.getDay() < 6) removed++;
  }
  return d.toISOString().slice(0, 10);
}

function _fisicaFreeSlots(machineName) {
  const eq = (state.homeData?.equipments ?? []).find(e => e.displayName === machineName);
  if (!eq?.standardSlotMinutes || !eq?.workingHours) return null;
  const total = Math.floor((Number(eq.workingHours) - Number(eq.reservedSpecialHours || 0)) * 60 / eq.standardSlotMinutes);
  return total - (eq.agendaPatients ?? 0);
}

async function _fisicaGetStageDays(stages) {
  if (!state.fisica.recoWeeklyStats) {
    try {
      const resp = await fetch('/api/stats/weekly');
      state.fisica.recoWeeklyStats = resp.ok ? await resp.json() : [];
    } catch { state.fisica.recoWeeklyStats = []; }
  }
  const stats = state.fisica.recoWeeklyStats;
  const { source, weeksAvailable, weeksRequired } = resolvePlanningTimeSource(stats);

  if (source === 'expected_days') {
    return {
      map: Object.fromEntries(stages.map(s => [s.code, s.expectedDays ?? 0])),
      source: 'expected_days',
      weeksUsed: weeksAvailable,
      weeksRequired
    };
  }

  const weekStarts = [...new Set(stats.map(s => s.weekStart))].sort().reverse();
  const N = Math.min(weekStarts.length, 8);
  const recentWeeks = new Set(weekStarts.slice(0, N));
  const agg = {};
  for (const s of stats.filter(s => recentWeeks.has(s.weekStart))) {
    if (!agg[s.stageCode]) agg[s.stageCode] = { count: 0, sumDays: 0 };
    agg[s.stageCode].count += s.count;
    agg[s.stageCode].sumDays += s.sumDays;
  }
  const map = {};
  for (const s of stages) {
    map[s.code] = agg[s.code]?.count > 0 ? agg[s.code].sumDays / agg[s.code].count : (s.expectedDays ?? 0);
  }
  return { map, source: 'weekly_stats', weeksUsed: N, weeksRequired };
}

async function _fisicaEstimatedDate(fromStageCode) {
  const stages = state.configData?.stages ?? state.homeData?.stages ?? [];
  const f6b = stages.find(s => s.code === 'F6B');
  const f11 = stages.find(s => s.code === 'F11');
  if (!f11) return { dateStr: null, source: 'expected_days', weeksUsed: 0, breakdown: [] };
  const from = stages.find(s => s.code === fromStageCode) ?? f6b;
  const fromSort = from?.sortOrder ?? 0;
  const rangeStages = stages
    .filter(s => (s.sortOrder ?? 0) >= fromSort && (s.sortOrder ?? 0) <= (f11.sortOrder ?? 999))
    .sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0));
  const { map, source, weeksUsed, weeksRequired } = await _fisicaGetStageDays(stages);
  const breakdown = rangeStages.map(s => ({
    code: s.code,
    name: s.displayName ?? s.code,
    days: map[s.code] ?? s.expectedDays ?? 0,
    expected: s.expectedDays ?? 0
  }));
  const totalDays = breakdown.reduce((sum, b) => sum + b.days, 0);
  return { dateStr: _addBusinessDays(todayStr(), Math.round(totalDays)), totalDays, source, weeksUsed, weeksRequired, breakdown };
}

function _fisicaHighlightedKeys(patient) {
  if (!patient) return [];
  const label = (patient.treatmentLabel || '').toUpperCase();
  const tech  = (patient.treatmentTechnique || '').toUpperCase();
  if (label.startsWith('3DC') || tech === '3DC') return TECH_HIGHLIGHT_MAP['3DC'];
  if (label.includes('IGRT') && label.includes('VMAT')) return [...TECH_HIGHLIGHT_MAP['IGRT'], ...TECH_HIGHLIGHT_MAP['VMAT']];
  if (label.startsWith('IGRT') || tech === 'IGRT') return TECH_HIGHLIGHT_MAP['IGRT'];
  if (label.includes('VMAT') || tech === 'VMAT') return TECH_HIGHLIGHT_MAP['VMAT'];
  if (label === 'IMRT' || tech === 'IMRT') return TECH_HIGHLIGHT_MAP['IMRT'];
  if (label.startsWith('SBRT') || tech === 'SBRT' || tech === 'SRS') return TECH_HIGHLIGHT_MAP['SBRT'];
  if (label.startsWith('RC') || tech === 'RC') return TECH_HIGHLIGHT_MAP['RC'];
  return [];
}

function _fisicaPatientRowClickable(row, p) {
  row.style.cursor = 'pointer';
  row.classList.toggle('fisica-pat-selected', state.fisica.selectedPatient?.patientId === p.patientId);
  row.addEventListener('click', () => {
    state.fisica.selectedPatient = state.fisica.selectedPatient?.patientId === p.patientId ? null : p;
    document.querySelectorAll('#fisicaDetailPanel .patient-row').forEach(r => r.classList.remove('fisica-pat-selected'));
    if (state.fisica.selectedPatient) row.classList.add('fisica-pat-selected');
    // Activar columna de reco aunque no haya un centro explícito seleccionado
    const fisicaLayout = document.getElementById('fisicaLayout');
    if (fisicaLayout) fisicaLayout.classList.toggle('fisica-three-col', !!state.fisica.selectedPatient);
    renderFisicaReco();
  });
}

async function renderFisicaReco() {
  const panel = document.getElementById('fisicaRecoPanel');
  if (!panel) return;

  const patient = state.fisica.selectedPatient;
  // Centro: selección explícita (1 centro) o inferido del paciente seleccionado
  const center = state.fisica.selectedCenters.size === 1
    ? [...state.fisica.selectedCenters][0]
    : (patient?.centerName ?? null);
  if (!center) { panel.innerHTML = ''; return; }

  panel.innerHTML = '<p class="detail-placeholder">Calculando…</p>';

  if (!state.configData) {
    try {
      const resp = await fetch('/api/configuration');
      state.configData = resp.ok ? await resp.json() : null;
    } catch {}
  }
  const allMachines = state.homeData?.configuration?.machines || [];
  const centerMachines = allMachines.filter(m => m.centerName === center);
  const allCaps = state.configData?.machineCapabilities || [];
  const centerCaps = centerMachines.map(m => allCaps.find(c => c.machineName === m.displayName)).filter(Boolean);

  const fromStage = patient?.stageCode ?? 'F6B';
  const { dateStr, totalDays, source, weeksUsed, weeksRequired, breakdown } = await _fisicaEstimatedDate(fromStage);

  const highlighted = _fisicaHighlightedKeys(patient);

  panel.innerHTML = '';
  const titleText = patient
    ? `Recomendación — ${patient.patientName.split(' ').slice(0, 2).join(' ')}`
    : 'Recomendación de equipo';
  panel.appendChild(el('div', 'detail-title', titleText));

  if (dateStr) {
    panel.appendChild(el('div', 'fisica-reco-subtitle',
      `Inicio estimado: ${_fmtDate(dateStr)}${patient ? ` (desde ${patient.stageCode})` : ' (desde F6B)'}`));
  }

  const grid = document.createElement('div');
  grid.className = 'fisica-reco-grid';

  const rankMachines = machines => machines
    .map(m => ({ name: m.displayName, freeSlots: _fisicaFreeSlots(m.displayName) }))
    .sort((a, b) => (b.freeSlots ?? -1) - (a.freeSlots ?? -1))
    .slice(0, 2);

  const cardInnerHtml = (tech, ranked) =>
    `<div class="technique-card-title">${tech.label}</div>` +
    `<hr class="technique-card-divider">` +
    ranked.map((m, i) =>
      `<div class="technique-card-machine">` +
        `<span class="technique-card-rank">${i === 0 ? '1°' : '2°'}</span>` +
        `<span class="technique-card-mname">${_shortName(m.name)}</span>` +
        `<span class="technique-card-slots"${m.freeSlots != null && m.freeSlots < 0 ? ' style="color:var(--red,#c0392b)"' : ''}>${m.freeSlots != null ? `${m.freeSlots} libres` : '?'}</span>` +
      `</div>`
    ).join('');

  if (patient && highlighted.length > 0) {
    // Con paciente: mostrar solo las tarjetas relevantes con equipo disponible
    for (const tech of FISICA_TECHNIQUES) {
      if (!highlighted.includes(tech.key)) continue;
      const enabledMachines = centerMachines.filter(m => {
        const cap = allCaps.find(c => c.machineName === m.displayName);
        return cap ? tech.machineEnabled(cap) : false;
      });
      if (!enabledMachines.length) continue;
      const card = document.createElement('div');
      card.className = 'technique-card technique-card--highlighted';
      card.innerHTML = cardInnerHtml(tech, rankMachines(enabledMachines));
      grid.appendChild(card);
    }
  } else {
    // Sin paciente: mostrar todas las tarjetas habilitadas del centro
    for (const tech of FISICA_TECHNIQUES) {
      if (!tech.centerEnabled(centerCaps)) continue;
      const enabledMachines = centerMachines.filter(m => {
        const cap = allCaps.find(c => c.machineName === m.displayName);
        return cap ? tech.machineEnabled(cap) : false;
      });
      if (!enabledMachines.length) continue;
      const card = document.createElement('div');
      card.className = 'technique-card';
      card.innerHTML = cardInnerHtml(tech, rankMachines(enabledMachines));
      grid.appendChild(card);
    }
  }

  panel.appendChild(grid);

  let legendLine1, legendLine2;
  if (source === 'weekly_stats') {
    legendLine1 = `Tiempos basados en estadísticas de las últimas ${weeksUsed} semanas`;
  } else if (weeksUsed === 0) {
    legendLine1 = 'Tiempos basados en valores de referencia';
    legendLine2 = `(sin estadísticas acumuladas aún — se usarán automáticamente al completar ${weeksRequired} semanas)`;
  } else {
    legendLine1 = 'Tiempos basados en valores de referencia';
    legendLine2 = `(${weeksUsed} semana${weeksUsed !== 1 ? 's' : ''} acumulada${weeksUsed !== 1 ? 's' : ''} de ${weeksRequired} necesarias para usar estadísticas)`;
  }
  const legendEl = document.createElement('div');
  legendEl.className = 'fisica-reco-legend';
  legendEl.innerHTML = legendLine1 + (legendLine2 ? `<br>${legendLine2}` : '') +
    '<br>(desde F6B — Esperando Planificación)';
  panel.appendChild(legendEl);
}

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
    state.fisica.selectedPatient = null;
    renderFisicaView();
  }));
  centers.forEach(c => {
    row.appendChild(makePill(c, state.fisica.selectedCenters.has(c), () => {
      if (state.fisica.selectedCenters.has(c)) state.fisica.selectedCenters.delete(c);
      else state.fisica.selectedCenters.add(c);
      state.fisica.selectedPatient = null;
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

  // Toggle 3-col layout and reco panel
  const hasSingleCenter = state.fisica.selectedCenters.size === 1;
  const hasPatient = !!state.fisica.selectedPatient;
  const showReco = hasSingleCenter || hasPatient;
  const fisicaLayout = document.getElementById('fisicaLayout');
  if (fisicaLayout) fisicaLayout.classList.toggle('fisica-three-col', showReco);
  if (showReco) renderFisicaReco();
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
        daysBadge(p, dc);
      panel.appendChild(row);
      _fisicaPatientRowClickable(row, p);
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
          daysBadge(p, dc);
        panel.appendChild(row);
        _fisicaPatientRowClickable(row, p);
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

  const p1Row = document.createElement('article');
  p1Row.className = 'config-row';
  p1Row.innerHTML =
    `<strong>P1 — alerta despues de</strong>` +
    `<span>Dias en planificacion (F6A en adelante) antes de alertar para prioridad 1</span>` +
    `<label><input type="number" class="config-input-number" id="cfgP1Threshold" min="1" value="${state.configData.p1AlertThresholdDays ?? 5}"> dias</label>`;
  gen.appendChild(p1Row);

  // Stages
  const stageList = document.getElementById('stageConfigList');
  stageList.innerHTML = '';
  const stageTable = document.createElement('table');
  stageTable.className = 'config-table';
  stageTable.innerHTML = '<thead><tr><th>Código</th><th>Nombre</th><th>Grupo</th><th>Días ref</th></tr></thead>';
  const stageTbody = document.createElement('tbody');
  (state.configData.stages ?? []).forEach(s => {
    const tr = document.createElement('tr');
    tr.innerHTML = `<td class="readonly">${esc(s.code)}</td><td class="readonly">${esc(s.displayName)}</td><td class="readonly">${esc(s.groupName)}</td><td><input type="number" id="cfgStage_${s.code}" min="1" value="${s.expectedDays}"></td>`;
    stageTbody.appendChild(tr);
  });
  stageTable.appendChild(stageTbody);
  stageList.appendChild(stageTable);
  wireTableHighlight(stageTable, false);

  // Machine capacities
  renderMachineTable('machineConfigList', state.configData.machineCapacities ?? [], state.configData.machineCapabilities ?? []);

  // Tomograph capacities
  renderTomographTable('tomographConfigList', state.configData.tomographCapacities ?? []);

  // Technique durations
  const techList = document.getElementById('techniqueDurationList');
  techList.innerHTML = '';
  const techTable = document.createElement('table');
  techTable.className = 'config-table';
  techTable.innerHTML = '<thead><tr><th>Técnica</th><th>Min opción 1</th><th>Min opción 2</th></tr></thead>';
  const techTbody = document.createElement('tbody');
  (state.configData.techniqueDurations ?? []).forEach((t, i) => {
    const mins = t.validDurationMinutes ?? [];
    const hasSecond = mins.length > 1;
    const tr = document.createElement('tr');
    tr.innerHTML = `<td class="readonly">${esc(t.treatmentLabel)}</td><td><input type="number" id="td_${i}_0" min="1" value="${mins[0] ?? 15}"></td><td><input type="number" id="td_${i}_1" min="1" value="${mins[1] ?? ''}" ${hasSecond ? '' : 'disabled placeholder="—"'}></td>`;
    techTbody.appendChild(tr);
  });
  techTable.appendChild(techTbody);
  techList.appendChild(techTable);
  wireTableHighlight(techTable, false);
}

function wireTableHighlight(table, includeCol) {
  table.addEventListener('focusin', e => {
    const inp = e.target.closest('input');
    if (!inp) return;
    table.querySelectorAll('.row-active').forEach(r => r.classList.remove('row-active'));
    table.querySelectorAll('.col-active').forEach(h => h.classList.remove('col-active'));
    inp.closest('tr').classList.add('row-active');
    if (includeCol) {
      const td = inp.closest('td');
      const colIdx = [...td.parentElement.children].indexOf(td);
      const th = table.querySelector(`thead tr th:nth-child(${colIdx + 1})`);
      if (th) th.classList.add('col-active');
    }
  });
  table.addEventListener('focusout', () => {
    table.querySelectorAll('.row-active').forEach(r => r.classList.remove('row-active'));
    table.querySelectorAll('.col-active').forEach(h => h.classList.remove('col-active'));
  });
}

const CAP_COLS = [
  { key: 'vmat',      label: 'VMAT',  prop: 'canDoVMAT'      },
  { key: 'electrons', label: 'Electr', prop: 'canDoElectrons' },
  { key: 'sbrt',      label: 'SBRT',  prop: 'canDoSBRT'      },
  { key: 'rc',        label: 'RC',    prop: 'canDoRC'        },
  { key: 'tbi',       label: 'TBI',   prop: 'canDoTBI'       },
  { key: 'tset',      label: 'TSET',  prop: 'canDoTSET'      },
  { key: 'igrt',      label: 'IGRT',  prop: 'canDoIGRT'      },
];
const ENERGY_COLS = [{ label: '10X', id: '10x' }, { label: '15X', id: '15x' }, { label: '18X', id: '18x' }];

function renderMachineTable(containerId, capacities, machCaps) {
  const container = document.getElementById(containerId);
  container.innerHTML = '';
  const table = document.createElement('table');
  table.className = 'config-table';
  table.innerHTML = `<thead><tr>
    <th>Equipo</th><th>Centro</th><th>Hs</th><th>Min</th><th>Hs res</th>
    ${CAP_COLS.map(c => `<th>${c.label}</th>`).join('')}
    ${ENERGY_COLS.map(e => `<th>${e.label}</th>`).join('')}
  </tr></thead>`;
  const tbody = document.createElement('tbody');
  capacities.forEach((c, i) => {
    const mc = machCaps.find(m => m.machineName === c.machineName);
    const heb = mc?.highEnergyBeams ?? [];
    const tr = document.createElement('tr');
    tr.innerHTML =
      `<td class="readonly">${esc(c.machineName)}</td>` +
      `<td class="readonly">${esc(c.centerName)}</td>` +
      `<td><input type="number" id="mach_hs_${i}" min="1" step="0.5" value="${c.workingHours}"></td>` +
      `<td><input type="number" id="mach_slot_${i}" min="1" value="${c.standardSlotMinutes}"></td>` +
      `<td><input type="number" id="mach_res_${i}" min="0" step="0.5" value="${c.reservedSpecialHours}"></td>` +
      CAP_COLS.map(col => mc
        ? `<td><input type="checkbox" id="cap_${i}_${col.key}" ${mc[col.prop] ? 'checked' : ''}></td>`
        : '<td>—</td>').join('') +
      ENERGY_COLS.map(e => mc
        ? `<td><input type="checkbox" id="cap_${i}_${e.id}" ${heb.includes(e.label) ? 'checked' : ''}></td>`
        : '<td>—</td>').join('');
    tbody.appendChild(tr);
  });
  table.appendChild(tbody);
  container.appendChild(table);
  wireTableHighlight(table, true);
}

function renderTomographTable(containerId, capacities) {
  const container = document.getElementById(containerId);
  container.innerHTML = '';
  const table = document.createElement('table');
  table.className = 'config-table';
  table.innerHTML = '<thead><tr><th>Tomógrafo</th><th>Centro</th><th>Hs</th><th>Min turno</th><th>Hs res</th></tr></thead>';
  const tbody = document.createElement('tbody');
  capacities.forEach((c, i) => {
    const tr = document.createElement('tr');
    tr.innerHTML =
      `<td class="readonly">${esc(c.machineName)}</td>` +
      `<td class="readonly">${esc(c.centerName)}</td>` +
      `<td><input type="number" id="tomo_hs_${i}" min="1" step="0.5" value="${c.workingHours}"></td>` +
      `<td><input type="number" id="tomo_slot_${i}" min="1" value="${c.standardSlotMinutes}"></td>` +
      `<td><input type="number" id="tomo_res_${i}" min="0" step="0.5" value="${c.reservedSpecialHours}"></td>`;
    tbody.appendChild(tr);
  });
  table.appendChild(tbody);
  container.appendChild(table);
  wireTableHighlight(table, false);
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
  const p1Input = document.getElementById('cfgP1Threshold');
  if (p1Input) state.configData.p1AlertThresholdDays = parseInt(p1Input.value) || state.configData.p1AlertThresholdDays;

  // Read technique durations
  (state.configData.techniqueDurations ?? []).forEach((t, i) => {
    const mins = t.validDurationMinutes ?? [];
    t.validDurationMinutes = mins.map((_, j) => {
      const inp = document.getElementById(`td_${i}_${j}`);
      return inp ? (parseInt(inp.value) || mins[j]) : mins[j];
    });
  });

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
  renderResumenTendencias();
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
    let cells = `<td><strong>${def?.displayName ?? code}</strong></td>`;

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

function _limpiarDerivacion() {
  deriv.equipoFallido = null;
  deriv.fechaInicio = todayStr();
  deriv.fechaFin = todayStr();
  deriv.incluirPlanificacion = false;
  deriv.agendaSlots = {};
  deriv.pacientes = [];
  deriv.asignaciones = {};
  deriv.seleccionado = null;
  document.getElementById('derivContent').hidden = true;
  document.getElementById('derivBarraResumen').hidden = true;
  _renderDerivConfig();
}

async function openDerivacion() {
  if (!_derivWired) {
    document.getElementById('derivCalcularBtn').addEventListener('click', _calcularDerivacion);
    document.getElementById('derivExportarBtn').addEventListener('click', () => exportarDerivacion(false));
    document.getElementById('derivExportarIgualBtn').addEventListener('click', () => exportarDerivacion(true));
    document.getElementById('derivLimpiarBtn').addEventListener('click', _limpiarDerivacion);
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
  const prev = deriv.equipoFallido;
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

  const warningEl = document.getElementById('derivAttendedWarning');
  if (warningEl) warningEl.hidden = true;

  try {
    const dates = _weekdaysBetween(fi, ff);
    const [agendaResults, attendedGuids] = await Promise.all([
      Promise.all(dates.map(d => fetch(`/api/agenda?date=${d}`).then(r => r.ok ? r.json() : []).catch(() => []))),
      _fetchAttendedGuids(equipo, dates)
    ]);
    dates.forEach((d, i) => {
      const raw = agendaResults[i];
      deriv.agendaSlots[d] = Array.isArray(raw) ? raw : (raw?.slots || []);
    });
    deriv.pacientes = _buildPacientesAfectados(equipo, dates, incluir);
    for (const p of deriv.pacientes) {
      if (p.sitraMedGuid && attendedGuids.has(p.sitraMedGuid.toLowerCase()))
        deriv.asignaciones[p.key] = { estado: 'atendido', equipo: null };
    }
    _renderDerivResult();
  } finally {
    btn.disabled = false; btn.textContent = 'Calcular derivacion';
  }
}

async function _fetchAttendedGuids(machineDisplayName, dates) {
  const guids = new Set();
  const results = await Promise.allSettled(
    dates.map(d =>
      fetch(`/api/derivation/attended-patients?machine=${encodeURIComponent(machineDisplayName)}&date=${d}`)
        .then(r => r.ok ? r.json() : null)
        .catch(() => null)
    )
  );
  let hadError = false;
  for (const r of results) {
    if (r.status === 'rejected' || !r.value) { hadError = true; continue; }
    if (r.value.error) hadError = true;
    for (const g of (r.value.attendedGuids || [])) guids.add(g.toLowerCase());
  }
  if (hadError) {
    const warningEl = document.getElementById('derivAttendedWarning');
    if (warningEl) warningEl.hidden = false;
  }
  return guids;
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

const _CENTER_ABBR = {
  'MEVA-Central':  'Cent',
  'CETRO':         'Cetro',
  'RT MEDRANO':    'Medrano',
  'MEVA-Viamonte': 'VMT',
  'QUILMES':       'Q',
  'SAN JUSTO':     'SJ',
};

// Label para botones de derivación: mismo centro → nombre corto; distinto centro → abreviatura + Eq#
function _derivMachineLabel(machine, failedCenter) {
  if (!machine) return '—';
  if (machine.centerName === failedCenter) return _shortName(machine.displayName);
  const abbr = _CENTER_ABBR[machine.centerName] || _shortName(machine.displayName);
  const num = _shortName(machine.displayName).match(/(\d+)$/);
  return num ? `${abbr} Eq${num[1]}` : abbr;
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

  // Horario: siempre mostrar fecha+hora juntas
  const horariosStr = (p.horarios || []).length
    ? p.horarios.map(h => {
        const [datePart, timePart] = h.split(' ');
        return timePart ? `${_fmtDate(datePart)} ${timePart}` : _fmtDate(datePart);
      }).filter(Boolean).join(' · ')
    : null;

  // Info de etapa/fracciones: solo cuando no hay horarios
  const infoStr = horariosStr
    ? null
    : (p.fechasTurno
      ? p.fechasTurno.map(_fmtDate).join(', ')
      : (p.fracciones ? `${p.etapaDisplay} · ${p.fracciones} fx` : (p.etapaDisplay || '—')));

  const allMachines = state.homeData?.configuration?.machines || [];
  const failedCenter = allMachines.find(m => m.displayName === deriv.equipoFallido)?.centerName;
  const assignedMach = asig?.equipo ? allMachines.find(m => m.displayName === asig.equipo) : null;

  const badgeHtml = estado === 'derivado'
    ? `<span class="deriv-badge deriv-badge-ok">→ ${_derivMachineLabel(assignedMach, failedCenter)}</span>`
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
      ${_derivMachineLabel(m, failedCenter)} →${m.compat?.warn ? ' ⚠' : ''}
    </button>`;
  }).join('');
  const othersSelect = others.length
    ? `<select class="deriv-others-sel" data-key="${p.key}">
        <option value="">Otros...</option>
        ${others.map(m => `<option value="${m.displayName}"${asig?.estado === 'derivado' && asig?.equipo === m.displayName ? ' selected' : ''}>${_derivMachineLabel(m, failedCenter)}${m.compat?.warn ? ' ⚠' : ''}</option>`).join('')}
       </select>`
    : '';

  const card = document.createElement('div');
  card.className = `deriv-patient-card estado-${estado}`;
  card.dataset.key = p.key;
  card.innerHTML = `
    <div class="deriv-card-header">
      ${priorityBadge(p.priority)}<span class="deriv-card-name">${p.nombre}</span>
      ${fmtHc(p.hc) !== '—' ? `<span class="deriv-card-hc">${fmtHc(p.hc)}</span>` : ''}
    </div>
    <div class="deriv-card-row">
      <span class="treatment-badge ${_derivLabelClass(label)}">${label}</span>
      ${infoStr ? `<span class="deriv-card-dates">${infoStr}</span>` : ''}
      ${horariosStr ? `<span class="deriv-card-horario">🕐 ${horariosStr}</span>` : ''}
    </div>
    ${estado !== 'atendido' ? `<div class="deriv-quickbtns">${quickBtns}${othersSelect}</div>` : ''}
    <div class="deriv-card-footer">
      ${badgeHtml}
      <div class="deriv-card-actions">
        <button class="deriv-atendido-btn${estado === 'atendido' ? ' active' : ''}"
                title="${estado === 'atendido' ? 'Quitar ya atendido' : 'Ya atendido hoy'}">✓</button>
        ${estado !== 'atendido' ? `<button class="deriv-suspend-btn${estado === 'suspendido' ? ' active' : ''}"
                title="${estado === 'suspendido' ? 'Quitar suspension' : 'Suspender'}">
          ${estado === 'suspendido' ? '↩' : '⊗'}
        </button>` : ''}
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

  card.querySelector('.deriv-suspend-btn')?.addEventListener('click', e => {
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
  if (l === 'VMAT')
    return cap?.canDoVMAT ? { ok: true, warn: false, reason: '' } : { ok: false, warn: false, reason: 'Sin capacidad VMAT' };
  if (l === 'SBRT - VMAT') {
    if (!cap?.canDoSBRT) return { ok: false, warn: false, reason: 'Sin capacidad SBRT' };
    if (!cap?.canDoVMAT) return { ok: false, warn: false, reason: 'Sin capacidad VMAT' };
    return { ok: true, warn: false, reason: '' };
  }
  if (l === 'RC - VMAT') {
    if (!cap?.canDoRC) return { ok: false, warn: false, reason: 'Sin capacidad RC' };
    if (!cap?.canDoVMAT) return { ok: false, warn: false, reason: 'Sin capacidad VMAT' };
    return { ok: true, warn: false, reason: '' };
  }
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
  const exportList = [...derivados, ...suspendidos, ...(incluirSinAsignar ? sinAsignar : [])];

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
    return `<tr style="${bg}"><td>${esc(p.nombre)}</td><td>${esc(fmtHc(p.hc))}</td><td>${esc(p.treatmentLabel||'—')}</td><td>${esc(fechas)}</td><td>${esc(destino)}</td><td>${esc(obs)}</td></tr>`;
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
  Generado el: ${now}&nbsp;·&nbsp;Generado por: MevaDash
</div>
${atendidos.length ? `<h2>Pacientes ya atendidos al momento del calculo</h2>
<table style="margin-bottom:24px"><thead><tr><th>Paciente</th><th>HC</th><th>Tecnica</th><th>Turno(s) / Etapa</th></tr></thead>
<tbody>${atendidos.map(p => {
  const fechas = p.fechasTurno ? p.fechasTurno.map(_fmtDate).join(', ') : (p.etapaDisplay || '—');
  return `<tr style="background:#f0faf0"><td>${esc(p.nombre)}</td><td>${esc(fmtHc(p.hc))}</td><td>${esc(p.treatmentLabel||'—')}</td><td>${esc(fechas)}</td></tr>`;
}).join('')}</tbody></table>` : ''}
<h2>Tabla de derivaciones</h2>
<table><thead><tr><th>Paciente</th><th>HC</th><th>Tecnica</th><th>Turno(s) / Etapa</th><th>Destino</th><th>Observaciones</th></tr></thead>
<tbody>${tableRows||'<tr><td colspan="6" style="color:#999;text-align:center">Sin pacientes</td></tr>'}</tbody></table>
<h2>Carga por equipo destino</h2>
<table style="width:auto"><thead><tr><th>Equipo destino</th><th>Pacientes derivados</th></tr></thead>
<tbody>${summaryRows}</tbody></table>
<div class="note">
  Total: ${deriv.pacientes.length}&nbsp;|&nbsp;Atendidos: ${atendidos.length}&nbsp;|&nbsp;Derivados: ${derivados.length}&nbsp;|&nbsp;Suspendidos: ${suspendidos.length}${incluirSinAsignar&&sinAsignar.length?'&nbsp;|&nbsp;Sin asignar: '+sinAsignar.length:''}<br>
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

function _fmtDayOfWeek(dateStr) {
  const d = new Date(dateStr + 'T12:00:00');
  return ['Domingo','Lunes','Martes','Miércoles','Jueves','Viernes','Sábado'][d.getDay()];
}

function _derivLabelClass(label) {
  if (!label) return '';
  const f = label.split(/[\s\-]/)[0].toUpperCase();
  const m = { VMAT:'VMAT', IMRT:'IMRT', SBRT:'SBRT', IGRT:'IGRT',
              RC:'RC', TBI:'TBI', TSET:'TSET', BQT:'BQT', IORT:'IORT', '3DC':'3DC' };
  return m[f] ? `tt-${m[f]}` : '';
}

// ── Alertas ───────────────────────────────────────────────────────────────────

function parseTime(timeStr) {
  if (!timeStr) return null;
  const parts = timeStr.split(':');
  const h = parseInt(parts[0]), m = parseInt(parts[1] ?? '0');
  if (isNaN(h) || isNaN(m)) return null;
  return h * 60 + m;
}

function validateSlotDuration(slot, techniqueDurations) {
  if (!slot.startTime || !slot.endTime) return null;
  if (!slot.treatmentLabel) return null;
  const start = parseTime(slot.startTime);
  const end   = parseTime(slot.endTime);
  if (start === null || end === null) return null;
  const actual = end - start;
  if (actual <= 0) return null;
  const config = (techniqueDurations || []).find(t =>
    t.treatmentLabel === slot.treatmentLabel ||
    slot.treatmentLabel.startsWith(t.treatmentLabel)
  );
  if (!config) return null;
  const isValid = config.validDurationMinutes.some(v => Math.abs(actual - v) <= 2);
  return isValid ? null : { slot, actualMinutes: actual, validMinutes: config.validDurationMinutes };
}

function _computeStageRefMap(weeklyStats, stageDefs) {
  const { source } = resolvePlanningTimeSource(weeklyStats || []);
  if (source === 'weekly_stats') {
    const weekStarts = [...new Set((weeklyStats || []).map(s => s.weekStart))].sort().reverse();
    const N = Math.min(weekStarts.length, 8);
    const recentWeeks = new Set(weekStarts.slice(0, N));
    const agg = {};
    for (const s of (weeklyStats || []).filter(s => recentWeeks.has(s.weekStart))) {
      if (!agg[s.stageCode]) agg[s.stageCode] = { count: 0, sumDays: 0 };
      agg[s.stageCode].count += s.count;
      agg[s.stageCode].sumDays += s.sumDays;
    }
    const map = {};
    for (const s of stageDefs)
      map[s.code] = agg[s.code]?.count > 0
        ? agg[s.code].sumDays / agg[s.code].count
        : (s.expectedDays ?? 0);
    return map;
  }
  return Object.fromEntries(stageDefs.map(s => [s.code, s.expectedDays ?? 0]));
}

const ALERT_EXCLUDED_TECHNIQUES = ['TBI', 'TSET', 'BQT', 'IORT'];

// Retorna {ref, source} o null (caller usa expectedDays).
// source: 'est. semanal' | 'promedio actual'
function getReferenceForAlerts(stageCode, weeklyStats, eligiblePatients) {
  const { source } = resolvePlanningTimeSource(weeklyStats || []);

  if (source === 'weekly_stats') {
    const stageStats = (weeklyStats || []).filter(s => s.stageCode === stageCode);
    if (stageStats.length > 0) {
      const recent = [...stageStats]
        .sort((a, b) => b.weekStart.localeCompare(a.weekStart))
        .slice(0, 8);
      const totalCount = recent.reduce((s, w) => s + w.count, 0);
      const totalSum   = recent.reduce((s, w) => s + w.sumDays, 0);
      if (totalCount > 0) return { ref: totalSum / totalCount, source: 'est. semanal' };
    }
  }

  // Fuente 2: promedio de pacientes activos en esa etapa (mínimo 3)
  const inStage = (eligiblePatients || []).filter(p => p.stageCode === stageCode);
  if (inStage.length >= 3) {
    const avg = inStage.reduce((s, p) => s + p.daysInStage, 0) / inStage.length;
    return { ref: avg, source: 'promedio actual' };
  }

  return null;  // caller usa expectedDays
}

// Suma getReferenceForAlerts para cada etapa desde F3 hasta upToStageCode (inclusive)
function _getAccumReference(upToStageCode, weeklyStats, eligiblePatients, stageDefs, stageMap) {
  const F3_SORT = stageDefs.find(s => s.code === 'F3')?.sortOrder ?? 10;
  const upSort  = stageMap[upToStageCode]?.sortOrder ?? 0;
  const stagesUpTo = stageDefs
    .filter(s => (s.sortOrder ?? 0) >= F3_SORT && (s.sortOrder ?? 0) <= upSort)
    .sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0));

  let total = 0, dominantSource = 'referencia';
  for (const stage of stagesUpTo) {
    const result = getReferenceForAlerts(stage.code, weeklyStats, eligiblePatients);
    if (result !== null) {
      total += result.ref;
      if (result.source === 'est. semanal') dominantSource = 'est. semanal';
      else if (dominantSource === 'referencia') dominantSource = 'promedio actual';
    } else {
      total += stage.expectedDays ?? 0;
    }
  }
  return { total, source: dominantSource };
}

function _alertasSourceLegend(source, weeksAvailable) {
  if (source === 'est. semanal')
    return `<p class="alertas-legend">Referencias basadas en estadísticas de las últimas ${weeksAvailable} semanas.</p>`;
  if (source === 'promedio actual')
    return `<p class="alertas-legend">Referencias basadas en el promedio actual de pacientes en cada etapa. Se usarán estadísticas semanales al completar 4 semanas.</p>`;
  return `<p class="alertas-legend">Referencias basadas en valores de referencia de configuración — algunas etapas sin datos suficientes.</p>`;
}

function _lastNBusinessDays(n) {
  const days = [];
  const d = new Date();
  while (days.length < n) {
    d.setDate(d.getDate() - 1);
    const dow = d.getDay();
    if (dow !== 0 && dow !== 6)
      days.push(`${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}`);
  }
  return days;
}

function _alertasSectionHtml(id, title, alerts, renderRows) {
  const count = alerts.length;
  const badge = `<span class="alertas-badge ${count > 0 ? 'alertas-badge-red' : 'alertas-badge-green'}">${count}</span>`;
  const open = count > 0 ? ' open' : '';
  return `<details class="alertas-details"${open}>
    <summary class="alertas-summary">${title} ${badge}</summary>
    <div class="alertas-content">
      ${count === 0
        ? '<p class="alertas-ok">Sin alertas</p>'
        : renderRows(alerts)
      }
    </div>
  </details>`;
}

function _alertasTable(headers, rows) {
  return `<table class="alertas-table">
    <thead><tr>${headers.map(h => `<th>${h}</th>`).join('')}</tr></thead>
    <tbody>${rows.join('')}</tbody>
  </table>`;
}

// ── Sección A: Centros ────────────────────────────────────────────────────────

function _buildAlertasCentros(patients, stageDefs, stageRefMap) {
  // A1: alertas por (centro, etapa)
  const excluded = new Set(ALERT_EXCLUDED_TECHNIQUES);
  const stageMap = Object.fromEntries(stageDefs.map(s => [s.code, s]));
  const byCenterStage = {};
  for (const p of patients) {
    if (p.isLongWait) continue;
    if (excluded.has(p.treatmentTechnique)) continue;
    const key = `${p.centerName}||${p.stageCode}`;
    if (!byCenterStage[key]) byCenterStage[key] = [];
    byCenterStage[key].push(p.daysInStage);
  }
  const alertsFlat = [];
  for (const [key, days] of Object.entries(byCenterStage)) {
    const [center, code] = key.split('||');
    const ref = stageRefMap[code] ?? stageMap[code]?.expectedDays ?? 0;
    if (ref <= 0) continue;
    const avg = days.reduce((a, b) => a + b, 0) / days.length;
    if (avg > ref * 2)
      alertsFlat.push({ center, stage: stageMap[code]?.displayName ?? code, avg: Math.round(avg), ref: Math.round(ref) });
  }

  return { alertsFlat };
}

function _renderAlertasCentros(patients, stageDefs, stageRefMap, weeklyStats) {
  const { alertsFlat } = _buildAlertasCentros(patients, stageDefs, stageRefMap);

  // Agrupar A1 por centro
  const byCenter = {};
  for (const a of alertsFlat) {
    if (!byCenter[a.center]) byCenter[a.center] = [];
    byCenter[a.center].push(a);
  }
  const centrosConAlerta = Object.keys(byCenter).length;
  const a1BadgeCls = centrosConAlerta > 0 ? 'alertas-badge-red' : 'alertas-badge-green';

  // Centros únicos de pacientes
  const allCenters = [...new Set(patients.map(p => p.centerName))].sort();

  // Rango F4→F11 para A2 per centro
  const f4Sort  = stageDefs.find(s => s.code === 'F4')?.sortOrder ?? 0;
  const f11Sort = stageDefs.find(s => s.code === 'F11')?.sortOrder ?? 999;
  const pathStages = stageDefs
    .filter(s => (s.sortOrder ?? 0) >= f4Sort && (s.sortOrder ?? 0) <= f11Sort)
    .sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0));

  // HTML: una tarjeta por centro (todos), con delays A1 + A2 al pie
  const a1CardContent = allCenters.length === 0
    ? '<p class="alertas-ok">Sin centros</p>'
    : (() => {
        const cards = allCenters.map(center => {
          const centerAlerts = byCenter[center] || [];
          const stageRows = centerAlerts
            .map(a => `<div class="alertas-stage-row"><span>${esc(a.stage)}</span><span class="alertas-red">${a.avg}&thinsp;d <small style="color:var(--muted);font-weight:400">(ref&nbsp;${a.ref})</small></span></div>`)
            .join('');
          // A2 calculado con estadísticas propias del centro
          const centerStats = (weeklyStats || []).filter(s => s.centerName === center);
          const centerRefMap = _computeStageRefMap(centerStats, stageDefs);
          const a2Days = Math.round(pathStages.reduce((sum, s) => sum + (centerRefMap[s.code] ?? s.expectedDays ?? 0), 0));
          const a2Row = `<div class="alertas-a2-value">Tomo → Inicio estimado: <strong>${a2Days}&thinsp;días</strong></div>`;
          return `<div class="alertas-center-card"><div class="alertas-center-name">${esc(center)}</div>${stageRows}${a2Row}</div>`;
        }).join('');
        return `<div class="alertas-center-grid">${cards}</div>`;
      })();

  const a1Html = `<details class="alertas-details"${centrosConAlerta > 0 ? ' open' : ''}>
    <summary class="alertas-summary">Etapas demoradas y tiempo estimado por centro <span class="alertas-badge ${a1BadgeCls}">${centrosConAlerta} con alertas</span></summary>
    <div class="alertas-content">${a1CardContent}</div>
  </details>`;

  const container = document.getElementById('alertasCentros');
  container.innerHTML =
    `<h3 class="alertas-group-title">Centros <span class="alertas-badge ${a1BadgeCls}">${centrosConAlerta}</span></h3>` +
    a1Html;
}

// ── Sección B: Equipos ────────────────────────────────────────────────────────

function _renderAlertasEquipos(agendaItems, machineCapacities, techniqueDurations) {
  const excludedTech = new Set(ALERT_EXCLUDED_TECHNIQUES);
  const allSlots = (agendaItems || []);
  const dateOf = s => typeof s.agendaDate === 'string' ? s.agendaDate : String(s.agendaDate);

  // Para B2 excluir técnicas especiales; para B3 usar todos
  const stdSlots = allSlots.filter(s => !excludedTech.has(
    (s.treatmentLabel || '').split(/[\s\-]/)[0].toUpperCase() === 'TBI' ? 'TBI'
    : (s.treatmentLabel || '').toUpperCase().includes('TSET') ? 'TSET'
    : (s.treatmentLabel || '').toUpperCase().includes('BQT')  ? 'BQT'
    : (s.treatmentLabel || '').toUpperCase().includes('IORT') ? 'IORT'
    : ''
  ));

  // ── B1: Tarjetas de capacidad por equipo ──────────────────────────────────
  // Contar turnos reales por equipo (todos los slots del bootstrap, sin filtro de fecha)
  const slotsByMachine = {};
  for (const s of allSlots) {
    slotsByMachine[s.machineName] = (slotsByMachine[s.machineName] ?? 0) + 1;
  }

  const caps = machineCapacities || [];
  let b1OverloadCount = 0;

  // Agrupar máquinas por centro preservando el orden de configuración
  const b1CenterOrder = [];
  const b1MachByCenter = {};
  for (const cap of caps) {
    const cn = cap.centerName;
    if (!b1MachByCenter[cn]) { b1MachByCenter[cn] = []; b1CenterOrder.push(cn); }
    b1MachByCenter[cn].push(cap);
  }

  const b1CenterCards = b1CenterOrder.map(centerName => {
    const machines = b1MachByCenter[centerName];
    const machineRows = machines.map(cap => {
      const shortName = cap.machineName.replace(/^[^-]+ - /, '');
      const real     = slotsByMachine[cap.machineName] ?? 0;
      const capacity = Math.floor((Number(cap.workingHours) - Number(cap.reservedSpecialHours || 0)) * 60 / cap.standardSlotMinutes);
      const pct = capacity > 0 ? real / capacity : 0;
      const cls = pct > 0.9 ? 'mv-full' : pct > 0.7 ? 'mv-warn' : '';
      if (pct > 0.9) b1OverloadCount++;
      return `<div class="alertas-stage-row">` +
        `<span>${esc(shortName)}</span>` +
        `<span class="alertas-equipo-slots ${cls}">${real}&thinsp;/&thinsp;${capacity}</span>` +
      `</div>`;
    }).join('');
    return `<div class="alertas-center-card">` +
      `<div class="alertas-center-name">${esc(centerName)}</div>` +
      machineRows +
    `</div>`;
  }).join('');

  const b1BadgeCls = b1OverloadCount > 0 ? 'alertas-badge-red' : 'alertas-badge-green';
  const b1Html = `<details class="alertas-details"${b1OverloadCount > 0 ? ' open' : ''}>
    <summary class="alertas-summary">Agenda por equipo <span class="alertas-badge ${b1BadgeCls}">${b1OverloadCount} sobrecargados</span></summary>
    <div class="alertas-content">
      ${caps.length === 0 ? '<p class="alertas-ok">Sin equipos configurados</p>' : `<div class="alertas-center-grid">${b1CenterCards}</div>`}
    </div>
  </details>`;

  // ── B2: Duración incorrecta (tabla, sin cambios) ──────────────────────────
  const b2 = [];
  for (const s of stdSlots) {
    const err = validateSlotDuration(s, techniqueDurations);
    if (err)
      b2.push({ machine: s.machineName, date: dateOf(s), time: s.startTime, patient: s.patientName, label: s.treatmentLabel, actual: err.actualMinutes, valid: err.validMinutes });
  }
  const b2Html = _alertasSectionHtml('b2', 'Duración de turno incorrecta', b2, rows =>
    _alertasTable(['Equipo', 'Fecha', 'Hora', 'Paciente', 'Técnica', 'Duración real', 'Esperado'],
      rows.map(r => `<tr>
        <td>${esc(r.machine)}</td><td>${_fmtDate(r.date)}</td><td>${esc(r.time||'—')}</td>
        <td>${esc(r.patient||'—')}</td><td>${esc(r.label||'—')}</td>
        <td class="alertas-red">${r.actual} min</td>
        <td>${r.valid.join(' ó ')} min</td>
      </tr>`)
    )
  );

  // ── B3: Superposición — tarjetas por equipo ───────────────────────────────
  const byMachDateAll = {};
  for (const s of allSlots) {
    const key = `${s.machineName}||${dateOf(s)}`;
    if (!byMachDateAll[key]) byMachDateAll[key] = [];
    byMachDateAll[key].push(s);
  }

  // Detectar superposiciones y agrupar por equipo
  const b3ByMachine = {};
  for (const [key, slots] of Object.entries(byMachDateAll)) {
    const [machine] = key.split('||');
    const sorted = [...slots].filter(s => s.startTime && s.endTime)
      .sort((a, b) => (parseTime(a.startTime) ?? 0) - (parseTime(b.startTime) ?? 0));
    for (let i = 0; i < sorted.length - 1; i++) {
      const endI   = parseTime(sorted[i].endTime);
      const startJ = parseTime(sorted[i+1].startTime);
      if (endI !== null && startJ !== null && endI > startJ &&
          sorted[i].patientName !== sorted[i+1].patientName) {
        if (!b3ByMachine[machine]) b3ByMachine[machine] = [];
        b3ByMachine[machine].push({
          line1: `${sorted[i].startTime}-${sorted[i].endTime} ${sorted[i].patientName}`,
          line2: `${sorted[i+1].startTime}-${sorted[i+1].endTime} ${sorted[i+1].patientName}`
        });
      }
    }
  }

  const b3Machines = Object.keys(b3ByMachine);
  const b3PairCount = Object.values(b3ByMachine).reduce((sum, arr) => sum + arr.length, 0);
  const b3BadgeCls = b3PairCount > 0 ? 'alertas-badge-red' : 'alertas-badge-green';
  const b3CardContent = b3Machines.length === 0
    ? '<p class="alertas-ok">Sin alertas</p>'
    : `<div class="alertas-equipo-grid">${
        b3Machines.sort().map(machine => {
          const rows = b3ByMachine[machine].map(t =>
            `<div class="alertas-overlap-pair">` +
              `<div class="alertas-overlap-line">${esc(t.line1)}</div>` +
              `<div class="alertas-overlap-line">${esc(t.line2)}</div>` +
            `</div>`
          ).join('');
          return `<div class="alertas-equipo-card alertas-b3-card"><div class="alertas-equipo-name">${esc(machine)}</div>${rows}</div>`;
        }).join('')
      }</div>`;

  const b3Html = `<details class="alertas-details"${b3PairCount > 0 ? ' open' : ''}>
    <summary class="alertas-summary">Turnos superpuestos <span class="alertas-badge ${b3BadgeCls}">${b3PairCount}</span></summary>
    <div class="alertas-content">${b3CardContent}</div>
  </details>`;

  const totalB = b1OverloadCount + b2.length + b3PairCount;
  const container = document.getElementById('alertasEquipos');
  container.innerHTML =
    `<h3 class="alertas-group-title">Equipos <span class="alertas-badge ${totalB > 0 ? 'alertas-badge-red' : 'alertas-badge-green'}">${totalB}</span></h3>` +
    b1Html + b2Html + b3Html;
}

// ── Sección C: Pacientes ──────────────────────────────────────────────────────

function _renderAlertasPacientes(patients, stageDefs, weeklyStats, p1Threshold) {
  const excludedTech = new Set(ALERT_EXCLUDED_TECHNIQUES);
  const stageMap  = Object.fromEntries(stageDefs.map(s => [s.code, s]));
  const F6A_SORT  = stageDefs.find(s => s.code === 'F6A')?.sortOrder ?? 40;
  const F3_SORT   = stageDefs.find(s => s.code === 'F3')?.sortOrder ?? 10;
  const { weeksAvailable } = resolvePlanningTimeSource(weeklyStats || []);

  // Pacientes elegibles para C1/C2/C3 (excluye IsLongWait y técnicas especiales)
  const eligible = patients.filter(p =>
    !p.isLongWait && !excludedTech.has(p.treatmentTechnique)
  );

  // C1: P1 con demora (aplica exclusiones igual que el resto)
  const c1 = eligible.filter(p =>
    p.priority === 1 &&
    (stageMap[p.stageCode]?.sortOrder ?? 0) >= F6A_SORT &&
    p.daysInStage > (p1Threshold ?? 5)
  ).map(p => ({
    center: p.centerName, name: p.patientName, hc: p.patientId,
    stage: stageMap[p.stageCode]?.displayName ?? p.stageCode,
    days: p.daysInStage, threshold: p1Threshold ?? 5
  }));

  // C2: Demora por etapa individual — referencia dinámica
  let c2DominantSource = 'referencia';
  const c2 = eligible.flatMap(p => {
    const result = getReferenceForAlerts(p.stageCode, weeklyStats, eligible);
    const ref    = result?.ref ?? stageMap[p.stageCode]?.expectedDays ?? 0;
    const src    = result ? result.source : 'referencia';
    if (ref <= 0 || p.daysInStage <= ref * 2) return [];
    if (src === 'est. semanal') c2DominantSource = 'est. semanal';
    else if (c2DominantSource === 'referencia' && src === 'promedio actual') c2DominantSource = 'promedio actual';
    return [{ center: p.centerName, name: p.patientName, hc: p.patientId,
      stage: stageMap[p.stageCode]?.displayName ?? p.stageCode,
      days: p.daysInStage, ref: Math.round(ref), src, desv: Math.round(p.daysInStage - ref) }];
  });

  // C3: Demora acumulada desde F3
  let c3DominantSource = 'referencia';
  const c3 = eligible.filter(p =>
    (stageMap[p.stageCode]?.sortOrder ?? 0) >= F3_SORT
  ).flatMap(p => {
    // Acumulado "antes" de la etapa actual: suma de referencias F3..etapa anterior
    const F3_SORT_V = F3_SORT;
    const curSort   = stageMap[p.stageCode]?.sortOrder ?? 0;
    const prevStages = stageDefs.filter(s =>
      (s.sortOrder ?? 0) >= F3_SORT_V && (s.sortOrder ?? 0) < curSort
    );
    let before = 0;
    for (const s of prevStages) {
      const r = getReferenceForAlerts(s.code, weeklyStats, eligible);
      before += r !== null ? r.ref : (s.expectedDays ?? 0);
    }
    const diasDesdeF3 = p.daysInStage + before;

    const { total: refAcum, source: acumSrc } =
      _getAccumReference(p.stageCode, weeklyStats, eligible, stageDefs, stageMap);
    if (refAcum <= 0 || diasDesdeF3 <= refAcum * 2) return [];
    if (acumSrc === 'est. semanal') c3DominantSource = 'est. semanal';
    else if (c3DominantSource === 'referencia' && acumSrc === 'promedio actual') c3DominantSource = 'promedio actual';
    return [{ center: p.centerName, name: p.patientName, hc: p.patientId,
      stage: stageMap[p.stageCode]?.displayName ?? p.stageCode,
      acum: Math.round(diasDesdeF3), refAcum: Math.round(refAcum),
      desv: Math.round(diasDesdeF3 - refAcum) }];
  });

  const totalC = c1.length + c2.length + c3.length;

  const c1Html = c1.length === 0
    ? _alertasSectionHtml('c1', 'Pacientes P1 con demora', [], () => '')
    : `<details class="alertas-details" open>
        <summary class="alertas-summary">Pacientes P1 con demora <span class="alertas-badge alertas-badge-red">${c1.length}</span></summary>
        <div class="alertas-content">
          ${_alertasTable(['Centro', 'Paciente', 'HC', 'Etapa', 'Días en etapa', 'Umbral P1'],
            c1.map(r => `<tr><td>${esc(r.center)}</td><td>${esc(r.name)}</td><td>${esc(fmtHc(r.hc))}</td><td>${esc(r.stage)}</td><td class="alertas-red">${r.days}</td><td>${r.threshold}</td></tr>`)
          )}
        </div>
      </details>`;

  const c2Html = _alertasSectionHtml('c2', 'Demora por etapa individual', c2, rows =>
    _alertasTable(['Centro', 'Paciente', 'HC', 'Etapa', 'Días', 'Referencia', 'Desvío'],
      rows.map(r => `<tr>
        <td>${esc(r.center)}</td><td>${esc(r.name)}</td><td>${esc(fmtHc(r.hc))}</td>
        <td>${esc(r.stage)}</td><td>${r.days}</td>
        <td>${r.ref} días <span class="alertas-src">(${r.src})</span></td>
        <td class="alertas-red">+${r.desv}</td>
      </tr>`)
    ) + _alertasSourceLegend(c2DominantSource, weeksAvailable)
  );

  const f3DisplayName = stageMap['F3']?.displayName ?? 'F3';
  const c3Html = _alertasSectionHtml('c3', `Demora acumulada (desde ${f3DisplayName})`, c3, rows =>
    _alertasTable(['Centro', 'Paciente', 'HC', 'Etapa actual', 'Días acumulados', 'Referencia acum.', 'Desvío'],
      rows.map(r => `<tr>
        <td>${esc(r.center)}</td><td>${esc(r.name)}</td><td>${esc(fmtHc(r.hc))}</td>
        <td>${esc(r.stage)}</td><td>${r.acum}</td><td>${r.refAcum}</td>
        <td class="alertas-red">+${r.desv}</td>
      </tr>`)
    ) + _alertasSourceLegend(c3DominantSource, weeksAvailable)
  );

  const container = document.getElementById('alertasPacientes');
  container.innerHTML = `<h3 class="alertas-group-title">Pacientes <span class="alertas-badge ${totalC > 0 ? 'alertas-badge-red' : 'alertas-badge-green'}">${totalC}</span></h3>${c1Html}${c2Html}${c3Html}`;
}

// ── Sección C4: Eventos recientes de proceso ──────────────────────────────────

function _renderAlertasEventos(events) {
  const container = document.getElementById('alertasEventos');
  if (!container) return;

  const fmt = dt => new Date(dt).toLocaleDateString('es-AR', { day: '2-digit', month: '2-digit', year: 'numeric' });

  const byType = t => (events || []).filter(e => e.eventType === t);
  const tecnica   = byType('TechniqueChanged');
  const retroceso = byType('StageRegressed');
  const total = tecnica.length + retroceso.length;

  const tecnicaHtml = _alertasSectionHtml('c4a', 'Cambios de técnica', tecnica, rows =>
    _alertasTable(
      ['Fecha', 'Centro', 'Paciente', 'HC', 'Técnica anterior', 'Técnica nueva', 'Etapa al cambiar'],
      rows.map(r => {
        const stage = (r.notes || '').replace('Etapa al momento del cambio: ', '');
        return `<tr>
          <td>${fmt(r.detectedAtUtc)}</td>
          <td>${esc(r.centerName)}</td><td>${esc(r.patientName)}</td><td>${esc(fmtHc(r.patientId))}</td>
          <td>${esc(r.previousValue ?? '—')}</td>
          <td><strong>${esc(r.newValue ?? '—')}</strong></td>
          <td>${esc(stage)}</td>
        </tr>`;
      })
    )
  );

  const retrocesoHtml = _alertasSectionHtml('c4b', 'Retrocesos de etapa', retroceso, rows =>
    _alertasTable(
      ['Fecha', 'Centro', 'Paciente', 'HC', 'Etapa anterior', 'Etapa nueva', 'Días en etapa anterior'],
      rows.map(r => {
        const diasStr = (r.notes || '').replace('Días en etapa anterior: ', '');
        return `<tr>
          <td>${fmt(r.detectedAtUtc)}</td>
          <td>${esc(r.centerName)}</td><td>${esc(r.patientName)}</td><td>${esc(fmtHc(r.patientId))}</td>
          <td>${esc(r.previousValue ?? '—')}</td>
          <td class="alertas-red"><strong>${esc(r.newValue ?? '—')}</strong></td>
          <td>${esc(diasStr)}</td>
        </tr>`;
      })
    )
  );

  container.innerHTML =
    `<h3 class="alertas-group-title">Eventos recientes de proceso` +
    ` <span class="alertas-badge ${total > 0 ? 'alertas-badge-red' : 'alertas-badge-green'}">${total}</span>` +
    ` <span class="alertas-legend" style="font-size:11px;font-weight:400">(últimos 30 días)</span></h3>` +
    tecnicaHtml + retrocesoHtml;
}

// ── Cargador principal de Alertas ─────────────────────────────────────────────

async function loadAlertasTab() {
  if (!state.homeData) {
    ['alertasCentros','alertasEquipos','alertasPacientes','alertasEventos'].forEach(id => {
      const el = document.getElementById(id);
      if (el) el.innerHTML = '<p class="alertas-info">Sin datos. Actualice primero.</p>';
    });
    return;
  }

  const statusEl = document.getElementById('alertasStatus');
  statusEl.textContent = 'Calculando...';

  // Cargar config y weekly stats si no están
  if (!state.configData) await loadConfigData();
  if (!state.alertas.weeklyStats) {
    try {
      const resp = await fetch('/api/stats/weekly');
      state.alertas.weeklyStats = resp.ok ? await resp.json() : [];
    } catch { state.alertas.weeklyStats = []; }
  }

  // Cargar eventos de proceso
  let processEvents = [];
  try {
    const resp = await fetch('/api/patient-events?days=30');
    processEvents = resp.ok ? await resp.json() : [];
  } catch { processEvents = []; }

  const patients  = state.homeData.patients ?? [];
  const stageDefs = state.homeData.stages ?? state.configData?.stages ?? [];
  const agendaItems = state.homeData.agenda ?? [];
  const machCaps  = state.homeData.configuration?.machineCapacities ?? [];
  const techDurs  = state.configData?.techniqueDurations ?? [];
  const p1Thresh  = state.configData?.p1AlertThresholdDays ?? 5;
  const stageRefMap = _computeStageRefMap(state.alertas.weeklyStats, stageDefs);

  _renderAlertasCentros(patients, stageDefs, stageRefMap, state.alertas.weeklyStats);
  _renderAlertasEquipos(agendaItems, machCaps, techDurs);
  _renderAlertasPacientes(patients, stageDefs, state.alertas.weeklyStats, p1Thresh);
  _renderAlertasEventos(processEvents);

  state.alertas.loaded = true;
  statusEl.textContent = `Actualizado ${new Date().toLocaleTimeString()}`;
}

// ── Técnicas Especiales tab ───────────────────────────────────────────────────

const ESPECIALES_TECHNIQUES = ['SBRT', 'RC'];

const ESPECIALES_STAGES = ['F4B', 'F5', 'F6A', 'F6B', 'F6C', 'F7A', 'F7C', 'F11'];

const _STAGE_ORDER = [
  'F1','F2A','F2B','F3','F4','F4B','F5',
  'F6A','F6B','F6C','F6D','F6E','F6F','F6G',
  'F7A','F7B','F7C','F8','F9','F10','F11'
];
const _ESPECIALES_MIN_IDX = _STAGE_ORDER.indexOf('F4B');

function _especialesPatients() {
  return (state.homeData?.patients ?? []).filter(p =>
    (p.treatmentTechnique === 'SBRT' || p.treatmentTechnique === 'RC') &&
    _STAGE_ORDER.indexOf((p.stageCode ?? '').toUpperCase()) >= _ESPECIALES_MIN_IDX
  );
}

function buildEspecialesFilters(data) {
  const techRow = document.getElementById('especiales-tech-pills');
  if (!techRow) return;
  techRow.innerHTML = '';
  techRow.appendChild(makePill('Todas', state.especiales.techniqueFilter === null, () => {
    state.especiales.techniqueFilter = null; renderEspeciales();
  }));
  ESPECIALES_TECHNIQUES.forEach(t => techRow.appendChild(
    makePill(t, state.especiales.techniqueFilter === t, () => {
      state.especiales.techniqueFilter = t; renderEspeciales();
    })
  ));

  const stageRow = document.getElementById('especiales-stage-pills');
  stageRow.innerHTML = '';
  stageRow.appendChild(makePill('Todas', state.especiales.stageFilter === null, () => {
    state.especiales.stageFilter = null; renderEspeciales();
  }));
  const stageDefs = (data.stages ?? []).filter(s => ESPECIALES_STAGES.includes(s.code));
  stageDefs.forEach(s => stageRow.appendChild(
    makePill(s.displayName, state.especiales.stageFilter === s.code, () => {
      state.especiales.stageFilter = s.code; renderEspeciales();
    })
  ));
}

function _especialesDaysClass(days, expected, isLongWait) {
  if (isLongWait) return 'spec-days-gray';
  if (days <= expected) return 'spec-days-ok';
  if (days <= expected * 2) return 'spec-days-warn';
  return 'spec-days-crit';
}

function renderEspeciales() {
  const wrap = document.getElementById('especiales-table-wrap');
  if (!wrap) return;

  if (state.homeData) buildEspecialesFilters(state.homeData);

  let patients = _especialesPatients();
  if (state.especiales.techniqueFilter)
    patients = patients.filter(p => p.treatmentTechnique === state.especiales.techniqueFilter);
  if (state.especiales.stageFilter)
    patients = patients.filter(p => p.stageCode === state.especiales.stageFilter);

  // Sort
  const { col, dir } = state.especiales.sort;
  const mult = dir === 'asc' ? 1 : -1;
  patients = patients.slice().sort((a, b) => {
    if (col === 'hc') return mult * (a.patientId ?? '').localeCompare(b.patientId ?? '');
    if (col === 'name') return mult * (a.patientName ?? '').localeCompare(b.patientName ?? '');
    if (col === 'technique') return mult * (a.treatmentTechnique ?? '').localeCompare(b.treatmentTechnique ?? '');
    if (col === 'tomo') {
      const ta = a.tomographyDate ?? '', tb = b.tomographyDate ?? '';
      return mult * (ta < tb ? -1 : ta > tb ? 1 : 0);
    }
    if (col === 'stage') return mult * ((a.stageCode ?? '').localeCompare(b.stageCode ?? ''));
    if (col === 'days') return mult * (a.daysInStage - b.daysInStage);
    if (col === 'doctor') return mult * (a.responsibleDoctor ?? '').localeCompare(b.responsibleDoctor ?? '');
    if (col === 'physicist') return mult * (a.assignedPhysicist ?? '').localeCompare(b.assignedPhysicist ?? '');
    // Default: priority ASC nulls-last → stageCode sortOrder → daysInStage DESC
    const pa = a.priority ?? 999, pb = b.priority ?? 999;
    if (pa !== pb) return pa - pb;
    const stageDefs = state.homeData?.stages ?? [];
    const sa = stageDefs.find(s => s.code === a.stageCode)?.sortOrder ?? 999;
    const sb = stageDefs.find(s => s.code === b.stageCode)?.sortOrder ?? 999;
    if (sa !== sb) return sa - sb;
    return b.daysInStage - a.daysInStage;
  });

  function thSort(label, key) {
    const active = col === key;
    const arrow = active ? (dir === 'asc' ? ' ▲' : ' ▼') : ' ⇅';
    return `<th class="spec-th-sort${active ? ' active' : ''}" data-col="${key}">${label}${arrow}</th>`;
  }

  const rows = patients.map(p => {
    const daysClass = _especialesDaysClass(p.daysInStage, p.expectedDaysInStage, p.isLongWait);
    const tomoStr = p.tomographyDate
      ? p.tomographyDate.replace(/^(\d{4})-(\d{2})-(\d{2})$/, '$3/$2/$1')
      : '<span class="muted-italic">—</span>';
    const doctorStr = esc(p.responsibleDoctor ?? '—');
    const physicistStr = esc(p.assignedPhysicist ?? '—');
    const techCssKey = p.treatmentTechnique ?? '';
    const techBadge = techCssKey
      ? `<span class="treatment-badge tt-${techCssKey}">${techCssKey}</span>`
      : '—';
    const nameHtml = priorityBadge(p.priority) +
      (p.sitraMedGuid
        ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${p.sitraMedGuid}/overview" target="_blank" rel="noreferrer">${esc(p.patientName)}</a>`
        : esc(p.patientName));
    const resv = window.activeReservations.get(p.patientId);
    let resvCell = '<span class="muted-italic">—</span>';
    if (resv) {
      const [, rm, rd] = resv.reservedDate.split('-');
      resvCell = `<span class="reservation-badge">${rd}/${rm} ${resv.reservedTime}</span>`;
    }
    const trClass = resv ? ' class="has-reservation"' : '';
    return `<tr${trClass}>
      <td>${esc(fmtHc(p.patientId))}</td>
      <td>${nameHtml}</td>
      <td>${techBadge}</td>
      <td>${tomoStr}</td>
      <td>${esc(p.stageDisplayName ?? p.stageCode)}</td>
      <td class="${daysClass}">${p.daysInStage}</td>
      <td>${doctorStr}</td>
      <td>${physicistStr}</td>
      <td>${resvCell}</td>
    </tr>`;
  }).join('');

  const html = `<table class="spec-table">
    <thead><tr>
      ${thSort('HC', 'hc')}
      ${thSort('Nombre', 'name')}
      ${thSort('Técnica', 'technique')}
      ${thSort('Fecha Tomo', 'tomo')}
      ${thSort('Etapa actual', 'stage')}
      ${thSort('Días en etapa', 'days')}
      ${thSort('Médico', 'doctor')}
      ${thSort('Físico asignado', 'physicist')}
      <th>Turno reservado</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="9" class="muted-italic" style="text-align:center;padding:1rem">Sin pacientes</td></tr>'}</tbody>
  </table>`;
  wrap.innerHTML = html;

  wrap.querySelectorAll('.spec-th-sort').forEach(th => {
    th.addEventListener('click', () => {
      const key = th.dataset.col;
      if (state.especiales.sort.col === key) {
        state.especiales.sort.dir = state.especiales.sort.dir === 'asc' ? 'desc' : 'asc';
      } else {
        state.especiales.sort.col = key;
        state.especiales.sort.dir = 'asc';
      }
      renderEspeciales();
    });
  });
}

// ── Pacientes tab ─────────────────────────────────────────────────────────────

async function loadPacientesTab() {
  const inp = document.getElementById('pacientesSearch');
  if (inp && !inp._wired) {
    inp._wired = true;
    inp.addEventListener('input', e => {
      state.pacientes.query = e.target.value.trim().toLowerCase();
      _renderPacientesResults();
    });
  }

  if (!state.configData) await loadConfigData();

  if (!state.alertas.weeklyStats) {
    try {
      const resp = await fetch('/api/stats/weekly');
      state.alertas.weeklyStats = resp.ok ? await resp.json() : [];
    } catch { state.alertas.weeklyStats = []; }
  }
  if (!state.fisica.recoWeeklyStats) state.fisica.recoWeeklyStats = state.alertas.weeklyStats;

  if (state.pacientes.events === null) {
    try {
      const resp = await fetch('/api/patient-events?days=90');
      state.pacientes.events = resp.ok ? await resp.json() : [];
    } catch { state.pacientes.events = []; }
  }

  _renderPacientesResults();
}

function _searchPacientes(query) {
  if (!query || !state.homeData) return [];
  const q = query.toLowerCase();

  const followupMatches = (state.homeData.patients ?? []).filter(p =>
    p.patientName?.toLowerCase().includes(q) || p.patientId?.toLowerCase().includes(q)
  );

  const agendaByKey = new Map();
  for (const slot of (state.homeData.agenda ?? [])) {
    const name = slot.patientName;
    if (!name || name === '~' || name === '-') continue;
    if (!name.toLowerCase().includes(q)) continue;
    const key = slot.sitraMedGuid || name.toLowerCase();
    if (!agendaByKey.has(key)) agendaByKey.set(key, []);
    agendaByKey.get(key).push(slot);
  }

  const results = [];
  const processedGuids = new Set();

  for (const p of followupMatches) {
    const agendaKey = p.sitraMedGuid ?? p.patientName?.toLowerCase();
    const slots = agendaKey ? (agendaByKey.get(agendaKey) ?? []) : [];
    if (agendaKey) agendaByKey.delete(agendaKey);
    if (p.sitraMedGuid) processedGuids.add(p.sitraMedGuid);
    results.push({ mode: slots.length > 0 ? 'both' : 'followup', followup: p, agendaSlots: slots });
  }

  for (const [, slots] of agendaByKey) {
    const slot0 = slots[0];
    if (slot0.sitraMedGuid && processedGuids.has(slot0.sitraMedGuid)) continue;
    results.push({ mode: 'agenda', followup: null, agendaSlots: slots });
  }

  return results;
}

function _renderPacientesResults() {
  const panel = document.getElementById('pacientesResults');
  if (!panel) return;
  const q = state.pacientes.query;
  if (!q) { panel.innerHTML = ''; state.pacientes.selected = null; return; }
  if (!state.homeData) {
    panel.innerHTML = '<p class="detail-placeholder">Sin datos. Actualice primero.</p>';
    return;
  }

  const results = _searchPacientes(q);
  if (results.length === 0) {
    panel.innerHTML = `<p class="detail-placeholder">Sin resultados para "${esc(q)}".</p>`;
    return;
  }

  // Auto-select first result if nothing selected (or selected is no longer in results)
  const selId = state.pacientes.selected?.followup?.patientId;
  const stillValid = selId && results.some(r => r.followup?.patientId === selId);
  if (!stillValid) state.pacientes.selected = results[0];

  panel.innerHTML = '';

  const layout = document.createElement('div');
  layout.className = 'pacientes-detail-layout';

  // ── Columna izquierda: lista compacta ─────────────────────────────────────
  const listCol = document.createElement('div');
  listCol.className = 'pacientes-list-col';
  listCol.appendChild(el('p', 'pacientes-count',
    `${results.length} resultado${results.length !== 1 ? 's' : ''}`));

  const stageDefs = state.homeData.stages ?? [];
  results.forEach(r => {
    const p = r.followup;
    const slot0 = r.agendaSlots?.[0];
    const patientId = p?.patientId ?? slot0?.patientId;
    const name = p?.patientName ?? slot0?.patientName ?? '?';
    const centerName = p?.centerName ?? slot0?.centerName ?? '';
    const stageCode = p?.stageCode ?? '';
    const stageLabel = stageDefs.find(s => s.code === stageCode)?.displayName ?? stageCode;
    const resv = window.activeReservations.get(patientId);

    const isSelected = patientId && patientId === state.pacientes.selected?.followup?.patientId;
    const item = document.createElement('div');
    item.className = `pacientes-list-item${isSelected ? ' selected' : ''}`;
    item.innerHTML =
      `<div class="pli-name">${priorityBadge(p?.priority ?? slot0?.priority)}${esc(name)}${hcTag(patientId)}</div>` +
      ((centerName || stageLabel)
        ? `<div class="pli-context">${[centerName, stageLabel].filter(Boolean).map(esc).join(' · ')}</div>`
        : '') +
      (resv ? `<div class="pli-resv">${_reservationBadge(patientId)}</div>` : '');
    item.addEventListener('click', () => {
      state.pacientes.selected = r;
      _renderPacientesResults();
    });
    listCol.appendChild(item);
  });

  // ── Columna central: ficha expandida del paciente seleccionado ────────────
  const centerCol = document.createElement('div');
  centerCol.className = 'pacientes-center-col';
  if (state.pacientes.selected) {
    centerCol.appendChild(_renderPacienteCard(state.pacientes.selected));
  }

  // ── Columna derecha: panel de acción ──────────────────────────────────────
  const detailCol = document.createElement('div');
  detailCol.className = 'pacientes-detail-col';
  detailCol.id = 'pacientes-detail-col';
  _renderPatientActionPanel(state.pacientes.selected, detailCol);

  layout.appendChild(listCol);
  layout.appendChild(centerCol);
  layout.appendChild(detailCol);
  panel.appendChild(layout);
}

function _renderPacienteCard(result) {
  const { mode, followup: p, agendaSlots } = result;
  const card = document.createElement('article');
  card.className = 'paciente-card';
  const stageDefs = state.homeData.stages ?? [];
  const stageMap = Object.fromEntries(stageDefs.map(s => [s.code, s]));
  const events = state.pacientes.events ?? [];

  if (mode === 'followup' || mode === 'both') {
    const dc = delayClass(p.daysInStage, p.expectedDaysInStage, p.isLongWait);
    const stageDef = stageMap[p.stageCode];

    const nameHtml = priorityBadge(p.priority) + (p.sitraMedGuid
      ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${p.sitraMedGuid}/overview" target="_blank" rel="noopener noreferrer"><strong>${esc(p.patientName)}</strong></a>`
      : `<strong>${esc(p.patientName)}</strong>`);

    // Línea 1: header
    let html =
      `<div class="paciente-card-header">${nameHtml}${hcTag(p.patientId)}${_reservationBadge(p.patientId)}<span class="patient-context">${esc(p.centerName)}</span></div>`;

    // Línea 2: etapa + badge (simplificado, sin técnica ni físico)
    html +=
      `<div class="paciente-card-stage">` +
        `<span class="paciente-stage-label">${esc(stageDef?.displayName ?? p.stageCode)}</span>` +
        daysBadge(p, dc) +
      `</div>`;

    // Línea 3: contexto clínico — técnica + haz + equipo planificado + físico (si hay algo)
    const ctxHtml = renderTreatmentLabel(p) + ariaBadges(p) +
      (p.assignedPhysicist ? `<span class="physicist-tag">Físico: ${esc(p.assignedPhysicist)}</span>` : '');
    if (ctxHtml) html += `<div class="paciente-card-stage">${ctxHtml}</div>`;

    // Línea 4: tiempo transcurrido desde ingreso (C3)
    const c3 = _computePatientAccumDelay(p, stageDefs, stageMap);
    if (c3) {
      const demoraTxt = c3.desvio > 0
        ? ` Lleva <span class="alertas-red">${c3.desvio}d de demora</span>.`
        : '';
      html += `<div class="paciente-card-detail">Tiempo transcurrido desde ingreso: <strong>${c3.diasDesdeF3}d</strong> (${Math.round(c3.refAcum)}d según referencia).${demoraTxt}</div>`;
    }

    if (mode === 'both' && agendaSlots.length > 0) {
      // Línea 5 (both): slots reales agendados — sin estimados ni disponibilidad
      const dates = [...new Set(agendaSlots.map(s => s.agendaDate).filter(Boolean))].sort();
      html += `<div class="paciente-card-agenda">En tratamiento · ${esc(agendaSlots[0].machineName ?? '')}:` +
        dates.map(d => `<span class="paciente-slot-date">${_fmtDate(d)}</span>`).join('') +
        `</div>`;
    } else {
      // Línea 5 (followup): inicio estimado (placeholder async)
      html += `<div class="paciente-card-detail paciente-est">Inicio estimado: <em>calculando...</em></div>`;
      // Línea 6 (followup): disponibilidad en el equipo planificado (siempre se muestra si hay equipo)
      const avail = _pacienteFirstAvailableSlot(p);
      if (avail) {
        if (avail.noMachine) {
          html += `<div class="paciente-card-detail muted-italic">Sin equipo asignado en ARIA</div>`;
        } else if (avail.date) {
          html += `<div class="paciente-card-detail">Primera disponibilidad en ${esc(avail.machineName)}: <strong>${_fmtDate(avail.date)}</strong></div>`;
        } else {
          html += `<div class="paciente-card-detail muted-italic">${esc(avail.machineName)}: sin disponibilidad en agenda actual</div>`;
        }
      }
    }

    const patEvents = events.filter(e =>
      e.patientId === p.patientId ||
      e.patientName?.toLowerCase() === p.patientName?.toLowerCase()
    );
    html += _pacienteEventsHtml(patEvents);

    card.innerHTML = html;

    if (mode === 'followup') {
      _fisicaEstimatedDate(p.stageCode).then(est => {
        const estEl = card.querySelector('.paciente-est');
        if (!estEl) return;
        if (est.dateStr) {
          estEl.innerHTML = `Inicio estimado: <strong>${_fmtDate(est.dateStr)}</strong> <span class="muted-italic">(~${Math.round(est.totalDays)}d hábiles)</span>`;
        } else {
          estEl.innerHTML = 'Inicio estimado: <em class="muted-italic">sin datos suficientes</em>';
        }
      });
    }

  } else {
    const slot0 = agendaSlots[0];
    const nameHtml = slot0.sitraMedGuid
      ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${slot0.sitraMedGuid}/overview" target="_blank" rel="noopener noreferrer"><strong>${esc(slot0.patientName)}</strong></a>`
      : `<strong>${esc(slot0.patientName)}</strong>`;

    const dates = [...new Set(agendaSlots.map(s => s.agendaDate).filter(Boolean))].sort();

    let html =
      `<div class="paciente-card-header">${priorityBadge(slot0.priority)}${nameHtml}<span class="paciente-agenda-badge">En tratamiento</span></div>` +
      `<div class="paciente-card-stage">${esc(slot0.machineName ?? '')}${renderTreatmentLabel(slot0)}</div>` +
      `<div class="paciente-card-agenda">Turnos:` +
        dates.map(d => `<span class="paciente-slot-date">${_fmtDate(d)}</span>`).join('') +
      `</div>`;

    const patEvents = events.filter(e =>
      e.patientName?.toLowerCase() === slot0.patientName?.toLowerCase()
    );
    html += _pacienteEventsHtml(patEvents);

    card.innerHTML = html;
  }

  return card;
}

function _renderPatientActionPanel(result, container) {
  if (!container) container = document.getElementById('pacientes-detail-col');
  if (!container) return;
  container.innerHTML = '';

  if (!result) {
    container.innerHTML = '<p class="detail-placeholder">Seleccione un paciente.</p>';
    return;
  }

  const p = result.followup;
  if (!p) {
    container.innerHTML = '<p class="detail-placeholder">Acciones no disponibles para pacientes solo en agenda.</p>';
    return;
  }

  const reservation = window.activeReservations.get(p.patientId);
  const panel = document.createElement('div');
  panel.className = 'pacientes-action-panel';

  const infoDiv = document.createElement('div');
  infoDiv.className = 'pacientes-action-patient';
  infoDiv.innerHTML =
    `<strong>${esc(p.patientName)}</strong>${hcTag(p.patientId)}<br>` +
    `<span class="patient-context">${esc(p.centerName)}</span>` +
    (p.stageCode ? ` · <span class="muted-italic">${esc(p.stageCode)}</span>` : '');
  panel.appendChild(infoDiv);

  if (!reservation) {
    const btn = document.createElement('button');
    btn.className = 'tab-button active reserve-btn';
    btn.textContent = 'Reservar Turno';
    btn.addEventListener('click', () => _openReservationModal(p, null));
    panel.appendChild(btn);
  } else {
    const [ry, rm, rd] = reservation.reservedDate.split('-');
    const card = document.createElement('div');
    card.className = 'reservation-info-card';
    card.innerHTML =
      `<div class="res-info-header"><span class="reservation-badge">Turno reservado</span></div>` +
      `<div class="res-info-row"><span class="res-info-label">Fecha:</span> ${rd}/${rm}/${ry}</div>` +
      `<div class="res-info-row"><span class="res-info-label">Hora:</span> ${esc(reservation.reservedTime)}</div>` +
      `<div class="res-info-row"><span class="res-info-label">Equipo:</span> ${esc(reservation.machineDisplayName)}</div>` +
      (reservation.observations ? `<div class="res-info-row"><span class="res-info-label">Obs:</span> ${esc(reservation.observations)}</div>` : '') +
      `<div class="res-info-row muted-italic">Registrado por ${esc(reservation.registeredByUsername)} el ${_fmtDateTime(reservation.registeredAtUtc)}</div>`;
    panel.appendChild(card);

    const btnRow = document.createElement('div');
    btnRow.className = 'res-action-buttons';

    const editBtn = document.createElement('button');
    editBtn.className = 'tab-button active reserve-btn';
    editBtn.textContent = 'Editar reserva';
    editBtn.addEventListener('click', () => _openReservationModal(p, reservation));

    const delBtn = document.createElement('button');
    delBtn.className = 'ghost-button reserve-btn-delete';
    delBtn.textContent = 'Eliminar reserva';
    delBtn.addEventListener('click', () => _deleteReservation(reservation.reservationId, p));

    btnRow.appendChild(editBtn);
    btnRow.appendChild(delBtn);
    panel.appendChild(btnRow);
  }

  container.appendChild(panel);
}

function _deleteReservation(reservationId, patient) {
  const reservation = window.activeReservations.get(patient.patientId);
  if (!reservation) return;

  const [ry, rm, rd] = reservation.reservedDate.split('-');
  const fechaStr = `${rd}/${rm}/${ry} ${reservation.reservedTime ?? ''}`.trim();

  // Modal dinámico de confirmación con credenciales
  const overlay = document.createElement('div');
  overlay.className = 'auth-overlay';
  overlay.innerHTML = `
    <div class="auth-modal del-confirm-modal">
      <h3 class="auth-modal-title">Eliminar reserva</h3>
      <p class="del-confirm-question">¿Confirma eliminar la reserva de turno?</p>
      <div class="del-confirm-info">
        <div class="del-info-row"><span class="del-info-label">Paciente:</span> ${esc(reservation.patientName)}</div>
        <div class="del-info-row"><span class="del-info-label">Fecha:</span> ${esc(fechaStr)}</div>
        <div class="del-info-row"><span class="del-info-label">Equipo:</span> ${esc(reservation.machineDisplayName)}</div>
      </div>
      <p class="del-confirm-warn">Esta acción no se puede deshacer.</p>
      <div class="auth-field">
        <label class="auth-label">Usuario</label>
        <input id="del-username" type="text" class="auth-input" autocomplete="off">
      </div>
      <div class="auth-field">
        <label class="auth-label">Contraseña</label>
        <input id="del-password" type="password" class="auth-input">
      </div>
      <div id="del-error" class="auth-error" hidden></div>
      <div class="auth-buttons">
        <button id="del-cancel" type="button" class="ghost-button">Cancelar</button>
        <button id="del-confirm-btn" type="button" class="tab-button active del-confirm-btn" disabled>Confirmar eliminación</button>
      </div>
    </div>`;
  document.body.appendChild(overlay);

  const usernameIn = overlay.querySelector('#del-username');
  const passwordIn = overlay.querySelector('#del-password');
  const errorDiv  = overlay.querySelector('#del-error');
  const confirmBtn = overlay.querySelector('#del-confirm-btn');
  const cancelBtn  = overlay.querySelector('#del-cancel');
  usernameIn.focus();

  function checkReady() {
    confirmBtn.disabled = !(usernameIn.value.trim() && passwordIn.value);
  }
  usernameIn.addEventListener('input', checkReady);
  passwordIn.addEventListener('input', checkReady);

  function close() { document.body.removeChild(overlay); }

  cancelBtn.addEventListener('click', close);
  overlay.addEventListener('keydown', e => { if (e.key === 'Escape') close(); });

  confirmBtn.addEventListener('click', async () => {
    confirmBtn.disabled = true;
    errorDiv.hidden = true;
    try {
      const resp = await fetch(`/api/reservations/${encodeURIComponent(reservationId)}`, {
        method: 'DELETE',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username: usernameIn.value.trim(), password: passwordIn.value })
      });
      if (resp.status === 401) {
        errorDiv.textContent = 'Contraseña incorrecta.';
        errorDiv.hidden = false;
        passwordIn.value = '';
        confirmBtn.disabled = true;
        usernameIn.focus();
        return;
      }
      if (resp.status === 429) {
        errorDiv.textContent = 'Demasiados intentos. Espere unos minutos.';
        errorDiv.hidden = false;
        confirmBtn.disabled = false;
        return;
      }
      if (!resp.ok) {
        errorDiv.textContent = `Error al eliminar (${resp.status}).`;
        errorDiv.hidden = false;
        confirmBtn.disabled = false;
        return;
      }
      window.activeReservations.delete(patient.patientId);
      state.pacientes.selected = null;
      close();
      _renderPacientesResults();
    } catch {
      errorDiv.textContent = 'Error de red.';
      errorDiv.hidden = false;
      confirmBtn.disabled = false;
    }
  });
}

function _openReservationModal(patient, existing) {
  const overlay = document.getElementById('reservation-modal-overlay');
  const titleEl = document.getElementById('res-modal-title');
  const patInfoEl = document.getElementById('res-modal-patient-info');
  const dateIn = document.getElementById('res-date');
  const timeIn = document.getElementById('res-time');
  const machineIn = document.getElementById('res-machine');
  const warnEl = document.getElementById('res-machine-warn');
  const capEl = document.getElementById('res-capacity');
  const obsIn = document.getElementById('res-observations');
  const confirmedIn = document.getElementById('res-confirmed');
  const usernameIn = document.getElementById('res-username');
  const passwordIn = document.getElementById('res-password');
  const errorDiv = document.getElementById('res-error');
  const cancelBtn = document.getElementById('res-cancel-btn');
  const submitBtn = document.getElementById('res-submit-btn');
  if (!overlay) return;

  titleEl.textContent = existing ? 'Editar reserva de turno' : 'Reservar Turno';
  patInfoEl.textContent = `${patient.patientName} · ${patient.centerName ?? ''}`;

  // Populate machines — filtrar por centro del paciente
  machineIn.innerHTML = '<option value="">— Seleccionar equipo —</option>';
  const allMachines = state.homeData?.configuration?.machines ?? [];
  let machines = allMachines;
  if (patient.centerName) {
    machines = allMachines.filter(m => m.centerName === patient.centerName);
    if (machines.length === 0) {
      console.warn(`_openReservationModal: sin equipos para centro "${patient.centerName}", mostrando todos`);
      machines = allMachines;
    }
  } else {
    console.warn('_openReservationModal: paciente sin centerName, mostrando todos los equipos');
  }
  machines.forEach(m => {
    const opt = document.createElement('option');
    opt.value = m.displayName;
    opt.textContent = m.displayName;
    machineIn.appendChild(opt);
  });

  // Pre-fill
  if (existing) {
    dateIn.value = existing.reservedDate;
    timeIn.value = existing.reservedTime;
    machineIn.value = existing.machineDisplayName;
    obsIn.value = existing.observations ?? '';
    confirmedIn.checked = false;
  } else {
    const tomorrow = todayStr().replace(/(\d{4})-(\d{2})-(\d{2})/, (_, y, m, d) => {
      const dt = new Date(+y, +m - 1, +d + 1);
      return `${dt.getFullYear()}-${pad(dt.getMonth()+1)}-${pad(dt.getDate())}`;
    });
    dateIn.value = tomorrow;
    timeIn.value = '';
    machineIn.value = patient.plannedMachineDisplayName ?? '';
    obsIn.value = '';
    confirmedIn.checked = false;
  }
  usernameIn.value = '';
  passwordIn.value = '';
  errorDiv.hidden = true;
  warnEl.hidden = true;
  capEl.hidden = true;
  submitBtn.disabled = true;
  overlay.hidden = false;
  dateIn.focus();

  // Planned machine mismatch warning
  function checkMachineWarn() {
    const planned = patient.plannedMachineDisplayName;
    const selected = machineIn.value;
    if (planned && selected && selected !== planned) {
      warnEl.textContent = `El equipo planificado en ARIA es: ${planned}`;
      warnEl.hidden = false;
    } else {
      warnEl.hidden = true;
    }
  }

  // Capacity check
  async function checkCapacity() {
    const date = dateIn.value;
    const machine = machineIn.value;
    if (!date || !machine) { capEl.hidden = true; return; }
    try {
      const resp = await fetch(`/api/machine-capacity?date=${encodeURIComponent(date)}&machine=${encodeURIComponent(machine)}`);
      if (!resp.ok) { capEl.hidden = true; return; }
      const { realSlots, capacity, overload } = await resp.json();
      const pct = capacity > 0 ? Math.round(realSlots / capacity * 100) : 0;
      const cls = overload ? 'res-cap-full' : pct > 80 ? 'res-cap-warn' : 'res-cap-ok';
      capEl.className = `res-capacity ${cls}`;
      capEl.textContent = `Turnos en agenda: ${realSlots} / ${capacity} (${pct}%)${overload ? ' — LLENO' : ''}`;
      capEl.hidden = false;
    } catch { capEl.hidden = true; }
  }

  function checkReady() {
    submitBtn.disabled = !(dateIn.value && machineIn.value && usernameIn.value && passwordIn.value);
  }

  machineIn.addEventListener('change', () => { checkMachineWarn(); checkCapacity(); checkReady(); });
  dateIn.addEventListener('change', () => { checkCapacity(); checkReady(); });
  timeIn.addEventListener('input', checkReady);
  usernameIn.addEventListener('input', checkReady);
  passwordIn.addEventListener('input', checkReady);

  checkMachineWarn();
  checkCapacity();

  async function onSubmit() {
    submitBtn.disabled = true;
    errorDiv.hidden = true;
    try {
      const body = {
        patientId: patient.patientId,
        patientName: patient.patientName,
        centerName: patient.centerName ?? null,
        machineDisplayName: machineIn.value,
        reservedDate: dateIn.value,
        reservedTime: timeIn.value || '00:00',
        observations: obsIn.value.trim() || null,
        username: usernameIn.value.trim(),
        password: passwordIn.value
      };
      const resp = await fetch('/api/reservations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
      });
      if (resp.status === 401) {
        errorDiv.textContent = 'Contraseña incorrecta.';
        errorDiv.hidden = false;
        submitBtn.disabled = false;
        return;
      }
      if (resp.status === 429) {
        errorDiv.textContent = 'Demasiados intentos. Espere unos minutos.';
        errorDiv.hidden = false;
        submitBtn.disabled = false;
        return;
      }
      if (!resp.ok) {
        errorDiv.textContent = `Error al guardar la reserva (${resp.status}).`;
        errorDiv.hidden = false;
        submitBtn.disabled = false;
        return;
      }
      const saved = await resp.json();
      window.activeReservations.set(patient.patientId, saved);
      cleanup();
      _renderPacientesResults();
    } catch {
      errorDiv.textContent = 'Error de red al guardar.';
      errorDiv.hidden = false;
      submitBtn.disabled = false;
    }
  }

  function onCancel() { cleanup(); }

  function onKeydown(e) {
    if (e.key === 'Escape') onCancel();
  }

  function cleanup() {
    overlay.hidden = true;
    machineIn.removeEventListener('change', checkMachineWarn);
    cancelBtn.removeEventListener('click', onCancel);
    submitBtn.removeEventListener('click', onSubmit);
    document.removeEventListener('keydown', onKeydown);
  }

  cancelBtn.addEventListener('click', onCancel);
  submitBtn.addEventListener('click', onSubmit);
  document.addEventListener('keydown', onKeydown);
}

function _pacienteFirstAvailableSlot(p) {
  if (!state.homeData) return null;
  const displayName = p.plannedMachineDisplayName;

  // Sin equipo asignado en ARIA: informar si el centro tiene ARIA habilitado
  if (!displayName) {
    return isAriaEnabled(p.centerName) ? { machineName: null, date: null, noMachine: true } : null;
  }

  // Buscar capacidad: exact → case-insensitive → vía equipments
  const allCaps = state.homeData.configuration?.machineCapacities ?? [];
  let cap = allCaps.find(c => c.machineName === displayName)
    ?? allCaps.find(c => c.machineName?.toLowerCase() === displayName.toLowerCase());

  // Fallback: usar datos del equipment (displayName === machineName en agenda)
  let slotMachineName = cap?.machineName ?? displayName;
  if (!cap) {
    const eq = (state.homeData.equipments ?? []).find(e =>
      e.displayName === displayName || e.displayName?.toLowerCase() === displayName.toLowerCase()
    );
    if (eq) {
      cap = { machineName: eq.displayName, workingHours: eq.workingHours,
              standardSlotMinutes: eq.standardSlotMinutes, reservedSpecialHours: eq.reservedSpecialHours ?? 0 };
      slotMachineName = eq.displayName;
    }
  }

  // Si no hay cap y no hay equipments match, mostrar igual pero sin cálculo de free
  const capKnown = !!cap;

  const techEntry = (state.configData?.techniqueDurations ?? [])
    .find(t => t.treatmentLabel === p.treatmentLabel);
  const reqMin = techEntry?.validDurationMinutes?.length
    ? Math.min(...techEntry.validDurationMinutes)
    : (cap?.standardSlotMinutes ?? 15);

  const today = todayStr();
  const byDate = {};
  for (const slot of (state.homeData.agenda ?? [])) {
    const sName = slot.machineName;
    if ((sName !== slotMachineName && sName?.toLowerCase() !== slotMachineName.toLowerCase()) || slot.isEstimated || isExcludedSlot(slot)) continue;
    const d = typeof slot.agendaDate === 'string' ? slot.agendaDate : String(slot.agendaDate);
    if (d < today) continue;
    byDate[d] = (byDate[d] ?? 0) + 1;
  }

  if (!capKnown) {
    // Nombre conocido pero sin config de capacidad — no podemos calcular disponibilidad
    return { machineName: displayName, date: null, noCapacity: true };
  }

  for (const d of Object.keys(byDate).sort()) {
    if (freeMinutes(cap, byDate[d]) >= reqMin) return { machineName: slotMachineName, date: d };
  }

  // No hay slot libre en fechas scrapeadas
  return { machineName: slotMachineName, date: null };
}

function _computePatientAccumDelay(p, stageDefs, stageMap) {
  const weeklyStats = state.alertas.weeklyStats ?? [];
  const eligible = (state.homeData.patients ?? []).filter(q => !q.isLongWait && !isExcludedTechnique(q));
  const F3_SORT = stageDefs.find(s => s.code === 'F3')?.sortOrder ?? 10;
  if ((stageMap[p.stageCode]?.sortOrder ?? 0) < F3_SORT) return null;

  const curSort = stageMap[p.stageCode]?.sortOrder ?? 0;
  const prevStages = stageDefs.filter(s =>
    (s.sortOrder ?? 0) >= F3_SORT && (s.sortOrder ?? 0) < curSort
  );
  let before = 0;
  for (const s of prevStages) {
    const r = getReferenceForAlerts(s.code, weeklyStats, eligible);
    before += r !== null ? r.ref : (s.expectedDays ?? 0);
  }
  const diasDesdeF3 = p.daysInStage + before;
  const { total: refAcum } = _getAccumReference(p.stageCode, weeklyStats, eligible, stageDefs, stageMap);
  if (refAcum <= 0) return null;
  return { diasDesdeF3: Math.round(diasDesdeF3), refAcum, desvio: Math.round(diasDesdeF3 - refAcum) };
}

function _pacienteEventsHtml(events) {
  if (!events.length) return '';
  const fmt = dt => new Date(dt).toLocaleDateString('es-AR', { day: '2-digit', month: '2-digit' });
  const rows = events.slice(0, 5).map(e => {
    let desc = '';
    if (e.eventType === 'TechniqueChanged')
      desc = `Cambio de técnica: ${esc(e.previousValue ?? '—')} → ${esc(e.newValue ?? '—')}`;
    else if (e.eventType === 'StageRegressed')
      desc = `Retroceso: ${esc(e.previousValue ?? '—')} → ${esc(e.newValue ?? '—')}`;
    else
      desc = esc(e.eventType);
    return `<div class="paciente-event-row"><span class="paciente-event-date">${fmt(e.detectedAtUtc)}</span>${desc}</div>`;
  }).join('');
  return `<div class="paciente-card-events"><div class="paciente-events-title">Eventos recientes</div>${rows}</div>`;
}

// ── Feriados alert ────────────────────────────────────────────────────────────

async function checkFeriadosAlert() {
  try {
    const resp = await fetch('/api/alerts/feriados');
    if (!resp.ok) return;
    const data = await resp.json();
    const banner = document.getElementById('feriados-alert-banner');
    if (!banner) return;
    if (data.show) {
      banner.textContent = `⚠ Faltan menos de 10 días para terminar el año. Actualizar feriados.txt con los feriados de ${data.year}.`;
      banner.style.display = 'block';
    } else {
      banner.style.display = 'none';
    }
  } catch { }
}

// ── Inicios tab ───────────────────────────────────────────────────────────────

function _buildIniciosCenterFilter() {
  const centers = [...new Set(
    (state.homeData?.configuration?.machineCapacities ?? []).map(c => c.centerName)
  )].sort();
  const row = document.getElementById('iniciosCenterPills');
  if (!row) return;
  row.innerHTML = '';
  row.appendChild(makePill('Todos', state.inicios.centerFilter === null, () => {
    state.inicios.centerFilter = null; renderIniciosTab();
  }));
  centers.forEach(c => row.appendChild(
    makePill(c, state.inicios.centerFilter === c, () => {
      state.inicios.centerFilter = c; renderIniciosTab();
    })
  ));
}

async function loadIniciosTab() {
  _buildIniciosCenterFilter();

  const content = document.getElementById('iniciosContent');
  const counter = document.getElementById('iniciosCounter');
  if (!state.homeData) {
    content.innerHTML = '<p class="detail-placeholder">Sin datos. Actualice primero.</p>';
    counter.textContent = '';
    return;
  }

  // Refrescar la lista de fechas pre-scrapeadas
  try {
    const resp = await fetch('/api/agenda/available-dates');
    if (resp.ok) state.agenda.availableDates = await resp.json();
  } catch {}

  const today = todayStr();
  const d1  = _addBusinessDays(today, 1);
  const d2  = _addBusinessDays(today, 2);
  const d3  = _addBusinessDays(today, 3);
  const dm1 = _subtractBusinessDays(today, 1);
  const dm2 = _subtractBusinessDays(today, 2);

  state.inicios.futureDates = [d1, d2, d3];
  const availSet = new Set(state.agenda.availableDates);

  // Cargar días pasados (sin advertencia si no hay datos)
  await Promise.all([dm1, dm2].map(async d => {
    if (state.inicios.agendaByDate[d] !== undefined) return;
    try {
      const resp = await fetch(`/api/agenda?date=${d}`);
      if (resp.ok) {
        const data = await resp.json();
        state.inicios.agendaByDate[d] = (data.slots ?? data).filter(s => !s.isEstimated);
      } else {
        state.inicios.agendaByDate[d] = [];
      }
    } catch { state.inicios.agendaByDate[d] = []; }
  }));

  // Cargar días futuros disponibles; trackear faltantes
  const missingDates = [];
  await Promise.all([d1, d2, d3].map(async d => {
    if (state.inicios.agendaByDate[d] !== undefined) return;
    if (!availSet.has(d)) { missingDates.push(d); return; }
    try {
      const resp = await fetch(`/api/agenda?date=${d}`);
      if (resp.ok) {
        const data = await resp.json();
        state.inicios.agendaByDate[d] = (data.slots ?? data).filter(s => !s.isEstimated);
      } else {
        missingDates.push(d);
      }
    } catch { missingDates.push(d); }
  }));

  state.inicios.missingDates = missingDates;
  renderIniciosTab();

  // Scrape en segundo plano para fechas faltantes, reintentar en 15s
  if (missingDates.length > 0 && !state.inicios.scrapePending) {
    state.inicios.scrapePending = true;
    fetch('/api/agenda/scrape-upcoming?days=7', { method: 'POST' }).catch(() => {});

    if (state.inicios.retryHandle) clearTimeout(state.inicios.retryHandle);
    state.inicios.retryHandle = setTimeout(async () => {
      state.inicios.scrapePending = false;
      try {
        const resp = await fetch('/api/agenda/available-dates');
        if (resp.ok) state.agenda.availableDates = await resp.json();
      } catch {}

      const newAvailSet = new Set(state.agenda.availableDates);
      const stillMissing = [];
      await Promise.all(missingDates.map(async d => {
        if (state.inicios.agendaByDate[d] !== undefined) return;
        if (!newAvailSet.has(d)) { stillMissing.push(d); return; }
        try {
          const resp = await fetch(`/api/agenda?date=${d}`);
          if (resp.ok) {
            const data = await resp.json();
            state.inicios.agendaByDate[d] = (data.slots ?? data).filter(s => !s.isEstimated);
          } else {
            stillMissing.push(d);
          }
        } catch { stillMissing.push(d); }
      }));

      state.inicios.missingDates = [];
      for (const d of stillMissing) state.inicios.failedDates.add(d);

      if (state.activeTab === 'inicios') renderIniciosTab();
    }, 15000);
  }
}

// Detecta "inicios" en targetDate: slots reales que NO aparecen en los 2 días
// hábiles previos a targetDate (los 2 días antes de ese día específico, no de hoy).
function _detectInicios(targetDate) {
  const today = todayStr();
  const prev1 = _subtractBusinessDays(targetDate, 1);
  const prev2 = _subtractBusinessDays(targetDate, 2);

  function getSlotsForDay(d) {
    if (d === today) return (state.homeData?.agenda ?? []).filter(s => !s.isEstimated && !isExcludedSlot(s));
    return (state.inicios.agendaByDate[d] ?? []);
  }

  const prevKeys = new Set(
    [...getSlotsForDay(prev1), ...getSlotsForDay(prev2)]
      .map(s => s.sitraMedGuid || s.patientName?.toLowerCase())
      .filter(Boolean)
  );

  const daySlots = state.inicios.agendaByDate[targetDate];
  if (!Array.isArray(daySlots)) return [];

  return daySlots.filter(slot => {
    if (!slot.patientName || slot.patientName === '~' || slot.patientName === '-') return false;
    if (isExcludedSlot(slot)) return false;
    const key = slot.sitraMedGuid || slot.patientName?.toLowerCase();
    return !key || !prevKeys.has(key);
  });
}

function renderIniciosTab() {
  _buildIniciosCenterFilter();
  const content = document.getElementById('iniciosContent');
  const counter = document.getElementById('iniciosCounter');
  if (!content) return;

  if (!state.homeData) {
    content.innerHTML = '<p class="detail-placeholder">Sin datos. Actualice primero.</p>';
    counter.textContent = '';
    return;
  }

  const today = todayStr();
  const futureDates = state.inicios.futureDates.length
    ? state.inicios.futureDates
    : [_addBusinessDays(today, 1), _addBusinessDays(today, 2), _addBusinessDays(today, 3)];

  // Mapa GUID → paciente de seguimiento (para obtener etapa)
  const followupByGuid = {};
  for (const p of (state.homeData.patients ?? [])) {
    if (p.sitraMedGuid) followupByGuid[p.sitraMedGuid] = p;
  }
  const stageDefs = state.homeData.stages ?? [];
  const stageMap = Object.fromEntries(stageDefs.map(s => [s.code, s]));

  // Calcular conteos con filtro de centro aplicado
  const counts = {};
  let totalCount = 0;
  for (const d of futureDates) {
    const inicios = _detectInicios(d).filter(s =>
      !state.inicios.centerFilter || s.centerName === state.inicios.centerFilter
    );
    counts[d] = inicios.length;
    totalCount += inicios.length;
  }

  const [d1, d2, d3] = futureDates;
  counter.textContent =
    `${totalCount} inicio${totalCount !== 1 ? 's' : ''} en los próximos 3 días hábiles ` +
    `(${counts[d1]} mañana, ${counts[d2]} en 2 días, ${counts[d3]} en 3 días)`;

  content.innerHTML = '';

  for (const d of futureDates) {
    const allInicios = _detectInicios(d);
    const inicios = state.inicios.centerFilter
      ? allInicios.filter(s => s.centerName === state.inicios.centerFilter)
      : allInicios;

    const isMissing = state.inicios.missingDates.includes(d);
    const isFailed  = state.inicios.failedDates.has(d);

    const daySection = document.createElement('div');
    daySection.className = 'inicios-day-section';

    // Encabezado del día
    const hdr = document.createElement('div');
    hdr.className = 'inicios-day-header';
    hdr.innerHTML =
      `<span class="inicios-day-title">${_fmtDayOfWeek(d)} ${_fmtDate(d)}</span>` +
      `<span class="inicios-day-count">${inicios.length} inicio${inicios.length !== 1 ? 's' : ''}</span>`;
    daySection.appendChild(hdr);

    // Advertencias de disponibilidad
    if (isFailed) {
      const w = document.createElement('div');
      w.className = 'inicios-warn inicios-warn-failed';
      w.textContent = `No se pudieron cargar datos para ${_fmtDate(d)}`;
      daySection.appendChild(w);
    } else if (isMissing) {
      const w = document.createElement('div');
      w.className = 'inicios-warn';
      w.textContent = `Agenda del ${_fmtDate(d)} no disponible — actualizando en segundo plano...`;
      daySection.appendChild(w);
    }

    if (inicios.length === 0) {
      if (!isMissing && !isFailed) {
        const p = document.createElement('p');
        p.className = 'detail-placeholder';
        p.textContent = 'Sin inicios este día.';
        daySection.appendChild(p);
      }
    } else {
      // Agrupar por equipo
      const byMachine = new Map();
      for (const slot of inicios) {
        if (!byMachine.has(slot.machineName)) byMachine.set(slot.machineName, []);
        byMachine.get(slot.machineName).push(slot);
      }

      const sortedMachines = [...byMachine.entries()].sort((a, b) => {
        const ca = a[1][0].centerName ?? '', cb = b[1][0].centerName ?? '';
        return ca.localeCompare(cb) || a[0].localeCompare(b[0]);
      });

      const grid = document.createElement('div');
      grid.className = 'inicios-machines-grid';

      for (const [machineName, slots] of sortedMachines) {
        // Ordenar por prioridad ASC (nulls al fondo), luego por hora
        slots.sort((a, b) => {
          const pa = a.priority ?? 99, pb = b.priority ?? 99;
          if (pa !== pb) return pa - pb;
          return (a.startTime || '').localeCompare(b.startTime || '');
        });

        const hasP1 = slots.some(s => s.priority === 1);
        const centerName = slots[0].centerName ?? '';

        const card = document.createElement('div');
        card.className = `inicios-machine-card${hasP1 ? ' has-p1' : ''}`;

        // Encabezado de la card de equipo
        let hdrHtml = `<div class="inicios-machine-header">`;
        hdrHtml += `<span class="inicios-machine-name">${esc(machineName)}</span>`;
        if (hasP1) hdrHtml += `<span class="inicios-p1-badge">P1</span>`;
        hdrHtml += `<span class="inicios-machine-count">${slots.length}</span>`;
        hdrHtml += `</div>`;

        // Subcards de pacientes
        let patientsHtml = '<div class="inicios-patient-list">';
        for (const slot of slots) {
          const fp = slot.sitraMedGuid ? followupByGuid[slot.sitraMedGuid] : null;
          const stageLabel = fp
            ? (stageMap[fp.stageCode]?.displayName ?? fp.stageCode)
            : 'En tratamiento';
          const guid = slot.sitraMedGuid;

          const nameHtml = priorityBadge(slot.priority) +
            (guid
              ? `<a href="https://sitramed.mevaterapia.com.ar/medical_histories/${guid}/overview" target="_blank" rel="noopener noreferrer"><strong>${esc(slot.patientName)}</strong></a>`
              : `<strong>${esc(slot.patientName)}</strong>`);
          const hcHtml = fp ? hcTag(fp.patientId) : '';

          const timeStr = slot.startTime && slot.endTime
            ? `${slot.startTime.slice(0, 5)} — ${slot.endTime.slice(0, 5)}`
            : slot.startTime ? slot.startTime.slice(0, 5) : '';

          const labelBadge = renderTreatmentLabel(slot);

          patientsHtml += `<div class="inicios-patient-card">`;
          patientsHtml += `<div class="inicios-patient-name">${nameHtml}${hcHtml}</div>`;
          if (timeStr) patientsHtml += `<div class="inicios-patient-detail">🕐 ${esc(timeStr)}</div>`;
          patientsHtml += `<div class="inicios-patient-detail">Etapa: ${esc(stageLabel)}</div>`;
          if (labelBadge) patientsHtml += `<div class="inicios-patient-detail">${labelBadge}</div>`;
          patientsHtml += `</div>`;
        }
        patientsHtml += `</div>`;

        card.innerHTML = hdrHtml + patientsHtml;
        grid.appendChild(card);
      }

      daySection.appendChild(grid);
    }

    // Reservations for this day
    const dayReservations = [...window.activeReservations.values()].filter(r => r.reservedDate === d);
    const filteredReservations = state.inicios.centerFilter
      ? dayReservations.filter(r => r.centerName === state.inicios.centerFilter)
      : dayReservations;

    if (filteredReservations.length > 0) {
      const resHdr = document.createElement('div');
      resHdr.className = 'inicios-day-header';
      resHdr.style.marginTop = '0.5rem';
      resHdr.innerHTML = `<span class="inicios-day-title" style="color:var(--color-reservation)">Turnos reservados</span><span class="inicios-day-count">${filteredReservations.length}</span>`;
      daySection.appendChild(resHdr);

      const resGrid = document.createElement('div');
      resGrid.className = 'inicios-machines-grid';

      // Group by machine
      const byMachine = new Map();
      for (const r of filteredReservations) {
        if (!byMachine.has(r.machineDisplayName)) byMachine.set(r.machineDisplayName, []);
        byMachine.get(r.machineDisplayName).push(r);
      }

      for (const [machineName, resvList] of byMachine) {
        const card = document.createElement('div');
        card.className = 'inicios-machine-card';
        card.style.borderColor = 'var(--color-reservation-border)';
        card.style.background = 'var(--color-reservation-bg)';

        let hdrHtml = `<div class="inicios-machine-header">`;
        hdrHtml += `<span class="inicios-machine-name" style="color:var(--color-reservation)">${esc(machineName)}</span>`;
        hdrHtml += `<span class="inicios-machine-count">${resvList.length}</span>`;
        hdrHtml += `</div>`;

        let patientsHtml = '<div class="inicios-patient-list">';
        for (const r of resvList) {
          const p = (state.homeData?.patients ?? []).find(pt => pt.patientId === r.patientId);
          const stageLabel = p ? (state.homeData?.stages?.find(s => s.code === p.stageCode)?.displayName ?? p.stageCode) : '';
          patientsHtml += `<div class="inicios-patient-card is-reservation">`;
          patientsHtml += `<div class="inicios-patient-name">${esc(r.patientName)}${hcTag(r.patientId)}</div>`;
          if (r.reservedTime) patientsHtml += `<div class="inicios-patient-detail">🕐 ${esc(r.reservedTime)}</div>`;
          if (stageLabel) patientsHtml += `<div class="inicios-patient-detail">Etapa: ${esc(stageLabel)}</div>`;
          if (r.observations) patientsHtml += `<div class="inicios-patient-detail muted-italic">${esc(r.observations)}</div>`;
          patientsHtml += `</div>`;
        }
        patientsHtml += `</div>`;

        card.innerHTML = hdrHtml + patientsHtml;
        resGrid.appendChild(card);
      }

      daySection.appendChild(resGrid);
    }

    content.appendChild(daySection);
  }
}

// ── Status polling (auto-refresh) ─────────────────────────────────────────────

const POLL_INTERVAL_MS = 3 * 60 * 1000;
let _poll = { knownDataTime: null, knownVersion: null };

async function _checkStatus() {
  try {
    const resp = await fetch('/api/status');
    if (!resp.ok) return;
    const { generatedAtUtc, appVersion } = await resp.json();

    if (_poll.knownVersion === null) {
      _poll.knownDataTime = generatedAtUtc;
      _poll.knownVersion = appVersion;
      return;
    }

    if (appVersion !== _poll.knownVersion) {
      _showAutoUpdateBanner('Nueva versión disponible. Actualizando...');
      setTimeout(() => location.reload(true), 2500);
      return;
    }

    if (generatedAtUtc && generatedAtUtc !== _poll.knownDataTime) {
      _poll.knownDataTime = generatedAtUtc;
      _showAutoUpdateBanner('Datos actualizados. Recargando...');
      setTimeout(() => location.reload(), 1500);
    }
  } catch { /* ignorar errores de red */ }
}

function _showAutoUpdateBanner(msg) {
  const b = document.getElementById('auto-update-banner');
  if (b) { b.textContent = msg; b.style.display = 'block'; }
}

// ── Boot ──────────────────────────────────────────────────────────────────────

wireTabs();
wireActions();
loadAvailableDates();
loadAvailableTomographDates();
loadHome();
loadWeeklyStats();
checkFeriadosAlert();
setTimeout(_checkStatus, 10000);
setInterval(_checkStatus, POLL_INTERVAL_MS);
