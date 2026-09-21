// Everything the page renders from the server goes through textContent or setAttribute,
// never through markup parsing, so nothing a terminal or a reference contains can become markup.

export const $ = (selector, root = document) => root.querySelector(selector);

/** Creates an element. Strings and numbers become text nodes; null and false are skipped. */
export function h(tag, props = {}, ...children) {
  const el = document.createElement(tag);
  for (const [name, value] of Object.entries(props)) {
    if (value == null || value === false) continue;
    if (name === 'class') el.className = value;
    else if (name.startsWith('on') && typeof value === 'function') el.addEventListener(name.slice(2), value);
    else el.setAttribute(name, value === true ? '' : String(value));
  }
  for (const child of children.flat()) {
    if (child == null || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

/**
 * Shows or hides an element with the `hidden` attribute (the stylesheet makes it stick).
 * The attribute, not the `hidden` property: SVG elements have no such property.
 */
export function show(el, visible) {
  el.toggleAttribute('hidden', !visible);
}

/** Sets text, and hides the element when there is none. */
export function setText(el, text) {
  const value = text ?? '';
  el.textContent = value;
  show(el, value !== '');
}

let announcer;

/** Says something to screen readers without moving focus. */
export function announce(message) {
  announcer ??= $('#announcer');
  // Clearing first makes the same message be read twice in a row.
  announcer.textContent = '';
  setTimeout(() => { announcer.textContent = message; }, 30);
}

export async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
