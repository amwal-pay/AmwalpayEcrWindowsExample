// Proves the browser's amount entry matches Core's AmountEntry / AmountLimits and the
// WPF AmountInput behaviour. No dependencies: run with `node tools/test-amount.mjs`.
//
// The vectors are the ones in AmwalECR.Example.Tests/AmountTests.cs. The module is
// loaded through a data: URL so it needs no package.json or file extension games.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(here, '../src/AmwalECR.WebExample/wwwroot/js/amount.js'), 'utf8');
const { keyedDigits, format, append, backspace, attachAmountInput } =
  await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

const MAX_DIGITS = 9; // AmountLimits.MaxDigits: 100000.000 is 100000000 mills

let failures = 0;
let checks = 0;
function eq(actual, expected, label) {
  checks++;
  if (actual !== expected) {
    failures++;
    console.error(`FAIL ${label}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
  }
}

// AmountEntryTests.Format_shifts_digits_in_from_the_right
for (const [digits, shown] of [['', '0.000'], ['1', '0.001'], ['12', '0.012'], ['123', '0.123'], ['1234', '1.234'], ['100000000', '100000.000']]) {
  eq(format(digits), shown, `format(${digits})`);
}

// Keys_1_2_3_4_then_backspace_follow_the_readme_table
{
  let digits = '';
  const shown = [];
  for (const key of '1234') {
    digits = append(digits, key, MAX_DIGITS);
    shown.push(format(digits));
  }
  digits = backspace(digits);
  shown.push(format(digits));
  eq(shown.join(','), '0.001,0.012,0.123,1.234,0.123', 'keys 1,2,3,4 then backspace');
  // The wire amount (minor units, 12 digits) for the same states:
  eq('1'.padStart(12, '0'), '000000000001', 'wire 0.001');
  eq('1234'.padStart(12, '0'), '000000001234', 'wire 1.234');
  eq('123'.padStart(12, '0'), '000000000123', 'wire 0.123');
}

// A_zero_on_an_empty_field_changes_nothing; Backspace_on_an_empty_field_is_harmless
eq(append('', '0', MAX_DIGITS), '', 'leading zero');
eq(append('1', '0', MAX_DIGITS), '10', 'zero after 1');
eq(backspace(''), '', 'backspace empty');

// Keying_stops_at_the_maximum_number_of_digits
{
  let digits = '';
  for (const key of '12345678901234') digits = append(digits, key, MAX_DIGITS);
  eq(digits, '123456789', 'capped digits');
  eq(format(digits), '123456.789', 'capped format');
}

// KeyedDigits_keeps_ascii_digits_and_drops_leading_zeros
for (const [text, expected] of [['1a2b3', '123'], ['000123', '123'], ['0.000', ''], ['١٢', '']]) {
  eq(keyedDigits(text, MAX_DIGITS), expected, `keyedDigits(${text})`);
}

// --- The DOM behaviour, against a stand-in input ------------------------------------------------

class FakeInput {
  constructor() {
    this.value = '';
    this.selectionStart = 0;
    this.selectionEnd = 0;
    this.handlers = {};
  }
  addEventListener(name, fn) { (this.handlers[name] ??= []).push(fn); }
  setSelectionRange(a, b) { this.selectionStart = a; this.selectionEnd = b; }
  fire(name, props = {}) {
    const event = { defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, ...props };
    for (const fn of this.handlers[name] ?? []) fn(event);
    return event;
  }
  type(char) { return this.fire('beforeinput', { inputType: 'insertText', data: char }); }
  backspace() { return this.fire('beforeinput', { inputType: 'deleteContentBackward' }); }
}

{
  const changes = [];
  const input = new FakeInput();
  const amount = attachAmountInput(input, { maxDigits: MAX_DIGITS, onChange: (d) => changes.push(d) });

  eq(input.value, '0.000', 'reads 0.000 when empty');

  const e = input.type('1');
  eq(e.defaultPrevented, true, 'typing is handled by the field, not the browser');
  eq(input.value, '0.001', 'DOM: 1');
  input.type('2');
  eq(input.value, '0.012', 'DOM: 12');
  input.type('3'); input.type('4');
  eq(input.value, '1.234', 'DOM: 1234');
  eq(amount.digits, '1234', 'DOM: digits');
  input.backspace();
  eq(input.value, '0.123', 'DOM: backspace shifts out');

  // A decimal point or a letter is not a digit.
  input.type('.'); input.type('x');
  eq(input.value, '0.123', 'DOM: non-digits ignored');

  // Caret pinned to the end, wherever the user clicks.
  input.setSelectionRange(1, 1);
  input.fire('click');
  eq(input.selectionStart, input.value.length, 'caret pinned after click');
  input.setSelectionRange(0, 3);
  input.fire('select');
  eq(input.selectionEnd, input.value.length, 'selection collapsed to the end');
  eq(input.selectionStart, input.value.length, 'selection collapsed to the end (start)');

  // Paste keys its digits in as if typed; punctuation and spaces are dropped.
  input.fire('beforeinput', { inputType: 'insertFromPaste', data: ' 4,5 6.7' });
  eq(amount.digits, '123' + '4567', 'DOM: paste appends the digits');
  eq(input.value, '1234.567', 'DOM: paste shows 1234.567');

  // Enter is not text: the form must still submit.
  eq(input.fire('beforeinput', { inputType: 'insertLineBreak' }).defaultPrevented, false, 'Enter left alone');

  // Delete and Space are swallowed; Escape clears.
  eq(input.fire('keydown', { key: 'Delete' }).defaultPrevented, true, 'Delete swallowed');
  eq(input.fire('keydown', { key: ' ' }).defaultPrevented, true, 'Space swallowed');
  eq(input.fire('keydown', { key: 'Escape' }).defaultPrevented, true, 'Escape handled when there are digits');
  eq(input.value, '0.000', 'Escape clears');
  eq(input.fire('keydown', { key: 'Escape' }).defaultPrevented, false, 'Escape left alone when empty');

  // Cut and drop would edit derived text.
  eq(input.fire('cut').defaultPrevented, true, 'cut blocked');
  eq(input.fire('drop').defaultPrevented, true, 'drop blocked');

  // The max is enforced while typing: the 10th digit is dropped.
  for (const ch of '9999999999') input.type(ch);
  eq(amount.digits, '999999999', 'DOM: capped at MaxDigits');
  eq(input.value, '999999.999', 'DOM: capped display');

  // Focusing from another control starts a fresh amount; re-focus in place does not.
  input.fire('focus', { relatedTarget: null });
  eq(amount.digits, '999999999', 'focus from the window keeps the amount');
  input.fire('focus', { relatedTarget: input });
  eq(amount.digits, '999999999', 'focus from itself keeps the amount');
  input.fire('focus', { relatedTarget: {} });
  eq(amount.digits, '', 'focus from another control starts fresh');
  eq(input.value, '0.000', 'fresh amount reads 0.000');

  // The page moving focus here itself (to show an error) keeps the amount.
  amount.digits = '1234';
  input.focus = function () { this.fire('focus', { relatedTarget: {} }); };
  amount.focus();
  eq(amount.digits, '1234', 'programmatic focus keeps the amount');
  input.fire('focus', { relatedTarget: {} });
  eq(amount.digits, '', 'a real move from another control still starts fresh');

  // A repair path: text put into the field behind its back is rendered away.
  input.value = '12abc';
  input.fire('input');
  eq(input.value, '0.000', 'stray input repaired');

  eq(changes.length > 0, true, 'onChange reported edits');
}

console.log(`${checks - failures}/${checks} checks passed`);
process.exit(failures === 0 ? 0 : 1);
