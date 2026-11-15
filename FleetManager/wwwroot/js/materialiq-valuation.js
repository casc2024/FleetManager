// Confirmed inventory valuation. Supplier reference rates never create a historical cost split.
const valuationCents = value => Math.sign(value) * Math.round(Math.abs(value) * 100 + 1e-8);
function validValuedClosing(d) {
  const day = /^\d{4}-\d{2}-\d{2}$/.test(d.date || '') ? new Date(d.date + 'T12:00:00Z') : null;
  return day && !isNaN(day) && day.toISOString().slice(0, 10) === d.date && Number.isFinite(d.qty) && d.qty >= 0 && Number.isFinite(d.value) && d.value >= 0 &&
    ((d.productValue == null && d.freightValue == null) || (Number.isFinite(d.productValue) && d.productValue >= 0 && Number.isFinite(d.freightValue) && d.freightValue >= 0 && Math.abs(d.productValue + d.freightValue - d.value) <= 0.010000001));
}
function validInventoryCheckpoint(e) {
  if(e.type==='snapshot') return validValuedClosing(e.data);
  const d=e.data;
  return e.type==='inventoryAdjustment' && validValuedClosing({...d,productValue:null,freightValue:null}) &&
    ['productValue','freightValue','unallocatedValue','averageCost'].every(k=>Number.isFinite(d[k])&&d[k]>=0) &&
    Math.abs(d.productValue+d.freightValue+d.unallocatedValue+(d.roundingAdjustment||0)-d.value)<=0.010000001;
}
function confirmedInventoryLedger(i, at = date()) {
  const related = events.filter(e => ['snapshot','inventoryAdjustment'].includes(e.type) && e.data.plant === i.plant && e.data.sku === i.sku && e.data.date <= at);
  const snapshot = related.filter(validInventoryCheckpoint).sort((a,b) => a.data.date.localeCompare(b.data.date) || a.created.localeCompare(b.created) || a.id.localeCompare(b.id)).at(-1);
  const initial = snapshot?.data || i, start = initial.date;
  let q = initial.qty, value = Number.isFinite(initial.value) && initial.value >= 0 ? valuationCents(initial.value) : null;
  const adjusted=snapshot?.type==='inventoryAdjustment';
  const split = adjusted || validValuedClosing(initial) && initial.productValue != null;
  let product = value === null ? null : split ? valuationCents(initial.productValue) : 0;
  let freight = value === null ? null : split ? valuationCents(initial.freightValue) : 0;
  let unallocated = value === null ? null : adjusted ? valuationCents(initial.unallocatedValue) : split ? 0 : value;
  let averageCost=adjusted ? initial.averageCost : value!==null && q>0 ? value/100/q : null;
  let productUnitCost=adjusted ? initial.productUnitCost : value!==null && q>0 ? product/100/q : null;
  let freightUnitCost=adjusted ? initial.freightUnitCost : value!==null && q>0 ? freight/100/q : null;
  let unallocatedUnitCost=adjusted ? initial.unallocatedUnitCost : value!==null && q>0 ? unallocated/100/q : null;
  if (at < start) return { ...i, qty: 0, value: null, productValue: null, freightValue: null, unallocatedValue: null, roundingAdjustment: null, unavailable: true, start };
  const moves = [];
  for (const o of purchaseRows(at).filter(o => o.plant === i.plant && o.sku === i.sku)) {
    const receipt = events.find(e => e.type === 'receipt' && e.data.order === o.id && e.data.date <= at);
    const day = o.status === 'received' ? o.date : receipt?.data.date;
    if (day && day > start && day <= at) moves.push({ date: day, qty: o.qty, product: o.product, freight: o.freight, kind: 0, created: receipt?.created || events.find(e => e.id === o.id)?.created || '', id: o.id });
  }
  for (const e of events.filter(e => e.type === 'usage' && e.data.plant === i.plant && e.data.sku === i.sku && e.data.date > start && e.data.date <= at))
    moves.push({ ...e.data, kind: 1, created: e.created, id: e.id });
  for (const e of events.filter(e => e.type === 'quantityAdjustment' && e.data.plant === i.plant && e.data.sku === i.sku && Number.isFinite(e.data.qty) && e.data.qty >= 0 && e.data.date <= at &&
    (e.data.date > start || e.data.date === start && (!snapshot || e.created > snapshot.created))))
    moves.push({ ...e.data, kind: 2, created: e.created, id: e.id });
  let shortage = false;
  for (const m of moves.sort((a,b) => a.date.localeCompare(b.date) || a.kind-b.kind || a.created.localeCompare(b.created) || a.id.localeCompare(b.id))) {
    if (m.kind === 0) {
      if (value === null && q === 0) value = product = freight = unallocated = 0;
      if (value !== null) { value += valuationCents(m.qty * (m.product + m.freight)); product += valuationCents(m.qty * m.product); freight += valuationCents(m.qty * m.freight); }
      q += m.qty;
    } else if(m.kind===2) {
      if(value!==null && averageCost!==null) {
        value=valuationCents(m.qty*averageCost);product=valuationCents(m.qty*productUnitCost);
        freight=valuationCents(m.qty*freightUnitCost);unallocated=valuationCents(m.qty*unallocatedUnitCost);
      } else if(m.qty===0 && value!==null) value=product=freight=unallocated=0;
      else value=product=freight=unallocated=null;
      q=m.qty;
    } else {
      const remaining = q - m.qty;
      if (remaining < 0) { shortage = true; value = product = freight = unallocated = null; }
      else if (remaining === 0) value = product = freight = unallocated = 0;
      else if (value !== null && q > 0) { const ratio = remaining / q; value = Math.round(value * ratio); product = Math.round(product * ratio); freight = Math.round(freight * ratio); unallocated = Math.round(unallocated * ratio); }
      q = remaining;
    }
    if(value===null || q<0) averageCost=productUnitCost=freightUnitCost=unallocatedUnitCost=null;
    else if(q>0 && m.kind!==2) {averageCost=value/100/q;productUnitCost=product/100/q;freightUnitCost=freight/100/q;unallocatedUnitCost=unallocated/100/q;}
  }
  return { ...config(i, at), qty: q, value: value === null ? null : value / 100,
    productValue: product === null ? null : product / 100, freightValue: freight === null ? null : freight / 100,
    unallocatedValue: unallocated === null ? null : unallocated / 100,
    roundingAdjustment: value === null ? null : (value - product - freight - unallocated) / 100,
    averageCost,productUnitCost,freightUnitCost,unallocatedUnitCost,
    start, closingId: snapshot?.id || null, shortage, unavailable: false, invalidClosingCount: related.filter(e => !validInventoryCheckpoint(e)).length };
}
function confirmedInventoryTotal(items) {
  return items.filter(i => i.value !== null).reduce((sum,i) => sum + valuationCents(i.value),0) / 100;
}
function confirmedPlantTotals(items) {
  const groups = new Map();
  for (const i of items) {
    if (!groups.has(i.plant)) groups.set(i.plant, { plant: i.plant, total: 0, product: 0, freight: 0, unallocated: 0, adjustment: 0, known: 0, pending: 0 });
    const g = groups.get(i.plant);
    if (i.value === null) { g.pending++; continue; }
    g.known++; g.total += valuationCents(i.value); g.product += valuationCents(i.productValue);
    g.freight += valuationCents(i.freightValue); g.unallocated += valuationCents(i.unallocatedValue); g.adjustment += valuationCents(i.roundingAdjustment);
  }
  return [...groups.values()].map(g => ({ ...g, total: g.total/100, product: g.product/100, freight: g.freight/100,
    unallocated: g.unallocated/100, adjustment: g.adjustment/100, dark: (g.total-g.freight)/100 }));
}
function confirmedInventoryChart(items) {
  const groups = confirmedPlantTotals(items), max = Math.max(1,confirmedInventoryTotal(items));
  return `<section class="panel"><div class="panelhead"><div><h2>Confirmed inventory value by plant</h2><span class="hint">Confirmed physical inventory · product + transportation</span></div><span class="pill">USD</span></div>${groups.map(g => `<div class="barrow"><span>${esc(g.plant)}</span><div class="track" title="${esc('Confirmed product: '+money(g.product)+'; Cost without recorded breakdown: '+money(g.unallocated)+'; Confirmed transportation: '+money(g.freight)+'; Rounding adjustment: '+money(g.adjustment))}"><div class="fill" style="width:${Math.max(0,g.dark)/max*100}%"></div><div class="freight" style="width:${Math.max(0,g.freight)/max*100}%"></div></div><b>${g.known ? money(g.total) : 'Valuation pending'}</b></div><div class="hint">${esc(g.plant)} · Confirmed product: ${money(g.product)} · Cost without recorded breakdown: ${money(g.unallocated)} · Confirmed transportation: ${money(g.freight)}${g.adjustment ? ' · Rounding adjustment: '+money(g.adjustment) : ''}${g.pending ? ' · '+g.pending+' SKU values pending' : ''}</div>`).join('')}<p class="hint">● Dark green: confirmed product + cost without recorded breakdown (including any rounding adjustment). Dark green does not necessarily represent product alone.<br><span style="color:#91ac63">●</span> Light green: transportation recorded separately. No transportation share is inferred from historical totals.</p><p class="hint">Confirmed plant total: <b>${money(confirmedInventoryTotal(items))}</b> · Matches Inventory · confirmed cost for the selected cutoff and plant filter.</p></section>`;
}
function valuedBalanceForm(p, s) {
  modal('Confirm valued closing balance', common() + field('Physical inventory (tons)','fq','number','','required') +
    field('Total delivered value USD','fv','number','','required step="0.01"') +
    field('Confirmed product cost USD · optional','fProduct','number','','step="0.01"') +
    field('Confirmed transportation cost USD · optional','fFreight','number','','step="0.01"') +
    '<div class="note" style="grid-column:1/-1">Review the date, physical tons and confirmed total. Enter both actual cost components or leave both blank. Product + transportation must equal the total within USD 0.01. Blank components preserve the total as cost without recorded breakdown. A new closing balance replaces movements on or before that date; the prior values and signed-in user remain in history.</div>', () => {
      const product = val('fProduct').trim(), freight = val('fFreight').trim();
      if (!!product !== !!freight) throw Error('Enter both product and transportation costs, or leave both blank.');
      const total = nval('fv'), productValue = product === '' ? null : nval('fProduct'), freightValue = freight === '' ? null : nval('fFreight');
      if (productValue !== null && Math.abs(productValue + freightValue - total) > 0.010000001) throw Error('Product plus transportation must equal the confirmed total within USD 0.01.');
      return save('snapshot', { plant: val('fp'), sku: val('fs'), date: val('fd'), qty: nval('fq'), value: total, productValue, freightValue });
    });
  const fill = () => {
    const inventory = seed.inventories.find(i => i.plant === val('fp') && i.sku === val('fs'));
    const current = inventory ? ledger(inventory, val('fd')) : null;
    $('fq').value = current && !current.unavailable ? current.qty : '';
    $('fv').value = current?.value ?? '';
    const complete = current?.value != null && current.unallocatedValue === 0;
    $('fProduct').value = complete ? current.productValue : '';
    $('fFreight').value = complete ? current.freightValue : '';
  };
  if (p && s) { $('fp').value = p; syncFormSku(s); $('fs').value = s; }
  $('fp').onchange = fill; $('fs').onchange = fill; $('fd').onchange = fill; fill();
}
function inventoryTonsAdjustmentForm(p,s) {
  const currentBalance=()=>{
    const initial=seed.inventories.find(i=>i.plant===val('fp')&&i.sku===val('fs'));
    return initial ? ledger(initial,val('fd')) : null;
  };
  modal('Adjust inventory tons only',common()+field('Physical tons counted','fq','number','','required')+
    field('Reason · optional','reason','text','','maxlength="300"')+
    '<div id="tonsAdjustmentInfo" class="note" style="grid-column:1/-1"></div>',()=>{
      if(!val('fq').trim()) throw Error('Enter the physical tons counted.');
      const counted=nval('fq'),current=currentBalance();
      if(!current || current.unavailable) throw Error('Select a valid plant, material and adjustment date.');
      if(counted===current.qty) throw Error('Enter physical tons different from the recorded balance.');
      if(current.value===null || current.averageCost==null || current.qty<0) throw Error('A confirmed average cost is required. Use Update valued balance first.');
      return save('inventoryAdjustment',{plant:val('fp'),sku:val('fs'),date:val('fd'),qty:counted,reason:val('reason').trim()});
    });
  const fill=()=>{
    const current=currentBalance();
    $('fq').value=current&&!current.unavailable ? current.qty : '';
    $('tonsAdjustmentInfo').textContent=`Recorded balance: ${current&&!current.unavailable?num(current.qty)+' t':'Pending'}. Confirmed average cost: ${current?.averageCost!=null?money(current.averageCost)+' / t':'Pending'}. Adjusted value = counted tons × confirmed average cost. The reason is optional. This count replaces the balance on this date and is not a purchase or actual consumption.`;
  };
  if(p&&s){$('fp').value=p;syncFormSku(s);$('fs').value=s;}
  $('fp').onchange=fill;$('fs').onchange=fill;$('fd').onchange=fill;fill();
}
function replacementInventoryChart(items) {
  const groups=plants.filter(p=>!plant()||p===plant()).map(p=>({plant:p,...items.filter(i=>i.plant===p).reduce((a,i)=>{
    const rate=choices(i)[0];if(rate){a.product+=i.qty*rate.product;a.freight+=i.qty*rate.freight;a.known++;}else a.pending++;
    return a;
  },{product:0,freight:0,known:0,pending:0})}));
  const reference=groups.reduce((sum,g)=>sum+g.product+g.freight,0);
  return `<section class="panel"><div class="panelhead"><div><h2>Material inventory value by plant</h2><span class="hint">Replacement reference · product + transportation</span></div><span class="pill">USD</span></div>${groups.map(g=>`<div class="barrow"><span>${esc(g.plant)}</span><div class="track"><div class="fill" style="width:${g.product/(reference||1)*100}%"></div><div class="freight" style="width:${g.freight/(reference||1)*100}%"></div></div><b>${g.known?money(g.product+g.freight):'Rates pending'}</b></div>${g.pending?`<div class="hint">${g.pending} SKU rates pending</div>`:''}`).join('')}<div class="hint">● Product &nbsp; <span style="color:#91ac63">● Transportation</span></div><p class="hint">Reference rates applied to current physical tons. This is replacement reference, separate from confirmed inventory cost.</p></section>`;
}
