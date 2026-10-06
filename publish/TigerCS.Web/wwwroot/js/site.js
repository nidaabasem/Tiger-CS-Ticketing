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
      // Never start a second navigation over one already in flight — the
      // browser would abort the first, and the server would be left holding
      // a cancelled query. The in-flight submit already carries this
      // control's new value if it changed before the request left.
      if (el.form && !el.form.hasAttribute("data-submitting")) {
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

  // Prevent duplicate submission: the FIRST submit of a form wins, and every
  // further submit while that one is still in flight is dropped. This covers
  // the double-click, the second Enter in a search box, and a filter control
  // changing while the page is already navigating — on the Customers
  // directory's search/filter bar (#customerFilters) as much as on the
  // ticket forms.
  //
  // It matters beyond the duplicate itself: a second navigation makes the
  // browser abort the first one, and an aborted request cancels
  // HttpContext.RequestAborted all the way down to the Customers query in
  // TigerCS.Api, which then throws TaskCanceledException out of
  // CustomerDirectoryRepository. The server treats that as the expected
  // client cancellation it is (ClientDisconnectMiddleware), but not starting
  // the pointless second request is better than cancelling the first.
  //
  // Without JS every click still submits normally, and the server is
  // unchanged either way — nothing here is a correctness guarantee, the
  // Api enforces its own rules regardless.
  document.addEventListener("submit", function (event) {
    var form = event.target;
    if (!(form instanceof HTMLFormElement)) return;

    if (form.hasAttribute("data-submitting")) {
      event.preventDefault();
      event.stopImmediatePropagation();
      return;
    }

    form.setAttribute("data-submitting", "");
    // Deferred by one turn so the submit button is still enabled while the
    // browser builds the form data: a disabled control is barred from
    // submission, and some forms (TicketDetails' Approve/Reject) submit
    // their decision AS the button's own name/value. The attribute above,
    // not the disabled state, is what actually blocks the second submit.
    window.setTimeout(function () {
      form.querySelectorAll('button[type="submit"]').forEach(function (btn) {
        btn.disabled = true;
      });
    }, 0);
  });

  // Back/forward can restore this page from the browser's cache exactly as
  // it was left — mid-submit, with the guard set and the buttons disabled.
  // Release it, or the agent returns to a filter bar that refuses to apply.
  window.addEventListener("pageshow", function () {
    document.querySelectorAll("form[data-submitting]").forEach(function (form) {
      form.removeAttribute("data-submitting");
      form.querySelectorAll('button[type="submit"]').forEach(function (btn) {
        btn.disabled = false;
      });
    });
  });
})();
