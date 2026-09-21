import { api } from './api.js';
import { $, copyText } from './dom.js';

const POLL_MS = 1000;

/** The debug output panel: the SDK's diagnostics, last 500 lines. Polls while open. */
export function initLog() {
  const panel = $('#log-panel');
  const summary = $('#log-summary');
  const text = $('#log-text');
  const status = $('#log-status');
  let timer = null;
  let last = null;

  async function refresh() {
    try {
      const log = await api.log();
      summary.textContent = `Debug output (${log.count} lines)`;
      if (log.text !== last) {
        // Stay pinned to the newest line unless the reader has scrolled up.
        const pinned = text.scrollHeight - text.scrollTop - text.clientHeight < 24;
        last = log.text;
        text.value = log.text;
        if (pinned) text.scrollTop = text.scrollHeight;
      }
    } catch {
      // The next tick tries again.
    }
  }

  function schedule() {
    clearTimeout(timer);
    if (!panel.open || document.hidden) return;
    timer = setTimeout(async () => {
      await refresh();
      schedule();
    }, POLL_MS);
  }

  panel.addEventListener('toggle', () => {
    if (panel.open) refresh().then(() => { text.scrollTop = text.scrollHeight; });
    schedule();
  });
  document.addEventListener('visibilitychange', schedule);

  $('#log-copy').addEventListener('click', async () => {
    status.textContent = (await copyText(text.value)) ? 'Copied to clipboard' : 'Could not copy: the clipboard is in use';
  });

  $('#log-clear').addEventListener('click', async () => {
    await api.clearLog();
    status.textContent = '';
    last = null;
    await refresh();
  });

  return { refresh };
}
