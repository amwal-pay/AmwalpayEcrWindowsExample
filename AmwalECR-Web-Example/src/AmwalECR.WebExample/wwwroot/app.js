import { api } from './js/api.js';
import { $, show } from './js/dom.js';
import { initTransaction } from './js/transaction.js';
import { initTerminals } from './js/terminals.js';
import { initSettings } from './js/settings.js';
import { initLog } from './js/log.js';

const TITLE = 'Amwal ECR Example';
// Settings live on the Terminals screen (Android ECR-simulator / WPF parity).
const routes = ['transaction', 'terminals'];

/** A small modal confirm, in place of window.confirm: styled, focus-trapped, and safe by default. */
function confirm({ title, message, action }) {
  const dialog = $('#dlg-confirm');
  $('#c-title').textContent = title;
  $('#c-message').textContent = message;
  $('#c-yes').textContent = action;

  return new Promise((resolve) => {
    let answer = false;
    const yes = () => { answer = true; dialog.close(); };
    const no = () => dialog.close();
    $('#c-yes').addEventListener('click', yes, { once: true });
    $('#c-no').addEventListener('click', no, { once: true });
    dialog.addEventListener('close', () => {
      $('#c-yes').removeEventListener('click', yes);
      $('#c-no').removeEventListener('click', no);
      resolve(answer);
    }, { once: true });
    dialog.showModal();
  });
}

async function main() {
  let meta;
  try {
    meta = await api.meta();
  } catch {
    const fatal = $('#fatal');
    fatal.textContent = 'The app could not be reached. Is it still running?';
    show(fatal, true);
    return;
  }

  const log = initLog();
  const transaction = initTransaction({ meta, onFinished: log.refresh });
  const terminals = initTerminals({ meta, confirm });
  const settings = initSettings({ meta, confirm });
  const screens = { transaction, terminals };

  let current = null;
  function route({ moveFocus }) {
    let hash = location.hash.slice(1);
    // Old bookmarks / docs links to #settings → Terminals (where settings live).
    if (hash === 'settings') {
      location.replace('#terminals');
      hash = 'terminals';
    }
    const name = routes.includes(hash) ? hash : 'transaction';
    const changed = name !== current;
    current = name;

    for (const r of routes) show($(`#view-${r}`), r === name);
    for (const link of document.querySelectorAll('nav a')) {
      if (link.dataset.route === name) link.setAttribute('aria-current', 'page');
      else link.removeAttribute('aria-current');
    }
    document.title = `${name[0].toUpperCase()}${name.slice(1)} · ${TITLE}`;

    // Terminals or keys may have changed while away; the config card must not be stale.
    screens[name].refresh?.();
    if (name === 'terminals') settings.refresh?.();
    if (changed && moveFocus) screens[name].focus();
  }

  window.addEventListener('hashchange', () => route({ moveFocus: true }));
  // Escape goes back from the terminals screen, like the WPF window. Not from a field, where
  // it would throw away what is being typed, and never under an open dialog, which uses it.
  document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape' || current === 'transaction' || event.defaultPrevented) return;
    if (document.querySelector('dialog[open]') || event.target.closest?.('input, textarea, select')) return;
    location.hash = '#transaction';
  });
  // Keys may have been changed in another tab.
  document.addEventListener('visibilitychange', () => {
    if (!document.hidden && current === 'transaction') transaction.refresh();
  });

  route({ moveFocus: false });
  await transaction.start();
}

main();
