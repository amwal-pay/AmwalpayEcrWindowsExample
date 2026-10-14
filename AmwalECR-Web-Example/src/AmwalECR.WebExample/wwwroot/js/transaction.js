import { api, ApiError, qrUrl } from './api.js';
import { attachAmountInput } from './amount.js';
import { $, h, show, setText, announce, copyText } from './dom.js';

const POLL_MS = 500;

/**
 * The transaction screen: the port of TransactionView / TransactionViewModel.
 *
 * Which fields exist comes from /api/meta (the SDK's transaction-type flags, laid out by
 * Core's TransactionFormState); nothing about the rules is decided here. The server
 * validates what is sent and answers with per-field messages, shown under the fields.
 *
 * A transaction runs on the server, not in the request, so this page only starts it and
 * then watches it. Closing the tab does not stop a payment.
 */
export function initTransaction({ meta, onFinished }) {
  const els = {
    form: $('#txn-form'),
    fields: $('#txn-fields'),
    type: $('#f-type'),
    amount: $('#f-amount'),
    merchantReference: $('#f-merchantReference'),
    lookUpByReference: $('#f-lookUpByReference'),
    originalReference: $('#f-originalReference'),
    receiptNumber: $('#f-receiptNumber'),
    originalDate: $('#f-originalDate'),
    useOtherTerminal: $('#f-useOtherTerminal'),
    originalTerminalId: $('#f-originalTerminalId'),
    terminal: $('#f-terminal'),
    picker: $('#terminal-picker'),
    noTerminals: $('#no-terminals'),
    message: $('#form-message'),
    start: $('#btn-start'),
    busy: $('#busy'),
    busyStatus: $('#busy-status'),
    busyNote: $('#busy-note'),
    cancel: $('#btn-cancel'),
    countdown: $('#busy-countdown'),
    countdownCaption: $('#busy-countdown-caption'),
    linkLost: $('#link-lost'),
  };
  const groups = Object.fromEntries([...document.querySelectorAll('#txn-form [data-field]')].map((el) => [el.dataset.field, el]));
  const errorFields = ['amount', 'merchantReference', 'originalReference', 'receiptNumber', 'originalTerminalId', 'type', 'terminal'];

  const operations = new Map(meta.operations.map((op) => [op.value, op]));

  let form = newForm('Sale');
  let errors = {};
  let terminals = [];
  let selectedSerial = null;
  let config = null;
  let run = null;
  let starting = false;
  let pollTimer = null;
  let renderedDialogKey = null;
  let lastState = null;

  for (const op of meta.operations) els.type.append(h('option', { value: op.value }, op.label));

  const amountInput = attachAmountInput(els.amount, {
    maxDigits: meta.amount.maxDigits,
    onChange: (digits) => edit('amountDigits', digits, 'amount'),
  });

  // Form state ----------------------------------------------------------------------

  function newForm(type) {
    // Switching type resets the choices that no longer apply, so a terminal id or
    // reference left over cannot travel with the next operation.
    return {
      type,
      amountDigits: '',
      receiptNumber: '',
      originalTerminalId: '',
      originalDate: meta.today,
      useOtherTerminal: false,
      lookUpByReference: false,
      originalReference: '',
      merchantReference: '',
    };
  }

  /** Editing a field clears that field's own error, so a stale message does not sit next to a fixed value. */
  function edit(name, value, errorKey = name) {
    form[name] = value;
    if (errors[errorKey]) {
      delete errors[errorKey];
      renderErrors();
    }
    if (name === 'amountDigits') renderAmountHelp();
  }

  const layout = () => operations.get(form.type).layouts[`${form.useOtherTerminal ? 1 : 0}${form.lookUpByReference ? 1 : 0}`];

  function syncForm() {
    const op = operations.get(form.type);
    const shown = layout();

    els.type.value = form.type;
    amountInput.digits = form.amountDigits;
    els.merchantReference.value = form.merchantReference;
    els.lookUpByReference.checked = form.lookUpByReference;
    els.originalReference.value = form.originalReference;
    els.receiptNumber.value = form.receiptNumber;
    els.originalDate.value = form.originalDate;
    els.originalDate.max = meta.today;
    els.useOtherTerminal.checked = form.useOtherTerminal;
    els.originalTerminalId.value = form.originalTerminalId;

    show(groups.merchantReference, shown.merchantReference);
    show(groups.referenceSwitch, shown.referenceSwitch);
    show(groups.originalReference, shown.originalReference);
    show(groups.receiptNumber, shown.receiptNumber);
    show(groups.originalDate, shown.originalDate);
    show(groups.otherTerminalSwitch, shown.otherTerminalSwitch);
    show(groups.originalTerminal, shown.originalTerminal);

    // Dimmed, with the reason, when the type does not take an amount.
    els.amount.disabled = !shown.amount;
    els.start.textContent = op.startButtonText;
    renderAmountHelp();
    renderConfig();
    renderErrors();
  }

  function renderAmountHelp() {
    const help = operations.get(form.type).amountHelp;
    setText($('#h-amount'), errors.amount ? '' : help);
  }

  function renderErrors() {
    for (const name of errorFields) {
      const message = errors[name] ?? '';
      setText($(`#e-${name}`), message);
      const input = name === 'amount' ? els.amount : els[name];
      input?.setAttribute('aria-invalid', message ? 'true' : 'false');
    }
    renderAmountHelp();
  }

  const setMessage = (text) => setText(els.message, text);

  // Wiring the inputs ------------------------------------------------------------------

  els.type.addEventListener('change', () => {
    form = newForm(els.type.value);
    errors = {};
    setMessage('');
    syncForm();
  });

  els.merchantReference.addEventListener('input', () => edit('merchantReference', els.merchantReference.value));
  els.originalReference.addEventListener('input', () => edit('originalReference', els.originalReference.value));

  els.receiptNumber.addEventListener('input', () => {
    const digits = els.receiptNumber.value.replace(/[^0-9]/g, '').slice(0, meta.receiptNumberMaxLength);
    els.receiptNumber.value = digits;
    edit('receiptNumber', digits);
  });

  els.originalTerminalId.addEventListener('input', () => {
    const digits = els.originalTerminalId.value.replace(/[^0-9]/g, '');
    els.originalTerminalId.value = digits;
    edit('originalTerminalId', digits);
  });

  els.originalDate.addEventListener('change', () => {
    // A cleared date keeps the previous one; the original cannot be in the future.
    const picked = els.originalDate.value;
    form.originalDate = picked && picked <= meta.today ? picked : picked ? meta.today : form.originalDate;
    els.originalDate.value = form.originalDate;
  });

  els.lookUpByReference.addEventListener('change', () => {
    form = { ...form, lookUpByReference: els.lookUpByReference.checked, receiptNumber: '', originalReference: '' };
    errors = {};
    syncForm();
  });

  els.useOtherTerminal.addEventListener('change', () => {
    form = { ...form, useOtherTerminal: els.useOtherTerminal.checked, originalTerminalId: '' };
    syncForm();
  });

  els.terminal.addEventListener('change', () => {
    selectedSerial = els.terminal.value;
    delete errors.terminal;
    loadConfig({ announce: true });
  });

  // Terminals and config ------------------------------------------------------------------

  /** Re-reads the terminals and the selected terminal's plan: keys or terminals may have changed elsewhere. */
  async function refresh() {
    let list;
    try {
      list = (await api.terminals()).terminals;
    } catch {
      return;
    }
    terminals = list;
    if (!terminals.some((t) => t.serialNumber === selectedSerial)) selectedSerial = terminals[0]?.serialNumber ?? null;

    els.terminal.replaceChildren(...terminals.map((t) => h('option', { value: t.serialNumber }, t.displayName)));
    if (selectedSerial) els.terminal.value = selectedSerial;
    show(els.noTerminals, terminals.length === 0);
    show(els.picker, terminals.length !== 0);
    await loadConfig({ announce: false });
  }

  async function loadConfig({ announce: log }) {
    if (!selectedSerial) {
      config = null;
    } else {
      const serial = selectedSerial;
      try {
        const next = await api.config(serial, log);
        if (serial !== selectedSerial) return; // the operator picked another meanwhile
        config = next;
      } catch {
        config = null;
      }
    }
    renderConfig();
  }

  function renderConfig() {
    const card = $('#config-card');
    show(card, config != null);
    if (config) {
      card.classList.toggle('not-ready', !config.isReady);
      setText($('#cfg-mode'), config.modeLabel);
      setText($('#cfg-transport'), config.transportLabel);
      setText($('#cfg-connection'), config.connectionSummary);
      setText($('#cfg-key-label'), `${config.keyLabel}:`);
      setText($('#cfg-key-status'), config.keyStatus);
      const issues = $('#cfg-issues');
      issues.replaceChildren(...config.issues.map((issue) => h('li', {}, issue)));
      show(issues, config.issues.length !== 0);

    }
    els.start.disabled = !canStart();
  }

  // Only a ready plan may start: the operator sees what is missing before paying.
  const canStart = () => !starting && !(run?.busy) && config?.isReady === true;

  // Starting ---------------------------------------------------------------------------------

  els.form.addEventListener('submit', async (event) => {
    event.preventDefault();
    if (!canStart()) return;

    starting = true;
    els.start.disabled = true;
    setMessage('');
    try {
      adopt(await api.start({ terminalSerial: selectedSerial, ...form }));
    } catch (error) {
      handleStartError(error);
    } finally {
      starting = false;
      els.start.disabled = !canStart();
    }
  });

  function handleStartError(error) {
    if (!(error instanceof ApiError)) throw error;

    if (error.status === 422) {
      errors = { ...error.fields };
      renderErrors();
      setMessage(error.body?.message ?? '');
      const first = errorFields.find((name) => errors[name]);
      if (first) {
        if (first === 'amount') amountInput.focus();
        else els[first]?.focus();
        announce(`${Object.keys(errors).length === 1 ? 'One field needs' : `${Object.keys(errors).length} fields need`} attention. ${errors[first]}`);
      } else if (error.body?.message) {
        announce(error.body.message);
      }
    } else if (error.status === 409 && error.body?.transactionId) {
      // Already running on this terminal (another tab, or a double click): show that one.
      setMessage(error.message);
      api.transaction(error.body.transactionId).then(adopt, () => {});
    } else {
      setMessage(error.status === 0 ? 'The app could not be reached. Nothing was sent to the terminal.' : error.message);
    }
  }

  // Watching the run --------------------------------------------------------------------------

  /** Takes a state from the server. An older one (a slow response overtaken by a newer poll) is ignored. */
  function adopt(view) {
    if (run && run.id === view.id && view.version < run.version) return;
    run = view;
    els.linkLost.hidden = true;
    render();
    schedulePoll();
    if (lastState !== `${view.id}:${view.state}`) {
      lastState = `${view.id}:${view.state}`;
      onFinished();
    }
  }

  function schedulePoll() {
    clearTimeout(pollTimer);
    if (!run || !(run.busy || run.dialog?.receipt.fetching)) return;
    pollTimer = setTimeout(poll, POLL_MS);
  }

  async function poll() {
    const id = run?.id;
    if (!id) return;
    try {
      const view = await api.transaction(id);
      if (run?.id === id) adopt(view);
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) {
        // Acknowledged elsewhere, or gone from the server.
        forget();
        return;
      }
      // The transaction does not depend on this page: keep asking.
      els.linkLost.hidden = false;
      schedulePoll();
    }
  }

  function render() {
    const busy = run?.busy === true;
    const wasBusy = els.fields.disabled;

    // The form is disabled as a whole while a transaction runs.
    els.fields.disabled = busy;
    show(els.busy, busy);
    if (busy) {
      setText(els.busyStatus, run.statusText);
      startCountdown();
      show(els.busyNote, run.state === 'inProgress');
      els.cancel.disabled = false;
      if (!wasBusy) els.busyStatus.focus();
    }

    if (run?.isResult) showDialog();
    else if (dialog.open) closeDialog({ silent: true });

    // Cancelled before anything was sent: nothing to acknowledge, back to the form.
    if (run && run.state === 'idle') {
      const id = run.id;
      forget();
      api.acknowledge(id).catch(() => {});
      announce('Stopped. No money was moved.');
    }

    if (!busy) stopCountdown();

    els.start.disabled = !canStart();
  }

  // The server counts the budget down and reports what is left with every poll; the page
  // only ticks the label between polls, so it needs no clock that agrees with the server.
  let countdownTimer = null;
  let countdownDeadline = 0;

  function startCountdown() {
    if (typeof run?.remainingSeconds !== 'number') {
      stopCountdown();
      return;
    }
    countdownDeadline = Date.now() + run.remainingSeconds * 1000;
    setText(els.countdownCaption, `remaining of ${run.timeoutSeconds} s timeout`);
    show(els.countdown, true);
    show(els.countdownCaption, true);
    tickCountdown();
    if (!countdownTimer) countdownTimer = setInterval(tickCountdown, 1000);
  }

  function tickCountdown() {
    const left = Math.max(0, Math.ceil((countdownDeadline - Date.now()) / 1000));
    setText(els.countdown, `${Math.floor(left / 60)}:${String(left % 60).padStart(2, '0')}`);
  }

  function stopCountdown() {
    clearInterval(countdownTimer);
    countdownTimer = null;
    show(els.countdown, false);
    show(els.countdownCaption, false);
  }

  els.cancel.addEventListener('click', async () => {
    if (!run) return;
    els.cancel.disabled = true;
    try {
      adopt(await api.cancel(run.id));
    } catch {
      els.cancel.disabled = false;
    }
  });

  function forget() {
    clearTimeout(pollTimer);
    run = null;
    renderedDialogKey = null;
    lastState = null;
    if (dialog.open) closeDialog({ silent: true });
    render();
  }

  // Result dialog -------------------------------------------------------------------------------

  const dialog = $('#dlg-result');
  const r = {
    approved: $('#r-icon-approved'),
    declined: $('#r-icon-declined'),
    headline: $('#r-headline'),
    amount: $('#r-amount'),
    amountText: $('#r-amount-text'),
    message: $('#r-message'),
    note: $('#r-note'),
    fields: $('#r-fields'),
    hints: $('#r-hints'),
    unknown: $('#r-unknown'),
    warning: $('#r-warning'),
    inquire: $('#r-inquire'),
    ref: $('#r-ref'),
    receipt: $('#r-receipt'),
    receiptBtn: $('#r-receipt-btn'),
    fetching: $('#r-receipt-fetching'),
    qr: $('#r-qr'),
    qrImg: $('#r-qr-img'),
    qrUrl: $('#r-qr-url'),
    problem: $('#r-receipt-problem'),
    receiptNote: $('#r-receipt-note'),
    raw: $('#r-raw'),
    rawSummary: $('#r-raw-summary'),
    rawText: $('#r-raw-text'),
    copyStatus: $('#r-copy-status'),
    closeReceipt: $('#r-close-receipt'),
    closeReceiptStatus: $('#r-close-receipt-status'),
  };
  let silentClose = false;

  function showDialog() {
    const d = run.dialog;
    const key = `${run.id}:${run.state}`;

    if (renderedDialogKey !== key) {
      renderedDialogKey = key;
      const approved = d.tone === 'approved';
      show(r.approved, approved);
      show(r.declined, !approved);
      r.headline.className = `headline ${d.tone}`;
      r.headline.textContent = d.headline;
      show(r.amount, d.amount !== '');
      r.amountText.textContent = d.amount;
      setText(r.message, d.message);
      setText(r.note, d.note);

      r.fields.replaceChildren(...d.fields.map((f) => h('div', {}, h('dt', {}, f.label), h('dd', {}, f.value))));
      show(r.fields, d.fields.length !== 0);
      r.hints.replaceChildren(...d.hints.map((hint) => h('li', {}, hint)));
      show(r.hints, d.hints.length !== 0);

      show(r.unknown, d.isUnknownOutcome);
      r.warning.textContent = d.unknownOutcomeWarning ?? '';
      r.ref.textContent = d.inquirableReference ?? '';
      r.inquire.disabled = false;

      setText(r.receiptNote, d.receiptNote);
      r.rawText.textContent = d.rawPayload ?? '';
      show(r.raw, !!d.rawPayload);
      r.raw.open = false;
      r.rawSummary.textContent = 'Show raw response';
      r.copyStatus.textContent = '';
      setText(r.closeReceiptStatus, '');
    }

    renderReceipt(d);
    show(r.closeReceipt, !!d.canCloseReceipt);
    r.closeReceipt.disabled = false;

    if (!dialog.open) {
      dialog.showModal();
      $('#r-close').focus();
    }
  }

  function renderReceipt(d) {
    show(r.receipt, d.offersReceipt || !!d.receiptNote);
    show(r.receiptBtn, d.receipt.showButton);
    show(r.fetching, d.receipt.fetching);
    setText(r.problem, d.receipt.problem);
    show(r.qr, !!d.receipt.url);
    if (d.receipt.url) {
      const src = qrUrl(d.receipt.url);
      if (r.qrImg.getAttribute('src') !== src) r.qrImg.setAttribute('src', src);
      r.qrUrl.value = d.receipt.url;
    }
  }

  function closeDialog({ silent }) {
    silentClose = silent;
    // Whatever opens next is a different result, even under the same key.
    renderedDialogKey = null;
    dialog.close();
  }

  // Closing by any route (button, Escape) means "the operator has read the result": back
  // to idle, and the next transaction starts fresh. A close made because the run moved
  // on (the look-up offered here) must not reset anything under it.
  dialog.addEventListener('close', () => {
    const silent = silentClose;
    silentClose = false;
    if (silent || !run) return;

    const id = run.id;
    forget();
    api.acknowledge(id).catch(() => {});
    // Amount and references are per transaction and must not linger into the next sale.
    form = { ...form, amountDigits: '', merchantReference: '', originalReference: '' };
    syncForm();
    if (els.amount.disabled) els.type.focus();
    else amountInput.focus();
  });

  $('#r-close').addEventListener('click', () => closeDialog({ silent: false }));

  r.closeReceipt.addEventListener('click', async () => {
    if (!run?.dialog?.canCloseReceipt) return;
    r.closeReceipt.disabled = true;
    try {
      const result = await api.closeReceipt(run.id);
      setText(r.closeReceiptStatus, result?.message ?? 'Terminal is back on its idle screen');
    } catch (error) {
      setText(
        r.closeReceiptStatus,
        error instanceof ApiError ? error.message : 'Could not close the receipt',
      );
    } finally {
      r.closeReceipt.disabled = false;
    }
  });

  r.inquire.addEventListener('click', async () => {
    if (!run?.dialog?.isUnknownOutcome) return;
    r.inquire.disabled = true;
    try {
      adopt(await api.inquire(run.id));
    } catch (error) {
      r.inquire.disabled = false;
      r.copyStatus.textContent = error instanceof ApiError ? error.message : 'Could not start the look-up';
    }
  });

  r.receiptBtn.addEventListener('click', async () => {
    if (!run) return;
    r.receiptBtn.disabled = true;
    try {
      adopt(await api.receipt(run.id));
    } catch (error) {
      r.copyStatus.textContent = error instanceof ApiError ? error.message : 'Could not request the receipt';
    } finally {
      r.receiptBtn.disabled = false;
    }
  });

  r.raw.addEventListener('toggle', () => {
    r.rawSummary.textContent = r.raw.open ? 'Hide raw response' : 'Show raw response';
  });

  $('#r-copy-raw').addEventListener('click', async () => {
    r.copyStatus.textContent = (await copyText(r.rawText.textContent)) ? 'Copied to clipboard' : 'Could not copy: the clipboard is in use';
  });

  $('#r-copy-ref').addEventListener('click', async () => {
    r.copyStatus.textContent = (await copyText(r.ref.textContent)) ? 'Reference copied to clipboard' : 'Could not copy: the clipboard is in use';
  });

  // Closing the page does not stop a transaction the terminal has already started, and the
  // WPF window asks before it goes; the browser asks in its own words. The run itself
  // survives: reopening the page picks it up again.
  window.addEventListener('beforeunload', (event) => {
    if (run?.busy) {
      event.preventDefault();
      event.returnValue = '';
    }
  });

  // Start-up ---------------------------------------------------------------------------------------

  async function start() {
    syncForm();
    await refresh();
    // A reload (or a second tab) must not lose a payment that is running or a result nobody read.
    try {
      const { transactions } = await api.pending();
      if (transactions.length !== 0) adopt(transactions[0]);
    } catch {
      // Nothing to resume; the form works regardless.
    }
  }

  return { start, refresh, focus: () => $('#h-transaction').focus() };
}
