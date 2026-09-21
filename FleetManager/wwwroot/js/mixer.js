// =====================================================================
//  NBR Ready Mix — Control diario de mezcladoras
//  Misma funcionalidad que la página original, pero la data vive en
//  PostgreSQL (esquema "mezcladoras") y se consume vía /api/mixer/*.
// =====================================================================
let data = { camiones: [], conductores: [], plantas: [], estados: [], resumen: { plantas: [] }, historial: [] };
let config = { config: null, envios: [] };
let plantaSeleccionada = null;
let filtroEstado = "all";
const USER_KEY = "nbr_mixer_user";

const $ = id => document.getElementById(id);
const esc = s => String(s ?? "").replace(/[&<>"']/g, m => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[m]));
const usuario = () => ($("userName").value || "").trim() || "Owner";
const COLOR = { manned: "#18a874", down: "#e85353", open: "#e99a2c" };
const ETIQUETA = { manned: "Manned", down: "Down", open: "Open Trucks" };
const ORDEN = ["manned", "down", "open"];

let toastTimer;
function toast(msg, error) {
    const t = $("toast"); t.textContent = msg; t.classList.toggle("error", !!error); t.classList.add("show");
    // los mensajes con ajustes automáticos son más largos: se muestran más tiempo
    clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.remove("show"), Math.max(2600, String(msg).length * 60));
}

async function api(url, body) {
    const res = await fetch(url, body ? { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) } : undefined);
    const json = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(json.mensaje || json.title || ("HTTP " + res.status));
    return json;
}

async function cargar(mensaje) {
    try {
        data = await api("/api/mixer/datos");
        render();
        if (mensaje) toast(mensaje);
    } catch (e) { toast("Error loading data: " + e.message, true); }
}

async function accion(url, body, recargar = true) {
    try {
        const r = await api(url, { ...body, usuario: usuario() });
        if (recargar) await cargar();
        toast(r.mensaje || "Changes saved");
        return true;
    } catch (e) { toast(e.message, true); await cargar(); return false; }
}

// ---------------------------------------------------------------- cálculos
const plantas = () => data.plantas.map(p => p.codigo);
const camionesEn = p => data.camiones.filter(c => c.planta === p);
const conductoresEn = p => data.conductores.filter(d => d.planta === p);
const conductoresDe = n => data.conductores.filter(d => d.numeroCamion === n);
const cuenta = (estado, p = null) => (p ? camionesEn(p) : data.camiones).filter(c => c.estado === estado).length;
const total = p => (p ? camionesEn(p) : data.camiones).length;
const pct = (v, t) => t ? Math.round(v / t * 100) : 0;

