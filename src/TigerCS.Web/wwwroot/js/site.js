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

  // Filter forms: auto-submit on select/checkbox change, so a mouse/keyboard
  // user doesn't need to also press the Apply button. The button still works
  // (and is the only way to apply filters) without this script. Applies to
  // any select or checkbox marked data-autosubmit (the ticket queue's
  // filters, the customer workspace's unit filter, the admin list toolbars).
  document.querySelectorAll("select[data-autosubmit], input[type=checkbox][data-autosubmit]").forEach(function (el) {
    el.addEventListener("change", function () {
      if (el.form) {
        el.form.requestSubmit();
      }
    });
  });

  // Clickable table rows (Customers directory, customer ticket lists): the
  // first cell carries a real link; with JS the whole row follows it, except
  // when the click was on another link, button or form control in the row.
  document.querySelectorAll("tr.is-link[data-href]").forEach(function (row) {
    row.addEventListener("click", function (event) {
      if (event.target.closest("a, button, input, select, label, form, details")) return;
      window.location.assign(row.getAttribute("data-href"));
    });
  });

  // Confirmation before a deactivation/archive/delete/publish action:
  // forms marked data-confirm ask first. Without JS the form submits
  // directly — the Api still enforces every rule server-side.
  document.addEventListener("submit", function (event) {
    var form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    var message = form.getAttribute("data-confirm");
    if (message && !window.confirm(message)) {
      event.preventDefault();
      event.stopPropagation();
    }
  }, true);

  // New Ticket Step 1: the phone field's "required" state follows the
  // selected channel's own configuration (each option carries the
  // channel's RequiresPhone flag from Administration → Channels). Without
  // JS the server-rendered state stands, and the PageModel and Api enforce
  // the same rule regardless of what the browser did.
  document.querySelectorAll("form[data-channel-form]").forEach(function (form) {
    var select = form.querySelector("[data-channel-select]");
    var phone = form.querySelector("[data-phone-input]");
    var hint = form.querySelector("[data-phone-optional-hint]");
    if (!select || !phone) return;
    var apply = function () {
      var option = select.options[select.selectedIndex];
      var requiresPhone = !option || option.getAttribute("data-requires-phone") !== "false";
      phone.required = requiresPhone;
      phone.setAttribute("aria-required", requiresPhone ? "true" : "false");
      if (hint) hint.hidden = requiresPhone;
    };
    select.addEventListener("change", apply);
    apply();
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
