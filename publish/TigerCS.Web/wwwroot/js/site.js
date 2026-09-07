(function () {
  "use strict";

  // ---------- Progressive enhancement only: every feature below has a
  // working non-JS fallback (native <details>, a real <form> submit). ----------

  // Close open <details> disclosures when clicking outside them.
  document.addEventListener("click", function (event) {
    document.querySelectorAll("details.disclosure[open]").forEach(function (details) {
      if (!details.contains(event.target)) {
        details.removeAttribute("open");
      }
    });
  });

  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape") {
      document.querySelectorAll("details.disclosure[open]").forEach(function (d) {
        d.removeAttribute("open");
      });
    }
  });

  // Password show/hide. Without JS the field just stays type="password".
  document.querySelectorAll(".password-toggle").forEach(function (toggle) {
    toggle.addEventListener("click", function () {
      var input = document.getElementById(toggle.getAttribute("data-target"));
      if (!input) return;
      var showing = input.type === "text";
      input.type = showing ? "password" : "text";
      toggle.setAttribute("aria-pressed", showing ? "false" : "true");
      toggle.setAttribute("aria-label", showing ? "Show password" : "Hide password");
      toggle.innerHTML = showing ? toggle.dataset.iconShow : toggle.dataset.iconHide;
    });
  });

  // Filter forms: auto-submit on select change, so a mouse/keyboard user
  // doesn't need to also press the Apply button. The button still works
  // (and is the only way to apply filters) without this script. Applies to
  // any select marked data-autosubmit (the ticket queue's filters, the
  // customer workspace's unit filter).
  document.querySelectorAll("select[data-autosubmit]").forEach(function (el) {
    el.addEventListener("change", function () {
      if (el.form) {
        el.form.requestSubmit();
      }
    });
  });

  // Prevent duplicate submission: disable a form's submit button(s) the
  // moment it submits, so a double-click can't fire the request twice.
  // Without JS the form still submits normally on every click.
  document.addEventListener("submit", function (event) {
    var form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    form.querySelectorAll('button[type="submit"]').forEach(function (btn) {
      btn.disabled = true;
    });
  });
})();
