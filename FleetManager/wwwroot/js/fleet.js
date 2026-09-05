// Fleet Manager - lógica del tablero (misma funcionalidad que la página original,
// pero la data vive en PostgreSQL vía /api/*).
let data = { equipos: [], catalogos: { ubicaciones: [], estados: [], cargas: [], camionesPorFamilia: {} }, historial: [], snapshots: [] };
let selectedLocation = "";
const USER_KEY = "fleet_manager_user";

const $ = id => document.getElementById(id);
function fmt(v) { return v ? new Date(v).toLocaleString() : "—"; }
function esc(s) { return String(s ?? "").replace(/[&<>"']/g, m => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[m])); }
function toast(msg, error) { const t = $("toast"); t.textContent = msg; t.classList.toggle("error", !!error); t.classList.add("show"); setTimeout(() => t.classList.remove("show"), 2200); }
function usuario() { return ($("userName").value || "").trim() || "Owner"; }
function options(arr, current, blank = "—") { return `<option value="">${blank}</option>` + arr.map(x => `<option ${String(x) === String(current) ? "selected" : ""}>${esc(x)}</option>`).join(""); }
function codigos(lista) { return lista.map(x => x.codigo); }
function isTrailer(r) { return !r.esCamion; }

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
    ["search", "fLoc", "fStatus", "fLoad"].forEach(id => $(id).addEventListener("input", renderTable));
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

function renderKpis() {
    const f = data.equipos, t = f.length, ok = f.filter(x => x.estado === "OK").length, down = f.filter(x => x.estado === "DOWN").length;
    $("kTotal").textContent = t; $("kOk").textContent = ok; $("kDown").textContent = down;
    $("kAvail").textContent = (t ? ok / t * 100 : 0).toFixed(1) + "%";
    $("kCement").textContent = f.filter(x => x.carga === "CEMENT").length;
    $("kAsh").textContent = f.filter(x => x.carga === "ASH").length;
    $("kEmpty").textContent = f.filter(x => x.carga === "EMPTY").length;
}

function renderLocations() {
    $("locationGrid").innerHTML = codigos(data.catalogos.ubicaciones).map(l => {
        const rows = data.equipos.filter(x => x.ubicacion === l), ok = rows.filter(x => x.estado === "OK").length, down = rows.filter(x => x.estado === "DOWN").length;
        return `<div class="loc-card ${selectedLocation === l ? "active" : ""}" data-loc="${esc(l)}"><b>${esc(l)}</b><small>Total ${rows.length}<br>OK ${ok} · DOWN ${down}</small></div>`;
    }).join("");
    document.querySelectorAll(".loc-card").forEach(c => c.onclick = () => {
        selectedLocation = selectedLocation === c.dataset.loc ? "" : c.dataset.loc;
        $("fLoc").value = selectedLocation; render();
    });
}

function renderTable() {
    const rows = filtered();
    $("fleetBody").innerHTML = rows.map(r => `<tr data-eq="${esc(r.numeroEquipo)}">
<td data-label="Equipment">${esc(r.numeroEquipo)}</td>
<td data-label="Group">${esc(r.grupo)}</td>
<td data-label="Truck / Trailer">${truckSelector(r)}</td>
<td data-label="Location"><select class="loc">${options(codigos(data.catalogos.ubicaciones), r.ubicacion)}</select></td>
<td data-label="Status"><select class="status">${options(codigos(data.catalogos.estados), r.estado)}</select></td>
<td data-label="Load"><select class="load">${options(codigos(data.catalogos.cargas), r.carga)}</select></td>
<td data-label="Updated By">${esc(r.actualizadoPor || "—")}</td>
<td data-label="Updated At">${esc(fmt(r.fechaActualizacion))}</td>
<td data-label="Action" class="action"><button class="primary saveRow">Save</button></td></tr>`).join("");
    $("rowCount").textContent = `${rows.length} of ${data.equipos.length} equipment`;
    document.querySelectorAll(".saveRow").forEach(b => b.onclick = () => saveRow(b.closest("tr"), b));
}

async function saveRow(tr, btn) {
    const truckEl = tr.querySelector(".truck");
    const body = {
        numeroEquipo: tr.dataset.eq,
        camion: truckEl ? truckEl.value : null,
        ubicacion: tr.querySelector(".loc").value,
        estado: tr.querySelector(".status").value,
        carga: tr.querySelector(".load").value,
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

function renderCharts() {
    const f = data.equipos;
    drawBars("statusChart", data.catalogos.estados.map(s => ({ label: s.codigo, v: f.filter(x => x.estado === s.codigo).length, color: s.colorHex || "#2f6fb3" })));
    drawBars("loadChart", data.catalogos.cargas.map(s => ({ label: s.codigo, v: f.filter(x => x.carga === s.codigo).length, color: s.colorHex || "#6b7280" })));
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
