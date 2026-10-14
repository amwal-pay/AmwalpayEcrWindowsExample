export class ApiError extends Error {
  constructor(status, body) {
    super(body?.error ?? body?.message ?? (status === 0 ? 'The app could not be reached' : `Request failed (${status})`));
    this.status = status;
    this.body = body;
  }

  /** Per-field messages of a 422, keyed like the form's fields. */
  get fields() {
    return this.body?.errors ?? {};
  }
}

// Mutating calls are always JSON, even with nothing to say: the server refuses anything
// else, which is what stops another site's form from driving it.
async function request(method, url, body, { keepalive = false } = {}) {
  let response;
  try {
    response = await fetch(url, {
      method,
      headers: method === 'GET' ? { Accept: 'application/json' } : { Accept: 'application/json', 'Content-Type': 'application/json' },
      body: method === 'GET' ? undefined : JSON.stringify(body ?? {}),
      credentials: 'same-origin',
      cache: 'no-store',
      keepalive,
    });
  } catch {
    throw new ApiError(0, null);
  }

  if (response.status === 204) return null;
  const data = await response.json().catch(() => null);
  if (!response.ok) throw new ApiError(response.status, data);
  return data;
}

const enc = encodeURIComponent;

export const api = {
  meta: () => request('GET', '/api/meta'),

  terminals: () => request('GET', '/api/terminals'),
  addTerminal: (form) => request('POST', '/api/terminals', form),
  saveTerminal: (serial, form) => request('PUT', `/api/terminals/${enc(serial)}`, form),
  deleteTerminal: (serial) => request('DELETE', `/api/terminals/${enc(serial)}`),
  config: (serial, announce = false) => request('GET', `/api/terminals/${enc(serial)}/config${announce ? '?announce=true' : ''}`),

  settings: () => request('GET', '/api/settings'),
  setEnvironment: (environment) => request('PUT', '/api/settings/environment', { environment }),
  setKey: (kind, value) => request('PUT', `/api/settings/keys/${enc(kind)}`, { value }),
  clearKey: (kind) => request('DELETE', `/api/settings/keys/${enc(kind)}`),

  pending: () => request('GET', '/api/transactions'),
  start: (form) => request('POST', '/api/transactions', form),
  transaction: (id) => request('GET', `/api/transactions/${enc(id)}`),
  cancel: (id) => request('POST', `/api/transactions/${enc(id)}/cancel`),
  inquire: (id) => request('POST', `/api/transactions/${enc(id)}/inquire`),
  receipt: (id) => request('POST', `/api/transactions/${enc(id)}/receipt`),
  closeReceipt: (id) => request('POST', `/api/transactions/${enc(id)}/close-receipt`),
  // keepalive: the acknowledgement must still go out if the page is closed or reloaded right after.
  acknowledge: (id) => request('DELETE', `/api/transactions/${enc(id)}`, undefined, { keepalive: true }),

  log: () => request('GET', '/api/log'),
  clearLog: () => request('DELETE', '/api/log'),
};

export const qrUrl = (text) => `/api/qr?text=${enc(text)}`;
