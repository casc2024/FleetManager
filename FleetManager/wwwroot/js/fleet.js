// Fleet Manager — board, administration and daily performance (server-backed).
// Data model rebuilt from Fleet_Manager_v15; persistence is PostgreSQL via /api.
(function () {
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const fmt = (v) => v ? new Date(v).toLocaleString() : "—";
  const money = (n) => new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" }).format(Number(n) || 0);

  let D = { equipos: [], plantas: [], conductores: [], historial: [], snapshots: [] };
  let perfRows = [];
  let selectedLocation = "";

  function toast(msg, err) {
    const t = $("toast");
    t.textContent = msg; t.className = "toast show" + (err ? " error" : "");
    setTimeout(() => (t.className = "toast"), 2000);
  }

  async function api(url, body) {
    const res = await fetch(url, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body || {})
    });
    if (res.status === 401) { window.location.href = "/login"; return { ok: false }; }
    const data = await res.json().catch(() => null);
    return { ok: res.ok, data };
  }

  // ---------- helpers on the current fleet ----------
  const trucks = (fam) => D.equipos.filter((e) => e.grupo === "TRUCK " + fam).map((e) => e.numeroEquipo);
  const allTrucks = () => D.equipos.filter((e) => e.grupo.startsWith("TRUCK ")).map((e) => e.numeroEquipo);
  const allTrailers = () => D.equipos.filter((e) => e.grupo.startsWith("TRAILER ")).map((e) => e.numeroEquipo);
  const famOf = (grupo) => grupo.split(" ")[1];               // T | CT | BUMPER
  const isT = (e) => String(e.numeroEquipo).toUpperCase().startsWith("T") || e.grupo === "TRAILER T";
  const trailerForTruck = (truckNo) => {
    const r = D.equipos.find((x) => x.grupo.startsWith("TRAILER ") && String(x.numeroCamion) === String(truckNo));
    return r ? r.numeroEquipo : "";
  };
  const options = (arr, current, blank) =>
    `<option value="">${blank == null ? "—" : blank}</option>` +
    arr.map((x) => `<option ${String(x) === String(current || "") ? "selected" : ""}>${esc(x)}</option>`).join("");

  // ---------- load ----------
  async function loadAll() {
    try {
      const [d, p] = await Promise.all([fetch("/api/datos"), fetch("/api/perf")]);
      if (d.status === 401 || p.status === 401) { window.location.href = "/login"; return; }
      D = await d.json();
      perfRows = await p.json();
    } catch { toast("Could not load data.", true); return; }
    render();
    renderPerformance();
  }

  function render() {
    renderKpis(); renderLocations(); renderFilters(); renderTable();
    renderCharts(); renderAdmin(); renderHistory(); renderSnapshots();
  }

  // ---------- KPIs / locations / charts ----------
  function renderKpis() {
    const f = D.equipos, t = f.length, ok = f.filter((x) => x.estado === "OK").length, down = f.filter((x) => x.estado === "DOWN").length;
    $("kTotal").textContent = t; $("kOk").textContent = ok; $("kDown").textContent = down;
    $("kAvail").textContent = (t ? ok / t * 100 : 0).toFixed(1) + "%";
    $("kCement").textContent = f.filter((x) => x.carga === "CEMENT").length;
    $("kAsh").textContent = f.filter((x) => x.carga === "ASH").length;
    $("kEmpty").textContent = f.filter((x) => x.carga === "EMPTY").length;
  }

  function renderLocations() {
    $("locationGrid").innerHTML = D.plantas.map((l) => {
      const rows = D.equipos.filter((x) => x.ubicacion === l);
      const ok = rows.filter((x) => x.estado === "OK").length, down = rows.filter((x) => x.estado === "DOWN").length;
      return `<div class="loc-card ${selectedLocation === l ? "active" : ""}" data-loc="${esc(l)}"><b>${esc(l)}</b><small>Total ${rows.length}<br>OK ${ok} · DOWN ${down}</small></div>`;
    }).join("");
    document.querySelectorAll(".loc-card").forEach((c) => c.onclick = () => {
      selectedLocation = selectedLocation === c.dataset.loc ? "" : c.dataset.loc;
      $("fLoc").value = selectedLocation; renderTable(); renderLocations();
    });
  }

  function drawBars(canvasId, items) {
    const c = $(canvasId), ctx = c.getContext("2d"); ctx.clearRect(0, 0, c.width, c.height);
    const max = Math.max(1, ...items.map((x) => x.v)), base = c.height - 35, w = c.width / items.length;
    ctx.font = "12px Segoe UI"; ctx.textAlign = "center";
    items.forEach((it, i) => {
      const h = (it.v / max) * (c.height - 70), x = i * w + w * 0.2, y = base - h, bw = w * 0.6;
      ctx.fillStyle = it.color; ctx.fillRect(x, y, bw, h);
      ctx.fillStyle = "#1d2733"; ctx.fillText(it.v, x + bw / 2, y - 8);
      ctx.fillStyle = "#6b7785"; ctx.fillText(it.label, x + bw / 2, base + 18);
    });
  }
  function renderCharts() {
    const f = D.equipos;
    drawBars("statusChart", [{ label: "OK", v: f.filter((x) => x.estado === "OK").length, color: "#18864b" },
                             { label: "DOWN", v: f.filter((x) => x.estado === "DOWN").length, color: "#c33d3d" }]);
    drawBars("loadChart", [{ label: "CEMENT", v: f.filter((x) => x.carga === "CEMENT").length, color: "#2f6fb3" },
                           { label: "ASH", v: f.filter((x) => x.carga === "ASH").length, color: "#d68124" },
                           { label: "EMPTY", v: f.filter((x) => x.carga === "EMPTY").length, color: "#6b7280" }]);
  }

  // ---------- board ----------
  function renderFilters() {
    const sel = $("fLoc"), cur = sel.value;
    sel.innerHTML = '<option value="">All Locations</option>' + D.plantas.map((x) => `<option>${esc(x)}</option>`).join("");
    sel.value = D.plantas.includes(cur) ? cur : "";
  }
  function filtered() {
    const q = $("search").value.toLowerCase().trim(), l = $("fLoc").value || selectedLocation,
      s = $("fStatus").value, ld = $("fLoad").value;
    return D.equipos.filter((r) => {
      const pull = trailerForTruck(r.numeroEquipo);
      return (!q || r.numeroEquipo.toLowerCase().includes(q) || String(r.numeroCamion || "").toLowerCase().includes(q) || pull.toLowerCase().includes(q))
        && (!l || r.ubicacion === l) && (!s || r.estado === s) && (!ld || r.carga === ld);
    });
  }
  function truckSelector(r) {
    if (r.grupo.startsWith("TRAILER ")) {
      // Any trailer may take any truck, regardless of family (T / CT / Bumper).
      return `<select class="truck">${options(allTrucks(), r.numeroCamion, "No Truck")}</select>`;
    }
    const t = trailerForTruck(r.numeroEquipo);
    return `<span class="auto-pair">${t ? esc(t) : "No Trailer"}</span>`;
  }
  function renderTable() {
    const rows = filtered();
    $("fleetBody").innerHTML = rows.map((r) => {
      const esCamion = r.grupo.startsWith("TRUCK ");
      const loadOpts = isT(r) ? ["EMPTY", "LOADED"] : ["CEMENT", "ASH", "EMPTY"];
      return `<tr data-eq="${esc(r.numeroEquipo)}">
        <td data-label="Equipment" class="locked">${esc(r.numeroEquipo)}</td>
        <td data-label="Group">${esc(r.grupo)}</td>
        <td data-label="Plant">${esCamion ? `<select class="truckPlant">${options(D.plantas, r.plantaCamion)}</select>` : '<span class="na">—</span>'}</td>
        <td data-label="Truck / Trailer">${truckSelector(r)}</td>
        <td data-label="Location"><select class="loc">${options(D.plantas, r.ubicacion)}</select></td>
        <td data-label="Status"><select class="status">${options(["OK", "DOWN"], r.estado)}</select></td>
        <td data-label="Load"><select class="load">${options(loadOpts, r.carga)}</select></td>
        <td data-label="Updated By">${esc(r.actualizadoPor || "—")}</td>
        <td data-label="Updated At">${esc(fmt(r.fechaActualizacion))}</td>
        <td data-label="Action" class="action"><button class="primary saveRow">Save</button></td></tr>`;
    }).join("");
    $("rowCount").textContent = `${rows.length} of ${D.equipos.length} units`;
    document.querySelectorAll(".saveRow").forEach((b) => b.onclick = () => saveRow(b.closest("tr")));
  }
  async function saveRow(tr) {
    const eq = tr.dataset.eq;
    const truckEl = tr.querySelector(".truck"), plantEl = tr.querySelector(".truckPlant");
    const body = {
      numeroEquipo: eq,
      camion: truckEl ? truckEl.value : null,
      plantaCamion: plantEl ? plantEl.value : null,
      ubicacion: tr.querySelector(".loc").value,
      estado: tr.querySelector(".status").value,
      carga: tr.querySelector(".load").value
    };
    const { ok, data } = await api("/api/guardar", body);
    toast(ok ? (data && data.mensaje) || "Saved." : (data && data.mensaje) || "Could not save.", !ok);
    if (ok) await loadAll();
  }

  // ---------- administration ----------
  function renderAdmin() {
    renderRemoveEquipment();
    $("plantList").innerHTML = D.plantas.map((p) =>
      `<span class="auto-pair">${esc(p)} <button class="link-remove delPlant" data-p="${esc(p)}">Remove</button></span>`).join("");
    document.querySelectorAll(".delPlant").forEach((b) => b.onclick = async () => {
      if (!confirm(`Remove plant ${b.dataset.p}? Assigned equipment and drivers will be unassigned.`)) return;
      const { ok, data } = await api("/api/planta/eliminar", { codigo: b.dataset.p });
      toast(ok ? "Plant removed." : (data && data.mensaje) || "Error", !ok); if (ok) await loadAll();
    });

    const truckNums = allTrucks();
    $("driverBody").innerHTML = D.conductores.map((d) =>
      `<tr><td>${esc(d.nombre)}</td>
        <td><select class="dPlant" data-id="${d.idConductor}">${options(D.plantas, d.planta)}</select></td>
        <td><select class="dTruck" data-id="${d.idConductor}">${options(truckNums, d.camion)}</select></td>
        <td><button class="danger-btn delDriver" data-id="${d.idConductor}">Remove</button></td></tr>`).join("")
      || `<tr><td colspan="4" style="text-align:center;color:var(--muted);padding:16px">No drivers yet.</td></tr>`;
    document.querySelectorAll(".dPlant").forEach((e) => e.onchange = () => updateDriver(e.dataset.id));
    document.querySelectorAll(".dTruck").forEach((e) => e.onchange = () => updateDriver(e.dataset.id));
    document.querySelectorAll(".delDriver").forEach((e) => e.onclick = async () => {
      if (!confirm("Remove driver?")) return;
      const { ok } = await api("/api/conductor/eliminar", { idConductor: Number(e.dataset.id) });
      if (ok) { toast("Driver removed."); await loadAll(); }
    });
  }
  async function updateDriver(id) {
    const plant = document.querySelector(`.dPlant[data-id="${id}"]`).value;
    const truck = document.querySelector(`.dTruck[data-id="${id}"]`).value;
    const { ok, data } = await api("/api/conductor/actualizar", { idConductor: Number(id), planta: plant, camion: truck });
    if (!ok) toast((data && data.mensaje) || "Could not update driver.", true);
    await loadAll();  // reload either way: on rejection this reverts the dropdown to the stored value
  }
  function renderRemoveEquipment() {
    const type = $("removeType").value, el = $("removeEquipment"), old = el.value;
    const rows = D.equipos.filter((r) => r.grupo.startsWith(type + " "))
      .sort((a, b) => a.numeroEquipo.localeCompare(b.numeroEquipo, undefined, { numeric: true }));
    el.innerHTML = '<option value="">Select equipment…</option>' +
      rows.map((r) => `<option value="${esc(r.numeroEquipo)}">${esc(r.numeroEquipo)} — ${esc(r.grupo)}</option>`).join("");
    if (rows.some((r) => r.numeroEquipo === old)) el.value = old;
  }
  function wireAdmin() {
    $("addEntity").onclick = async () => {
      const type = $("addType").value, number = $("addNumber").value.trim();
      if (!number) return toast("Enter equipment number or driver name", true);
      const r = type === "DRIVER"
        ? await api("/api/conductor/agregar", { nombre: number })
        : await api("/api/equipo/agregar", { numeroEquipo: number, grupo: type });
      if (r.ok) { $("addNumber").value = ""; toast("Added."); await loadAll(); }
      else toast((r.data && r.data.mensaje) || "Could not add.", true);
    };
    $("removeType").onchange = renderRemoveEquipment;
    $("removeEntity").onclick = async () => {
      const eq = $("removeEquipment").value;
      if (!eq) return toast("Select equipment to remove", true);
      if (!confirm(`Remove ${eq}? This also clears its assignments.`)) return;
      const { ok, data } = await api("/api/equipo/eliminar", { numeroEquipo: eq });
      toast(ok ? "Equipment removed." : (data && data.mensaje) || "Error", !ok); if (ok) await loadAll();
    };
    $("addPlant").onclick = async () => {
      const v = $("plantName").value.trim();
      if (!v) return;
      const { ok, data } = await api("/api/planta/agregar", { codigo: v });
      if (ok) { $("plantName").value = ""; toast("Plant added."); await loadAll(); }
      else toast((data && data.mensaje) || "Could not add plant.", true);
    };
  }

  // ---------- history / snapshots ----------
  function renderHistory() {
    const h = $("history");
    h.innerHTML = D.historial.length
      ? D.historial.slice(0, 30).map((x) => `<div class="hist"><b>${esc(x.numeroEquipo)}</b> · ${esc(x.campo)}: <b>${esc(x.valorAnterior || "—")}</b> → <b>${esc(x.valorNuevo || "—")}</b> · ${esc(x.usuario)} · ${esc(fmt(x.fecha))}</div>`).join("")
      : '<div class="hist">No changes yet.</div>';
  }
  function renderSnapshots() {
    $("snapshotBody").innerHTML = D.snapshots.map((s) =>
      `<tr><td data-label="Date">${esc(String(s.fecha).slice(0, 10))}</td><td data-label="Total">${s.total}</td><td data-label="OK">${s.ok}</td><td data-label="DOWN">${s.down}</td><td data-label="Availability">${Number(s.disponibilidad).toFixed(1)}%</td><td data-label="CEMENT">${s.cement}</td><td data-label="ASH">${s.ash}</td><td data-label="EMPTY">${s.empty}</td></tr>`).join("");
  }

  // ---------- daily performance ----------
  const isoDate = (d) => [d.getFullYear(), String(d.getMonth() + 1).padStart(2, "0"), String(d.getDate()).padStart(2, "0")].join("-");
  const monday = (s) => { const d = new Date(s + "T12:00:00"); d.setDate(d.getDate() - (d.getDay() + 6) % 7); return isoDate(d); };
  const weekDays = (start) => { const d = new Date(start + "T12:00:00"); return Array.from({ length: 7 }, (_, i) => { const v = new Date(d); v.setDate(v.getDate() + i); return isoDate(v); }); };
  const perfColor = (n) => Number(n) >= 1200 ? "perf-green" : Number(n) >= 500 ? "perf-yellow" : "perf-red";

  function perfOptions() {
    const truck = $("perfTruck").value, driver = $("perfDriver").value, trailer = $("perfTrailer").value;
    $("perfTruck").innerHTML = options(allTrucks(), truck, "Select Truck");
    $("perfTrailer").innerHTML = options(allTrailers(), trailer, "No Trailer");
    $("perfDriver").innerHTML = options(D.conductores.map((d) => d.nombre), driver, "Select Driver");
    if (!$("perfTrailer").value) autoPerfTrailer();
  }
  function autoPerfTrailer() { const t = trailerForTruck($("perfTruck").value); if (t) $("perfTrailer").value = t; }

  async function savePerformance() {
    const body = {
      fecha: $("perfDate").value, tipo: $("perfType").value, camion: $("perfTruck").value,
      remolque: $("perfTrailer").value, conductor: $("perfDriver").value,
      cargas: Number($("perfLoads").value), ingreso: Number($("perfRevenue").value)
    };
    if (!body.fecha || !body.camion || !body.conductor) return toast("Select date, truck and driver", true);
    if (!Number.isInteger(body.cargas) || body.cargas < 0 || !Number.isFinite(body.ingreso) || body.ingreso < 0)
      return toast("Enter valid loads and revenue", true);
    const existing = perfRows.find((x) => x.fecha === body.fecha && x.tipo === body.tipo && x.camion === body.camion);
    if (existing && !confirm("Replace the existing daily result for this truck?")) return;
    const { ok, data } = await api("/api/perf/guardar", body);
    if (ok) { $("perfReportDate").value = body.fecha; $("perfWeek").value = monday(body.fecha); perfRows = await (await fetch("/api/perf")).json(); renderPerformance(); toast("Daily result saved"); }
    else toast((data && data.mensaje) || "Could not save", true);
  }
  async function deletePerformance(id) {
    if (!confirm("Delete this daily result?")) return;
    const { ok } = await api("/api/perf/eliminar", { idPerf: id });
    if (ok) { perfRows = await (await fetch("/api/perf")).json(); renderPerformance(); }
  }
  window.deletePerformance = deletePerformance;

  function renderPerformance() {
    perfOptions();
    const date = $("perfReportDate").value;
    const rows = perfRows.filter((r) => r.fecha === date).sort((a, b) => a.tipo.localeCompare(b.tipo) || a.camion.localeCompare(b.camion));
    $("perfDailyRows").innerHTML = rows.map((r) =>
      `<tr><td>${esc(r.tipo)}</td><td>${esc(r.camion)}</td><td>${esc(r.remolque || "—")}</td><td>${esc(r.conductor || "—")}</td><td>${r.cargas}</td><td class="${perfColor(r.ingreso)}">${money(r.ingreso)}</td><td class="${perfColor(r.ingreso)}">${r.ingreso >= 1200 ? "TARGET MET" : r.ingreso >= 500 ? "BELOW TARGET" : "CRITICAL"}</td><td><button class="secondary" onclick="deletePerformance(${r.idPerf})">Delete</button></td></tr>`).join("")
      || '<tr><td colspan="8" style="text-align:center;color:var(--muted)">No results for this date.</td></tr>';

    const dates = weekDays(monday($("perfWeek").value)), w = perfRows.filter((r) => dates.includes(r.fecha));
    const total = w.reduce((a, r) => a + Number(r.ingreso), 0), loads = w.reduce((a, r) => a + r.cargas, 0), met = w.filter((r) => r.ingreso >= 1200).length;
    $("perfSummary").innerHTML = [["Weekly Revenue", money(total)], ["Weekly Loads", loads], ["Truck-Days", w.length], ["Target Met", w.length ? Math.round(met / w.length * 100) + "%" : "0%"]]
      .map(([a, b]) => `<div class="kpi"><span>${a}</span><strong>${b}</strong></div>`).join("");

    renderWeeklyType("PNEUMATIC", "perfChartP", "perfWeekP", dates, w);
    renderWeeklyType("END DUMP", "perfChartE", "perfWeekE", dates, w);
    renderWeeklyType("BUMPER TRAILER", "perfChartB", "perfWeekB", dates, w);
  }
  function renderWeeklyType(type, canvasId, tableId, dates, rows) {
    const day = dates.map((d) => {
      const a = rows.filter((r) => r.tipo === type && r.fecha === d);
      return { date: d, revenue: a.reduce((s, r) => s + Number(r.ingreso), 0), loads: a.reduce((s, r) => s + r.cargas, 0), count: a.length, met: a.filter((r) => r.ingreso >= 1200).length };
    });
    const total = day.reduce((s, r) => s + r.revenue, 0), met = day.reduce((s, r) => s + r.met, 0), n = day.reduce((s, r) => s + r.count, 0);
    $(tableId).innerHTML = `<div class="perf-totals"><b>Weekly total: ${money(total)}</b> · ${day.reduce((s, r) => s + r.loads, 0)} loads · ${n ? Math.round(met / n * 100) : 0}% truck-days ≥ $1,200</div>`;
    const c = $(canvasId), ctx = c.getContext("2d"); ctx.clearRect(0, 0, c.width, c.height);
    const max = Math.max(1200, ...day.map((r) => r.revenue)), base = 210, step = 86;
    ctx.font = "12px Segoe UI"; ctx.textAlign = "center"; ctx.strokeStyle = "#cfd8e3";
    ctx.beginPath(); ctx.moveTo(40, base); ctx.lineTo(680, base); ctx.stroke();
    day.forEach((r, i) => {
      const h = r.revenue / max * 155, x = 52 + i * step;
      ctx.fillStyle = r.revenue >= 1200 ? "#18864b" : r.revenue >= 500 ? "#e2b82d" : "#c33d3d";
      ctx.fillRect(x, base - h, 45, h);
      ctx.fillStyle = "#1d2733"; ctx.fillText("$" + Math.round(r.revenue).toLocaleString(), x + 22, base - h - 9);
      ctx.fillStyle = "#667487"; ctx.fillText(r.date.slice(5), x + 22, base + 20);
    });
  }
  function exportPerf(scope) {
    const dates = weekDays(monday($("perfWeek").value));
    const rows = perfRows.filter((r) => scope === "daily" ? r.fecha === $("perfReportDate").value : dates.includes(r.fecha));
    const q = (v) => '"' + String(v == null ? "" : v).replaceAll('"', '""') + '"';
    const csv = [["Date", "Type", "Truck", "Trailer", "Driver", "Loads", "Revenue USD", "Target Status"],
      ...rows.map((r) => [r.fecha, r.tipo, r.camion, r.remolque, r.conductor, r.cargas, r.ingreso, r.ingreso >= 1200 ? "GREEN" : r.ingreso >= 500 ? "YELLOW" : "RED"])]
      .map((r) => r.map(q).join(",")).join("\r\n");
    const url = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" })), a = document.createElement("a");
    a.href = url; a.download = "fleet_" + scope + "_performance.csv"; a.click(); setTimeout(() => URL.revokeObjectURL(url), 500);
  }

  // ---------- wiring ----------
  function wire() {
    ["search", "fLoc", "fStatus", "fLoad"].forEach((id) => $(id).addEventListener("input", renderTable));
    $("newSnapshot").onclick = async () => { const r = await api("/api/snapshot", {}); if (r.ok) { toast("Weekly snapshot saved"); await loadAll(); } };
    $("exportCsv").onclick = () => { window.location.href = "/api/export.csv"; };
    wireAdmin();

    const today = isoDate(new Date());
    ["perfDate", "perfReportDate"].forEach((id) => $(id).value = today);
    $("perfWeek").value = monday(today);
    $("perfSave").onclick = savePerformance;
    $("perfTruck").onchange = autoPerfTrailer;
    ["perfDate", "perfReportDate", "perfWeek", "perfType"].forEach((id) => $(id).addEventListener("change", renderPerformance));
    $("perfExport").onclick = () => exportPerf("daily");
    $("perfExportWeek").onclick = () => exportPerf("weekly");
  }

  wire();
  loadAll();
})();
