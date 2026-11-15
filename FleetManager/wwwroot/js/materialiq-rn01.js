// RN-01: server-owned immutable evaluations; local preview never creates purchase orders.
let rn01State = { cutoff: '', versions: [], history: [], loading: false, error: '' };
let rn01Request = 0;
async function refreshSuggestions() {
  const cutoff = date(), request = ++rn01Request;
  rn01State = { ...rn01State, cutoff, versions: [], loading: true, error: '' };
  try {
    const response = await fetch('/api/materialiq/model/evaluations', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ cutoff })
    });
    const result = await response.json().catch(() => null);
    if (!response.ok || !result?.versions || !result?.history) throw Error(result?.error || 'Unable to save the suggestion evaluation. Reload to retry.');
    if (request !== rn01Request || cutoff !== date()) return;
    rn01State = { cutoff, versions: result.versions, history: result.history, loading: false, error: '' };
  } catch (error) {
    if (request !== rn01Request) return;
    rn01State = { ...rn01State, loading: false, error: error.message };
    throw error;
  }
}
async function reloadEvaluation() {
  const task = refreshSuggestions(); render();
  try { await task; } catch (error) { toast(error.message); }
  render();
}
function suggestionPeriod(start = add(date(), 1), end = add(date(), 7)) {
  const a = new Date(start + 'T12:00:00Z'), b = new Date(end + 'T12:00:00Z');
  const fmt = d => new Intl.DateTimeFormat('en-US', { timeZone: 'UTC', month: 'long', day: 'numeric', year: 'numeric' }).format(d);
  if (start === end) return fmt(a);
  if (start.slice(0, 7) === end.slice(0, 7)) return `${a.toLocaleString('en-US', { month: 'long', timeZone: 'UTC' })} ${a.getUTCDate()}–${b.getUTCDate()}, ${b.getUTCFullYear()}`;
  return `${fmt(a)} – ${fmt(b)}`;
}
function rn01Version(plant, sku) {
  return rn01State.cutoff === date() ? rn01State.versions.find(v => v.result.input.plant === plant && v.result.input.sku === sku) : null;
}
function rn01Plan(i) {
  const version = rn01Version(i.plant, i.sku), saved = version?.result;
  const days = Array.from({ length: 7 }, (_, k) => add(date(), k + 1));
  const ds = saved ? saved.input.demand : days.map(d => plan(i.plant, i.sku, d));
  const localRate = supplyChoice(i).selected;
  const best = saved ? (saved.input.rate ? { ...choices(i).find(r => r.supplier === saved.input.rate.supplier), ...saved.input.rate } : null) : localRate;
  const current = choices(i).find(r => r.supplier === i.supplier);
  let daily = saved?.days;
  if (!daily) {
    const orders = purchaseRows().filter(o => o.plant === i.plant && o.sku === i.sku && !received(o));
    let balance = i.unavailable || i.qty < 0 || i.safety == null || i.lot <= 0 ? null : i.qty;
    daily = days.map((day, k) => {
      const opening = balance, inbound = orders.filter(o => o.arrival === day).reduce((sum, o) => sum + o.qty, 0);
      const shortfall = balance === null || ds[k] === null ? null : Math.max(0, ds[k] + i.safety - balance - inbound);
      const buy = shortfall === null ? null : Math.ceil(shortfall / i.lot) * i.lot;
      const before = buy === null ? null : balance + inbound + buy;
      const closing = before === null ? null : before - ds[k]; balance = closing;
      return { date: day, demand: ds[k], inbound, opening, shortfall, buy, cost: buy === null ? null : buy === 0 ? 0 : best ? buy * (best.product + best.freight) : null,
        closing, capacityRisk: before !== null && i.capacity != null && before > i.capacity };
    });
  }
  const missing = ds.filter(d => d === null).length, prefixTons = daily.reduce((sum, d) => sum + (d.buy ?? 0), 0);
  const needed = saved ? saved.tons : missing && prefixTons === 0 ? null : daily[0].opening === null ? null : prefixTons;
  const cost = saved ? saved.cost : needed === null || daily.some(d => d.buy > 0 && d.cost === null) ? null : daily.reduce((sum, d) => sum + (d.cost ?? 0), 0);
  const status = saved?.status || (daily[0].opening === null ? 'Data pending' : missing && !prefixTons ? 'Demand pending' : cost === null ? 'Rate pending' : missing ? 'Provisional' : prefixTons ? 'Purchase suggested' : 'No purchase needed');
  const first = daily.find(d => d.buy > 0)?.date || '', total = ds.reduce((sum, d) => sum + (d ?? 0), 0);
  return { versionId: version?.id, version: version?.version, status, days, ds, daily, total, best, current, needed, cost, first,
    chart: daily.map(d => d.closing), missing, late: !!first && first < add(date(), i.lead), capacityRisk: daily.some(d => d.capacityRisk),
    excess: Math.max(0, i.qty - total - i.safety), saving: needed === null ? null : best && current ? Math.max(0, (current.product + current.freight - best.product - best.freight) * needed) : 0 };
}
function rn01Tons(p) { return p.needed === null ? p.status : `${num(p.needed)} t${p.missing ? ' · Provisional' : ''}`; }
function rn01Amount(p) { return p.cost === null ? p.status : `${money(p.cost)}${p.missing ? ' · Provisional' : p.needed === 0 ? ' · No purchase needed' : ''}`; }
function rn01Summary(plans) {
  const period = suggestionPeriod();
  if (rn01State.loading) return { value: 'Evaluating…', hint: period + ' · Saving evaluation history' };
  if (rn01State.error) return { value: 'Evaluation unavailable', hint: period + ' · ' + rn01State.error };
  if (!plans.length) return { value: 'Data pending', hint: period + ' · No confirmed inventory for this cutoff and plant filter' };
  const tons = plans.reduce((a, p) => a + (p.needed ?? 0), 0), cost = plans.reduce((a, p) => a + (p.cost ?? 0), 0);
  const missing = plans.reduce((a, p) => a + p.missing, 0), dataPending = plans.some(p => p.status === 'Data pending');
  let value = dataPending ? 'Data pending' : missing && tons === 0 ? 'Demand pending' :
    plans.some(p => p.needed > 0 && p.cost === null) ? 'Rate pending' : `${money(cost)}${missing ? ' · Provisional' : tons === 0 ? ' · No purchase needed' : ''}`;
  return { value, hint: `${period} · ${tons > 0 ? num(tons) + ' t · ' : ''}${missing ? missing + ' pending daily demand entries' : 'All seven demand days confirmed'}${dataPending ? ' · Confirm inventory and policy' : ''}` };
}
function rn01TomorrowRows() {
  return rows().map(i => {
    const p = procurement(i), day = p.daily[0], d = day.demand;
    return { i, d, inbound: day.inbound, qty: day.buy, supplier: p.best, coverage: skuCoverage(i.qty, d, i.safety),
      loads: day.buy === null ? null : day.buy / i.lot, balance: day.closing, capacityRisk: day.capacityRisk, late: day.buy > 0 && i.lead > 1 };
  });
}
function rn01PurchaseNote(id) {
  const v = rn01State.history.find(v => v.id === id);
  if (!v) return '';
  return `<div class="note" style="grid-column:1/-1">Review suggestion v${v.version} · ${esc(v.result.input.plant)} / ${esc(v.result.input.sku)} · ${esc(suggestionPeriod(v.result.periodStart, v.result.periodEnd))}<br>${num(v.result.tons)} t · ${money(v.result.cost)} · ${esc(v.result.status)}. This order will remain linked to this exact version.</div>`;
}
function rn01CheckPurchaseLink(id, plant, sku) {
  if (!id) return;
  const v = rn01State.history.find(v => v.id === id);
  if (!v || v.result.input.plant !== plant || v.result.input.sku !== sku) throw Error('This suggestion belongs to another plant or material. Open a new purchase form to change them.');
}
function rn01BuySupply(k) {
  const i = ledger(supplyIndex(k)), p = procurement(i), r = p.best;
  if (!p.versionId || rn01State.loading || rn01State.error) { toast('Wait for a saved evaluation before recording the suggested purchase.'); return; }
  if (!(p.needed > 0) || p.cost === null || !r) { toast('Complete the required data and review the suggested purchase first.'); return; }
  purchaseForm(i.plant, i.sku, r.id, p.needed, p.versionId);
  const submit = document.getElementById('form')?.querySelector?.('button[type="submit"]');
  if (submit) submit.textContent = 'Record purchase';
}
function renderSuggestionHistory() {
  let section = document.getElementById('rn01History');
  if (!section) { section = document.createElement('section'); section.id = 'rn01History'; section.className = 'panel'; document.getElementById('content').append(section); }
  const versions = rn01State.history.filter(v => !plant() || v.result.input.plant === plant());
  section.innerHTML = `<h2>Purchase suggestion history · RN-01</h2><p>Immutable versions for every evaluated cutoff. A suggestion is not a purchase order. Refreshing unchanged inputs reuses the saved version.</p>${table(['Created','User','Cutoff / evaluated period','Plant / SKU','Version','Suggested tons','Suggested cost','Status','Review'], versions.map(v => `<tr><td>${esc(new Intl.DateTimeFormat('en-US', {timeZone:'America/Chicago',dateStyle:'short',timeStyle:'short'}).format(new Date(v.created)))}</td><td>${esc(v.user)}</td><td>${esc(v.result.input.cutoff)}<small>${esc(suggestionPeriod(v.result.periodStart,v.result.periodEnd))}</small></td><td>${esc(v.result.input.plant)}<small>${esc(v.result.input.sku)}</small></td><td>${v.version}</td><td>${v.result.tons === null ? 'Demand pending' : num(v.result.tons)}</td><td>${v.result.cost === null ? esc(v.result.status) : money(v.result.cost)}</td><td>${esc(v.result.status)}<small>${v.result.pendingDays} demand days pending</small></td><td><button onclick="openSuggestionVersion('${v.id}')">Review version</button></td></tr>`).join(''))}`;
}
function openSuggestionVersion(id) {
  const v = rn01State.history.find(v => v.id === id); if (!v) return;
  const r = v.result, i = r.input, orders = i.orders.map(o => `${esc(o.id)} · ${esc(o.arrival)} · ${num(o.qty)} t`).join('<br>');
  const linked = purchaseRows('9999-12-31', true).filter(o => o.suggestionId === id);
  const dlg = document.getElementById('modal');
  dlg.innerHTML = `<h2>Purchase suggestion · version ${v.version}</h2>${rn01PurchaseNote(id)}<p>Cutoff: ${esc(i.cutoff)} · Evaluated by ${esc(v.user)}</p><p>Confirmed inventory: ${i.inventory === null ? 'Pending' : num(i.inventory)+' t'} · Balance date: ${esc(i.balanceDate || 'Pending')}<br>Safety stock: ${i.safety === null ? 'Pending' : num(i.safety)+' t'} · Purchase lot: ${i.lot === null ? 'Pending' : num(i.lot)+' t'}</p><p>Applied supplier: ${esc(i.rate?.supplier || 'Rate pending')}<br>Product: ${i.rate ? money(i.rate.product) : 'Pending'} / t · Transportation: ${i.rate ? money(i.rate.freight) : 'Pending'} / t · ${i.rate?.approved ? 'Approved' : 'Approval pending'}</p><p>Scheduled inbound orders: ${orders || 'None in this period'}</p>${table(['Date','Demand (t)','Opening (t)','Inbound (t)','Shortfall (t)','Suggested purchase (t)','Cost','Closing (t)'],r.days.map(d=>`<tr>${[d.date,d.demand,d.opening,d.inbound,d.shortfall,d.buy,d.cost,d.closing].map((value,k)=>`<td>${value===null?'Pending':k===0?esc(value):k===6?money(value):num(value)}</td>`).join('')}</tr>`).join(''))}<p>${r.pendingDays} demand days pending. Days after a missing demand remain uncalculated; known earlier purchases are provisional.</p><p>Linked purchase orders: ${linked.length ? linked.map(o=>esc(o.ref)+' · '+(o.voided?'Voided':received(o)?'Received':'In transit')).join(', ') : 'None. This version has not been recorded as a purchase.'}</p><button type="button" onclick="document.getElementById('modal').close()">Close</button>`;
  dlg.showModal();
}
