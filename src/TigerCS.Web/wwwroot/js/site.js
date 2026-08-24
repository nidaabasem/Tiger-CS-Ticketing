(function () {
  "use strict";

  // ---------- Dropdown / menu handling ----------
  function closeAllMenus(except) {
    document.querySelectorAll(".menu-panel.is-open").forEach(function (panel) {
      if (panel !== except) {
        panel.classList.remove("is-open");
      }
    });
  }

  document.addEventListener("click", function (event) {
    var trigger = event.target.closest("[data-menu-trigger]");
    if (trigger) {
      var id = trigger.getAttribute("data-menu-trigger");
      var panel = document.getElementById(id);
      if (panel) {
        var willOpen = !panel.classList.contains("is-open");
        closeAllMenus(willOpen ? panel : null);
        panel.classList.toggle("is-open", willOpen);
        trigger.setAttribute("aria-expanded", willOpen ? "true" : "false");
      }
      event.stopPropagation();
      return;
    }

    if (!event.target.closest(".menu-panel")) {
      closeAllMenus(null);
    }
  });

  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape") {
      closeAllMenus(null);
    }
  });

  // menu item selection: reflect chosen value on trigger button, close menu
  document.querySelectorAll("[data-menu-select]").forEach(function (item) {
    item.addEventListener("click", function () {
      var panel = item.closest(".menu-panel");
      var group = panel ? panel.getAttribute("data-menu-select") : null;
      if (group) {
        var target = document.querySelector('[data-menu-select-target="' + group + '"]');
        if (target) {
          target.textContent = item.getAttribute("data-menu-select");
        }
      }
      if (panel) {
        panel.classList.remove("is-open");
      }
    });
  });

  // ---------- Password show / hide ----------
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

  // ---------- Login form (mock auth) ----------
  var loginForm = document.getElementById("loginForm");
  if (loginForm) {
    loginForm.addEventListener("submit", function (event) {
      event.preventDefault();
      var identifier = document.getElementById("identifier");
      var password = document.getElementById("password");
      var errorBox = document.getElementById("loginError");

      var valid = identifier.value.trim().length > 0 && password.value.trim().length > 0;

      if (!valid) {
        errorBox.textContent = "Enter your email or employee ID and password to continue.";
        errorBox.classList.add("is-visible");
        return;
      }

      errorBox.classList.remove("is-visible");
      window.location.href = "/Tickets";
    });
  }

  // ---------- Tabs ----------
  document.querySelectorAll("[data-tabs]").forEach(function (tabGroup) {
    var buttons = tabGroup.querySelectorAll(".tab-btn");
    var scope = document.getElementById(tabGroup.getAttribute("data-tabs"));

    buttons.forEach(function (btn) {
      btn.addEventListener("click", function () {
        buttons.forEach(function (b) { b.classList.remove("is-active"); b.setAttribute("aria-selected", "false"); });
        btn.classList.add("is-active");
        btn.setAttribute("aria-selected", "true");

        if (scope) {
          scope.querySelectorAll(".tab-panel").forEach(function (panel) {
            panel.classList.toggle("is-active", panel.id === btn.getAttribute("data-tab-target"));
          });
        }
      });
    });
  });

  // ---------- Composer mode toggle (Reply / Internal note) ----------
  var composerToggle = document.querySelector("[data-composer-toggle]");
  if (composerToggle) {
    var modeButtons = composerToggle.querySelectorAll("button[data-mode]");
    var textarea = document.getElementById("composerMessage");
    var sendBtn = document.getElementById("composerSend");

    modeButtons.forEach(function (btn) {
      btn.addEventListener("click", function () {
        modeButtons.forEach(function (b) { b.classList.remove("is-active"); });
        btn.classList.add("is-active");
        var mode = btn.getAttribute("data-mode");
        if (textarea) {
          textarea.placeholder = mode === "note"
            ? "Add an internal note — visible to staff only…"
            : "Write a reply to the customer…";
        }
        if (sendBtn) {
          sendBtn.textContent = mode === "note" ? "Add Note" : "Send Reply";
        }
      });
    });
  }

  // ---------- Ticket rows: click row or ticket id to open details ----------
  document.querySelectorAll("tr[data-href]").forEach(function (row) {
    row.addEventListener("click", function (event) {
      if (event.target.closest("a")) return;
      window.location.href = row.getAttribute("data-href");
    });
  });

  // ---------- Filters: client-side search / filter on Tickets page ----------
  var filterBar = document.getElementById("ticketFilters");
  if (filterBar) {
    var searchInput = document.getElementById("filterSearch");
    var statusSelect = document.getElementById("filterStatus");
    var prioritySelect = document.getElementById("filterPriority");
    var deptSelect = document.getElementById("filterDepartment");
    var ownerSelect = document.getElementById("filterOwner");
    var slaSelect = document.getElementById("filterSla");
    var clearBtn = document.getElementById("filterClear");

    function applyFilters() {
      var term = (searchInput.value || "").trim().toLowerCase();
      var status = statusSelect.value;
      var priority = prioritySelect.value;
      var dept = deptSelect.value;
      var owner = ownerSelect.value;
      var sla = slaSelect.value;

      document.querySelectorAll("[data-ticket-row]").forEach(function (row) {
        var matches =
          (!term || row.dataset.search.indexOf(term) !== -1) &&
          (!status || row.dataset.status === status) &&
          (!priority || row.dataset.priority === priority) &&
          (!dept || row.dataset.department === dept) &&
          (!owner || row.dataset.owner === owner) &&
          (!sla || row.dataset.sla === sla);

        row.style.display = matches ? "" : "none";
      });

      document.querySelectorAll("[data-table-body]").forEach(function (tbody) {
        var visible = tbody.querySelectorAll('[data-ticket-row]:not([style*="display: none"])').length;
        var emptyRow = tbody.querySelector(".empty-row");
        if (emptyRow) {
          emptyRow.style.display = visible === 0 ? "" : "none";
        }
      });
    }

    [searchInput, statusSelect, prioritySelect, deptSelect, ownerSelect, slaSelect].forEach(function (el) {
      if (el) el.addEventListener("input", applyFilters);
      if (el) el.addEventListener("change", applyFilters);
    });

    if (clearBtn) {
      clearBtn.addEventListener("click", function () {
        searchInput.value = "";
        [statusSelect, prioritySelect, deptSelect, ownerSelect, slaSelect].forEach(function (sel) { sel.value = ""; });
        applyFilters();
      });
    }
  }

  // ---------- Pagination (visual state only — dataset is a single page) ----------
  document.querySelectorAll(".pagination").forEach(function (nav) {
    nav.querySelectorAll("button[data-page]").forEach(function (btn) {
      btn.addEventListener("click", function () {
        nav.querySelectorAll("button[data-page]").forEach(function (b) { b.classList.remove("is-active"); });
        btn.classList.add("is-active");
      });
    });
  });
})();
