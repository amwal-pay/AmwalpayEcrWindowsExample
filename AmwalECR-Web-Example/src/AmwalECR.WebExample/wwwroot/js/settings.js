import { api, ApiError } from './api.js';
import { $, h, show, setText, announce } from './dom.js';

/**
 * Environment and signing keys — embedded on the Terminals screen (Android ECR-simulator /
 * WPF SettingsView above the terminal list). Two mode-specific secrets, not per-terminal.
 *
 * Keys are write-only here. The server never returns a stored key, so where the WPF app
 * has Show / Hide this page has "Configured", Replace and Clear. A typed key lives in the
 * input only until it is saved, and is wiped from it the moment the server has it.
 */
export function initSettings({ meta, confirm }) {
  const notice = $('#settings-notice');
  const boxes = { wifi: $('#key-wifi'), webService: $('#key-webService') };
  const keyState = { wifi: { editing: false }, webService: { editing: false } };
  let settings = null;

  // Environment radios.
  const group = $('#env-group');
  for (const name of meta.environments) {
    group.append(h('label', { class: 'radio' },
      h('input', { type: 'radio', name: 'environment', value: name }),
      h('span', {}, name)));
  }
  group.addEventListener('change', async (event) => {
    if (event.target.name !== 'environment') return;
    try {
      apply(await api.setEnvironment(event.target.value));
      announce(`Environment set to ${event.target.value}`);
    } catch (error) {
      setText(notice, error.message);
      render();
    }
  });

  async function refresh() {
    try {
      apply(await api.settings());
    } catch {
      setText(notice, 'The settings could not be loaded.');
    }
  }

  function apply(next) {
    settings = next;
    setText(notice, next.notice);
    render();
  }

  function render() {
    if (!settings) return;
    for (const radio of group.querySelectorAll('input')) radio.checked = radio.value === settings.environment;
    renderKey('wifi', settings.wifi);
    renderKey('webService', settings.webService);
  }

  function renderKey(kind, key) {
    const state = keyState[kind];
    const box = boxes[kind];
    // Nothing to keep to: a key not yet set goes straight to the input.
    const editing = state.editing || !key.configured;
    const inputId = `k-${kind}`;
    const errorId = `e-k-${kind}`;

    const status = h('p', { class: 'body', id: `s-k-${kind}` },
      h('strong', {}, key.label), ': ', key.configured ? 'Configured ✓' : 'Not configured');

    if (!editing) {
      box.replaceChildren(
        status,
        h('div', { class: 'row' },
          h('button', { type: 'button', class: 'outlined compact', 'aria-describedby': `s-k-${kind}`, onclick: () => { state.editing = true; renderKey(kind, key); $(`#${inputId}`).focus(); } }, 'Replace'),
          h('button', { type: 'button', class: 'danger compact', 'aria-describedby': `s-k-${kind}`, onclick: () => clear(kind, key) }, 'Clear')));
      return;
    }

    const input = h('input', {
      id: inputId, type: 'password', autocomplete: 'off', spellcheck: 'false', autocapitalize: 'off',
      'aria-describedby': `${errorId} h-${inputId}`,
    });
    // Hex only, as in the WPF field.
    input.addEventListener('input', () => {
      const cleaned = input.value.replace(/[^0-9a-fA-F]/g, '');
      if (cleaned !== input.value) input.value = cleaned;
      setText($(`#${errorId}`), '');
      input.setAttribute('aria-invalid', 'false');
    });

    const save = async () => {
      const value = input.value;
      try {
        const next = await api.setKey(kind, value);
        state.editing = false;
        apply(next);
        announce(`${key.label} saved`);
        box.querySelector('button')?.focus();
      } catch (error) {
        if (error instanceof ApiError && error.status === 422) {
          setText($(`#${errorId}`), error.fields.value ?? error.message);
          input.setAttribute('aria-invalid', 'true');
          input.focus();
        } else {
          setText(notice, error.message);
        }
      }
    };

    const form = h('form', { novalidate: true, onsubmit: (e) => { e.preventDefault(); save(); } },
      h('label', { for: inputId }, `${key.label} ${key.configured ? '(enter a new key to replace it)' : ''}`.trim()),
      input,
      h('p', { class: 'error', id: errorId, hidden: true }),
      h('p', { class: 'caption', id: `h-${inputId}` }, 'An even-length hex string of at least 16 characters. It is stored encrypted and cannot be shown again.'),
      h('div', { class: 'row' },
        h('button', { type: 'submit', class: 'primary compact' }, 'Save key'),
        key.configured ? h('button', { type: 'button', class: 'outlined compact', onclick: () => { input.value = ''; state.editing = false; renderKey(kind, key); } }, 'Cancel') : null));

    box.replaceChildren(status, form);
  }

  async function clear(kind, key) {
    const yes = await confirm({
      title: 'Clear key',
      message: `Clear the ${key.label}? Terminals that use it cannot run transactions until a new one is set.`,
      action: 'Clear',
    });
    if (!yes) return;
    try {
      apply(await api.clearKey(kind));
      announce(`${key.label} cleared`);
    } catch (error) {
      setText(notice, error.message);
    }
  }

  return { refresh, focus: () => $('#h-settings').focus() };
}
