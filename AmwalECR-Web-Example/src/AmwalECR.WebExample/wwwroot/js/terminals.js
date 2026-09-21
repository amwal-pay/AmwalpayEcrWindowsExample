import { api, ApiError } from './api.js';
import { $, h, show, setText, announce } from './dom.js';

/**
 * Registered terminals: list, add, edit, delete — the port of TerminalsView and
 * TerminalEditWindow. The rules and the messages are Core's TerminalValidator; the
 * server answers with per-field errors and they are shown under the fields.
 */
export function initTerminals({ meta, confirm }) {
  const list = $('#terminal-list');
  const empty = $('#terminals-empty');
  const notice = $('#terminals-notice');
  const dialog = $('#dlg-terminal');
  const form = $('#terminal-form');
  const inputs = {
    name: $('#t-name'),
    serial: $('#t-serial'),
    ip: $('#t-ip'),
    port: $('#t-port'),
    merchantId: $('#t-merchantId'),
    terminalId: $('#t-terminalId'),
  };
  const errorKeys = ['name', 'serial', 'mode', 'ip', 'port', 'merchantId', 'terminalId'];
  const modes = new Map(meta.modes.map((m) => [m.value, m]));

  let originalSerial = null;
  let mode = meta.modes.find((m) => m.ipBased)?.value ?? meta.modes[0].value;
  let opener = null;

  async function refresh() {
    let data;
    try {
      data = await api.terminals();
    } catch {
      setText(notice, 'The terminal list could not be loaded.');
      return;
    }

    setText(notice, data.notice);
    show(empty, data.terminals.length === 0);
    list.replaceChildren(...data.terminals.map(card));
  }

  function card(t) {
    return h('li', { class: 'card' },
      h('h3', {}, t.name),
      h('p', { class: 'mode' }, t.modeLabel),
      h('p', { class: 'mono' }, `S/N ${t.serialNumber}`),
      h('p', { class: 'mono' }, t.connectionSummary),
      h('div', { class: 'actions' },
        h('button', { type: 'button', class: 'text compact', 'aria-label': `Edit ${t.name}`, 'data-edit': t.serialNumber, onclick: (e) => openEditor(t, e.currentTarget) }, 'Edit'),
        h('button', { type: 'button', class: 'danger compact', 'aria-label': `Delete ${t.name}`, onclick: () => remove(t) }, 'Delete')));
  }

  async function remove(t) {
    const yes = await confirm({ title: 'Delete terminal', message: `Delete “${t.name}”? This cannot be undone.`, action: 'Delete' });
    if (!yes) {
      list.querySelector(`[data-edit="${CSS.escape(t.serialNumber)}"]`)?.nextElementSibling?.focus();
      return;
    }
    try {
      await api.deleteTerminal(t.serialNumber);
      setText(notice, '');
      announce(`${t.name} deleted`);
    } catch (error) {
      setText(notice, `Could not delete the terminal: ${error.message}`);
    }
    await refresh();
    $('#btn-add-terminal').focus();
  }

  // Editor -------------------------------------------------------------------------------

  const modeGroup = $('#t-modes');
  for (const m of meta.modes) {
    const hintId = `t-mode-hint-${m.value}`;
    modeGroup.append(
      h('label', { class: 'radio' },
        h('input', { type: 'radio', name: 't-mode', value: m.value, 'aria-describedby': m.hint ? hintId : null }),
        h('span', {}, m.label, m.hint ? h('span', { class: 'caption', id: hintId }, h('br'), m.hint) : null)));
  }
  modeGroup.addEventListener('change', (event) => {
    if (event.target.name === 't-mode') setMode(Number(event.target.value));
  });

  function setMode(value) {
    mode = value;
    const m = modes.get(value);
    for (const radio of modeGroup.querySelectorAll('input')) radio.checked = Number(radio.value) === value;
    show($('#t-wifi'), m.ipBased);
    show($('#t-web'), value === 4);
    setText($('#t-note'), m.note);
  }

  function setErrors(errors) {
    for (const key of errorKeys) {
      const message = errors[key] ?? '';
      setText($(`#e-t-${key}`), message);
      (inputs[key] ?? null)?.setAttribute('aria-invalid', message ? 'true' : 'false');
    }
  }

  function openEditor(existing, trigger) {
    opener = trigger;
    originalSerial = existing?.serialNumber ?? null;
    $('#t-title').textContent = existing ? 'Terminal' : 'Add terminal';
    inputs.name.value = existing?.name ?? '';
    inputs.serial.value = existing?.serialNumber ?? '';
    inputs.ip.value = existing?.ipAddress ?? '';
    // A new terminal starts on the standard port; an existing one shows what it has.
    inputs.port.value = existing ? (existing.port > 0 ? String(existing.port) : '') : String(meta.defaultPort);
    inputs.merchantId.value = existing?.merchantId ?? '';
    inputs.terminalId.value = existing?.terminalId ?? '';
    setMode(existing?.ecrMode ?? meta.modes.find((m) => m.ipBased).value);
    setErrors({});
    dialog.showModal();
    inputs.name.focus();
  }

  $('#btn-add-terminal').addEventListener('click', (e) => openEditor(null, e.currentTarget));
  $('#t-cancel').addEventListener('click', () => dialog.close());

  // A field being edited loses its own message.
  for (const [key, input] of Object.entries(inputs)) {
    input.addEventListener('input', () => {
      if ($(`#e-t-${key}`).hidden) return;
      setText($(`#e-t-${key}`), '');
      input.setAttribute('aria-invalid', 'false');
    });
  }

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const body = {
      name: inputs.name.value,
      serialNumber: inputs.serial.value,
      ecrMode: mode,
      ipAddress: inputs.ip.value,
      port: inputs.port.value,
      terminalId: inputs.terminalId.value,
      merchantId: inputs.merchantId.value,
    };

    $('#t-save').disabled = true;
    try {
      const saved = originalSerial == null ? await api.addTerminal(body) : await api.saveTerminal(originalSerial, body);
      dialog.close();
      await refresh();
      announce(`Terminal ${saved.name} saved`);
      (list.querySelector(`[data-edit="${CSS.escape(saved.serialNumber)}"]`) ?? $('#btn-add-terminal')).focus();
    } catch (error) {
      if (!(error instanceof ApiError) || error.status !== 422) {
        setErrors({ serial: error.message });
        return;
      }
      setErrors(error.fields);
      const first = ['name', 'serial', 'ip', 'port', 'merchantId', 'terminalId'].find((k) => error.fields[k] && !inputs[k].closest('[hidden]'));
      if (first) inputs[first].focus();
      announce(Object.values(error.fields)[0] ?? 'The terminal could not be saved');
    } finally {
      $('#t-save').disabled = false;
    }
  });

  dialog.addEventListener('close', () => {
    // Focus goes back to where the editor was opened, unless the list was rebuilt.
    if (opener?.isConnected) opener.focus();
    opener = null;
  });

  return { refresh, focus: () => $('#h-terminals').focus() };
}
