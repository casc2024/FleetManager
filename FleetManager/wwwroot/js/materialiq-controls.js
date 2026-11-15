async function exportExcel(title, headers, rows, sheets) {
  try {
    const response = await fetch('/api/materialiq/model/export', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(sheets ? { title, sheets } : { title, headers, rows })
    });
    if (!response.ok || response.redirected) {
      const detail = await response.json().catch(() => null);
      if (response.status === 401 || response.redirected) throw Error('Your session has expired. Sign in again to download the Excel report.');
      if (response.status === 403) throw Error('Your account does not have permission to export this report.');
      if (response.status === 404) throw Error('Excel export is unavailable in this server version. Update and restart the application.');
      throw Error(detail?.error || detail?.detail || `Excel export failed (HTTP ${response.status}). Please retry or report this error.`);
    }
    if (!response.headers.get('Content-Type')?.includes('application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'))
      throw Error('The server did not return an Excel file. Reload the page and retry.');
    const url = URL.createObjectURL(await response.blob());
    const link = document.createElement('a'); link.href = url;
    link.download = 'JCK-' + title.replace(/[^a-z0-9]+/gi, '-') + '.xlsx'; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    toast('Excel report downloaded.');
  } catch (error) { toast(error.message); }
}
const screenExportTables = {
  dashboard: ['Purchasing priorities'],
  planning: ['Confirm next-day demand', 'Dispatch planning', 'Next-day requirements', 'Weekly forecast & supply'],
  inventory: ['Balances & inventory policy', 'Working capital release opportunity'],
  purchases: ['Purchases by plant and SKU', 'Purchase details · edit or void'],
  rates: ['Product + transportation = delivered cost'],
  analytics: ['Details by plant and material']
};
function exportCurrentScreen(screen) {
  const titles = screenExportTables[screen];
  if (!titles) return;
  const tables = [...document.querySelectorAll('#content table')];
  const sheets = titles.map(title => {
    if (title === 'Confirm next-day demand') return {
      title, headers: ['Date', 'Plant', 'Material / SKU', 'Required demand (tons)', 'Status'],
      rows: tomorrowRows().map(x => [add(date(), 1), x.i.plant, x.i.sku, x.d, x.d === null ? 'Pending' : 'Confirmed'])
        .sort((a, b) => (a[1] + ' ' + a[2]).localeCompare(b[1] + ' ' + b[2], 'en-US', { numeric: true }))
    };
    const table = tables.find(table => table.closest('section')?.querySelector('h2')?.textContent.trim() === title ||
      (title === 'Dispatch planning' && table.querySelector('th')?.textContent.trim() === 'Plant / SKU' && table.closest('section')?.textContent.includes('2. Dispatch planning')));
    if (table?.materialIqExport) return { ...table.materialIqExport(), title };
    if (title === 'Dispatch planning') return {
      title, headers: ['Date', 'Plant', 'Material / SKU', 'Status'],
      rows: tomorrowRows().map(x => [add(date(), 1), x.i.plant, x.i.sku, 'Planning pending: confirm all next-day demand.'])
    };
    throw Error(`The table "${title}" is not ready. Wait for it to load and retry.`);
  });
  return exportExcel(names[screen] || 'MaterialIQ', null, null, sheets);
}
function englishValidationMessage(input) {
  const v = input.validity;
  if (v.valueMissing) return 'This field is required.';
  if (v.customError) return input.validationMessage;
  if (v.badInput || v.typeMismatch) return input.type === 'number' ? 'Enter a valid number.' : 'Enter a valid value.';
  if (v.patternMismatch) return input.placeholder === 'MM/DD/YYYY' ? 'Enter a valid date in MM/DD/YYYY format.' : 'Use the requested format.';
  if (v.rangeUnderflow) return `Enter a value greater than or equal to ${input.min}.`;
  if (v.rangeOverflow) return `Enter a value less than or equal to ${input.max}.`;
  if (v.stepMismatch) return input.step === '1' ? 'Enter a whole number.' : 'Enter a value using the allowed increment.';
  if (v.tooLong) return `Enter no more than ${input.maxLength} characters.`;
  return '';
}
// Shared controls for every MaterialIQ table and data-entry form.
(() => {
  const isoDate = value => {
    const m = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(value);
    if (!m) return '';
    const iso = `${m[3]}-${m[1]}-${m[2]}`;
    const d = new Date(iso + 'T12:00:00Z');
    return !isNaN(d) && d.toISOString().slice(0, 10) === iso ? iso : '';
  };
  const usDate = value => value.replace(/\b(\d{4})-(\d{2})-(\d{2})\b/g, '$2/$3/$1');
  const isTextField = input => input instanceof HTMLInputElement && input.type === 'text' && !input.pattern && !!input.closest('form');
  const cleanText = (input, text) => input.id === 'ref'
    ? text.replace(/[^\p{L}\p{N} ._\/-]/gu, '')
    : text.replace(/[^\p{L}\p{M}\p{N} .,'&()/#:_\-]/gu, '').replace(/([&/#:_.,'()\-])\1+/gu, '$1');
  function enhance(root) {
    if(typeof enhanceUsCurrency==='function')enhanceUsCurrency(root);
    root.querySelectorAll('form').forEach(form => { form.noValidate = true; });
    root.querySelectorAll('input[type="date"]').forEach(input => {
      if (input.dataset.usDate) return;
      input.dataset.usDate = 'true';
      const visible = document.createElement('input');
      visible.type = 'text'; visible.placeholder = 'MM/DD/YYYY';
      visible.setAttribute('aria-label', (input.closest('label')?.textContent.trim() || 'Date') + ' (MM/DD/YYYY)');
      visible.pattern = '\\d{2}/\\d{2}/\\d{4}'; visible.required = input.required || input.id === 'date';
      visible.maxLength = 10;
      visible.value = usDate(input.value);
      const group = document.createElement('span');
      group.style.cssText = 'display:flex;position:relative;align-items:center;gap:6px;width:100%';
      if(input.id==='date') group.style.cssText = 'display:flex;position:relative;align-items:center;gap:6px;width:220px;flex:0 0 auto;max-width:100%';
      input.before(group); group.append(visible);
      visible.style.cssText = 'flex:1;min-width:0';
      const calendar = document.createElement('button');
      calendar.type = 'button'; calendar.textContent = '📅';
      calendar.setAttribute('aria-label', 'Open calendar'); calendar.title = 'Choose date';
      calendar.style.cssText = 'padding:8px;flex:0 0 42px';
      group.append(calendar, input);
      input.hidden = false; input.required = false; input.tabIndex = -1;
      input.setAttribute('aria-label', 'Choose date using calendar');
      input.style.cssText = 'position:absolute;right:0;bottom:0;width:42px;height:100%;opacity:0;cursor:pointer;pointer-events:none';
      const descriptor = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
      Object.defineProperty(input, 'value', {
        get() { return descriptor.get.call(this); },
        set(value) { descriptor.set.call(this, value); visible.value = usDate(this.value); visible.setCustomValidity(''); }
      });
      calendar.addEventListener('click', () => {
        if (typeof input.showPicker === 'function') {
          try { input.showPicker(); return; } catch { /* Enable the native control as a fallback. */ }
        }
        input.style.pointerEvents = 'auto'; input.focus(); input.click();
      });
      input.addEventListener('change', () => {
        visible.value = usDate(input.value); visible.setCustomValidity('');
      }, true);
      visible.addEventListener('input', () => {
        const iso = isoDate(visible.value);
        visible.setCustomValidity(visible.value && !iso ? 'Enter a valid date in MM/DD/YYYY format.' : '');
        descriptor.set.call(input, iso);
      });
      visible.addEventListener('change', () => {
        let error = group.querySelector('[data-date-error]');
        if (!error) {
          error = document.createElement('span'); error.dataset.dateError = 'true';
          error.setAttribute('role', 'alert'); error.style.cssText = 'color:#a14332;font-size:12px';
          group.append(error);
        }
        error.textContent = englishValidationMessage(visible);
        if (!error.textContent) input.dispatchEvent(new Event('change', { bubbles: true }));
      });
    });
    root.querySelectorAll('table').forEach(table => {
      if (table.dataset.paged) return;
      table.dataset.paged = 'true';
      const headings = [...table.querySelectorAll('thead th')].map(th => th.textContent.trim());
      const plantIndex = headings.findIndex(h => /plant/i.test(h));
      const skuIndex = headings.findIndex(h => /sku|material/i.test(h));
      const body = table.tBodies[0];
      if (!body) return;
      const rows = [...body.rows].filter(row => !row.querySelector('td.empty'));
      const key = row => [plantIndex, skuIndex].filter((v, i, a) => v >= 0 && a.indexOf(v) === i)
        .map(i => row.cells[i]?.textContent || '').join(' ');
      rows.sort((a, b) => key(a).localeCompare(key(b), 'en-US', { numeric: true, sensitivity: 'base' }));
      const wrap = table.parentElement;
      const toolbar = document.createElement('div'); toolbar.className = 'filters';
      const search = document.createElement('input'); search.type = 'search';
      search.placeholder = 'Search by plant or material'; search.setAttribute('aria-label', search.placeholder);
      toolbar.append(search); wrap.before(toolbar);
      table.materialIqExport = () => {
        const indices = headings.map((heading, index) => heading ? index : -1).filter(index => index >= 0);
        const term = search.value.trim().toLocaleLowerCase('en-US');
        return {
          headers: indices.map(index => headings[index]),
          rows: rows.filter(row => (!term || (key(row) || row.textContent).toLocaleLowerCase('en-US').includes(term)) &&
            (!row.classList.contains('voided-purchase') || document.getElementById('showVoided')?.checked))
            .map(row => indices.map(index => {
              const cell = row.cells[index];
              const clone = cell.cloneNode(true);
              clone.querySelectorAll('button,input,select').forEach(control => control.remove());
              clone.querySelectorAll('small').forEach(small => small.prepend(document.createTextNode(' · ')));
              const text = clone.textContent.trim().replace(/\s+/g, ' ').replace(/\b(\d{4})-(\d{2})-(\d{2})\b/g, '$2/$3/$1');
              // Keep identifiers, dates and mixed text intact; write quantitative columns as numbers.
              if (!/plant|sku|material|date|period|reference|ticket|supplier|status|source|action|plan/i.test(headings[index]) &&
                  /^\$?-?\d[\d,]*(?:\.\d+)?(?:\s*(?:t|tons|%))?$/.test(text))
                return Number(text.replace(/[$,%\s]|tons|t/g, ''));
              return text;
            }))
        };
      };
      const pager = document.createElement('div'); pager.className = 'filters'; wrap.after(pager);
      let page = 0;
      function draw() {
        const term = search.value.trim().toLocaleLowerCase('en-US');
        const filtered = rows.filter(row => !term || (key(row) || row.textContent).toLocaleLowerCase('en-US').includes(term));
        const pages = Math.max(1, Math.ceil(filtered.length / 10)); page = Math.min(page, pages - 1);
        body.replaceChildren(...filtered.slice(page * 10, page * 10 + 10));
        if (!filtered.length) {
          const row = body.insertRow(); const cell = row.insertCell(); cell.colSpan = headings.length;
          cell.textContent = 'No records match this filter.'; cell.className = 'empty';
        }
        const prev = document.createElement('button'), next = document.createElement('button'), status = document.createElement('span');
        prev.type = next.type = 'button'; prev.textContent = '‹ Previous'; next.textContent = 'Next ›';
        prev.disabled = page === 0; next.disabled = page === pages - 1;
        status.textContent = `Page ${page + 1} of ${pages} · ${filtered.length} records · 10 per page`;
        status.setAttribute('aria-live', 'polite');
        prev.onclick = () => { page--; draw(); }; next.onclick = () => { page++; draw(); };
        pager.replaceChildren(prev, status, next);
      }
      search.oninput = () => { page = 0; draw(); }; draw();
    });
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    let node;
    while ((node = walker.nextNode())) {
      if (node.parentElement.closest('script,style,textarea,option')) continue;
      const formatted = usDate(node.nodeValue);
      if (formatted !== node.nodeValue) node.nodeValue = formatted;
    }
  }
  document.addEventListener('submit', event => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    for (const input of form.querySelectorAll('input,select,textarea')) {
      if (input.type === 'number') {
        input.max = '1000000000';
        if (['lead', 'days'].includes(input.id)) input.step = '1';
      }
      if (input.type === 'text' && !input.pattern) {
        input.value = input.value.trim(); input.maxLength = input.maxLength > 0 ? input.maxLength : 300;
        input.setCustomValidity(cleanText(input, input.value) !== input.value || (input.value && !/[\p{L}\p{N}]/u.test(input.value)) ? 'Use letters, numbers and permitted punctuation. Ticket references allow spaces, hyphens, slashes, dots and underscores.' : '');
      }
      if (input.tagName === 'SELECT') input.required = !['supplier'].includes(input.id);
    }
    const invalid = [...form.querySelectorAll('input,select,textarea')].find(input => input.willValidate && !input.validity.valid);
    if (invalid) {
      event.preventDefault(); event.stopImmediatePropagation();
      let error = form.querySelector('#formError');
      if (!error) { error = document.createElement('div'); error.id = 'formError'; form.append(error); }
      error.setAttribute('role', 'alert'); error.style.color = '#a14332';
      const label = invalid.closest('label')?.childNodes[0]?.textContent?.trim() || 'Field';
      error.textContent = `${label}: ${englishValidationMessage(invalid)}`;
      invalid.setAttribute('aria-invalid', 'true'); invalid.setAttribute('aria-describedby', error.id);
      invalid.focus();
    }
  }, true);
  document.addEventListener('beforeinput', event => {
    if (isTextField(event.target) && event.data && cleanText(event.target, event.data) !== event.data)
      event.preventDefault();
  });
  document.addEventListener('input', event => {
    event.target.removeAttribute?.('aria-invalid');
    if (isTextField(event.target)) {
      const input = event.target, start = input.selectionStart, original = input.value;
      const cleaned = cleanText(input, original);
      if (original !== cleaned) {
        input.value = cleaned;
        const caret = cleanText(input, original.slice(0, start)).length;
        input.setSelectionRange(caret, caret);
      }
      input.setCustomValidity('');
    }
  });
  const observer = new MutationObserver(() => enhance(document.body));
  observer.observe(document.body, { childList: true, subtree: true });
  enhance(document.body);
})();
