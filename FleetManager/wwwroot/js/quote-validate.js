// Fleet Manager — input sanitizer injected into the JCK quoter (/quotes).
// Blocks strange characters in the quote form fields (keyed by data-field),
// filtering as the user types/pastes and cleaning any pre-filled junk.
(function () {
  // Characters to REMOVE per field (everything not allowed).
  const RULES = {
    rfp:   /[^A-Za-z0-9 \-]/g,                 // RFP-2026-014
    phone: /[^0-9 +()\-]/g,                     // (940) 305-9656
    email: /[^A-Za-z0-9@._%+\-]/g              // name@company.com
  };
  // Default for business text fields: letters (any language), digits, spaces
  // and basic punctuation . , & ' - / ( ) #
  const TEXT = /[^\p{L}\p{N} .,&'\-/()#]/gu;

  function ruleFor(el) {
    const f = el.getAttribute("data-field");
    if (RULES[f]) return RULES[f];
    const t = (el.getAttribute("type") || "text").toLowerCase();
    if (t === "number" || t === "date") return null; // leave native numeric/date inputs
    return TEXT;
  }

  function sanitize(el) {
    const rule = ruleFor(el);
    if (!rule) return;
    const before = el.value;
    const after = before.replace(rule, "");
    if (after === before) return;
    const drop = before.length - after.length;
    const pos = (el.selectionStart || before.length) - drop;
    el.value = after;
    try { el.setSelectionRange(pos, pos); } catch { /* not a text input */ }
    // Let the quoter recompute/save; re-entry is a no-op because value is now clean.
    el.dispatchEvent(new Event("input", { bubbles: true }));
  }

  function attach(root) {
    root.querySelectorAll("input[data-field]").forEach((el) => {
      if (el.__jckSan) return;
      el.__jckSan = true;
      el.addEventListener("input", () => sanitize(el));
      sanitize(el); // clean anything already present (e.g. a corrupted draft)
    });
  }

  attach(document);
  // The quoter adds quote-line inputs dynamically; sanitize those too.
  new MutationObserver((muts) => {
    for (const m of muts) for (const n of m.addedNodes) if (n.nodeType === 1) attach(n);
  }).observe(document.body, { childList: true, subtree: true });
})();
