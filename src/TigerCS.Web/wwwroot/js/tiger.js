/*
  Tiger Group Service Desk — progressive enhancement only.

  Every screen works with this file absent: forms submit, links navigate,
  dialogs fall back to always-visible sections, and no validation or business
  rule lives here. Nothing in this file decides what a user may do — the API
  decides that, and the server renders the result.
*/
(function () {
  'use strict';

  /* ---------------------------------------------------------- password */
  // The toggle button ships hidden and is revealed here, so a no-JS browser
  // never shows a control that would do nothing.
  document.querySelectorAll('[data-pw-toggle]').forEach(function (button) {
    var input = document.getElementById(button.getAttribute('data-pw-toggle'));
    if (!input) { return; }

    button.hidden = false;
    button.addEventListener('click', function () {
      var revealed = input.type === 'text';
      input.type = revealed ? 'password' : 'text';
      button.textContent = revealed ? 'Show' : 'Hide';
      button.setAttribute('aria-pressed', String(!revealed));
      // Announce the change without moving focus away from the field.
      button.setAttribute('aria-label', revealed ? 'Show password' : 'Hide password');
      input.focus({ preventScroll: true });
    });
  });

  /* ------------------------------------------------------- submit once */
  // Guards against a double-click. It is NOT the duplicate-submission
  // protection for ticket creation — that is the server-side SubmitToken,
  // which also survives a refresh, a Back-and-resubmit, and this script
  // failing to load.
  document.querySelectorAll('form').forEach(function (form) {
    form.addEventListener('submit', function () {
      form.querySelectorAll('[data-submit-once]').forEach(function (button) {
        if (button.dataset.busyLabel) {
          button.textContent = button.dataset.busyLabel;
        }
        // Disabled after the current tick so the button's own value still
        // posts with the form.
        window.setTimeout(function () {
          button.disabled = true;
          button.setAttribute('aria-disabled', 'true');
        }, 0);
      });
    });
  });

  /* ------------------------------------------------------------ dialogs */
  document.querySelectorAll('[data-dialog-open]').forEach(function (trigger) {
    trigger.addEventListener('click', function () {
      var dialog = document.getElementById(trigger.getAttribute('data-dialog-open'));
      if (!dialog) { return; }

      if (typeof dialog.showModal === 'function') {
        dialog.showModal();
        var firstField = dialog.querySelector('select, input:not([type=hidden]), textarea');
        if (firstField) { firstField.focus(); }
      } else {
        // Older engines: show it inline rather than trapping the action.
        dialog.setAttribute('open', 'open');
      }
    });
  });

  document.querySelectorAll('[data-dialog-close]').forEach(function (button) {
    button.addEventListener('click', function () {
      var dialog = button.closest('dialog');
      if (!dialog) { return; }
      if (typeof dialog.close === 'function') { dialog.close(); } else { dialog.removeAttribute('open'); }
    });
  });

  /* ------------------------------------------------- duplicate-of field */
  // Shows the "duplicate of" input only for the Duplicate outcome, which is
  // the only outcome the API requires it for.
  document.querySelectorAll('[data-toggle-duplicate]').forEach(function (select) {
    var field = select.closest('form').querySelector('[data-duplicate-field]');
    if (!field) { return; }

    var sync = function () {
      var isDuplicate = select.value === 'Duplicate';
      field.hidden = !isDuplicate;
      var input = field.querySelector('input');
      if (input) { input.required = isDuplicate; }
    };

    select.addEventListener('change', sync);
    sync();
  });

  /* ------------------------------------------------ contact label relay */
  // Carries the chosen contact's display label into the posted form, so the
  // review step can show who was confirmed. Identifiers still come from the
  // server-held wizard state — this is a label, nothing more.
  var labelTarget = document.querySelector('[data-contact-label-target]');
  if (labelTarget) {
    document.querySelectorAll('input[name="contactReferenceId"]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        labelTarget.value = radio.getAttribute('data-contact-label') || '';
      });
      if (radio.checked) {
        labelTarget.value = radio.getAttribute('data-contact-label') || '';
      }
    });
  }

  /* ------------------------------------------------- character counting */
  document.querySelectorAll('[data-count-target]').forEach(function (field) {
    var output = document.getElementById(field.getAttribute('data-count-target'));
    var max = parseInt(field.getAttribute('maxlength'), 10);
    if (!output || !max) { return; }

    var base = output.textContent;
    var update = function () {
      var remaining = max - field.value.length;
      output.textContent = remaining + ' characters remaining';
    };

    field.addEventListener('input', update);
    if (field.value.length > 0) { update(); } else { output.textContent = base; }
  });

  /* ----------------------------------------------- leave confirmation */
  document.querySelectorAll('[data-confirm-leave]').forEach(function (link) {
    link.addEventListener('click', function (event) {
      var form = link.closest('form') || document.querySelector('form');
      var dirty = form && Array.prototype.some.call(
        form.querySelectorAll('input[type=text], input[type=number], textarea'),
        function (field) { return field.value.trim().length > 0; });

      if (dirty && !window.confirm(link.getAttribute('data-confirm-leave'))) {
        event.preventDefault();
      }
    });
  });
})();