function fmtFecha(iso) {
    if (!iso) return "—";
    return new Date(iso).toLocaleString("en-US", { weekday: "short", month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
}

// ------------------------------------------------------------- paginación
// Todas las grillas se paginan de a 10 registros (configurable a 20/30/50/100).
const TAMANOS = [10, 20, 30, 50, 100];
const PG_KEY = "nbr_mixer_pagesize";
const paginas = {}, firmas = {};
let tamPorDefecto = 10;
try { const n = parseInt(localStorage.getItem(PG_KEY)); if (TAMANOS.includes(n)) tamPorDefecto = n; } catch { }

const pgEstado = clave => paginas[clave] || (paginas[clave] = { pagina: 1, tam: tamPorDefecto });

// Si cambian los filtros de una grilla, vuelve a la primera página.
function pgFirma(clave, firma) {
    if (firmas[clave] !== firma) { firmas[clave] = firma; pgEstado(clave).pagina = 1; }
}

function pgPagina(clave, lista) {
    const s = pgEstado(clave), totalPag = Math.max(1, Math.ceil(lista.length / s.tam));
    s.pagina = Math.min(Math.max(1, s.pagina), totalPag);
    const i = (s.pagina - 1) * s.tam;
    return lista.slice(i, i + s.tam);
}

function pgBarra(clave, n, etiqueta = "records") {
    const s = pgEstado(clave), totalPag = Math.max(1, Math.ceil(n / s.tam));
    const desde = n ? (s.pagina - 1) * s.tam + 1 : 0, hasta = Math.min(s.pagina * s.tam, n);
    const b = (txt, destino, off, titulo) =>
        `<button type="button" class="pg-btn" title="${titulo}" aria-label="${titulo}" ${off ? "disabled" : ""} onclick="irPagina('${clave}',${destino})">${txt}</button>`;
    return `<div class="pager">
      <span class="pg-count">${n ? `${desde}–${hasta} of ${n} ${esc(etiqueta)}` : `No ${esc(etiqueta)}`}</span>
      <div class="pg-ctrl">
        <label class="pg-size">Rows<select onchange="cambiarTamPagina('${clave}',this.value)">
          ${TAMANOS.map(t => `<option ${s.tam === t ? "selected" : ""}>${t}</option>`).join("")}</select></label>
        ${b("&laquo;", 1, s.pagina <= 1, "First page")}
        ${b("&lsaquo;", s.pagina - 1, s.pagina <= 1, "Previous page")}
        <span class="pg-info">${s.pagina} / ${totalPag}</span>
        ${b("&rsaquo;", s.pagina + 1, s.pagina >= totalPag, "Next page")}
        ${b("&raquo;", totalPag, s.pagina >= totalPag, "Last page")}
      </div></div>`;
}

const REDIBUJA = {
    trucks: () => renderTrucks(), drivers: () => renderDrivers(), hist: () => renderDashboard(),
    ptrucks: () => renderPlants(), pdrivers: () => renderPlants(), envios: () => renderReport()
};
window.irPagina = (clave, p) => { pgEstado(clave).pagina = p; (REDIBUJA[clave] || render)(); };
window.cambiarTamPagina = (clave, v) => {
    // El tamaño elegido pasa a ser el de todas las grillas (y se recuerda).
    const t = parseInt(v) || 10;
    tamPorDefecto = t;
    Object.values(paginas).forEach(s => { s.tam = t; s.pagina = 1; });
    pgEstado(clave);
    try { localStorage.setItem(PG_KEY, String(t)); } catch { }
    render(); renderReport();
};

// Repinta una sección conservando el foco y el cursor del campo activo
// (los buscadores rearman el HTML en cada tecla).
function pintar(idSeccion, html) {
    const act = document.activeElement;
    const foco = act && act.id ? act.id : null;
    const pos = act && typeof act.selectionStart === "number" ? act.selectionStart : null;
    $(idSeccion).innerHTML = html;
    if (!foco) return;
    const nuevo = $(foco);
    if (!nuevo) return;
    nuevo.focus();
    if (pos !== null && typeof nuevo.setSelectionRange === "function") { try { nuevo.setSelectionRange(pos, pos); } catch { } }
}

// ---------------------------------------------------------------- render
function render() {
    $("updatedAt").textContent = fmtFecha(data.resumen.ultimaActualizacion || new Date().toISOString());
    renderDashboard(); renderPlants(); renderTrucks(); renderDrivers(); renderReport();
}

function donut(p = null) {
    const t = total(p), a = pct(cuenta("manned", p), t), b = pct(cuenta("down", p), t), stop2 = a + b;
    const fondo = t ? `conic-gradient(${COLOR.manned} 0 ${a}%,${COLOR.down} ${a}% ${stop2}%,${COLOR.open} ${stop2}% 100%)` : "#e7eaf0";
    return `<div class="donut-wrap"><div class="donut" style="background:${fondo}"><div><strong>${t}</strong><span>trucks</span></div></div>
    <div class="donut-legend">${ORDEN.map(k => `<div><i style="background:${COLOR[k]}"></i><span>${ETIQUETA[k]}</span><strong>${pct(cuenta(k, p), t)}%</strong></div>`).join("")}</div></div>`;
}

function metric(label, valor, porcentaje, color, accion = "") {
    return `<article class="metric ${accion ? "clickable" : ""}" ${accion ? `onclick="${accion}" role="button" tabindex="0" onkeydown="if(event.key==='Enter')this.click()"` : ""}>
      <div class="metric-top"><span>${esc(label)}</span><i class="dot" style="background:${color}"></i></div>
      <strong>${valor}</strong><small>${porcentaje}% of fleet${accion ? " · View details" : ""}</small></article>`;
}

function renderDashboard() {
    const t = data.camiones.length;
    const hist = data.historial || [];
    const histPag = pgPagina("hist", hist);
    pintar("dashboard", `<div class="fleet-overview"><div class="metrics">
      ${metric("Mixer Trucks", t, 100, "#165dff", "abrirEstado('all')")}
      ${metric("Drivers", data.conductores.length, 100, "#7759e8", "abrirConductores()")}
      ${ORDEN.map(k => metric(ETIQUETA[k], cuenta(k), pct(cuenta(k), t), COLOR[k], `abrirEstado('${k}')`)).join("")}
    </div>
    <article class="panel donut-panel"><div><h2>Fleet Distribution</h2><p>Current percentage by status</p></div>${donut()}</article></div>
    <div class="section-head"><div><h2>Plant Overview</h2><p>Click a plant to open its detailed view.</p></div></div>
    <div class="plant-grid">${plantas().map(tarjetaPlanta).join("")}</div>
    <div class="section-head"><div><h2>Recent Activity</h2><p>Last changes recorded with user and time.</p></div></div>
    <div class="panel"><div class="history">${histPag.length
        ? histPag.map(h => `<div class="hist"><b>${esc(h.referencia)}</b> · ${esc(h.campo)}: <b>${esc(h.valorAnterior || "—")}</b> → <b>${esc(h.valorNuevo || "—")}</b> · ${esc(h.usuario)} · ${esc(fmtFecha(h.fecha))}</div>`).join("")
        : '<div class="hist">No changes yet.</div>'}</div>${pgBarra("hist", hist.length, "changes")}</div>`);
}

function anilloPlanta(p) {
    const t = total(p), a = pct(cuenta("manned", p), t), b = pct(cuenta("down", p), t);
    return t ? `conic-gradient(${COLOR.manned} 0 ${a}%,${COLOR.down} ${a}% ${a + b}%,${COLOR.open} ${a + b}% 100%)` : "#e7eaf0";
}

function tarjetaPlanta(p) {
    const t = total(p), ds = conductoresEn(p).length;
    return `<article class="plant-card" onclick="abrirPlanta('${esc(p)}')">
      <div class="plant-title"><h3>${esc(p)}</h3><b>${t} trucks</b></div>
      <div class="plant-ring-row"><div class="mini-donut" style="background:${anilloPlanta(p)}"><span>${t}</span></div>
      <div class="percent-list">${ORDEN.map(k => `<div><i style="background:${COLOR[k]}"></i>${ETIQUETA[k]}<strong>${pct(cuenta(k, p), t)}%</strong></div>`).join("")}</div></div>
      <div class="mini-stats">${ORDEN.map(k => `<div class="mini"><span style="color:${COLOR[k]}">●</span> ${ETIQUETA[k]}<strong>${cuenta(k, p)}</strong></div>`).join("")}</div>
      <div class="driver-count">Assigned Drivers <strong>${ds}</strong></div></article>`;
}

function renderPlants() {
    if (!plantaSeleccionada) {
        $("plants").innerHTML = `<div class="section-head"><div><h2>Plant Details</h2><p>Select a plant to review its trucks and drivers.</p></div></div>
        <div class="plant-grid">${plantas().map(tarjetaPlanta).join("")}</div>`;
        return;
    }
    const p = plantaSeleccionada, t = total(p), lista = camionesEn(p), conductores = conductoresEn(p);
    pgFirma("ptrucks", p); pgFirma("pdrivers", p);
    const listaPag = pgPagina("ptrucks", lista), conductoresPag = pgPagina("pdrivers", conductores);
    pintar("plants", `<button class="back-btn" onclick="verTodasLasPlantas()">← All Plants</button>
    <div class="detail-head"><div><p class="eyebrow">PLANT DETAILS</p><h2>${esc(p)}</h2><p>${t} trucks · ${conductoresEn(p).length} drivers</p></div>${donut(p)}</div>
    <div class="metrics plant-metrics">${ORDEN.map(k => metric(ETIQUETA[k], cuenta(k, p), pct(cuenta(k, p), t), COLOR[k])).join("")}</div>
    <div class="detail-columns">
      <article class="panel"><div class="section-head compact"><div><h2>Trucks at ${esc(p)}</h2><p>Information updates from Mixer Trucks.</p></div></div>
        <div class="detail-trucks">${listaPag.map(c => `<div><b>#${c.numero}</b>
          <span class="badge" style="color:${COLOR[c.estado]};background:${COLOR[c.estado]}14">${ETIQUETA[c.estado]}</span>
          <small>${esc(c.conductores.map(d => d.nombre).join(", ") || "No driver")}</small></div>`).join("") || '<p class="empty-note">No trucks are assigned to this plant.</p>'}</div>
        ${pgBarra("ptrucks", lista.length, "trucks")}</article>
      <article class="panel"><div class="section-head compact"><div><h2>Drivers</h2><p>${conductores.length} assigned</p></div></div>
        <div class="driver-detail-list">${conductoresPag.map(d => `<div><span>${esc(d.nombre)}</span><b>${d.numeroCamion ? "Truck #" + d.numeroCamion : "No truck"}</b></div>`).join("") || '<p class="empty-note">No drivers are assigned.</p>'}</div>
        ${pgBarra("pdrivers", conductores.length, "drivers")}</article>
    </div>`);
}

function renderTrucks() {
    const q = ($("truckSearch")?.value || "").trim();
    const pf = $("plantFilter")?.value || "All";
    const etiqueta = filtroEstado === "all" ? "All Trucks" : ETIQUETA[filtroEstado] || "Trucks";
    const filas = data.camiones.filter(c => String(c.numero).includes(q)
        && (pf === "All" || (pf === "Unassigned" ? !c.planta : c.planta === pf))
        && (filtroEstado === "all" || c.estado === filtroEstado));
    pgFirma("trucks", `${q}|${pf}|${filtroEstado}`);
    const filasPag = pgPagina("trucks", filas);

    pintar("trucks", `<div class="section-head"><div><h2>${etiqueta}</h2><p>${filas.length} units · Plant and assigned driver details.</p></div></div>
    <form class="truck-form" onsubmit="agregarCamion(event)">
      <input id="newTruckNumber" required inputmode="numeric" placeholder="New truck number">
      <button class="primary-btn">+ Add Truck</button></form>
    <div class="toolbar">
      <input id="truckSearch" class="search" placeholder="Search truck #" value="${esc(q)}" oninput="renderTrucks()">
      <select id="plantFilter" class="filter" onchange="renderTrucks()">
        <option>All</option>${["Unassigned", ...plantas()].map(p => `<option ${pf === p ? "selected" : ""}>${esc(p)}</option>`).join("")}</select>
      <select class="filter" onchange="setFiltroEstado(this.value)">
        <option value="all" ${filtroEstado === "all" ? "selected" : ""}>All Statuses</option>
        ${ORDEN.map(k => `<option value="${k}" ${filtroEstado === k ? "selected" : ""}>${ETIQUETA[k]}</option>`).join("")}</select>
    </div>
    <div class="table-wrap"><table>
      <thead><tr><th>Truck</th><th>Plant</th><th>Status</th><th>Truck Drivers</th><th>Action</th></tr></thead>
      <tbody>${filasPag.map(filaCamion).join("") || '<tr><td colspan="5" class="empty-note">No trucks have this status.</td></tr>'}</tbody>
    </table></div>
    ${pgBarra("trucks", filas.length, "trucks")}`);
}

// ------------------------------------------------------ reglas de negocio
//  1. Solo un camión Manned puede tener conductores (Open Trucks y Down no).
//  2. Un camión Manned debe tener al menos un conductor:
//     · al pasar a Manned se elige el conductor (obligatorio);
//     · si se quita su último conductor, el camión pasa a Open Trucks.
//  3. Al pasar a Open Trucks o Down se liberan sus conductores.
//  4. El conductor con camión está en la planta del camión; si se le cambia a
//     otra planta, deja el camión.
//  El servidor y la base de datos aplican las mismas reglas.
const libres = () => data.conductores.filter(d => !d.numeroCamion);
const camionDe = numero => data.camiones.find(c => c.numero === numero);
const etiquetaPlanta = p => p || "Unassigned";

// Aviso cuando el conductor es el único de un camión Manned
function notaUltimoConductor(d) {
    const c = d.numeroCamion ? camionDe(d.numeroCamion) : null;
    return c && c.estado === "manned" && c.conductores.length === 1
        ? `\n\n${d.nombre} is the only driver of truck #${c.numero}. A Manned truck needs at least one driver, so truck #${c.numero} will change to Open Trucks.`
        : "";
}

function celdaConductores(c) {
    const chips = c.conductores.map(d => `<span class="driver-chip">${esc(d.nombre)}<button title="Remove from truck" aria-label="Remove ${esc(d.nombre)} from truck" onclick="event.stopPropagation();quitarDeCamion(${d.idConductor})">×</button></span>`).join("");
    if (c.estado === "manned") {
        const disponibles = libres();
        return `<div class="driver-chips">${chips}</div>
        <div class="add-driver"><select id="add-${c.numero}" class="table-select" ${disponibles.length ? "" : "disabled"}>
          <option value="">${disponibles.length ? "Add driver…" : "No drivers available"}</option>
          ${disponibles.map(d => `<option value="${d.idConductor}">${esc(d.nombre)}</option>`).join("")}</select>
          <button class="primary-btn" onclick="agregarAlCamion(${c.numero})" ${disponibles.length ? "" : "disabled"}>Add</button></div>`;
    }
    // Open / Down: sin lista de conductores (los chips solo aparecen si hay datos anteriores a la regla)
    const nota = c.estado === "down" ? "Truck is Down · no drivers" : "Set status to Manned to assign a driver";
    return `${chips ? `<div class="driver-chips">${chips}</div>` : ""}<span class="rule-note ${c.estado}">${nota}</span>`;
}

function filaCamion(c) {
    return `<tr>
      <td data-label="Truck">#${c.numero}</td>
      <td data-label="Plant"><select class="table-select" onchange="actualizarCamion(${c.numero},'planta',this.value)">
        ${["Unassigned", ...plantas()].map(p => `<option ${(c.planta || "Unassigned") === p ? "selected" : ""}>${esc(p)}</option>`).join("")}</select></td>
      <td data-label="Status"><select class="table-select status-${c.estado}" onchange="cambiarEstado(${c.numero},this.value)">
        ${ORDEN.map(k => `<option value="${k}" ${c.estado === k ? "selected" : ""}>${ETIQUETA[k]}</option>`).join("")}</select></td>
      <td data-label="Drivers">${celdaConductores(c)}</td>
      <td data-label="Action"><button class="danger-btn" onclick="eliminarCamion(${c.numero})">Remove Truck</button></td></tr>`;
}

function renderDrivers() {
    const q = ($("driverSearch")?.value || "").toLocaleLowerCase();
    const lista = data.conductores.filter(d => d.nombre.toLocaleLowerCase().includes(q));
    pgFirma("drivers", q);
    const listaPag = pgPagina("drivers", lista);
    pintar("drivers", `<div class="section-head"><div><h2>Drivers</h2><p>${data.conductores.length} unique names. Assign a plant and truck.</p></div></div>
    <form class="driver-form" onsubmit="agregarConductor(event)">
      <input id="newDriverName" required placeholder="Driver full name">
      <select id="newDriverPlant">${["Unassigned", ...plantas()].map(p => `<option>${esc(p)}</option>`).join("")}</select>
      <select id="newDriverTruck" onchange="sincronizarPlantaNueva()" title="Only Manned trucks can have drivers"><option value="">No truck</option>
        ${data.camiones.filter(c => c.estado === "manned").map(c => `<option value="${c.numero}">Truck #${c.numero} · ${esc(etiquetaPlanta(c.planta))}</option>`).join("")}</select>
      <button class="primary-btn">Add Driver</button></form>
    <div class="toolbar"><input id="driverSearch" class="search" placeholder="Search driver" value="${esc(q)}" oninput="renderDrivers()"></div>
    <div class="table-wrap"><table>
      <thead><tr><th>Driver</th><th>Assigned Plant</th><th>Truck</th><th>Action</th></tr></thead>
      <tbody>${listaPag.map(filaConductor).join("") || '<tr><td colspan="4" class="empty-note">No drivers found.</td></tr>'}</tbody>
    </table></div>
    ${pgBarra("drivers", lista.length, "drivers")}`);
}

// Solo se ofrecen camiones Manned (regla 1). Si el conductor quedó en un camión
// que no es Manned (datos anteriores a la regla), se muestra marcado.
function opcionesCamionConductor(d) {
    const actual = d.numeroCamion ? camionDe(d.numeroCamion) : null;
    let html = '<option value="">No truck</option>';
    if (actual && actual.estado !== "manned")
        html += `<option value="${actual.numero}" selected>#${actual.numero} (${ETIQUETA[actual.estado]})</option>`;
    html += data.camiones.filter(c => c.estado === "manned").map(c =>
        `<option value="${c.numero}" ${d.numeroCamion === c.numero ? "selected" : ""}>#${c.numero} · ${esc(etiquetaPlanta(c.planta))}</option>`).join("");
    return html;
}

function filaConductor(d) {
    return `<tr>
      <td data-label="Driver">${esc(d.nombre)}</td>
      <td data-label="Plant"><select class="table-select" onchange="actualizarConductor(${d.idConductor},'planta',this.value)">
        ${["Unassigned", ...plantas()].map(p => `<option ${(d.planta || "Unassigned") === p ? "selected" : ""}>${esc(p)}</option>`).join("")}</select></td>
      <td data-label="Truck"><select class="table-select" title="Only Manned trucks can have drivers" onchange="actualizarConductor(${d.idConductor},'camion',this.value)">
        ${opcionesCamionConductor(d)}</select></td>
      <td data-label="Action"><button class="danger-btn" onclick="eliminarConductor(${d.idConductor})">Remove Driver</button></td></tr>`;
}

// ---------------------------------------------------------------- informe
function renderReport() {
    const c = config.config;
    if (!$("report")) return;                      // pestaña oculta en la vista
    if (!c) { $("report").innerHTML = '<div class="panel">Loading…</div>'; return; }
    const dias = new Set((c.diasSemana || "").split(",").map(x => parseInt(x.trim())).filter(Boolean));
    const nombresDias = [["1", "Mon"], ["2", "Tue"], ["3", "Wed"], ["4", "Thu"], ["5", "Fri"], ["6", "Sat"], ["7", "Sun"]];
    const envios = config.envios || [];
    const enviosPag = pgPagina("envios", envios);

    pintar("report", `<div class="section-head"><div><h2>Email report</h2><p>Send the Overview by email, now or on a schedule.</p></div></div>
    ${c.correoConfigurado
            ? `<div class="aviso ok">Email delivery is configured on the server (${esc(c.proveedorCorreo)}).</div>`
            : `<div class="aviso warn">Email is not configured yet. Set <b>SMTP_HOST / SMTP_USER / SMTP_PASS / MAIL_FROM</b> (or <b>RESEND_API_KEY</b>) in Railway → Variables.</div>`}
    <div class="report-grid">
      <article class="panel">
        <div class="section-head compact"><div><h2>Schedule</h2><p>The app checks every minute and sends at the configured time.</p></div></div>
        <div class="form-row"><label>Automatic send</label>
          <label class="switch"><input type="checkbox" id="cfgActivo" ${c.activo ? "checked" : ""}> Enabled</label></div>
        <div class="form-row"><label>Time</label><input type="time" id="cfgHora" value="${esc(c.horaEnvio)}"></div>
        <div class="form-row"><label>Days</label><div class="dias">${nombresDias.map(([n, t]) =>
                `<button type="button" class="dia ${dias.has(+n) ? "on" : ""}" data-dia="${n}" onclick="this.classList.toggle('on')">${t}</button>`).join("")}</div></div>
        <div class="form-row"><label>Time zone</label><input type="text" id="cfgZona" value="${esc(c.zonaHoraria)}" placeholder="America/Chicago"></div>
        <div class="form-row"><label>Subject</label><input type="text" id="cfgAsunto" value="${esc(c.asunto)}"></div>
        <div class="form-row"><label>Content</label><div>
          <label class="switch"><input type="checkbox" id="cfgPlantas" ${c.incluirPlantas ? "checked" : ""}> Plant table</label><br>
          <label class="switch" style="margin-top:6px"><input type="checkbox" id="cfgCamiones" ${c.incluirCamiones ? "checked" : ""}> List of trucks Down</label></div></div>
        <div class="form-row"><label>Last sent</label><div>${esc(fmtFecha(c.ultimoEnvio))}</div></div>
        <div class="toolbar" style="margin-top:14px">
          <button class="primary-btn" id="btnGuardarCfg" onclick="guardarConfig()">Save settings</button>
          <button class="ghost-btn" onclick="enviarAhora()">Send now</button>
          <button class="ghost-btn" onclick="enviarPrueba()">Send test to…</button>
        </div>
      </article>

      <article class="panel">
        <div class="section-head compact"><div><h2>Recipients</h2><p>Who receives the daily report.</p></div></div>
        <div class="dest-list" id="destList">${(c.destinatarios || []).map(filaDestinatario).join("")}</div>
        <button class="ghost-btn" onclick="agregarDestinatario()">+ Add recipient</button>
        <div class="section-head compact" style="margin-top:22px"><div><h2>Last deliveries</h2></div></div>
        <div class="envios">${enviosPag.length ? enviosPag.map(e =>
                `<div class="envio ${e.exito ? "ok" : "err"}"><b>${esc(fmtFecha(e.fecha))}</b> · ${esc(e.disparo)} · ${e.exito ? "OK" : "FAILED"}<br>
             <span style="color:var(--muted)">${esc(e.destinatarios || "—")}</span>${e.mensajeError ? `<br><span style="color:var(--red)">${esc(e.mensajeError)}</span>` : ""}</div>`).join("")
            : '<div class="envio">No deliveries yet.</div>'}</div>
        ${pgBarra("envios", envios.length, "deliveries")}
      </article>
    </div>

    <div class="section-head"><div><h2>Preview</h2><p>Exactly what the recipients will get.</p></div>
      <button class="ghost-btn" onclick="$('previewFrame').contentWindow.location.reload()">Refresh preview</button></div>
    <iframe id="previewFrame" class="preview-frame" src="/api/mixer/reporte/preview" title="Report preview"></iframe>`);
}

function filaDestinatario(d) {
    return `<div class="dest" data-id="${d.idDestinatario || 0}">
      <input type="email" class="d-correo" value="${esc(d.correo)}" placeholder="name@company.com">
      <select class="d-tipo">${["TO", "CC", "BCC"].map(t => `<option ${d.tipo === t ? "selected" : ""}>${t}</option>`).join("")}</select>
      <button onclick="this.parentElement.remove()">Remove</button></div>`;
}

window.agregarDestinatario = () => {
    $("destList").insertAdjacentHTML("beforeend", filaDestinatario({ idDestinatario: 0, correo: "", tipo: "TO", activo: true }));
};

function leerConfig() {
    return {
        activo: $("cfgActivo").checked,
        horaEnvio: $("cfgHora").value || "07:00",
        diasSemana: [...document.querySelectorAll(".dia.on")].map(b => b.dataset.dia).join(",") || "1,2,3,4,5",
        zonaHoraria: $("cfgZona").value.trim() || "America/Chicago",
        asunto: $("cfgAsunto").value.trim(),
        incluirPlantas: $("cfgPlantas").checked,
        incluirCamiones: $("cfgCamiones").checked,
        destinatarios: [...document.querySelectorAll("#destList .dest")].map(el => ({
            idDestinatario: +el.dataset.id || 0,
            correo: el.querySelector(".d-correo").value.trim(),
            tipo: el.querySelector(".d-tipo").value,
            activo: true
        })).filter(d => d.correo)
    };
}

window.guardarConfig = async () => {
    const btn = $("btnGuardarCfg"); btn.disabled = true;
    try {
        const r = await api("/api/mixer/reporte/config", { config: leerConfig(), usuario: usuario() });
        await cargarConfig(); toast(r.mensaje);
    } catch (e) { toast(e.message, true); }
    finally { btn.disabled = false; }
};

window.enviarAhora = async () => {
    if (!confirm("Send the Overview report to the configured recipients now?")) return;
    try { const r = await api("/api/mixer/reporte/enviar", { usuario: usuario() }); await cargarConfig(); toast(r.mensaje); }
    catch (e) { toast(e.message, true); await cargarConfig(); }
};

window.enviarPrueba = async () => {
    const correo = prompt("Send a test copy to which address?", "");
    if (!correo) return;
    try { const r = await api("/api/mixer/reporte/enviar", { usuario: usuario(), correoPrueba: correo.trim() }); await cargarConfig(); toast(r.mensaje); }
    catch (e) { toast(e.message, true); await cargarConfig(); }
};

async function cargarConfig() {
    try { config = await api("/api/mixer/reporte/config"); renderReport(); }
    catch (e) { toast("Error loading report settings: " + e.message, true); }
}

// ---------------------------------------------------------------- acciones
window.actualizarCamion = (numero, campo, valor) => {
    const c = camionDe(numero);
    if (campo === "estado") return cambiarEstado(numero, valor);
    const body = { numero, planta: c.planta, estado: c.estado };
    body[campo] = valor;
    accion("/api/mixer/camion/actualizar", body);
};

// Cambio de estado aplicando las reglas 2 y 3
window.cambiarEstado = async (numero, estado) => {
    const c = camionDe(numero);
    if (!c || c.estado === estado) return;

    if (estado === "manned" && c.conductores.length === 0) {
        const idConductor = await elegirConductor(c);
        if (!idConductor) { renderTrucks(); return; }          // cancelado: vuelve al estado anterior
        return accion("/api/mixer/camion/actualizar", { numero, planta: c.planta, estado, idConductor });
    }

    if (estado !== "manned" && c.conductores.length > 0) {
        const nombres = c.conductores.map(d => d.nombre).join(", ");
        const tipo = estado === "down" ? "A Down truck" : "An Open truck";
        if (!confirm(`Change truck #${numero} to ${ETIQUETA[estado]}?\n\n${tipo} cannot have drivers, so ${nombres} will be released from it.`)) {
            renderTrucks(); return;
        }
    }
    accion("/api/mixer/camion/actualizar", { numero, planta: c.planta, estado });
};

// Diálogo para elegir el conductor obligatorio al pasar a Manned
function elegirConductor(c) {
    return new Promise(resolve => {
        let dlg = $("driverDialog");
        if (!dlg) { dlg = document.createElement("dialog"); dlg.id = "driverDialog"; dlg.className = "modal"; document.body.appendChild(dlg); }
        const lista = libres();
        const mismaPlanta = c.planta ? lista.filter(d => d.planta === c.planta) : [];
        const otros = lista.filter(d => !mismaPlanta.includes(d));
        const opt = d => `<option value="${d.idConductor}">${esc(d.nombre)}${d.planta ? " · " + esc(d.planta) : ""}</option>`;

        dlg.innerHTML = `<form class="modal-body">
          <h3>Set truck #${c.numero} to Manned</h3>
          <p>A Manned truck must have at least one driver. Choose the driver for truck #${c.numero}${c.planta ? ` at <b>${esc(c.planta)}</b>` : ""}.</p>
          ${lista.length ? `
            <label for="dlgDriver">Driver</label>
            <select id="dlgDriver">
              <option value="">Select a driver…</option>
              ${mismaPlanta.length ? `<optgroup label="At ${esc(c.planta)}">${mismaPlanta.map(opt).join("")}</optgroup>` : ""}
              ${otros.length ? `<optgroup label="${mismaPlanta.length ? "Other available drivers" : "Available drivers"}">${otros.map(opt).join("")}</optgroup>` : ""}
            </select>
            <small class="modal-hint">Only drivers without a truck are listed. The driver moves to the truck's plant.</small>
            <small class="modal-error" id="dlgError"></small>`
            : `<div class="aviso warn">There are no available drivers. Add a driver in the Drivers tab, or release one from another truck first.</div>`}
          <div class="modal-actions">
            <button type="button" class="ghost-btn" id="dlgCancel">Cancel</button>
            ${lista.length ? '<button type="submit" class="primary-btn">Set Manned</button>' : ""}
          </div></form>`;

        let resuelto = false;
        const cerrar = v => { if (resuelto) return; resuelto = true; dlg.close(); resolve(v); };
        $("dlgCancel").onclick = () => cerrar(null);
        dlg.oncancel = e => { e.preventDefault(); cerrar(null); };          // tecla Esc
        dlg.querySelector("form").onsubmit = e => {
            e.preventDefault();
            const v = +($("dlgDriver")?.value || 0);
            if (!v) { $("dlgError").textContent = "Select a driver to continue."; $("dlgDriver").focus(); return; }
            cerrar(v);
        };
        dlg.showModal();
        ($("dlgDriver") || $("dlgCancel")).focus();
    });
}

window.sincronizarPlantaNueva = () => {
    // Con camión elegido, el conductor queda en la planta del camión
    const num = +$("newDriverTruck").value, sel = $("newDriverPlant");
    const c = num ? camionDe(num) : null;
    if (c) sel.value = etiquetaPlanta(c.planta);
    sel.disabled = !!c;
};

window.agregarCamion = e => {
    e.preventDefault();
    const raw = $("newTruckNumber").value.trim();
    if (!/^\d+$/.test(raw)) return toast("Enter a valid truck number", true);
    accion("/api/mixer/camion/agregar", { numero: +raw });
};

window.eliminarCamion = numero => {
    const asignados = conductoresDe(numero);
    const nota = asignados.length ? ` The ${asignados.length} associated driver${asignados.length === 1 ? "" : "s"} will be left without a truck.` : "";
    if (!confirm(`Remove truck #${numero}?${nota}`)) return;
    accion("/api/mixer/camion/eliminar", { numero });
};

window.agregarAlCamion = numero => {
    const c = camionDe(numero);
    if (c && c.estado !== "manned") return toast(`Truck #${numero} must be Manned to assign drivers`, true);
    const id = +$("add-" + numero).value;
    if (!id) return toast("Select a driver", true);
    accion("/api/mixer/conductor/asignar", { numero, idConductor: id });
};

window.quitarDeCamion = idConductor => {
    const d = data.conductores.find(x => x.idConductor === idConductor);
    const nota = d ? notaUltimoConductor(d) : "";
    if (nota && !confirm(`Remove ${d.nombre} from truck #${d.numeroCamion}?${nota}`)) return;
    accion("/api/mixer/conductor/quitar", { idConductor });
};

window.actualizarConductor = (idConductor, campo, valor) => {
    const d = data.conductores.find(x => x.idConductor === idConductor);
    const body = { idConductor, planta: etiquetaPlanta(d.planta), numeroCamion: d.numeroCamion };
    let aviso = "";

    if (campo === "planta") {
        body.planta = valor;
        const c = d.numeroCamion ? camionDe(d.numeroCamion) : null;
        // Regla 4: a otra planta distinta a la de su camión → deja el camión
        if (c && etiquetaPlanta(c.planta) !== valor)
            aviso = `Move ${d.nombre} to ${valor}?\n\nTruck #${c.numero} is at ${etiquetaPlanta(c.planta)}, so ${d.nombre} will be removed from it.${notaUltimoConductor(d)}`;
    } else {
        body.numeroCamion = valor ? +valor : null;
        if (d.numeroCamion && body.numeroCamion !== d.numeroCamion) {
            const nota = notaUltimoConductor(d);
            if (nota) aviso = (body.numeroCamion
                ? `Move ${d.nombre} from truck #${d.numeroCamion} to truck #${body.numeroCamion}?`
                : `Remove ${d.nombre} from truck #${d.numeroCamion}?`) + nota;
        }
    }

    if (aviso && !confirm(aviso)) { renderDrivers(); return; }      // cancelado: vuelve al valor anterior
    accion("/api/mixer/conductor/actualizar", body);
};

window.agregarConductor = e => {
    e.preventDefault();
    const nombre = $("newDriverName").value.trim();
    if (!nombre) return toast("Enter the driver name", true);
    const camion = $("newDriverTruck").value;
    accion("/api/mixer/conductor/agregar", { nombre, planta: $("newDriverPlant").value, numeroCamion: camion ? +camion : null });
};

window.eliminarConductor = idConductor => {
    const d = data.conductores.find(x => x.idConductor === idConductor);
    if (!confirm(`Remove ${d ? d.nombre : "this driver"} from the list?${d ? notaUltimoConductor(d) : ""}`)) return;
    accion("/api/mixer/conductor/eliminar", { idConductor });
};

// ------------------------------------------------------------- navegación
function activar(vista) {
    document.querySelectorAll(".tab,.view").forEach(x => x.classList.remove("active"));
    document.querySelector(`[data-view="${vista}"]`).classList.add("active");
    $(vista).classList.add("active");
}
window.abrirPlanta = p => { plantaSeleccionada = p; activar("plants"); renderPlants(); window.scrollTo({ top: 0, behavior: "smooth" }); };
window.verTodasLasPlantas = () => { plantaSeleccionada = null; renderPlants(); };
window.abrirEstado = estado => { filtroEstado = estado; activar("trucks"); renderTrucks(); window.scrollTo({ top: 0, behavior: "smooth" }); };
window.setFiltroEstado = estado => { filtroEstado = estado; renderTrucks(); };
window.abrirConductores = () => { activar("drivers"); renderDrivers(); window.scrollTo({ top: 0, behavior: "smooth" }); };
window.renderTrucks = renderTrucks;
window.renderDrivers = renderDrivers;
window.$ = $;

document.querySelectorAll(".tab").forEach(b => b.onclick = () => {
    if (b.dataset.view === "plants") plantaSeleccionada = null;
    if (b.dataset.view === "trucks") filtroEstado = "all";
    activar(b.dataset.view);
    if (b.dataset.view === "plants") renderPlants();
    if (b.dataset.view === "trucks") renderTrucks();
    if (b.dataset.view === "report") cargarConfig();
});

$("reloadBtn").onclick = () => cargar("Data reloaded");
try { $("userName").value = localStorage.getItem(USER_KEY) || ""; } catch { }
$("userName").addEventListener("change", () => { try { localStorage.setItem(USER_KEY, $("userName").value.trim()); } catch { } });

cargar();
// La pestaña "Email report" puede estar oculta en la vista; si no está, no se
// pide su configuración (el envío programado igual corre en el servidor).
if (document.querySelector('.tab[data-view="report"]')) cargarConfig();
