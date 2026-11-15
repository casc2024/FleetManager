// Shared US dollar editing for inventory, quotes, settings and fleet revenue.
function parseUsDollars(text,precision=2) {
  const raw=String(text).trim().replace(/^\$\s*/, '');
  if(raw==='')return {value:'',error:''};
  if(!new RegExp('^(?:(?:\\d+|\\d{1,3}(?:,\\d{3})+)(?:\\.\\d{0,'+precision+'})?|\\.\\d{1,'+precision+'})$').test(raw))
    return {value:'',error:'Use U.S. currency format, for example $41,452.00. Use commas for thousands and a period for decimals (up to '+precision+' decimal places).'};
  const number=Number(raw.replace(/,/g,''));
  if(!Number.isFinite(number)||number<0||number>1000000000)return {value:'',error:'Enter an amount from $0.00 to $1,000,000,000.00.'};
  return {value:String(number),error:''};
}
function isUsCurrencyInput(input) {
  const ids=new Set(['fv','fProduct','fFreight','product','freight','perfRevenue','mqf_valueUsd','mqf_productPriceT','mqf_freightT']);
  return input.type==='number' && (ids.has(input.id) || input.dataset.currency==='usd' || input.dataset.key==='override' ||
    input.dataset.prodPrice!==undefined || input.dataset.carrierRate!==undefined || input.dataset.carrierCost!==undefined ||
    ['roundTo','minFreightLoad','diesel'].includes(input.dataset.setting));
}
function enhanceUsCurrency(root) {
  root.querySelectorAll('input[type="number"]').forEach(raw=>{
    if(raw.dataset.usCurrency || !isUsCurrencyInput(raw))return;
    raw.dataset.usCurrency='true';
    const precision=raw.dataset.setting==='diesel'?3:2;
    const native=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value');
    const visible=document.createElement('input');
    visible.type='text';visible.inputMode='decimal';visible.lang='en-US';
    visible.required=raw.required;visible.placeholder='0.00';
    visible.dataset.usCurrencyEditor='true';
    // Pattern marks this as a numeric editor, exempt from the general text-character filter.
    visible.pattern='(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)(?:\\.[0-9]{0,'+precision+'})?|\\.[0-9]{1,'+precision+'}';
    visible.setAttribute('aria-label',(raw.closest('label')?.childNodes[0]?.textContent?.trim() || raw.id)+' (US dollars)');
    const group=document.createElement('span');group.style.cssText='display:flex;position:relative;align-items:center;width:100%';
    const symbol=document.createElement('span');symbol.textContent='$';symbol.setAttribute('aria-hidden','true');
    symbol.style.cssText='position:absolute;left:12px;pointer-events:none;color:#183c3a';
    visible.style.cssText='width:100%;min-width:0;padding-left:28px';
    raw.before(group);group.append(symbol,visible);raw.hidden=true;raw.required=false;raw.disabled=true;
    const format=value=>value===''?'':new Intl.NumberFormat('en-US',{minimumFractionDigits:precision,maximumFractionDigits:precision}).format(Number(value));
    const reflect=()=>{
      const value=native.get.call(raw);visible.value=format(value);
      visible.setCustomValidity(value!=='' && (Number(value)<0||Number(value)>1000000000)?'Enter an amount from $0.00 to $1,000,000,000.00.':'');
    };
    Object.defineProperty(raw,'value',{get(){return native.get.call(this);},set(value){native.set.call(this,value);reflect();}});
    visible.addEventListener('focus',()=>{
      if(!visible.validationMessage){const value=native.get.call(raw);visible.value=value===''?'':Number(value).toFixed(precision);}
    });
    visible.addEventListener('input',()=>{
      // A pasted dollar sign is accepted once as a prefix, never as a numeric character.
      visible.value=visible.value.replace(/^\s*\$\s*/, '');
      const parsed=parseUsDollars(visible.value,precision);
      native.set.call(raw,parsed.value);visible.setCustomValidity(parsed.error);
      if(!parsed.error)raw.dispatchEvent(new Event('input',{bubbles:true}));
    });
    visible.addEventListener('blur',()=>{
      const parsed=parseUsDollars(visible.value,precision);visible.setCustomValidity(parsed.error);
      if(!parsed.error){native.set.call(raw,parsed.value);visible.value=format(parsed.value);}
    });
    visible.addEventListener('change',()=>{
      if(!parseUsDollars(visible.value,precision).error)raw.dispatchEvent(new Event('change',{bubbles:true}));
    });
    reflect();
  });
}
function validateUsCurrency(scope) {
  const invalid=[...scope.querySelectorAll('[data-us-currency-editor]')].find(input=>!input.validity.valid);
  if(!invalid){const previous=scope.querySelector('[data-currency-error]');if(previous)previous.textContent='';return true;}
  let error=scope.querySelector('[data-currency-error]');
  if(!error){error=document.createElement('p');error.dataset.currencyError='true';error.setAttribute('role','alert');error.style.color='#a14332';scope.append(error);}
  error.textContent=invalid.validity.valueMissing?'Enter the required dollar amount.':invalid.validationMessage||'Enter a valid U.S. dollar amount, for example $1,234.56.';
  invalid.focus();return false;
}
if(typeof document!=='undefined' && document.body && typeof MutationObserver!=='undefined') {
  enhanceUsCurrency(document.body);
  new MutationObserver(()=>enhanceUsCurrency(document.body)).observe(document.body,{childList:true,subtree:true});
  document.addEventListener('submit',event=>{
    if(!validateUsCurrency(event.target)){event.preventDefault();event.stopImmediatePropagation();}
  },true);
  document.addEventListener('click',event=>{
    const button=event.target.closest('button');if(!button)return;
    const scopes={applySettings:'#settingsBody',perfSave:'.perf-entry',saveDraftBtn:'#lines',rfpBtn:'#lines'};
    const selector=scopes[button.id];if(!selector)return;
    const scope=selector==='.perf-entry'?button.parentElement:document.querySelector(selector);
    if(scope&&!validateUsCurrency(scope)){event.preventDefault();event.stopImmediatePropagation();}
  },true);
}
