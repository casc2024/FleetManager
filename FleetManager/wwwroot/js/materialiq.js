// MaterialIQ — JCK plant intelligence (admin / per-module permission).
(function () {
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const usd = (n) => (n == null ? "—" : "$" + Number(n).toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 }));
  const tn = (n) => (n == null ? "—" : Number(n).toLocaleString("en-US", { maximumFractionDigits: 1 }) + " t");
  const pct = (n) => (n == null ? "—" : Number(n).toFixed(1) + "%");
  const addDays = (iso, d) => { const dt = new Date(iso + "T12:00:00Z"); dt.setUTCDate(dt.getUTCDate() + d); return dt.toISOString().slice(0, 10); };
  const today = () => { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,"0")}-${String(d.getDate()).padStart(2,"0")}`; };
  const PER_PAGE = 10;

  const NAV = [
    { tab: "overview", label: "Inventory overview", title: "Inventory overview", sub: "Purchasing decisions connected to your production." },
    { tab: "demandsupply", label: "Demand & supply", title: "Demand & supply", sub: "Confirm tomorrow's tons and review the seven-day supply." },
    { tab: "inventory", label: "Valued inventory", title: "Valued inventory", sub: "Physical balances, cost and actual consumption." },
    { tab: "purchases", label: "Purchases & spending", title: "Purchases & spending", sub: "Orders, receipts, corrections and spend." },
    { tab: "suppliers", label: "Suppliers & rates", title: "Suppliers & rates", sub: "Effective delivered rates by plant and material." },
    { tab: "charts", label: "Purchase charts", title: "Purchase charts", sub: "Material and plant rankings by tons and USD." },
    { tab: "setup", label: "Administration", title: "Administration", sub: "Plants, materials, assignments and inventory policy." },
    { tab: "history", label: "History & methodology", title: "History & methodology", sub: "Who changed what, when and why." }
  ];
  const ICON = { overview: "▦", demandsupply: "☰", inventory: "▤", purchases: "✓", suppliers: "⇄", charts: "▮", setup: "⚙", history: "◷" };

  const state = { plant: "", date: today(), tab: "overview", plants: [], materials: [], suppliers: [] };
  const STATUS = { pending: "Pending demand", low: "Low stock", ok: "Sufficient" };

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
  function downloadCsv(name, rows) {
    const csv = rows.map((r) => r.map((c) => `"${String(c == null ? "" : c).replace(/"/g, '""')}"`).join(",")).join("\r\n");
    const a = document.createElement("a"); a.href = URL.createObjectURL(new Blob([csv], { type: "text/csv" }));
    a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  }

  // ---- paginated table (10 / page) ----------------------------------
  function renderPaged(host, cfg) {
    let page = 0;
    const total = cfg.rowsHtml.length;
    const size = cfg.all ? Math.max(total, 1) : PER_PAGE;   // cfg.all = show every row (no pager)
    const pages = Math.max(1, Math.ceil(total / size));
    function draw() {
      if (page > pages - 1) page = pages - 1;
      const slice = cfg.rowsHtml.slice(page * size, page * size + size).join("")
        || `<tr><td colspan="${cfg.colspan}" class="mq-empty">${esc(cfg.empty || "No records.")}</td></tr>`;
      host.innerHTML =
        `<div class="table-wrap board"><table class="mq-table"><thead>${cfg.headHtml}</thead><tbody>${slice}</tbody></table></div>` +
        (total > PER_PAGE
          ? `<div class="mq-pager"><button class="btn secondary" data-pg="prev" ${page === 0 ? "disabled" : ""}>‹ Prev</button>
               <span>Page ${page + 1} of ${pages} · ${total} records</span>
               <button class="btn secondary" data-pg="next" ${page >= pages - 1 ? "disabled" : ""}>Next ›</button></div>`
          : `<div class="mq-pager muted">${total} record${total === 1 ? "" : "s"}</div>`);
      if (cfg.wire) cfg.wire(host);
      host.querySelectorAll("[data-pg]").forEach((b) => b.addEventListener("click", () => {
        if (b.dataset.pg === "prev" && page > 0) page--; else if (b.dataset.pg === "next" && page < pages - 1) page++;
        draw();
      }));
    }
    draw();
  }

  // ---- charts helpers -----------------------------------------------
  function barsHtml(items, fmt) {
    const max = Math.max(1, ...items.map((i) => i.value || 0));
    return `<div class="mq-bars">${items.length ? items.map((i) => `
      <div class="mq-bar"><span class="mq-bar-l" title="${esc(i.label)}">${esc(i.label)}</span>
        <span class="mq-bar-t"><span class="mq-bar-f" style="width:${i.value ? Math.max(3, i.value / max * 100) : 0}%"></span></span>
        <b>${fmt(i.value)}</b></div>`).join("") : `<div class="mq-empty">No data.</div>`}</div>`;
  }
  function barsTwoTone(items, fmt) {
    const max = Math.max(1, ...items.map((i) => (i.a || 0) + (i.b || 0)));
    return `<div class="mq-bars">${items.length ? items.map((i) => {
      const t = (i.a || 0) + (i.b || 0);
      return `<div class="mq-bar"><span class="mq-bar-l" title="${esc(i.label)}">${esc(i.label)}</span>
        <span class="mq-bar-t"><span class="mq-bar-f" style="width:${i.a ? i.a / max * 100 : 0}%"></span><span class="mq-bar-f2" style="width:${i.b ? i.b / max * 100 : 0}%"></span></span>
        <b>${fmt(t)}</b></div>`;
    }).join("") : `<div class="mq-empty">No data.</div>`}</div>`;
  }
  function sparkHtml(days) {
    const max = Math.max(1, ...days.map((d) => d.value || 0));
    return `<div class="mq-spark">${days.map((d) => `<div class="mq-spk">
      <span class="${d.value == null ? "pend" : ""}" style="height:${d.value == null ? 2 : Math.max(2, d.value / max * 60)}px"></span>
      <b>${d.value == null ? "Pending" : tn(d.value)}</b><small>${esc(d.label)}</small></div>`).join("")}</div>`;
  }

  // ---- shared validation --------------------------------------------
  const RE = {
    name: /^[\p{L}0-9 .,'&()\/#\-]{2,120}$/u, code: /^[A-Za-z0-9_\-]{2,40}$/,
    matnum: /^[A-Za-z0-9\-]{1,10}$/, ticket: /^[\p{L}0-9 .,'&()\/#\-]{1,80}$/u
  };
  function validateField(f, raw) {
    if (f.type === "checkbox") return null;
    const empty = raw === "" || raw == null;
    if (f.required && empty) return "Required.";
    if (empty) return null;
    if (f.type === "number") {
      const n = Number(raw);
      if (!isFinite(n)) return "Must be a number.";
      if (f.integer && !Number.isInteger(n)) return "Must be a whole number.";
      const min = f.min != null ? f.min : 0;
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
      const v = {}; let firstErr = null;
      for (const f of fields) {
        const el = $("mqf_" + f.id); el.classList.remove("invalid");
        if (f.type === "checkbox") { v[f.id] = el.checked; continue; }
        const raw = (el.value || "").trim();
        const err = validateField(f, raw);
        if (err) { el.classList.add("invalid"); if (!firstErr) firstErr = `${f.label}: ${err}`; }
        v[f.id] = f.type === "number" ? (raw === "" ? null : Number(raw)) : raw;
      }
      if (firstErr) { $("mqDlgError").textContent = firstErr; return; }
      $("mqDlgSave").disabled = true;
      try { const r = await onSubmit(v); dlg.close(); if (r && r.mensaje) toast(r.mensaje); await RENDER[state.tab](); }
      catch (ex) { $("mqDlgError").textContent = (ex.data && ex.data.mensaje) || "Could not save."; }
      finally { $("mqDlgSave").disabled = false; }
    };
  }
  const optPlants = () => state.plants.map((p) => ({ value: p.idPlant, label: p.name }));
  const optMats = () => state.materials.map((m) => ({ value: m.idMaterial, label: `${m.matNumber} · ${m.name}` }));
  const optSup = () => [{ value: "", label: "— none —" }].concat(state.suppliers.map((s) => ({ value: s.idSupplier, label: s.name })));

  // ---- navigation ----------------------------------------------------
  const RENDER = { overview, demandsupply, inventory, purchases, suppliers, charts, setup, history };
  function buildNav() {
    $("mqNav").innerHTML = NAV.map((n) => `<button data-tab="${n.tab}" class="${n.tab === state.tab ? "active" : ""}"><i>${ICON[n.tab] || "•"}</i>${esc(n.label)}</button>`).join("");
    $("mqNav").querySelectorAll("[data-tab]").forEach((b) => b.addEventListener("click", () => go(b.dataset.tab)));
  }
  function go(tab) {
    state.tab = tab;
    const n = NAV.find((x) => x.tab === tab) || NAV[0];
    $("mqTitle").textContent = n.title; $("mqSub").textContent = n.sub;
    $("mqNav").querySelectorAll("[data-tab]").forEach((b) => b.classList.toggle("active", b.dataset.tab === tab));
    $("view").innerHTML = `<div class="mq-loading">Loading…</div>`;
    RENDER[tab]().catch(() => ($("view").innerHTML = `<div class="mq-loading">Could not load.</div>`));
  }

  // ---- OVERVIEW ------------------------------------------------------
  async function overview() {
    const [d, wk] = await Promise.all([api("/api/materialiq/overview?" + q()), api("/api/materialiq/week?" + q()).catch(() => null)]);
    const r = d.resumen;
    const total = d.filas.length, withVal = d.filas.filter((f) => f.valor != null).length, withBuy = d.filas.filter((f) => f.comprarTn > 0).length;
    const pendAppr = d.filas.filter((f) => f.tarifa != null && !f.tarifaApproved).length;
    const card = (t, v, sub, nav) => `<div class="mq-card"><span>${t}</span><strong>${v}</strong><small>${sub}</small><a data-nav="${nav}">View details and pending items →</a></div>`;

    const porPlanta = {};
    d.filas.forEach((f) => { if (f.stock) { const k = f.plant; (porPlanta[k] = porPlanta[k] || { a: 0, b: 0 }); porPlanta[k].a += (f.stock || 0) * (f.tarifaProd || 0); porPlanta[k].b += (f.stock || 0) * (f.tarifaFreight || 0); } });
    const valuePlant = Object.keys(porPlanta).map((k) => ({ label: k, a: porPlanta[k].a, b: porPlanta[k].b })).filter((x) => x.a + x.b > 0).sort((a,b)=>["Royse City","Seagoville","Kemp"].indexOf(a.label)-["Royse City","Seagoville","Kemp"].indexOf(b.label));
    const physicalByPlant = Object.values(d.filas.reduce((acc,f)=>{
      if(f.fechaCierre!=null || f.stock>0){acc[f.plant] ||= {label:f.plant,value:0};acc[f.plant].value+=f.stock||0;}
      return acc;
    },{}));
    const hasRates = d.filas.some(f=>f.tarifa!=null);
    const loaded = d.filas.filter(f=>f.fechaCierre!=null).length;
    const dias = [1, 2, 3, 4, 5, 6, 7].map((t) => {
      let sum = null; if (wk) wk.filas.forEach((f) => { const x = f.dias[t - 1]; if (x && x.demand != null) sum = (sum || 0) + x.demand; });
      return { label: addDays(d.fecha, t).slice(5), value: wk && wk.filas.every(f=>f.dias[t-1]?.demand!=null) ? sum : null };
    });
    const wkBuy = {}; if (wk) wk.filas.forEach((f) => (wkBuy[f.idPlant + "_" + f.idMaterial] = f.weeklyBuyTn));
    const plantOrder = {"Royse City":0,"Seagoville":1,"Kemp":2};
    const materialOrder = {"Bulk Cement":0,"Cement":0,"Fly Ash":1,"#57 Rock":2,"#57 Gravel":3,"#467 Rock":4,"#67 Rock":4,"#8 Pea Gravel":5,"#4 Sand":6};
    const prio = d.filas.slice().sort((a,b)=>(plantOrder[a.plant]??99)-(plantOrder[b.plant]??99) || (materialOrder[a.material]??99)-(materialOrder[b.material]??99) || a.material.localeCompare(b.material));

    $("view").innerHTML = `

      <div class="mq-cards">
        ${card("Inventory · confirmed cost", withVal ? usd(r.valorConfirmado) : "Pending valuation", `${withVal} of ${total} SKUs with confirmed valuation`, "inventory")}
        ${card("Replacement value · reference", d.filas.some(f=>f.tarifa!=null) ? usd(r.valorReferencia) : "Rates required", `Stock × lowest effective rate${pendAppr ? " · " + pendAppr + " approval pending" : ""}`, "suppliers")}
        ${card("Suggested purchases · next 7 days", wk ? usd(wk.filas.reduce((a,f)=>a+(f.weeklyBuyUsd??f.dias.reduce((n,d)=>n+(d.buy??0),0)*(f.delivered??0)),0)) : "Pending", wk && wk.filas.some(f=>f.pendingDays>0) ? "Provisional · incomplete demand; confirm all seven days" : "Suggested purchases · review supplier approval", "demandsupply")}
        ${card("Purchases through cutoff", usd(r.comprasHastaCorte), "Recorded, excluding voided", "purchases")}
      </div>
      <div class="mq-panels">
        <div class="mq-box"><div class="mq-box-head"><h3>${hasRates?"Material inventory value by plant":"Physical inventory by plant"}</h3><span class="mq-badge">${hasRates?"USD":"t"}</span></div>
          <small class="mq-note">${hasRates?"Replacement reference · product + transportation":"Imported physical quantities. Add confirmed valuations and supplier rates to display costs."}</small>
          ${hasRates?barsTwoTone(valuePlant, usd):barsHtml(physicalByPlant, tn)}
          ${hasRates?'<div class="mq-legend"><span><i class="d"></i>Product</span><span><i class="l"></i>Transportation</span></div>':""}</div>
        <div class="mq-box"><h3>Scheduled 7-day demand</h3><small class="mq-note">Confirmed tons per day from ${d.manana}</small>${sparkHtml(dias)}
          <small class="mq-note">${wk ? wk.filas.reduce((n,f)=>n+f.pendingDays,0) : "Unknown"} weekly demand fields pending. Blank means pending, not zero.</small></div>
      </div>
      <div class="mq-box" style="margin-top:14px"><div class="mq-box-head"><h3>Purchasing priorities</h3>
        <button class="btn secondary" data-nav="demandsupply">Open planning →</button></div>
        <div id="mqHost"></div></div>`;

    $("view").querySelectorAll("[data-nav]").forEach((a) => a.addEventListener("click", () => go(a.dataset.nav)));
    renderPaged($("mqHost"), {
      all: true, colspan: 5, empty: "No materials.",
      headHtml: `<tr><th>Plant / material</th><th>Inventory</th><th>Tomorrow</th><th>7-day purchase</th><th>Action</th></tr>`,
      rowsHtml: prio.map((f) => {
        const wb = wkBuy[f.idPlant + "_" + f.idMaterial];
        const act = f.demanda == null ? `<span class="mq-pill muted">Confirm demand</span>` : f.comprarTn > 0 ? `<span class="mq-pill low">Buy ${tn(f.comprarTn)}</span>` : `<span class="mq-pill muted">No purchase planned</span>`;
        return `<tr><td><b>${esc(f.plant)}</b><div class="mq-sub2">${esc(f.matNumber)} ${esc(f.material)}</div></td>
          <td>${tn(f.stock)}</td><td>${f.demanda == null ? "Pending" : tn(f.demanda)}</td>
          <td>${wb==null?"Pending":tn(wb)}${f.estado === "pending" ? '<div class="mq-sub2">Incomplete plan</div>' : ""}</td><td>${act}</td></tr>`;
      })
    });
  }

  // ---- DEMAND & SUPPLY ----------------------------------------------
  async function demandsupply() {
    const tomorrow = addDays(state.date, 1);
    const [ov, wk] = await Promise.all([api("/api/materialiq/overview?" + q()), api("/api/materialiq/week?" + q())]);
    const pending = ov.resumen.filasPendientes;
    const key = f => f.idPlant + "_" + f.idMaterial;
    const ovKey = Object.fromEntries(ov.filas.map(f=>[key(f), f]));
    $("view").innerHTML = `
      <div class="mq-plan"><h2>1. Confirm demand by plant and SKU</h2>
      <p>Enter tons or production CY × dosage t/CY. Explicit 0 means no production; blank means pending.</p>
      <button class="btn primary" id="mqSchedule">+ Schedule demand</button>
      <div class="mq-callout">${pending ? `${pending} next-day demand fields pending. Dispatch export is blocked.` : "Next-day demand complete. Review delivery dates, approvals and alerts before purchasing."}</div>
      </div>
      <div class="mq-box" style="margin-top:14px"><h3>2. Next-day requirements · ${tomorrow}</h3>
      <small class="mq-note">Only inbound orders scheduled for tomorrow reduce tomorrow's shortfall. Coverage uses physical stock only.</small>
      <button class="btn secondary" id="mqExportDispatch" ${pending ? "disabled" : ""}>Export dispatch plan</button><div id="mqHostReq"></div></div>
      <div class="mq-box" style="margin-top:14px"><h3>3. Weekly forecast & supply</h3>
      <p class="mq-note">Forecast: same-weekday actual consumption over 28 days, at least two observations per weekday. It never replaces confirmed demand. Missing days make weekly purchases pending.</p>
      <div id="mqHostWk"></div></div>`;
    function schedule(f) {
      openForm("Schedule demand", [
        { id:"idPlant",label:"Plant",type:"select",options:optPlants(),value:f?.idPlant || state.plant,required:true },
        { id:"idMaterial",label:"Material",type:"select",options:optMats(),value:f?.idMaterial,required:true },
        { id:"fecha",label:"Demand date",type:"date",value:tomorrow,required:true },
        { id:"tons",label:"Tons (leave blank to use CY × dosage)",type:"number",step:"0.01",max:100000000 },
        { id:"produccionCy",label:"Production CY",type:"number",step:"0.01",max:100000000 },
        { id:"dosisTCy",label:"Dosage t/CY",type:"number",step:"0.0001",max:9999 }
      ],v=>api("/api/materialiq/demand",{...v,idPlant:Number(v.idPlant),idMaterial:Number(v.idMaterial)}));
    }
    $("mqSchedule").onclick=()=>schedule();
    const cost = f => f.delivered == null ? "Rate required" : `${usd(f.delivered)}/t · Product ${usd(f.productPriceT)} + Transportation ${usd(f.freightT)}${f.approved ? "" : " · Approval pending"}`;
    renderPaged($("mqHostReq"), {
      all:true,colspan:9,
      headHtml:"<tr><th>Plant / SKU</th><th>Available</th><th>Demand t</th><th>Coverage</th><th>Inbound tomorrow</th><th>Buy / loads</th><th>Closing / alerts</th><th>Supply cost</th><th></th></tr>",
      rowsHtml:wk.filas.map(f=>{
        const o=ovKey[key(f)], d=f.dias[0];
        const status=d.demand==null?"muted":d.demand===0?"muted":o.stock<d.demand+o.seguridad?"low":"ok";
        const coverage=d.demand>0?`${pct(o.stock/d.demand*100)} · ${(o.stock/d.demand).toFixed(2)} days`:d.demand===0?"No demand":"Pending";
        return `<tr><td>${esc(f.plant)}<div>${esc(f.matNumber)} ${esc(f.material)}</div></td>
          <td>${tn(o.stock)}${o.stock<0?"<div class=\"mq-pill low\">Reconcile physical stock</div>":""}<br><button class="btn secondary" data-source="${key(f)}">Source & calculation</button></td>
          <td><input aria-label="${esc(f.plant+" "+f.material+" demand")}" class="mq-in mq-dem" data-key="${key(f)}" type="number" min="0" step="0.01" value="${d.demand??""}" placeholder="Pending"></td>
          <td><span class="mq-pill ${status}">${coverage}</span></td><td>${tn(d.inbound)}</td>
          <td>${d.buy==null?"Pending":`${tn(d.buy)} / ${d.buy/f.lotTons} loads`}</td>
          <td>${tn(d.closing)}${d.capRisk?"<div>Capacity exceeded before consumption</div>":""}${d.leadRisk?"<div>Lead time cannot meet arrival</div>":""}</td>
          <td>${cost(f)}<div>${d.buy==null||f.delivered==null?"Pending":usd(d.buy*f.delivered)}</div></td>
          <td><button class="btn secondary" data-schedule="${key(f)}">Schedule</button></td></tr>`;
      }),wire:host=>{
        host.querySelectorAll("[data-schedule]").forEach(b=>b.onclick=()=>schedule(wk.filas.find(f=>key(f)===b.dataset.schedule)));
        host.querySelectorAll("[data-source]").forEach(b=>b.onclick=()=>showSource(ovKey[b.dataset.source]));
        host.querySelectorAll(".mq-dem").forEach(inp=>inp.onchange=async()=>{
          const f=wk.filas.find(f=>key(f)===inp.dataset.key), raw=inp.value.trim();
          if(raw===""||!Number.isFinite(Number(raw))||Number(raw)<0){ inp.classList.add("invalid");return toast("Enter 0 or a positive number. Blank remains pending.",true); }
          try {await api("/api/materialiq/demand",{idPlant:f.idPlant,idMaterial:f.idMaterial,fecha:tomorrow,tons:Number(raw)});await demandsupply();}
          catch(ex){toast(ex.data?.mensaje||"Could not save.",true);}
        });
      }
    });
    $("mqExportDispatch").onclick=()=>{
      if(pending)return;
      downloadCsv(`dispatch_${tomorrow}.csv`,[["Plant","SKU","Demand t","Inbound t","Purchase t","Loads","Closing t","Product USD/t","Transportation USD/t","Total USD/t","Supplier","Approved","Capacity alert","Lead-time alert"]].concat(wk.filas.map(f=>{
        const d=f.dias[0];return [f.plant,f.matNumber,d.demand,d.inbound,d.buy,d.buy/f.lotTons,d.closing,f.productPriceT,f.freightT,f.delivered,f.supplier,f.approved,d.capRisk,d.leadRisk];
      })));
    };
    renderPaged($("mqHostWk"), {
      all:true,colspan:7,
      headHtml:"<tr><th>Plant / SKU</th><th>Demand / forecast</th><th>Purchase</th><th>Supplier & cost per ton</th><th>Savings vs assigned</th><th>Status</th><th></th></tr>",
      rowsHtml:wk.filas.map(f=>`<tr><td>${esc(f.plant)}<div>${esc(f.matNumber)} ${esc(f.material)}</div></td>
        <td>${tn(f.demand7Tn)} confirmed / ${f.pendingDays} days pending<div>Baseline ${tn(f.baselineTn)}</div></td>
        <td>${f.weeklyBuyTn==null?"Pending":tn(f.weeklyBuyTn)}<div>${f.weeklyBuyUsd==null?"Pending":usd(f.weeklyBuyUsd)}</div></td>
        <td>${esc(f.supplier||"No effective rate")}<div>${cost(f)}</div>
        <select aria-label="${esc(f.plant+" "+f.material+" supplier")}" data-select="${key(f)}"><option value="">Automatic · lowest delivered cost</option>${f.rates.map(r=>`<option value="${r.idSupplier}" ${r.idSupplier===f.selectedSupplierId?"selected":""}>${esc(r.supplier)} · ${usd(r.delivered)}/t${r.approved?"":" · unapproved"}</option>`).join("")}</select>
        <button class="btn secondary" data-save-selection="${key(f)}">Save selection</button></td>
        <td>${f.savings==null?"Assign supplier and complete plan":usd(f.savings)}</td>
        <td>${esc(f.status)}${f.capRisk?"<div>Capacity risk</div>":""}${f.leadRisk?"<div>Lead-time risk</div>":""}</td>
        <td><button class="btn secondary" data-days="${key(f)}">Edit 7 days</button></td></tr>`),
      wire:host=>{
        host.querySelectorAll("[data-days]").forEach(b=>b.onclick=()=>{
          const f=wk.filas.find(f=>key(f)===b.dataset.days);
          openForm(`Seven-day demand — ${f.plant} / ${f.material}`,f.dias.map(d=>({id:"day"+d.day,label:d.date+" tons",type:"number",step:"0.01",value:d.demand??"",max:100000000})),async v=>{
            // A blank is left untouched; explicit zero is saved.
            for(const d of f.dias)if(v["day"+d.day]!=null && v["day"+d.day]!==d.demand)await api("/api/materialiq/demand",{idPlant:f.idPlant,idMaterial:f.idMaterial,fecha:d.date,tons:v["day"+d.day]});
            return {mensaje:"Demand saved."};
          });
        });
        host.querySelectorAll("[data-select]").forEach(sel=>sel.onchange=()=>{
          const f=wk.filas.find(f=>key(f)===sel.dataset.select), r=f.rates.find(r=>r.idSupplier===Number(sel.value))||f.rates[0];
          toast(r?`Preview: Product ${usd(r.productPriceT)} + Transportation ${usd(r.freightT)} = ${usd(r.delivered)}/t. Save selection to apply.`:"No rate available.");
        });
        host.querySelectorAll("[data-save-selection]").forEach(b=>b.onclick=()=>{
          const f=wk.filas.find(f=>key(f)===b.dataset.saveSelection), sel=host.querySelector(`[data-select="${key(f)}"]`);
          openForm("Reason for supplier selection",[{id:"reason",label:"Business reason",type:"textarea",required:true,maxlength:300}],v=>api("/api/materialiq/selection",{idPlant:f.idPlant,idMaterial:f.idMaterial,cutoff:state.date,idSupplier:sel.value?Number(sel.value):null,reason:v.reason}));
        });
      }
    });
  }

  async function showSource(f) {
    const d=await api(`/api/materialiq/source?fecha=${state.date}&plant=${f.idPlant}&material=${f.idMaterial}`);
    $("mqDlgTitle").textContent=`Inventory source — ${f.plant} / ${f.material}`;
    $("mqDlgFields").innerHTML=`<div class="col-2"><p>${esc(d.description)}</p>
      <p>Cutoff: ${esc(state.date)} · Latest closing balance: ${esc(d.balanceDate||"None")}</p>
      <p>Workbook: ${esc(d.sourceFile||"Not recorded")} · Sheet: ${esc(d.sourceSheet||"Not recorded")} · Cell: ${esc(d.sourceCell||"Not recorded")}</p>
      <p>Recorded by: ${esc(d.createdBy||"Not recorded")}</p>
      <p>Available: ${tn(f.stock)} · Confirmed value: ${usd(f.valor)}</p>
      <p>Stock = latest closing balance + receipts after that closing date − actual consumption after that closing date. Same-day movements are included in the closing balance.</p>
      <p>Tomorrow's demand is entered by users, directly in tons or as CY × dosage. It is not inferred from Excel inventory balances.</p></div>`;
    $("mqDlgError").textContent="";$("mqDlgSave").hidden=true;$("mqDialog").showModal();
    $("mqDialog").addEventListener("close",()=>$("mqDlgSave").hidden=false,{once:true});
    $("mqForm").onsubmit=e=>e.preventDefault();
  }

  // ---- PURCHASES -----------------------------------------------------
  async function purchases() {
    const {desde,hasta} = purchaseRange();
    const all = await api(`/api/materialiq/purchases?desde=${desde}&hasta=${hasta}${state.plant ? "&plant=" + state.plant : ""}`);
    const list=all.filter(p=>(state._showVoided||p.status!=="Voided")&&(!state._purchaseMat||p.idMaterial===Number(state._purchaseMat)));
    const active=list.filter(p=>p.status!=="Voided");
    $("view").innerHTML = `${rangeControls()}<label><input id="mqVoided" type="checkbox" ${state._showVoided?"checked":""}> Show voided orders</label>
      <div class="mq-cards">${["day","week","month"].map(period=>{const begin=period==="day"?state.date:period==="week"?weekStart(state.date):state.date.slice(0,7)+"-01";
        const rows=active.filter(p=>p.purchaseDate>=begin&&p.purchaseDate<=state.date);
        return `<div class="mq-card"><span>${period} through cutoff (within selected range)</span><strong>${usd(rows.reduce((a,p)=>a+p.total,0))}</strong><small>${tn(rows.reduce((a,p)=>a+p.tons,0))}</small></div>`;
      }).join("")}</div><div id="mqSpend"></div><div id="mqHost"></div>`;
    wireRange(purchases);
    $("mqVoided").onchange=e=>{state._showVoided=e.target.checked;purchases();};
    const groups=new Map();
    active.forEach(p=>{const date=state._purchasePeriod==="month"?p.purchaseDate.slice(0,7):state._purchasePeriod==="week"?weekStart(p.purchaseDate):p.purchaseDate;
      const key=date+"|"+p.idPlant+"|"+p.idMaterial;const g=groups.get(key)||{date,plant:p.plant,material:p.matNumber+" "+p.material,tons:0,product:0,freight:0};
      g.tons+=p.tons;g.product+=p.tons*p.productPriceT;g.freight+=p.tons*p.freightT;groups.set(key,g);});
    renderPaged($("mqSpend"),{colspan:6,headHtml:"<tr><th>Period</th><th>Plant / SKU</th><th>Tons</th><th>Product USD</th><th>Transportation USD</th><th>Total USD</th></tr>",
      rowsHtml:[...groups.values()].sort((a,b)=>a.date.localeCompare(b.date)).map(g=>`<tr><td>${g.date}</td><td>${esc(g.plant)} / ${esc(g.material)}</td><td>${tn(g.tons)}</td><td>${usd(g.product)}</td><td>${usd(g.freight)}</td><td>${usd(g.product+g.freight)}</td></tr>`)});
    renderPaged($("mqHost"), {
      colspan: 10, empty: "No purchases in range.",
      headHtml: `<tr><th>Date</th><th>Plant</th><th>Material</th><th>Supplier</th><th>Tons</th><th>$/t</th><th>Total</th><th>Status</th><th>Ticket</th><th>Actions</th></tr>`,
      rowsHtml: list.map((p) => `<tr class="mq-st-${p.status.replace(/\s/g, "").toLowerCase()}">
        <td>${p.purchaseDate}</td><td>${esc(p.plant)}</td><td>${esc(p.matNumber)} ${esc(p.material)}</td>
        <td>${esc(p.supplier || "—")}</td><td>${tn(p.tons)}</td><td>${usd(p.productPriceT + p.freightT)}</td><td>${usd(p.total)}</td>
        <td><span class="mq-pill ${p.status === "Received" ? "ok" : p.status === "Voided" ? "low" : "muted"}">${esc(p.status)}</span></td>
        <td>${esc(p.ticketRef || "—")}</td>
        <td class="mq-actions">
          ${p.status === "In transit" ? `<button class="btn secondary" data-act="receive" data-id="${p.idPurchase}">Receive</button>` : ""}
          ${p.status !== "Voided" ? `<button class="btn secondary" data-act="edit" data-id="${p.idPurchase}">Edit</button><button class="btn danger" data-act="void" data-id="${p.idPurchase}">Void</button>` : `<button class="btn secondary" data-act="restore" data-id="${p.idPurchase}">Restore</button>`}
        </td></tr>`),
      wire: (host) => host.querySelectorAll("[data-act]").forEach((b) => b.addEventListener("click", (e) => {
        const id = Number(e.target.dataset.id), act = e.target.dataset.act, p = list.find((x) => x.idPurchase === id);
        if (act === "edit") return formCompra(p);
        if (act === "receive") return openForm("Receive order", [
          { id: "receivedDate", label: "Received date", type: "date", required: true, value: today() },
          { id: "ticketRef", label: "Ticket / reference", type: "text", maxlength: 80, pattern: RE.ticket }
        ], (v) => api("/api/materialiq/purchase/receive", { idPurchase: id, receivedDate: v.receivedDate || null, ticketRef: v.ticketRef || null }).then(after));
        if (act === "void") return openForm("Void order", [{ id: "reason", label: "Reason", type: "textarea", required: true, maxlength: 300, colspan: 2 }],
          (v) => api("/api/materialiq/purchase/void", { idPurchase: id, reason: v.reason }).then(after));
        if (act === "restore") return openForm("Restore order", [{id:"reason",label:"Reason",type:"textarea",required:true,maxlength:300}],v=>api("/api/materialiq/purchase/restore",{idPurchase:id,reason:v.reason}));
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
      const body = { idPurchase: p ? p.idPurchase : null, idPlant: Number(v.idPlant), idMaterial: Number(v.idMaterial),
        idSupplier: v.idSupplier ? Number(v.idSupplier) : null, purchaseDate: v.purchaseDate, tons: v.tons,
        productPriceT: v.productPriceT, freightT: v.freightT, expectedDate: v.expectedDate || null,
        status: v.status, receivedDate: v.receivedDate || null, ticketRef: v.ticketRef || null, reason: v.reason || null };
      return api(p ? "/api/materialiq/purchase/update" : "/api/materialiq/purchase", body).then((r) => { if (state.tab === "purchases") purchases(); else if (state.tab === "overview") overview(); return r; });
    });
  }

  // ---- VALUED INVENTORY ---------------------------------------------
  async function inventory() {
    const [ov, pol] = await Promise.all([api("/api/materialiq/overview?" + q()), api(`/api/materialiq/policies${state.plant ? "?plant=" + state.plant : ""}`)])
      .catch(() => [null, []]);
    if (!ov) throw new Error("load");
    const polKey = {}; pol.forEach((p) => (polKey[p.idPlant + "_" + p.idMaterial] = p));
    const withVal = ov.filas.filter((f) => f.valor != null).length;
    const sufix = (f) => (f.fechaCierre ? ` · closing ${f.fechaCierre}` : " · no closing balance");

    $("view").innerHTML = `
      <div class="mq-note mq-banner">Inventory reconstructed through ${ov.fecha}; the latest closing date is shown per SKU. Confirm physical inventory and its opening value to calculate weighted average cost. Movements apply from the day after the closing balance, with receipts before consumption. An adjustment replaces the closing balance for that date.</div>
      <div class="mq-box">
        <div class="mq-box-head"><h3>Balances &amp; inventory policy</h3>
          <div><button class="btn secondary" id="mqConsume">Record actual consumption</button>
          <button class="btn primary" id="mqBalance">Update valued balance</button></div></div>
        <div id="mqHostB"></div>
      </div>
      <div class="mq-box" style="margin-top:14px">
        <h3>Working capital release opportunity</h3>
        <small class="mq-note">Excess = inventory − scheduled 7-day demand − safety stock. This indicator is provisional when demand days are missing; it is not a recommendation to remove material.</small>
        <div id="mqHostW"></div>
      </div>`;

    renderPaged($("mqHostB"), {
      colspan: 8, empty: "No balances for this plant.",
      headHtml: `<tr><th>Plant / SKU</th><th>Balance (t)</th><th>Average cost / ton</th><th>Confirmed value</th><th>Replacement reference</th><th>Safety stock / capacity</th><th>Assigned supplier</th><th></th></tr>`,
      rowsHtml: ov.filas.map((f) => {
        const p = polKey[f.idPlant + "_" + f.idMaterial] || {};
        const cap = f.capacidadMax == null ? "not configured" : tn(f.capacidadMax);
        const ref = (f.tarifa != null && f.stock) ? f.stock * f.tarifa : null;
        return `<tr data-pl="${f.idPlant}" data-m="${f.idMaterial}">
          <td><b>${esc(f.plant)}</b><div class="mq-sub2">${esc(f.material)}${esc(sufix(f))}</div></td>
          <td>${tn(f.stock)}</td>
          <td>${f.costoPromedio == null ? "—" : usd(f.costoPromedio)}</td>
          <td>${f.valor == null ? "—" : usd(f.valor)}</td>
          <td>${ref == null ? "—" : usd(ref)}</td>
          <td>${tn(f.seguridad)} / ${cap}</td>
          <td>${esc(state.suppliers.find(s=>s.idSupplier===polKey[f.idPlant+"_"+f.idMaterial]?.assignedSupplierId)?.name||"Unassigned")}</td>
          <td><button class="btn secondary" data-cfg="1">Configure</button></td></tr>`;
      }),
      wire: (host) => host.querySelectorAll("[data-cfg]").forEach((b) => b.addEventListener("click", (e) => {
        const tr = e.target.closest("tr"); const pl = Number(tr.dataset.pl), m = Number(tr.dataset.m);
        const p = polKey[pl + "_" + m] || { safetyStockT: 0, purchaseLotT: 25, leadTimeDays: 1 };
        openForm("Configure safety stock, capacity and lot", [
          { id: "safetyStockT", label: "Safety stock t", type: "number", step: "0.01", max: 100000000, value: p.safetyStockT ?? 0, required: true },
          { id: "maxCapacityT", label: "Max capacity t", type: "number", step: "0.01", max: 100000000, value: p.maxCapacityT ?? "" },
          { id: "leadTimeDays", label: "Lead time days", type: "number", integer: true, max: 90, value: p.leadTimeDays ?? 1, required: true },
          { id: "assignedSupplierId", label: "Assigned supplier for savings comparison", type: "select", options: optSup(), value: p.assignedSupplierId ?? "" },
          { id: "purchaseLotT", label: "Purchase lot t", type: "number", step: "0.01", gt0: true, max: 100000000, value: p.purchaseLotT ?? 25, required: true }
        ], (v) => api("/api/materialiq/policy", { idPlant: pl, idMaterial: m, safetyStockT: v.safetyStockT, maxCapacityT: v.maxCapacityT, leadTimeDays: v.leadTimeDays, purchaseLotT: v.purchaseLotT, assignedSupplierId: v.assignedSupplierId ? Number(v.assignedSupplierId) : null }).then((r) => { inventory(); return r; }));
      }))
    });

    renderPaged($("mqHostW"), {
      colspan: 4, empty: "No excess detected.",
      headHtml: `<tr><th>Plant / SKU</th><th>Provisional excess</th><th>Capital at confirmed cost</th><th>Plan</th></tr>`,
      rowsHtml: ov.filas.filter((f) => f.stock > 0 || f.demanda != null).slice().sort((a, b) => a.idPlant - b.idPlant).map((f) => `<tr>
        <td><b>${esc(f.plant)}</b> · ${esc(f.material)}</td>
        <td>${tn(f.exceso)}</td>
        <td>${f.capital == null ? "—" : usd(f.capital)}</td>
        <td><span class="mq-pill ${f.planCompleto ? "ok" : "muted"}">${f.planCompleto ? "Complete" : "Incomplete"}</span></td></tr>`)
    });

    $("mqBalance").addEventListener("click", () => openForm("Update valued balance", [
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
      { id: "tons", label: "Tons used (0 confirms no usage)", type: "number", step: "0.01", max: 100000000, required: true }
    ], (v) => api("/api/materialiq/consumption", { idPlant: Number(v.idPlant), idMaterial: Number(v.idMaterial), useDate: v.useDate, tons: v.tons })));
  }

  // ---- SUPPLIERS & RATES --------------------------------------------
  async function suppliers() {
    const rates = await api(`/api/materialiq/rates${state.plant ? "?plant=" + state.plant : ""}`);
    $("view").innerHTML = `<div class="toolbar"><div class="filters"><small>Effective delivered rates (product + transport $/t)</small></div>
      <div><button class="btn secondary" id="mqNewSupplier">+ Supplier</button><button class="btn primary" id="mqNewRate">+ Rate</button></div></div><div id="mqHost"></div>`;
    $("mqNewSupplier").addEventListener("click", () => openForm("Add supplier",
      [{ id: "name", label: "Supplier name", type: "text", required: true, pattern: RE.name, maxlength: 120, colspan: 2 }],
      (v) => api("/api/materialiq/supplier", { name: v.name }).then(async (r) => { await loadRefs(); return r; })));
    const rateForm = (r) => openForm(r ? "Change rate (preserves history)" : "Add rate", [
      { id: "idPlant", label: "Plant", type: "select", options: optPlants(), value: r?.idPlant || state.plant, required: true },
      { id: "idMaterial", label: "Material", type: "select", options: optMats(), value: r?.idMaterial, required: true },
      { id: "idSupplier", label: "Supplier", type: "select", value: r?.idSupplier, options: state.suppliers.map((s) => ({ value: s.idSupplier, label: s.name })), required: true },
      { id: "effectiveDate", label: "Effective date", type: "date", value: state.date, required: true },
      { id: "productPriceT", label: "Product $/t", type: "number", step: "0.01", max: 1000000, value: r?.productPriceT ?? 0, required: true },
      { id: "freightT", label: "Transport $/t", type: "number", step: "0.01", max: 1000000, value: r?.freightT ?? 0, required: true },
      { id: "origin", label: "Origin", type: "text", maxlength: 160, pattern: RE.name },
      { id: "approved", label: "Approved", type: "checkbox", value: false, hint: "Approved for use" }
    ], (v) => api("/api/materialiq/rate", { idPlant: Number(v.idPlant), idMaterial: Number(v.idMaterial), idSupplier: Number(v.idSupplier),
        effectiveDate: v.effectiveDate, productPriceT: v.productPriceT, freightT: v.freightT, origin: v.origin || null, approved: v.approved }).then((r) => { if (state.tab === "suppliers") suppliers(); return r; }));
    $("mqNewRate").onclick=()=>rateForm();
    renderPaged($("mqHost"), {
      colspan: 9, empty: "No rates yet.",
      headHtml: `<tr><th>Plant</th><th>Material</th><th>Supplier</th><th>Effective</th><th>Product $/t</th><th>Freight $/t</th><th>Delivered $/t</th><th>Approved</th><th></th></tr>`,
      rowsHtml: rates.map((r) => `<tr><td>${esc(r.plant)}</td><td>${esc(r.matNumber)} ${esc(r.material)}</td><td>${esc(r.supplier)}</td>
        <td>${r.effectiveDate}</td><td>${usd(r.productPriceT)}</td><td>${usd(r.freightT)}</td><td><b>${usd(r.delivered)}</b></td>
        <td>${r.approved ? '<span class="mq-pill ok">Yes</span>' : '<span class="mq-pill muted">No</span>'}</td>
        <td><button class="btn secondary" data-edit-rate="${r.idRate}">Change price</button> ${r.approved ? "" : `<button class="btn secondary" data-approve="${r.idRate}">Approve</button>`}</td></tr>`),
      wire: (host) => { host.querySelectorAll("[data-edit-rate]").forEach(b=>b.onclick=()=>rateForm(rates.find(r=>r.idRate===Number(b.dataset.editRate)))); host.querySelectorAll("[data-approve]").forEach((b) => b.addEventListener("click", (e) =>
        api("/api/materialiq/rate/approve", { id: Number(e.target.dataset.approve) }).then((r) => { toast(r.mensaje); suppliers(); }))); }
    });
  }

  function weekStart(date){const d=new Date(date+"T12:00:00Z");return addDays(date,-((d.getUTCDay()+6)%7));}
  function purchaseRange(){return {desde:state._purchaseFrom||state.date.slice(0,7)+"-01",hasta:state._purchaseTo||state.date};}
  function rangeControls(){const {desde,hasta}=purchaseRange();return `<div class="toolbar"><div class="filters">
    <label>From <input id="mqFrom" type="date" value="${desde}"></label><label>To <input id="mqTo" type="date" value="${hasta}"></label>
    <label>SKU <select id="mqMat"><option value="">All</option>${state.materials.map(m=>`<option value="${m.idMaterial}" ${String(m.idMaterial)===String(state._purchaseMat)?"selected":""}>${esc(m.matNumber+" "+m.name)}</option>`).join("")}</select></label>
    <label>Group by <select id="mqPeriod">${["day","week","month"].map(x=>`<option ${x===(state._purchasePeriod||"day")?"selected":""}>${x}</option>`).join("")}</select></label>
    <button class="btn secondary" id="mqApplyRange">Apply</button><button class="btn secondary" id="mqExportPurchases">Export purchases CSV</button></div></div>`;}
  function wireRange(refresh){
    $("mqApplyRange").onclick=()=>{if(!$("mqFrom").value||!$("mqTo").value||$("mqFrom").value>$("mqTo").value)return toast("Select a valid date range.",true);
      state._purchaseFrom=$("mqFrom").value;state._purchaseTo=$("mqTo").value;state._purchaseMat=$("mqMat").value;state._purchasePeriod=$("mqPeriod").value;refresh();};
    $("mqExportPurchases").onclick=async()=>{const {desde,hasta}=purchaseRange();const rows=await api(`/api/materialiq/purchases?desde=${desde}&hasta=${hasta}${state.plant?"&plant="+state.plant:""}`);
      downloadCsv("purchases.csv",[["Date","Plant","SKU","Supplier","Tons","Product USD/t","Transportation USD/t","Total USD","Status","Ticket"]].concat(rows.filter(p=>p.status!=="Voided"&&(!state._purchaseMat||p.idMaterial===Number(state._purchaseMat))).map(p=>[p.purchaseDate,p.plant,p.matNumber,p.supplier,p.tons,p.productPriceT,p.freightT,p.total,p.status,p.ticketRef])));};
  }
  // ---- PURCHASE CHARTS ----------------------------------------------
  async function charts() {
    const {desde,hasta}=purchaseRange(),modo=state._chartModo||"received";
    const d=await api(`/api/materialiq/charts?desde=${desde}&hasta=${hasta}&modo=${modo}${state.plant?"&plant="+state.plant:""}${state._purchaseMat?"&material="+state._purchaseMat:""}`);
    $("view").innerHTML=`${rangeControls()}<label>Mode <select id="mqChMode"><option value="received" ${modo==="received"?"selected":""}>Received by receipt date</option><option value="recorded" ${modo==="recorded"?"selected":""}>Recorded by purchase date</option></select></label>
      <div class="mq-panels"><div class="mq-box"><h3>Materials — tons</h3>${barsHtml(d.materiales.map(m=>({label:m.matNumber+" "+m.name,value:m.tons})),tn)}</div>
      <div class="mq-box"><h3>Materials — product + transportation USD</h3>${barsTwoTone(d.materiales.map(m=>({label:m.matNumber+" "+m.name,a:m.product,b:m.freight})).sort((a,b)=>(b.a+b.b)-(a.a+a.b)),usd)}</div></div>
      <div class="mq-panels"><div class="mq-box"><h3>Plants — tons</h3>${barsHtml(d.plantas.map(p=>({label:p.plant,value:p.tons})),tn)}</div>
      <div class="mq-box"><h3>Plants — product + transportation USD</h3>${barsTwoTone(d.plantas.map(p=>({label:p.plant,a:p.product,b:p.freight})).sort((a,b)=>(b.a+b.b)-(a.a+a.b)),usd)}</div></div>
      <p class="mq-note">Dark green: product. Light green: transportation. Voided purchases are excluded.</p>`;
    wireRange(charts);$("mqChMode").onchange=e=>{state._chartModo=e.target.value;charts();};
  }

  // ---- HISTORY & METHODOLOGY ----------------------------------------
  async function history() {
    const desde = addDays(state.date, -30), hasta = state.date, entity = state._histEntity || "";
    const raw = await api(`/api/materialiq/history?desde=${desde}&hasta=${hasta}${entity ? "&entity=" + entity : ""}`);
    const list=raw.filter(h=>!state._histUser||h.by===state._histUser);
    const ents = ["", "plan_selection", "demand", "purchase", "closing_balance", "consumption", "plant_material", "supplier", "supplier_rate", "plant", "material"];
    $("view").innerHTML = `<div class="toolbar"><div class="filters"><small>Business changes ${desde} → ${hasta}</small>
      <label>User <select id="mqHistUser"><option value="">All users</option>${[...new Set(raw.map(h=>h.by).filter(Boolean))].map(u=>`<option ${u===state._histUser?"selected":""}>${esc(u)}</option>`).join("")}</select></label><label>Movement <select id="mqHistEnt">${ents.map((e) => `<option value="${e}" ${e === entity ? "selected" : ""}>${e || "All"}</option>`).join("")}</select></label></div></div><div id="mqHost"></div>`;
    $("mqHistUser").onchange=e=>{state._histUser=e.target.value;history();};
    $("mqHistEnt").addEventListener("change", (e) => { state._histEntity = e.target.value; history(); });
    renderPaged($("mqHost"), {
      colspan: 8, empty: "No changes in range.",
      headHtml: `<tr><th>When (UTC)</th><th>Movement</th><th>Action</th><th>Field</th><th>Old → New</th><th>Reason</th><th>By</th><th>Plant / SKU / supplier / ticket</th></tr>`,
      rowsHtml: list.map((h) => `<tr><td>${h.at}</td><td>${esc(h.entity)}${h.entityId ? " #" + h.entityId : ""}</td><td>${esc(h.action)}</td>
        <td>${esc(h.field || "—")}</td><td>${esc(h.oldValue || "—")} → ${esc(h.newValue || "—")}</td><td>${esc(h.reason || "—")}</td><td>${esc(h.by || "—")}</td><td>${esc([h.plant,h.material,h.supplier,h.ticket].filter(Boolean).join(" / ") || "Not recorded")}</td></tr>`)
    });
  }

  // ---- ADMINISTRATION (policy + admin) ------------------------------
  async function setup() {
    const pol = await api(`/api/materialiq/policies${state.plant ? "?plant=" + state.plant : ""}`);
    $("view").innerHTML = `<div class="toolbar"><div class="filters"><small>Plants, materials and inventory policy</small></div>
      <div><button class="btn secondary" id="mqAddPlant">+ Plant</button><button class="btn secondary" id="mqAddMat">+ Material</button><button class="btn secondary" id="mqAssign">Assign material</button></div></div><div id="mqHost"></div>`;
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
      headHtml: `<tr><th>Plant</th><th>Mat #</th><th>Material</th><th>Safety</th><th>Max cap</th><th>Lead days</th><th>Lot</th><th></th></tr>`,
      rowsHtml: pol.map((p) => `<tr><td>${esc(p.plant)}</td><td>${esc(p.matNumber)}</td><td>${esc(p.material)}</td>
        <td>${tn(p.safetyStockT)}</td><td>${p.maxCapacityT == null ? "—" : tn(p.maxCapacityT)}</td><td>${p.leadTimeDays}</td><td>${tn(p.purchaseLotT)}</td>
        <td><button class="btn secondary" data-pol='${esc(JSON.stringify(p))}'>Edit</button></td></tr>`),
      wire: (host) => host.querySelectorAll("[data-pol]").forEach((b) => b.addEventListener("click", (e) => {
        const p = JSON.parse(e.target.dataset.pol);
        openForm(`Policy — ${p.matNumber} ${p.material} @ ${p.plant}`, [
          { id: "safetyStockT", label: "Safety stock t", type: "number", step: "0.01", max: 100000000, value: p.safetyStockT, required: true },
          { id: "maxCapacityT", label: "Max capacity t", type: "number", step: "0.01", max: 100000000, value: p.maxCapacityT ?? "" },
          { id: "leadTimeDays", label: "Lead time days", type: "number", integer: true, max: 90, value: p.leadTimeDays, required: true },
          { id: "assignedSupplierId", label: "Assigned supplier for savings comparison", type: "select", options: optSup(), value: p.assignedSupplierId ?? "" },
          { id: "purchaseLotT", label: "Purchase lot t", type: "number", step: "0.01", gt0: true, max: 100000000, value: p.purchaseLotT, required: true }
        ], (v) => api("/api/materialiq/policy", { idPlant: p.idPlant, idMaterial: p.idMaterial, safetyStockT: v.safetyStockT, maxCapacityT: v.maxCapacityT, leadTimeDays: v.leadTimeDays, purchaseLotT: v.purchaseLotT, assignedSupplierId: v.assignedSupplierId ? Number(v.assignedSupplierId) : null }).then((r) => { setup(); return r; }));
      }))
    });
  }

  // ---- boot ----------------------------------------------------------
  async function loadRefs() {
    const [plants, materials, suppliers] = await Promise.all([api("/api/materialiq/plants"), api("/api/materialiq/materials"), api("/api/materialiq/suppliers")]);
    state.plants = plants; state.materials = materials; state.suppliers = suppliers;
    $("fPlant").innerHTML = `<option value="">All plants</option>` + plants.map((p) => `<option value="${p.idPlant}">${esc(p.name)}</option>`).join("");
    $("fPlant").value = state.plant;
  }

  $("fPlant").addEventListener("change", (e) => { state.plant = e.target.value; go(state.tab); });
  $("fDate").addEventListener("change", (e) => { state.date = e.target.value || today(); go(state.tab); });
  $("mqRecord").addEventListener("click", () => formCompra(null));
  $("mqExport").addEventListener("click", async () => {
    try {
      const d = await api("/api/materialiq/overview?" + q());
      downloadCsv(`materialiq_${state.date}.csv`, [["Plant", "Mat #", "Material", "Stock t", "Value USD", "Demand tomorrow", "Coverage %", "Inbound t", "Suggested t", "Rate $/t", "Suggested $", "Status"]].concat(
        d.filas.map((f) => [f.plant, f.matNumber, f.material, f.stock ?? 0, f.valor ?? "", f.demanda == null ? "Pending" : f.demanda, f.cobertura == null ? "" : f.cobertura.toFixed(1), f.llegadas, f.comprarTn, f.tarifa ?? "", f.comprarUsd, STATUS[f.estado]])));
    } catch { toast("Could not export.", true); }
  });
  $("mqDlgCancel").addEventListener("click", () => $("mqDialog").close());
  $("mqDialog").addEventListener("cancel", (e) => { e.preventDefault(); $("mqDialog").close(); });

  $("fDate").value = state.date;
  buildNav();
  loadRefs().then(() => go("overview")).catch(() => ($("view").innerHTML = `<div class="mq-loading">Could not load MaterialIQ. Run db/materialiq_esquema.sql.</div>`));
})();
