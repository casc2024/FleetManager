// Fleet Manager - lógica del tablero (misma funcionalidad que la página original,
// pero la data vive en PostgreSQL vía /api/*).
let data = { equipos: [], catalogos: { ubicaciones: [], estados: [], cargas: [], camionesPorFamilia: {} }, historial: [], snapshots: [] };
let selectedLocation = "";
const USER_KEY = "fleet_manager_user", PAGE_KEY = "fleet_manager_page_size";
let page = 1, pageSize = 10;

const $ = id => document.getElementById(id);
function fmt(v) { return v ? new Date(v).toLocaleString() : "—"; }
function esc(s) { return String(s ?? "").replace(/[&<>"']/g, m => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[m])); }
function toast(msg, error) { const t = $("toast"); t.textContent = msg; t.classList.toggle("error", !!error); t.classList.add("show"); setTimeout(() => t.classList.remove("show"), 2200); }
function usuario() { return ($("userName").value || "").trim() || "Owner"; }
function options(arr, current, blank = "—") { return `<option value="">${blank}</option>` + arr.map(x => `<option ${String(x) === String(current) ? "selected" : ""}>${esc(x)}</option>`).join(""); }
function codigos(lista) { return lista.map(x => x.codigo); }
function isTrailer(r) { return !r.esCamion; }

// Desglose de los KPIs por tipo de equipo (grupo de la base: es_camion + familia)
const GRUPOS_KPI = [
    { etiqueta: "CT", test: r => !r.esCamion && r.familia === "CT" },
    { etiqueta: "T", test: r => !r.esCamion && r.familia === "T" },
    { etiqueta: "ED Trucks", test: r => r.esCamion && r.familia === "T" },
    { etiqueta: "Pneumatic Trucks", test: r => r.esCamion && r.familia === "CT" }
];
// "OK" se muestra como "Operational" en toda la interfaz (el valor guardado sigue siendo OK)
function etiquetaEstado(codigo) { return codigo === "OK" ? "Operational" : codigo; }

// El Load (CEMENT / ASH / EMPTY) solo aplica a los equipos neumáticos (familia CT).
// Los de familia T (T trailers y ED Trucks) no llevan carga: se muestra "—".
function aplicaCarga(r) { return r.familia === "CT"; }

// Color de las barras de Load (prevalece sobre el color guardado en la base)
const COLOR_CARGA = { CEMENT: "#2f6fb3", ASH: "#d68124", EMPTY: "#c33d3d" };

async function api(url, body) {
    const res = await fetch(url, body ? { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) } : undefined);
    const json = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(json.mensaje || json.title || ("HTTP " + res.status));
    return json;
}

async function loadData() {
    try { data = await api("/api/datos"); render(); }
    catch (e) { toast("Error loading data: " + e.message, true); }
}

function truckSelector(r) {
    if (isTrailer(r)) {
        const trucks = data.catalogos.camionesPorFamilia[r.familia] || [];
        return `<select class="truck">${options(trucks, r.numeroCamion, "No Truck")}</select>`;
    }
    return `<span class="auto-pair">${r.numeroRemolque ? esc(r.numeroRemolque) : "No Trailer"}</span>`;
}

function setupFilters() {
    ["search", "fLoc", "fStatus", "fLoad"].forEach(id => $(id).addEventListener("input", () => { page = 1; renderTable(); }));
    try { const ps = parseInt(localStorage.getItem(PAGE_KEY)); if ([10, 20, 30, 50, 100].includes(ps)) pageSize = ps; } catch { }
    $("pageSize").value = String(pageSize);
    $("pageSize").addEventListener("change", () => {
        pageSize = parseInt($("pageSize").value) || 10; page = 1; renderTable();
        try { localStorage.setItem(PAGE_KEY, String(pageSize)); } catch { }
    });
    $("pgFirst").onclick = () => goPage(1);
    $("pgPrev").onclick = () => goPage(page - 1);
    $("pgNext").onclick = () => goPage(page + 1);
    $("pgLast").onclick = () => goPage(Infinity);
    $("newSnapshot").onclick = makeSnapshot;
    $("exportCsv").onclick = () => { window.location.href = "/api/export.csv"; };
    try { $("userName").value = localStorage.getItem(USER_KEY) || ""; } catch { }
    $("userName").addEventListener("change", () => { try { localStorage.setItem(USER_KEY, $("userName").value.trim()); } catch { } });
}

function renderFilterOptions() {
    const keep = (id, html) => { const el = $(id), v = el.value; el.innerHTML = html; el.value = v; };
    keep("fLoc", '<option value="">All Locations</option>' + codigos(data.catalogos.ubicaciones).map(x => `<option>${esc(x)}</option>`).join(""));
    keep("fStatus", '<option value="">All Status</option>' + codigos(data.catalogos.estados).map(x => `<option>${esc(x)}</option>`).join(""));
    keep("fLoad", '<option value="">All Loads</option>' + codigos(data.catalogos.cargas).map(x => `<option>${esc(x)}</option>`).join(""));
}

function filtered() {
    const q = $("search").value.toLowerCase().trim(), l = $("fLoc").value || selectedLocation, s = $("fStatus").value, ld = $("fLoad").value;
    return data.equipos.filter(r => {
        const pair = String(isTrailer(r) ? (r.numeroCamion || "") : (r.numeroRemolque || "")).toLowerCase();
        return (!q || r.numeroEquipo.toLowerCase().includes(q) || pair.includes(q))
            && (!l || r.ubicacion === l) && (!s || r.estado === s) && (!ld || r.carga === ld);
    });
}

function render() { renderFilterOptions(); renderKpis(); renderLocations(); renderTable(); renderCharts(); renderHistory(); renderSnapshots(); }

function desglose(id, filtro) {
    $(id).innerHTML = GRUPOS_KPI.map(g => {
        const n = data.equipos.filter(r => g.test(r) && filtro(r)).length;
        return `<div><span>${esc(g.etiqueta)}</span><b>${n}</b></div>`;
    }).join("");
}

function renderKpis() {
    const f = data.equipos, t = f.length, ok = f.filter(x => x.estado === "OK").length, down = f.filter(x => x.estado === "DOWN").length;
    $("kTotal").textContent = t; $("kOk").textContent = ok; $("kDown").textContent = down;
    desglose("bTotal", () => true);
    desglose("bOk", r => r.estado === "OK");
    desglose("bDown", r => r.estado === "DOWN");
    $("kAvail").textContent = (t ? ok / t * 100 : 0).toFixed(1) + "%";
    const conCarga = f.filter(aplicaCarga);
    $("kCement").textContent = conCarga.filter(x => x.carga === "CEMENT").length;
    $("kAsh").textContent = conCarga.filter(x => x.carga === "ASH").length;
    $("kEmpty").textContent = conCarga.filter(x => x.carga === "EMPTY").length;
}

function renderLocations() {
    $("locationGrid").innerHTML = codigos(data.catalogos.ubicaciones).map(l => {
        const rows = data.equipos.filter(x => x.ubicacion === l),
            ok = rows.filter(x => x.estado === "OK").length,
            down = rows.filter(x => x.estado === "DOWN").length;
        // Desglose por tipo de equipo: cuántos operativos y cuántos down en esta planta
        const detalle = GRUPOS_KPI.map(g => {
            const grupo = rows.filter(g.test);
            const gOk = grupo.filter(x => x.estado === "OK").length, gDown = grupo.filter(x => x.estado === "DOWN").length;
            return `<span class="lb">${esc(g.etiqueta)}</span><span class="v ok">${gOk}</span><span class="v dn">${gDown}</span>`;
        }).join("");
        return `<div class="loc-card ${selectedLocation === l ? "active" : ""}" data-loc="${esc(l)}">
<b>${esc(l)}</b><small>Total ${rows.length}<br>Operational ${ok} · Down ${down}</small>
<div class="loc-break"><span class="lb hd"></span><span class="hd ok">OP</span><span class="hd dn">DOWN</span>${detalle}</div></div>`;
    }).join("");
    document.querySelectorAll(".loc-card").forEach(c => c.onclick = () => {
        selectedLocation = selectedLocation === c.dataset.loc ? "" : c.dataset.loc;
        $("fLoc").value = selectedLocation; render();
    });
}

function goPage(n) {
    const total = Math.max(1, Math.ceil(filtered().length / pageSize));
    page = Math.min(Math.max(1, n), total);
    renderTable();
    $("fleetBody").closest(".panel").scrollIntoView({ behavior: "smooth", block: "start" });
}

function renderTable() {
    const all = filtered();
    const totalPages = Math.max(1, Math.ceil(all.length / pageSize));
    if (page > totalPages) page = totalPages;
    const start = (page - 1) * pageSize;
    const rows = all.slice(start, start + pageSize);
    $("fleetBody").innerHTML = rows.map(r => `<tr data-eq="${esc(r.numeroEquipo)}">
<td data-label="Equipment">${esc(r.numeroEquipo)}</td>
<td data-label="Group">${esc(r.grupo)}</td>
<td data-label="Truck / Trailer">${truckSelector(r)}</td>
<td data-label="Location"><select class="loc">${options(codigos(data.catalogos.ubicaciones), r.ubicacion)}</select></td>
<td data-label="Status"><select class="status">${options(codigos(data.catalogos.estados), r.estado)}</select></td>
<td data-label="Load">${aplicaCarga(r) ? `<select class="load">${options(codigos(data.catalogos.cargas), r.carga)}</select>` : '<span class="na">—</span>'}</td>
<td data-label="Updated By">${esc(r.actualizadoPor || "—")}</td>
<td data-label="Updated At">${esc(fmt(r.fechaActualizacion))}</td>
<td data-label="Action" class="action"><button class="primary saveRow">Save</button></td></tr>`).join("");
    $("rowCount").textContent = all.length
        ? `Showing ${start + 1}–${start + rows.length} of ${all.length} equipment${all.length !== data.equipos.length ? ` (filtered from ${data.equipos.length})` : ""}`
        : "No equipment matches the filters";
    $("pageInfo").textContent = `${page} / ${totalPages}`;
    $("pgFirst").disabled = $("pgPrev").disabled = page <= 1;
    $("pgNext").disabled = $("pgLast").disabled = page >= totalPages;
    document.querySelectorAll(".saveRow").forEach(b => b.onclick = () => saveRow(b.closest("tr"), b));
}

async function saveRow(tr, btn) {
    const truckEl = tr.querySelector(".truck"), loadEl = tr.querySelector(".load");
    const body = {
        numeroEquipo: tr.dataset.eq,
        camion: truckEl ? truckEl.value : null,
        ubicacion: tr.querySelector(".loc").value,
        estado: tr.querySelector(".status").value,
        carga: loadEl ? loadEl.value : null,
        usuario: usuario()
    };
    btn.disabled = true;
    try {
        const r = await api("/api/guardar", body);
        if (r.cambios === 0) { toast("No changes"); btn.disabled = false; return; }
        await loadData();
        toast(r.mensaje || "Saved");
    } catch (e) { toast("Error: " + e.message, true); btn.disabled = false; }
}

function drawBars(canvasId, items) {
    const c = $(canvasId), ctx = c.getContext("2d");
    ctx.clearRect(0, 0, c.width, c.height);
    const max = Math.max(1, ...items.map(x => x.v)), base = c.height - 35, w = c.width / items.length;
    ctx.font = "12px Segoe UI"; ctx.textAlign = "center";
    items.forEach((it, i) => {
        const h = (it.v / max) * (c.height - 70), x = i * w + w * .2, y = base - h, bw = w * .6;
        ctx.fillStyle = it.color; ctx.fillRect(x, y, bw, h);
        ctx.fillStyle = "#1d2733"; ctx.fillText(it.v, x + bw / 2, y - 8);
        ctx.fillStyle = "#6b7785"; ctx.fillText(it.label, x + bw / 2, base + 18);
    });
}

function drawDonut(id, items) {
    const c = $(id), ctx = c.getContext("2d"), w = c.width, h = c.height;
    const cx = w / 2, cy = h / 2, r = Math.min(w, h) / 2 - 6, inner = r * 0.64;
    const total = items.reduce((s, x) => s + x.v, 0);
    ctx.clearRect(0, 0, w, h);
    let a = -Math.PI / 2;
    if (total === 0) { ctx.beginPath(); ctx.arc(cx, cy, r, 0, Math.PI * 2); ctx.fillStyle = "#e8eff7"; ctx.fill(); }
    items.forEach(it => {
        const ang = (it.v / total) * Math.PI * 2;
        if (!ang) return;
        ctx.beginPath(); ctx.moveTo(cx, cy); ctx.arc(cx, cy, r, a, a + ang); ctx.closePath();
        ctx.fillStyle = it.color; ctx.fill();
        a += ang;
    });
    ctx.globalCompositeOperation = "destination-out";
    ctx.beginPath(); ctx.arc(cx, cy, inner, 0, Math.PI * 2); ctx.fill();
    ctx.globalCompositeOperation = "source-over";
    const pct = total ? items[0].v / total * 100 : 0;
    ctx.textAlign = "center";
    ctx.fillStyle = "#1d2733"; ctx.font = "bold 62px Segoe UI, Arial";
    ctx.fillText(pct.toFixed(1) + "%", cx, cy + 10);
    ctx.fillStyle = "#6b7785"; ctx.font = "26px Segoe UI, Arial";
    ctx.fillText(items[0].v + " of " + total, cx, cy + 48);
}

function renderStatusBreak() {
    const f = data.equipos;
    const ok = c => c.estado === "OK", down = c => c.estado === "DOWN";
    const celda = (n, base) => `<div class="num"><b>${n}</b><i>${base ? (n / base * 100).toFixed(0) + "%" : "—"}</i></div>`;
    let html = '<div class="hd"></div><div class="hd ok">Operational</div><div class="hd down">Down</div>';
    GRUPOS_KPI.forEach(g => {
        const grupo = f.filter(g.test), t = grupo.length;
        html += `<div class="lbl">${esc(g.etiqueta)}</div>${celda(grupo.filter(ok).length, t)}${celda(grupo.filter(down).length, t)}`;
    });
    const t = f.length;
    html += `<div class="lbl tot">Total</div><div class="tot">${celda(f.filter(ok).length, t)}</div><div class="tot">${celda(f.filter(down).length, t)}</div>`;
    $("statusBreak").innerHTML = html;
}

function renderCharts() {
    const f = data.equipos;
    drawBars("statusChart", data.catalogos.estados.map(s => ({ label: etiquetaEstado(s.codigo), v: f.filter(x => x.estado === s.codigo).length, color: s.colorHex || "#2f6fb3" })));
    const nOk = f.filter(x => x.estado === "OK").length, nDown = f.filter(x => x.estado === "DOWN").length;
    const sinEstado = f.length - nOk - nDown;   // equipos sin Status, para que el % coincida con Availability
    const rueda = [{ label: "Operational", v: nOk, color: "#18864b" }, { label: "Down", v: nDown, color: "#c33d3d" }];
    if (sinEstado > 0) rueda.push({ label: "No status", v: sinEstado, color: "#d7dfe8" });
    drawDonut("statusDonut", rueda);
    renderStatusBreak();
    const conCarga = f.filter(aplicaCarga);
    drawBars("loadChart", data.catalogos.cargas.map(s => ({ label: s.codigo, v: conCarga.filter(x => x.carga === s.codigo).length, color: COLOR_CARGA[s.codigo] || s.colorHex || "#6b7280" })));
}

function renderHistory() {
    const h = $("history");
    h.innerHTML = data.historial.length
        ? data.historial.map(x => `<div class="hist"><b>${esc(x.numeroEquipo)}</b> · ${esc(x.campo)}: <b>${esc(x.valorAnterior || "—")}</b> → <b>${esc(x.valorNuevo || "—")}</b> · ${esc(x.usuario)} · ${esc(fmt(x.fecha))}</div>`).join("")
        : '<div class="hist">No changes yet.</div>';
}

async function makeSnapshot() {
    const b = $("newSnapshot"); b.disabled = true;
    try { await api("/api/snapshot", { usuario: usuario() }); await loadData(); toast("Weekly snapshot saved"); }
    catch (e) { toast("Error: " + e.message, true); }
    finally { b.disabled = false; }
}

function renderSnapshots() {
    $("snapshotBody").innerHTML = data.snapshots.length
        ? data.snapshots.map(s => `<tr><td>${esc(String(s.fecha).slice(0, 10))}</td><td>${s.total}</td><td>${s.ok}</td><td>${s.down}</td><td>${Number(s.disponibilidad).toFixed(1)}%</td><td>${s.cement}</td><td>${s.ash}</td><td>${s.empty}</td></tr>`).join("")
        : '<tr><td colspan="8" style="color:var(--muted)">No snapshots yet.</td></tr>';
}

setupFilters();
loadData();
