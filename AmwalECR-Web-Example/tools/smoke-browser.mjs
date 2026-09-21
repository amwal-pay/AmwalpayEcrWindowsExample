// Loads the real page in headless Chrome and fails on anything a user would hit as a broken
// screen: a console error, an uncaught exception, a blocked (CSP) resource, a failed or 4xx/5xx
// request, or a section that does not show. Complements check-web-wiring.py, which reads the
// code; this runs it.
//
//   npm i --prefix /tmp/pp puppeteer-core            # once; not a dependency of the sample
//   PUPPETEER_DIR=/tmp/pp node tools/smoke-browser.mjs [baseUrl]
//
// CHROME=/path/to/chrome overrides the browser; the default is macOS Google Chrome.
import { createRequire } from 'node:module';

const require = createRequire(`${process.env.PUPPETEER_DIR ?? process.cwd()}/`);
const puppeteer = require('puppeteer-core');

const BASE = process.argv[2] ?? 'http://127.0.0.1:5080';
const CHROME = process.env.CHROME ?? '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';

const problems = [];
const browser = await puppeteer.launch({ executablePath: CHROME, headless: true });
try {
  const page = await browser.newPage();
  await page.setViewport({ width: 1100, height: 900 });
  await page.evaluateOnNewDocument(() => {
    window.__csp = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__csp.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  page.on('console', (m) => { if (m.type() === 'error') problems.push(`console error: ${m.text()}`); });
  page.on('pageerror', (e) => problems.push(`uncaught exception: ${e.message}`));
  page.on('requestfailed', (r) => problems.push(`request failed: ${r.url()} (${r.failure()?.errorText})`));
  page.on('response', (r) => { if (r.status() >= 400) problems.push(`HTTP ${r.status()}: ${r.url()}`); });

  await page.goto(BASE, { waitUntil: 'networkidle0' });

  const sections = await page.$$eval('nav[aria-label="Sections"] button, nav[aria-label="Sections"] a', (els) => els.map((e) => e.textContent.trim()));
  if (sections.length < 3) problems.push(`expected Transaction/Terminals/Settings in the nav, found: ${JSON.stringify(sections)}`);

  for (const [i, name] of sections.entries()) {
    await page.evaluate((idx) => document.querySelectorAll('nav[aria-label="Sections"] button, nav[aria-label="Sections"] a')[idx].click(), i);
    await new Promise((r) => setTimeout(r, 400));
    const shown = await page.$$eval('main section.view, section.view', (els) => els.filter((e) => !e.hidden && e.getClientRects().length).map((e) => e.id));
    if (shown.length !== 1) problems.push(`section "${name}": expected exactly one view visible, got ${JSON.stringify(shown)}`);
    console.log(`ok  section "${name}" -> ${shown.join(', ')}`);
  }

  // The app's own last-resort banner: shown when start-up fails in a way it caught itself.
  const fatal = await page.$eval('#fatal', (e) => (e.hidden ? '' : e.textContent.trim()));
  if (fatal) problems.push(`the page shows its fatal-error banner: ${fatal}`);

  const csp = await page.evaluate(() => window.__csp);
  for (const v of csp) problems.push(`CSP blocked: ${v}`);
} finally {
  await browser.close();
}

if (problems.length) {
  console.error(`\n${problems.length} problem(s):`);
  for (const p of problems) console.error(' - ' + p);
  process.exit(1);
}
console.log('page loads and every section shows, with no console errors, blocked resources or failed requests');
