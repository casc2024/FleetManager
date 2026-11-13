// MaterialIQ — admin planning module over the "materialiq" schema.
(function () {
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const usd = (n) => (n == null ? "—" : "$" + Number(n).toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 }));
  const tn = (n) => (n == null ? "—" : Number(n).toLocaleString("en-US", { maximumFractionDigits: 1 }));
  const pct = (n) => (n == null ? "—" : Number(n).toFixed(1) + "%");
  const addDays = (iso, d) => { const dt = new Date(iso + "T00:00:00"); dt.setDate(dt.getDate() + d); return dt.toISOString().slice(0, 10); };
  const today = () => new Date().toISOString().slice(0, 10);
  const PER_PAGE = 10;

  // horizontal bars: items = [{label, value}]
  function barsHtml(items, fmt) {
    const max = Math.max(1, ...items.map((i) => i.value || 0));
    return `<div class="mq-bars">${items.length ? items.map((i) => `
      <div class="mq-bar"><span class="mq-bar-l" title="${esc(i.label)}">${esc(i.label)}</span>
        <span class="mq-bar-t"><span class="mq-bar-f" style="width:${i.value ? Math.max(3, i.value / max * 100) : 0}%"></span></span>
        <b>${fmt(i.value)}</b></div>`).join("") : `<div class="mq-empty">No data.</div>`}</div>`;
  }
  // 7-day sparkline: days = [{label, value(nullable)}]
  function sparkHtml(days) {
    const max = Math.max(1, ...days.map((d) => d.value || 0));
    return `<div class="mq-spark">${days.map((d) => `<div class="mq-spk">
      <span class="${d.value == null ? "pend" : ""}" style="height:${d.value == null ? 2 : Math.max(2, d.value / max * 60)}px"></span>
      <b>${d.value == null ? "·" : tn(d.value)}</b><small>${esc(d.label)}</small></div>`).join("")}</div>`;
  }

  const state = { plant: "", date: today(), tab: "overview", plants: [], materials: [], suppliers: [] };
  const STATUS = { pending: "Pending demand", low: "Low stock", ok: "Sufficient" };

  // ---- api -----------------------------------------------------------
  async function api(url, body) {
    const opt = body ? { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) } : {};
    const res = await fetch(url, opt);
    if (res.status === 401 || res.status === 403) { window.location.href = "/login"; throw new Error("auth"); }
    const data = await res.json().catch(() => null);
    if (!res.ok) throw Object.assign(new Error("http"), { data });
    return data;
  }
  function toast(msg, err) { const t = $("toast"); t.textContent = msg; t.className = "toast show" + (err ? " error" : ""); setTimeout(() => (t.className = "toast"), 2600); }
  const q = () => `fecha=${state.date}${state.plant ? "&plant=" + state.plant : ""}`;

  // ---- paginated table (10 / page) ----------------------------------
  // host: container element; cfg = { headHtml, rowsHtml:[], colspan, empty, wire(host) }
  function renderPaged(host, cfg) {
    let page = 0;
    const total = cfg.rowsHtml.length;
    const pages = Math.max(1, Math.ceil(total / PER_PAGE));
    function draw() {
      if (page > pages - 1) page = pages - 1;
      const slice = cfg.rowsHtml.slice(page * PER_PAGE, page * PER_PAGE + PER_PAGE).join("")
        || `<tr><td colspan="${cfg.colspan}" class="mq-empty">${esc(cfg.empty || "No records.")}</td></tr>`;
      host.innerHTML =
        `<div class="table-wrap board"><table class="mq-table"><thead>${cfg.headHtml}</thead><tbody>${slice}</tbody></table></div>` +
        (total > PER_PAGE
          ? `<div class="mq-pager">
               <button class="btn secondary" data-pg="prev" ${page === 0 ? "disabled" : ""}>‹ Prev</button>
               <span>Page ${page + 1} of ${pages} · ${total} records</span>
               <button class="btn secondary" data-pg="next" ${page >= pages - 1 ? "disabled" : ""}>Next ›</button></div>`
          : `<div class="mq-pager muted">${total} record${total === 1 ? "" : "s"}</div>`);
      if (cfg.wire) cfg.wire(host);
      host.querySelectorAll("[data-pg]").forEach((b) => b.addEventListener("click", () => {
        if (b.dataset.pg === "prev" && page > 0) page--;
        else if (b.dataset.pg === "next" && page < pages - 1) page++;
        draw();
      }));
    }
    draw();
  }

  // ---- shared validation --------------------------------------------
  const RE = {
    name: /^[\p{L}0-9 .,'&()\/#\-]{2,120}$/u,   // supplier / material / plant name, origin
    code: /^[A-Za-z0-9_\-]{2,40}$/,              // plant code
    matnum: /^[A-Za-z0-9\-]{1,10}$/,             // material number
    ticket: /^[\p{L}0-9 .,'&()\/#\-]{1,80}$/u    // ticket / reference
  };
  // Validate one field's raw string value. Returns an error message or null.
  function validateField(f, raw) {
    if (f.type === "checkbox") return null;
    const empty = raw === "" || raw == null;
    if (f.required && empty) return "Required.";
    if (empty) return null; // optional and blank is fine
    if (f.type === "number") {
      const n = Number(raw);
      if (!isFinite(n)) return "Must be a number.";
      if (f.integer && !Number.isInteger(n)) return "Must be a whole number.";
      const min = f.min != null ? f.min : 0;              // quantities/prices are never negative
      if (n < min) return `Must be ≥ ${min}.`;
      if (f.gt0 && n <= 0) return "Must be greater than zero.";
      if (f.max != null && n > f.max) return `Must be ≤ ${f.max}.`;
    } else if (f.type === "date") {
      if (!/^\d{4}-\d{2}-\d{2}$/.test(raw) || isNaN(Date.parse(raw))) return "Invalid date (yyyy-mm-dd).";
    } else if (f.type === "text" || f.type === "textarea") {
      const max = f.maxlength || (f.type === "textarea" ? 300 : 120);
      if (raw.length > max) return `Too long (max ${max}).`;
      if (/[\u0000-\u001f]/.test(raw)) return "Contains invalid control characters.";
      if (f.pattern && !f.pattern.test(raw)) return "Contains invalid characters.";
    }
    return null;
  }

  // ---- generic modal form -------------------------------------------
  function openForm(title, fields, onSubmit) {
    const dlg = $("mqDialog");
    $("mqDlgTitle").textContent = title;
    $("mqDlgError").textContent = "";
    $("mqDlgFields").innerHTML = fields.map((f) => {
      const cls = f.colspan === 2 ? "col-2" : "";
      const ml = f.maxlength ? ` maxlength="${f.maxlength}"` : (f.type === "text" ? ` maxlength="120"` : "");
      let input;
      if (f.type === "select")
        input = `<select id="mqf_${f.id}">${(f.options || []).map((o) => `<option value="${esc(o.value)}" ${String(o.value) === String(f.value ?? "") ? "selected" : ""}>${esc(o.label)}</option>`).join("")}</select>`;
      else if (f.type === "textarea")
        input = `<textarea id="mqf_${f.id}" maxlength="${f.maxlength || 300}">${esc(f.value ?? "")}</textarea>`;
      else if (f.type === "checkbox")
        input = `<label class="switch"><input type="checkbox" id="mqf_${f.id}" ${f.value ? "checked" : ""}> <span>${esc(f.hint || "Yes")}</span></label>`;
      else if (f.type === "number")
        input = `<input id="mqf_${f.id}" type="number" ${f.step ? `step="${f.step}"` : 'step="any"'} ${f.min != null ? `min="${f.min}"` : (f.gt0 ? "" : 'min="0"')} ${f.max != null ? `max="${f.max}"` : ""} value="${esc(f.value ?? "")}">`;
      else
        input = `<input id="mqf_${f.id}" type="${f.type || "text"}"${ml} value="${esc(f.value ?? "")}">`;
      return `<div class="${cls}"><label for="mqf_${f.id}">${esc(f.label)}${f.required ? " *" : ""}</label>${input}${f.hint && f.type !== "checkbox" ? `<small class="hint">${esc(f.hint)}</small>` : ""}</div>`;
    }).join("");
    dlg.showModal();

    $("mqForm").onsubmit = async (e) => {
      e.preventDefault();
      const v = {};
      let firstErr = null;
      for (const f of fields) {
        const el = $("mqf_" + f.id);
        el.classList.remove("invalid");
        if (f.type === "checkbox") { v[f.id] = el.checked; continue; }
        const raw = (el.value || "").trim();
        const err = validateField(f, raw);
        if (err) { el.classList.add("invalid"); if (!firstErr) firstErr = `${f.label}: ${err}`; }
        v[f.id] = f.type === "number" ? (raw === "" ? null : Number(raw)) : raw;
      }
      if (firstErr) { $("mqDlgError").textContent = firstErr; return; }
      $("mqDlgSave").disabled = true;
      try {
        const r = await onSubmit(v);
        dlg.close();
        if (r && r.mensaje) toast(r.mensaje);
      } catch (ex) {
        $("mqDlgError").textContent = (ex.data && ex.data.mensaje) || "Could not save.";
      } finally { $("mqDlgSave").disabled = false; }
    };
  }
  const optPlants = () => state.plants.map((p) => ({ value: p.idPlant, label: p.name }));
  const optMats = () => state.materials.map((m) => ({ value: m.idMaterial, label: `${m.matNumber} · ${m.name}` }));
  const optSup = () => [{ value: "", label: "— none —" }].concat(state.suppliers.map((s) => ({ value: s.idSupplier, label: s.name })));

  // ---- tabs ----------------------------------------------------------
  const RENDER = { overview, week, demand, purchases, inventory, suppliers, charts, history, setup };
  function go(tab) {
    state.tab = tab;
    [...$("tabs").children].forEach((b) => b.classList.toggle("active", b.dataset.tab === tab));
    $("view").innerHTML = `<div class="mq-loading">Loading…</div>`;
    RENDER[tab]().catch(() => ($("view").innerHTML = `<div class="mq-loading">Could not load.</div>`));
  }

  // ---- OVERVIEW ------------------------------------------------------
  async function overview() {
    const [d, wk] = await Promise.all([api("/api/materialiq/overview?" + q()), api("/api/materialiq/week?" + q()).catch(() => null)]);
    const r = d.resumen;
    const card = (t, v, n) => `<div class="mq-card"><span>${t}</span><strong>${v}</strong><small>${n}</small></div>`;

    // value by plant (replacement reference = stock × rate)
    const porPlanta = {};
    d.filas.forEach((f) => { if (f.tarifa && f.stock) porPlanta[f.plant] = (porPlanta[f.plant] || 0) + f.stock * f.tarifa; });
    const valuePlant = Object.keys(porPlanta).map((k) => ({ label: k, value: porPlanta[k] })).sort((a, b) => b.value - a.value);
    // 7-day demand (sum across materials)
    const dias = [1, 2, 3, 4, 5, 6, 7].map((t) => {
      let sum = null;
      if (wk) wk.filas.forEach((f) => { const x = f.dias[t - 1]; if (x && x.demand != null) sum = (sum || 0) + x.demand; });
      return { label: addDays(d.fecha, t).slice(5), value: sum };
    });
    // purchasing priorities (highest suggested buy)
    const prio = d.filas.filter((f) => f.comprarTn > 0).sort((a, b) => (b.comprarUsd - a.comprarUsd) || (b.comprarTn - a.comprarTn)).slice(0, 8);

    $("view").innerHTML = `
      <div class="mq-cards">
        ${card("Inventory — confirmed cost", usd(r.valorConfirmado), "Recorded valuation")}
        ${card("Replacement value — reference", usd(r.valorReferencia), "Stock × lowest approved rate")}
        ${card("Suggested purchases — next day", usd(r.comprasSugeridasUsd), "Lot-rounded at lowest rate")}
        ${card("Purchases through cutoff", usd(r.comprasHastaCorte), "Recorded, excluding voided")}
      </div>
      <div class="mq-panels">
        <div class="mq-box"><h3>Inventory value by plant</h3><small class="mq-note">Replacement reference (stock × lowest rate)</small>${barsHtml(valuePlant, usd)}</div>
        <div class="mq-box"><h3>Scheduled 7-day demand</h3><small class="mq-note">Confirmed tons per day from ${d.manana}</small>${sparkHtml(dias)}</div>
      </div>
      <div class="mq-box" style="margin:14px 0"><h3>Purchasing priorities</h3>
        <table class="mq-table"><thead><tr><th>Plant</th><th>Material</th><th>Suggested t</th><th>Suggested $</th><th>Coverage</th></tr></thead>
        <tbody>${prio.map((f) => `<tr><td>${esc(f.plant)}</td><td>${esc(f.matNumber)} ${esc(f.material)}</td><td>${tn(f.comprarTn)}</td><td>${usd(f.comprarUsd)}</td><td>${pct(f.cobertura)}</td></tr>`).join("") || `<tr><td colspan="5" class="mq-empty">No purchases suggested for tomorrow.</td></tr>`}</tbody></table></div>
      <div class="mq-note">${r.filasPendientes} row(s) with pending demand for tomorrow (${d.manana}).</div>
      <div id="mqHost"></div>`;
    renderPaged($("mqHost"), {
      colspan: 12, empty: "No materials for this plant.",
      headHtml: `<tr><th>Plant</th><th>Mat #</th><th>Material</th><th>Stock t</th><th>Value</th><th>Demand tmrw</th>
        <th>Coverage</th><th>Inbound t</th><th>Suggested t</th><th>Rate $/t</th><th>Suggested $</th><th>Status</th></tr>`,
      rowsHtml: d.filas.map((f) => `<tr class="mq-${f.estado}">
        <td>${esc(f.plant)}</td><td>${esc(f.matNumber)}</td><td>${esc(f.material)}</td>
        <td>${tn(f.stock)}</td><td>${usd(f.valor)}</td><td>${f.demanda == null ? "<em>pending</em>" : tn(f.demanda)}</td>
        <td>${pct(f.cobertura)}</td><td>${tn(f.llegadas)}</td><td>${tn(f.comprarTn)}</td>
        <td>${usd(f.tarifa)}</td><td>${usd(f.comprarUsd)}</td>
        <td><span class="mq-pill ${f.estado}">${STATUS[f.estado]}</span></td></tr>`)
    });
  }

  // ---- 7-DAY ---------------------------------------------------------
  async function week() {
    const d = await api("/api/materialiq/week?" + q());
    $("view").innerHTML = `
      <div class="mq-note">Lot-rounded replenishment to keep safety stock over the next 7 days from ${d.fecha}. Confirm all 7 demand days for a complete plan.</div>
      <div id="mqHost"></div>`;
    renderPaged($("mqHost"), {
      colspan: 12, empty: "No materials.",
      headHtml: `<tr><th>Plant</th><th>Material</th>${[1,2,3,4,5,6,7].map((i) => `<th>D${i} close</th>`).join("")}
        <th>Weekly buy t</th><th>Weekly buy $</th><th>First delivery</th></tr>`,
      rowsHtml: d.filas.map((f) => `<tr${f.capRisk ? ' class="mq-low"' : ""}>
        <td>${esc(f.plant)}</td><td>${esc(f.matNumber)} · ${esc(f.material)}</td>
        ${f.dias.map((x) => `<td title="demand ${x.demand == null ? "pending" : tn(x.demand)} · buy ${tn(x.buy)}">${tn(x.closing)}</td>`).join("")}
        <td><b>${tn(f.weeklyBuyTn)}</b></td><td>${usd(f.weeklyBuyUsd)}</td><td>${f.firstDay ? "D" + f.firstDay + " (" + addDays(d.fecha, f.firstDay) + ")" : "—"}</td></tr>`)
    });
  }

  // ---- DEMAND --------------------------------------------------------
  async function demand() {
    const fecha = addDays(state.date, 1);
    const d = await api(`/api/materialiq/demand?fecha=${fecha}${state.plant ? "&plant=" + state.plant : ""}`);
    $("view").innerHTML = `
      <div class="mq-note">Enter tomorrow's tons (${d.fecha}) for every material. Type 0 for no use; blank stays pending.</div>
      <div id="mqHost"></div>`;
    renderPaged($("mqHost"), {
      colspan: 7, empty: "No materials.",
      headHtml: `<tr><th>Plant</th><th>Mat #</th><th>Material</th><th>Tons</th><th>or Prod CY</th><th>× Dosage t/CY</th><th></th></tr>`,
      rowsHtml: d.filas.map((f) => `<tr data-pl="${f.idPlant}" data-m="${f.idMaterial}">
        <td>${esc(f.plant)}</td><td>${esc(f.matNumber)}</td><td>${esc(f.material)}</td>
        <td><input class="mq-in" data-k="tons" type="number" step="0.01" value="${f.tons ?? ""}" placeholder="pending"></td>
        <td><input class="mq-in" data-k="cy" type="number" step="0.01" value="${f.produccionCy ?? ""}"></td>
        <td><input class="mq-in" data-k="do" type="number" step="0.0001" value="${f.dosisTCy ?? ""}"></td>
        <td><button class="btn secondary mq-save">Save</button></td></tr>`),
      wire: (host) => host.querySelectorAll(".mq-save").forEach((b) => b.addEventListener("click", async (e) => {
        const tr = e.target.closest("tr");
        const cell = (k) => tr.querySelector(`[data-k="${k}"]`);
        const g = (k) => { const el = cell(k); el.classList.remove("invalid"); const s = el.value.trim(); return s === "" ? null : Number(s); };
        const tons = g("tons"), cy = g("cy"), dose = g("do");
        // type check: each present value must be a non-negative number
        for (const k of ["tons", "cy", "do"]) {
          const s = cell(k).value.trim();
          if (s !== "" && (!isFinite(Number(s)) || Number(s) < 0)) { cell(k).classList.add("invalid"); return toast("Enter a non-negative number.", true); }
        }
        if (tons == null && (cy == null) !== (dose == null)) { cell(cy == null ? "cy" : "do").classList.add("invalid"); return toast("Enter both production CY and dosage, or tons.", true); }
        if (tons == null && cy == null && dose == null) return toast("Enter tons, or production CY and dosage.", true);
        try {
          const r = await api("/api/materialiq/demand", {
            idPlant: Number(tr.dataset.pl), idMaterial: Number(tr.dataset.m), fecha: d.fecha,
            tons, produccionCy: cy, dosisTCy: dose
          });
          toast(r.mensaje);
        } catch (ex) { toast((ex.data && ex.data.mensaje) || "Could not save.", true); }
      }))
    });
  }

  // ---- PURCHASES -----------------------------------------------------
  async function purchases() {
    const desde = addDays(state.date, -30), hasta = state.date;
    const list = await api(`/api/materialiq/purchases?desde=${desde}&hasta=${hasta}${state.plant ? "&plant=" + state.plant : ""}`);
    $("view").innerHTML = `
      <div class="toolbar"><div class="filters"><small>Purchases ${desde} → ${hasta}</small></div>
        <button class="btn primary" id="mqNewPurchase">+ Record purchase</button></div>
      <div id="mqHost"></div>`;
    $("mqNewPurchase").addEventListener("click", () => formCompra(null));
    renderPaged($("mqHost"), {
      colspan: 10, empty: "No purchases in range.",
      headHtml: `<tr><th>Date</th><th>Plant</th><th>Material</th><th>Supplier</th><th>Tons</th><th>$/t</th><th>Total</th>
        <th>Status</th><th>Ticket</th><th>Actions</th></tr>`,
      rowsHtml: list.map((p) => `<tr class="mq-st-${p.status.replace(/\s/g, "").toLowerCase()}">
        <td>${p.purchaseDate}</td><td>${esc(p.plant)}</td><td>${esc(p.matNumber)} ${esc(p.material)}</td>
        <td>${esc(p.supplier || "—")}</td><td>${tn(p.tons)}</td><td>${usd(p.productPriceT + p.freightT)}</td><td>${usd(p.total)}</td>
        <td><span class="mq-pill ${p.status === "Received" ? "ok" : p.status === "Voided" ? "low" : "pending"}">${esc(p.status)}</span></td>
        <td>${esc(p.ticketRef || "—")}</td>
        <td class="mq-actions">
          ${p.status === "In transit" ? `<button class="btn secondary" data-act="receive" data-id="${p.idPurchase}">Receive</button>` : ""}
          ${p.status !== "Voided" ? `<button class="btn secondary" data-act="edit" data-id="${p.idPurchase}">Edit</button>
            <button class="btn danger" data-act="void" data-id="${p.idPurchase}">Void</button>` :
            `<button class="btn secondary" data-act="restore" data-id="${p.idPurchase}">Restore</button>`}
        </td></tr>`),
      wire: (host) => host.querySelectorAll("[data-act]").forEach((b) => b.addEventListener("click", (e) => {
        const id = Number(e.target.dataset.id), act = e.target.dataset.act;
        const p = list.find((x) => x.idPurchase === id);
        if (act === "edit") return formCompra(p);
        if (act === "receive") return openForm("Receive order", [
          { id: "receivedDate", label: "Received date", type: "date", required: true, value: today() },
          { id: "ticketRef", label: "Ticket / reference", type: "text", maxlength: 80, pattern: RE.ticket }
        ], (v) => api("/api/materialiq/purchase/receive", { idPurchase: id, receivedDate: v.receivedDate || null, ticketRef: v.ticketRef || null }).then(after));
        if (act === "void") return openForm("Void order", [{ id: "reason", label: "Reason", type: "textarea", required: true, maxlength: 300, colspan: 2 }],
          (v) => api("/api/materialiq/purchase/void", { idPurchase: id, reason: v.reason }).then(after));
        if (act === "restore") return api("/api/materialiq/purchase/restore", { idPurchase: id }).then((r) => { toast(r.mensaje); after(); });
      }))
    });
    function after() { if (state.tab === "purchases") purchases(); }
  }
  function formCompra(p) {
    const fields = [
      { id: "idPlant", label: "Plant", type: "select", options: optPlants(), value: p ? p.idPlant : state.plant || (state.plants[0] && state.plants[0].idPlant), required: true },
      { id: "idMaterial", label: "Material", type: "select", options: optMats(), value: p ? p.idMaterial : "", required: true },
      { id: "idSupplier", label: "Supplier", type: "select", options: optSup(), value: p ? p.idSupplier : "" },
      { id: "purchaseDate", label: "Purchase date", type: "date", value: p ? p.purchaseDate : today(), required: true },
      { id: "tons", label: "Tons", type: "number", step: "0.01", gt0: true, max: 1000000, value: p ? p.tons : "", required: true },
      { id: "productPriceT", label: "Product $/t", type: "number", step: "0.01", max: 1000000, value: p ? p.productPriceT : 0, required: true },
      { id: "freightT", label: "Transport $/t", type: "number", step: "0.01", max: 1000000, value: p ? p.freightT : 0, required: true },
      { id: "expectedDate", label: "Expected arrival", type: "date", value: p ? p.expectedDate : "" },
      { id: "status", label: "Status", type: "select", options: [{ value: "In transit", label: "In transit" }, { value: "Received", label: "Received" }], value: p ? p.status : "In transit" },
      { id: "receivedDate", label: "Received date (if received)", type: "date", value: p ? p.receivedDate : "" },
      { id: "ticketRef", label: "Ticket / reference", type: "text", maxlength: 80, pattern: RE.ticket, value: p ? p.ticketRef : "" }
    ];
    if (p) fields.push({ id: "reason", label: "Reason for edit", type: "textarea", required: true, maxlength: 300, colspan: 2 });
    openForm(p ? "Edit purchase" : "Record purchase", fields, (v) => {
      const body = {
        idPurchase: p ? p.idPurchase : null, idPlant: Number(v.idPlant), idMaterial: Number(v.idMaterial),
        idSupplier: v.idSupplier ? Number(v.idSupplier) : null, purchaseDate: v.purchaseDate, tons: v.tons,
        productPriceT: v.productPriceT, freightT: v.freightT, expectedDate: v.expectedDate || null,
        status: v.status, receivedDate: v.receivedDate || null, ticketRef: v.ticketRef || null, reason: v.reason || null
      };
      return api(p ? "/api/materialiq/purchase/update" : "/api/materialiq/purchase", body).then((r) => { if (state.tab === "purchases") purchases(); return r; });
    });
  }

  // ---- INVENTORY (balance + consumption) ----------------------------
  async function inventory() {
    $("view").innerHTML = `
      <div class="mq-two">
        <div class="mq-box"><h3>Physical closing balance</h3>
          <p class="mq-note">Resets stock and value as of its date. Receipts and consumption after it are replayed.</p>
          <button class="btn primary" id="mqBalance">Update closing balance</button></div>
        <div class="mq-box"><h3>Actual consumption</h3>
          <p class="mq-note">Reduces stock and value at the weighted-average cost.</p>
          <button class="btn primary" id="mqConsume">Record consumption</button></div>
      </div>`;
    $("mqBalance").addEventListener("click", () => openForm("Update closing balance", [
      { id: "idPlant", label: "Plant", type: "select", options: optPlants(), value: state.plant, required: true },
      { id: "idMaterial", label: "Material", type: "select", options: optMats(), required: true },
      { id: "balanceDate", label: "Balance date", type: "date", value: state.date, required: true },
      { id: "tons", label: "Physical tons", type: "number", step: "0.01", max: 100000000, value: 0, required: true },
      { id: "valueUsd", label: "Total value USD (blank = unknown)", type: "number", step: "0.01", max: 1000000000 }
    ], (v) => api("/api/materialiq/balance", { idPlant: Number(v.idPlant), idMaterial: Number(v.idMaterial), balanceDate: v.balanceDate, tons: v.tons, valueUsd: v.valueUsd })));
    $("mqConsume").addEventListener("click", () => openForm("Record consumption", [
      { id: "idPlant", label: "Plant", type: "select", options: optPlants(), value: state.plant, required: true },
      { id: "idMaterial", label: "Material", type: "select", options: optMats(), required: true },
      { id: "useDate", label: "Use date", type: "date", value: state.date, required: true },
      { id: "tons", label: "Tons used", type: "number", step: "0.01", gt0: true, max: 100000000, required: true }
    ], (v) => api("/api/materialiq/consumption", { idPlant: Number(v.idPlant), idMaterial: Number(v.idMaterial), useDate: v.useDate, tons: v.tons })));
  }

  // ---- SUPPLIERS & RATES --------------------------------------------
  async function suppliers() {
    const rates = await api(`/api/materialiq/rates${state.plant ? "?plant=" + state.plant : ""}`);
    $("view").innerHTML = `
      <div class="toolbar"><div class="filters"><small>Effective delivered rates (product + transport $/t)</small></div>
        <div><button class="btn secondary" id="mqNewSupplier">+ Supplier</button>
        <button class="btn primary" id="mqNewRate">+ Rate</button></div></div>
      <div id="mqHost"></div>`;
    $("mqNewSupplier").addEventListener("click", () => openForm("Add supplier",
      [{ id: "name", label: "Supplier name", type: "text", required: true, pattern: RE.name, maxlength: 120, colspan: 2 }],
      (v) => api("/api/materialiq/supplier", { name: v.name }).then(async (r) => { await loadRefs(); return r; })));
    $("mqNewRate").addEventListener("click", () => openForm("Add rate", [
      { id: "idPlant", label: "Plant", type: "select", options: optPlants(), value: state.plant, required: true },
      { id: "idMaterial", label: "Material", type: "select", options: optMats(), required: true },
      { id: "idSupplier", label: "Supplier", type: "select", options: state.suppliers.map((s) => ({ value: s.idSupplier, label: s.name })), required: true },
      { id: "effectiveDate", label: "Effective date", type: "date", value: state.date, required: true },
      { id: "productPriceT", label: "Product $/t", type: "number", step: "0.01", max: 1000000, value: 0, required: true },
      { id: "freightT", label: "Transport $/t", type: "number", step: "0.01", max: 1000000, value: 0, required: true },
      { id: "origin", label: "Origin", type: "text", maxlength: 160, pattern: RE.name },
      { id: "approved", label: "Approved", type: "checkbox", value: false, hint: "Approved for use" }
    ], (v) => api("/api/materialiq/rate", {
        idPlant: Number(v.idPlant), idMaterial: Number(v.idMaterial), idSupplier: Number(v.idSupplier),
        effectiveDate: v.effectiveDate, productPriceT: v.productPriceT, freightT: v.freightT, origin: v.origin || null, approved: v.approved
      }).then((r) => { if (state.tab === "suppliers") suppliers(); return r; })));
    renderPaged($("mqHost"), {
      colspan: 9, empty: "No rates yet.",
      headHtml: `<tr><th>Plant</th><th>Material</th><th>Supplier</th><th>Effective</th><th>Product $/t</th><th>Freight $/t</th><th>Delivered $/t</th><th>Approved</th><th></th></tr>`,
      rowsHtml: rates.map((r) => `<tr>
        <td>${esc(r.plant)}</td><td>${esc(r.matNumber)} ${esc(r.material)}</td><td>${esc(r.supplier)}</td>
        <td>${r.effectiveDate}</td><td>${usd(r.productPriceT)}</td><td>${usd(r.freightT)}</td><td><b>${usd(r.delivered)}</b></td>
        <td>${r.approved ? '<span class="mq-pill ok">Yes</span>' : '<span class="mq-pill pending">No</span>'}</td>
        <td>${r.approved ? "" : `<button class="btn secondary" data-approve="${r.idRate}">Approve</button>`}</td></tr>`),
      wire: (host) => host.querySelectorAll("[data-approve]").forEach((b) => b.addEventListener("click", (e) =>
        api("/api/materialiq/rate/approve", { id: Number(e.target.dataset.approve) }).then((r) => { toast(r.mensaje); suppliers(); })))
    });
  }

  // ---- SETUP (policy + admin) ---------------------------------------
  async function setup() {
    const pol = await api(`/api/materialiq/policies${state.plant ? "?plant=" + state.plant : ""}`);
    $("view").innerHTML = `
      <div class="toolbar"><div class="filters"><small>Inventory policy per plant / material</small></div>
        <div><button class="btn secondary" id="mqAddPlant">+ Plant</button>
        <button class="btn secondary" id="mqAddMat">+ Material</button>
        <button class="btn secondary" id="mqAssign">Assign material</button></div></div>
      <div id="mqHost"></div>`;
    $("mqAddPlant").addEventListener("click", () => openForm("Add plant", [
      { id: "code", label: "Code", type: "text", required: true, pattern: RE.code, maxlength: 40, hint: "Letters, digits, - or _" },
      { id: "name", label: "Name", type: "text", required: true, pattern: RE.name, maxlength: 80 }
    ], (v) => api("/api/materialiq/plant", v).then(async (r) => { await loadRefs(); return r; })));
    $("mqAddMat").addEventListener("click", () => openForm("Add material", [
      { id: "matNumber", label: "Mat #", type: "text", required: true, pattern: RE.matnum, maxlength: 10 },
      { id: "name", label: "Name", type: "text", required: true, pattern: RE.name, maxlength: 120, colspan: 2 },
      { id: "category", label: "Category", type: "select", options: ["Produced", "Screened", "Recycled", "Received", "Purchased"].map((x) => ({ value: x, label: x })), required: true },
      { id: "unit", label: "Unit", type: "select", options: [{ value: "TN", label: "TN" }, { value: "CY", label: "CY" }], value: "TN", required: true },
      { id: "density", label: "Density TN/CY", type: "number", step: "0.01", max: 10 }
    ], (v) => api("/api/materialiq/material", v).then(async (r) => { await loadRefs(); return r; })));
    $("mqAssign").addEventListener("click", () => openForm("Assign material to plant", [
      { id: "idPlant", label: "Plant", type: "select", options: optPlants(), value: state.plant, required: true },
      { id: "idMaterial", label: "Material", type: "select", options: optMats(), required: true }
    ], (v) => api("/api/materialiq/assign", { idPlant: Number(v.idPlant), idMaterial: Number(v.idMaterial) }).then((r) => { setup(); return r; })));
    renderPaged($("mqHost"), {
      colspan: 8, empty: "No assignments.",
      headHtml: `<tr><th>Plant</th><th>Mat #</th><th>Material</th><th>Safety t</th><th>Max cap t</th><th>Lead days</th><th>Lot t</th><th></th></tr>`,
      rowsHtml: pol.map((p) => `<tr>
        <td>${esc(p.plant)}</td><td>${esc(p.matNumber)}</td><td>${esc(p.material)}</td>
        <td>${tn(p.safetyStockT)}</td><td>${p.maxCapacityT == null ? "—" : tn(p.maxCapacityT)}</td>
        <td>${p.leadTimeDays}</td><td>${tn(p.purchaseLotT)}</td>
        <td><button class="btn secondary" data-pol='${esc(JSON.stringify(p))}'>Edit</button></td></tr>`),
      wire: (host) => host.querySelectorAll("[data-pol]").forEach((b) => b.addEventListener("click", (e) => {
        const p = JSON.parse(e.target.dataset.pol);
        openForm(`Policy — ${p.matNumber} ${p.material} @ ${p.plant}`, [
          { id: "safetyStockT", label: "Safety stock t", type: "number", step: "0.01", max: 100000000, value: p.safetyStockT, required: true },
          { id: "maxCapacityT", label: "Max capacity t", type: "number", step: "0.01", max: 100000000, value: p.maxCapacityT ?? "" },
          { id: "leadTimeDays", label: "Lead time days", type: "number", integer: true, max: 90, value: p.leadTimeDays, required: true },
          { id: "purchaseLotT", label: "Purchase lot t", type: "number", step: "0.01", gt0: true, max: 100000000, value: p.purchaseLotT, required: true }
        ], (v) => api("/api/materialiq/policy", {
            idPlant: p.idPlant, idMaterial: p.idMaterial, safetyStockT: v.safetyStockT,
            maxCapacityT: v.maxCapacityT, leadTimeDays: v.leadTimeDays, purchaseLotT: v.purchaseLotT
          }).then((r) => { setup(); return r; }));
      }))
    });
  }

  // ---- PURCHASE CHARTS ----------------------------------------------
  async function charts() {
    const desde = addDays(state.date, -30), hasta = state.date;
    const modo = (state._chartModo || "received");
    const d = await api(`/api/materialiq/charts?desde=${desde}&hasta=${hasta}&modo=${modo}${state.plant ? "&plant=" + state.plant : ""}`);
    $("view").innerHTML = `
      <div class="toolbar"><div class="filters">
        <small>${desde} → ${hasta}</small>
        <label>Mode <select id="mqChMode">
          <option value="received" ${modo === "received" ? "selected" : ""}>Received (by receipt date)</option>
          <option value="recorded" ${modo === "recorded" ? "selected" : ""}>Recorded (by purchase date, incl. transit)</option>
        </select></label></div></div>
      <div class="mq-panels">
        <div class="mq-box"><h3>Materials — tons</h3>${barsHtml(d.materiales.map((m) => ({ label: m.matNumber + " " + m.name, value: m.tons })), tn)}</div>
        <div class="mq-box"><h3>Materials — delivered USD</h3>${barsHtml(d.materiales.slice().sort((a, b) => b.usd - a.usd).map((m) => ({ label: m.matNumber + " " + m.name, value: m.usd })), usd)}</div>
      </div>
      <div class="mq-panels">
        <div class="mq-box"><h3>Plant ranking — tons</h3>${barsHtml(d.plantas.map((p) => ({ label: p.plant, value: p.tons })), tn)}</div>
        <div class="mq-box"><h3>Plant ranking — delivered USD</h3>${barsHtml(d.plantas.slice().sort((a, b) => b.usd - a.usd).map((p) => ({ label: p.plant, value: p.usd })), usd)}</div>
      </div>`;
    $("mqChMode").addEventListener("change", (e) => { state._chartModo = e.target.value; charts(); });
  }

  // ---- HISTORY & METHODOLOGY ----------------------------------------
  async function history() {
    const desde = addDays(state.date, -30), hasta = state.date;
    const entity = state._histEntity || "";
    const list = await api(`/api/materialiq/history?desde=${desde}&hasta=${hasta}${entity ? "&entity=" + entity : ""}`);
    const ents = ["", "demand", "purchase", "closing_balance", "consumption", "plant_material", "supplier", "supplier_rate", "plant", "material"];
    $("view").innerHTML = `
      <div class="toolbar"><div class="filters">
        <small>Business changes ${desde} → ${hasta} (who changed what, when and why)</small>
        <label>Entity <select id="mqHistEnt">${ents.map((e) => `<option value="${e}" ${e === entity ? "selected" : ""}>${e || "All"}</option>`).join("")}</select></label>
      </div></div>
      <div id="mqHost"></div>`;
    $("mqHistEnt").addEventListener("change", (e) => { state._histEntity = e.target.value; history(); });
    renderPaged($("mqHost"), {
      colspan: 8, empty: "No changes in range.",
      headHtml: `<tr><th>When</th><th>Entity</th><th>Action</th><th>Field</th><th>Old → New</th><th>Reason</th><th>By</th><th>IP</th></tr>`,
      rowsHtml: list.map((h) => `<tr>
        <td>${h.at}</td><td>${esc(h.entity)}${h.entityId ? " #" + h.entityId : ""}</td><td>${esc(h.action)}</td>
        <td>${esc(h.field || "—")}</td><td>${esc(h.oldValue || "—")} → ${esc(h.newValue || "—")}</td>
        <td>${esc(h.reason || "—")}</td><td>${esc(h.by || "—")}</td><td>${esc(h.ip || "—")}</td></tr>`)
    });
  }

  // ---- boot ----------------------------------------------------------
  async function loadRefs() {
    const [plants, materials, suppliers] = await Promise.all([
      api("/api/materialiq/plants"), api("/api/materialiq/materials"), api("/api/materialiq/suppliers")
    ]);
    state.plants = plants; state.materials = materials; state.suppliers = suppliers;
    $("fPlant").innerHTML = `<option value="">All plants</option>` + plants.map((p) => `<option value="${p.idPlant}">${esc(p.name)}</option>`).join("");
    $("fPlant").value = state.plant;
  }

  $("fPlant").addEventListener("change", (e) => { state.plant = e.target.value; go(state.tab); });
  $("fDate").addEventListener("change", (e) => { state.date = e.target.value || today(); go(state.tab); });
  $("tabs").addEventListener("click", (e) => { const b = e.target.closest("[data-tab]"); if (b) go(b.dataset.tab); });
  $("mqDlgCancel").addEventListener("click", () => $("mqDialog").close());
  $("mqDialog").addEventListener("cancel", (e) => { e.preventDefault(); $("mqDialog").close(); });

  $("fDate").value = state.date;
  loadRefs().then(() => go("overview")).catch(() => ($("view").innerHTML = `<div class="mq-loading">Could not load MaterialIQ. Run db/materialiq_esquema.sql.</div>`));
})();
