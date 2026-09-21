// POS-style amount entry: the port of Core's AmountEntry and the WPF AmountInput.
//
// The field always shows three decimals and typed digits shift in from the right, so
// 1 gives 0.001, 12 gives 0.012, 1234 gives 1.234. Backspace shifts them back out and
// the box reads 0.000 when empty. There is no decimal point to type. The keyed digits
// are the source of truth; everything shown is derived from them.
//
// The pure functions have no DOM in them so tools/test-amount.mjs can run them under node.

export const FRACTION_DIGITS = 3;

/** Digits only, no leading zeros, capped so the terminal's limit cannot be over-keyed. */
export function keyedDigits(text, maxDigits) {
  const digits = String(text).replace(/[^0-9]/g, '').replace(/^0+/, '');
  return digits.length <= maxDigits ? digits : digits.slice(0, maxDigits);
}

/** Renders keyed digits as 0.000, shifting them in from the right. */
export function format(digits) {
  const padded = (digits.length === 0 ? '0' : digits).padStart(FRACTION_DIGITS + 1, '0');
  const whole = padded.slice(0, -FRACTION_DIGITS).replace(/^0+/, '');
  return `${whole === '' ? '0' : whole}.${padded.slice(-FRACTION_DIGITS)}`;
}

/** Digits after one more key: the new digit shifts in from the right. */
export function append(digits, digit, maxDigits) {
  return keyedDigits(digits + digit, maxDigits);
}

/** Digits after backspace: the last one shifts back out. */
export function backspace(digits) {
  return digits.slice(0, -1);
}

const BACKSPACE_TYPES = new Set(['deleteContentBackward', 'deleteWordBackward', 'deleteSoftLineBackward', 'deleteHardLineBackward']);

/**
 * Makes a plain text input behave as the amount field. All typing is handled here
 * rather than by the browser, and the caret is pinned to the end, so a click in the
 * middle of the number cannot make the next digit land there.
 *
 * Returns { get digits, set digits(value), render(), focus() }. focus() is for the page
 * moving focus here itself (to an error, say): that must not throw the amount away.
 */
export function attachAmountInput(input, { maxDigits, onChange = () => {} }) {
  let digits = '';
  let keepOnFocus = false;

  const pin = () => {
    const end = input.value.length;
    if (input.selectionStart !== end || input.selectionEnd !== end) input.setSelectionRange(end, end);
  };

  const render = () => {
    const text = format(digits);
    if (input.value !== text) input.value = text;
    pin();
  };

  const set = (next) => {
    const changed = next !== digits;
    digits = next;
    render();
    if (changed) onChange(digits);
  };

  const key = (text) => {
    let next = digits;
    for (const ch of String(text)) {
      if (ch >= '0' && ch <= '9') next = append(next, ch, maxDigits);
    }
    set(next);
  };

  // beforeinput covers hardware keys, on-screen keyboards (which report key
  // "Unidentified" on keydown) and paste with one code path.
  input.addEventListener('beforeinput', (event) => {
    // Enter is not text: leave it alone so the form still submits.
    if (event.inputType === 'insertLineBreak' || event.inputType === 'insertParagraph') return;
    event.preventDefault();
    if (event.inputType === 'insertText' || event.inputType === 'insertReplacementText') key(event.data ?? '');
    else if (event.inputType === 'insertFromPaste') key(event.data ?? event.dataTransfer?.getData('text/plain') ?? '');
    else if (BACKSPACE_TYPES.has(event.inputType)) set(backspace(digits));
    // Everything else (delete forward, cut, drop, undo) would edit text this field
    // derives; with the caret always at the end there is nothing to delete to the right.
  });

  input.addEventListener('keydown', (event) => {
    if (event.key === 'Delete' || event.key === ' ') event.preventDefault();
    else if (event.key === 'Escape' && digits.length !== 0) {
      set('');
      event.preventDefault();
    }
  });

  // Anything that slipped past (an input method composing text) is put right.
  input.addEventListener('input', render);
  input.addEventListener('cut', (event) => event.preventDefault());
  input.addEventListener('drop', (event) => event.preventDefault());

  // Focusing the field starts a fresh amount: there is no cursor to place, so a
  // half-typed figure would only be confusing. Only a move from another control
  // counts: the window being re-activated with focus already here does not.
  input.addEventListener('focus', (event) => {
    const from = event.relatedTarget;
    if (!keepOnFocus && from && from !== input && digits.length !== 0) set('');
    pin();
  });

  for (const name of ['select', 'click', 'keyup', 'touchend']) input.addEventListener(name, pin);
  input.addEventListener('mouseup', () => setTimeout(pin, 0));

  render();

  return {
    get digits() {
      return digits;
    },
    set digits(value) {
      digits = keyedDigits(value, maxDigits);
      render();
    },
    render,
    focus() {
      keepOnFocus = true;
      try {
        input.focus();
      } finally {
        keepOnFocus = false;
      }
    },
  };
}
