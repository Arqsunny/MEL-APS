// Browser test of the APS connect + "Link model" helper in Live mode against tests/e2e/fake_aps.py (NOT real APS).
// Run through run_live_tests.sh (needs the fake APS, a Live-mode backend and a fresh database).
const { chromium } = require('playwright-core');

const results = [];
const check = (name, ok, extra = '') => { results.push(!!ok); console.log((ok ? 'PASS ' : 'FAIL ') + name + (!ok && extra ? `  [${extra}]` : '')); };

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await (await browser.newContext()).newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error' && !/40[0-9]|50[0-9]/.test(m.text())) errors.push(m.text()); });
  const url = 'http://localhost:5080/';
  const dialog = page.locator('.modal-content');

  const signIn = async (name) => {
    await page.goto(url);
    await page.getByRole('button', { name: new RegExp(name) }).click();
    await page.waitForSelector('text=Review & Sync to Revit');
  };
  const signOut = async () => { await page.click('button[title="Sign out"]'); await page.waitForSelector('text=Development sign-in'); };

  // 1. lead, not connected yet
  await signIn('Lee BIM Lead');
  await page.waitForSelector('text=APS: not connected');   // status loads asynchronously after sign-in
  check('header shows APS not connected', true);
  check('lead sees a Connect button, no Link model yet',
    await page.getByRole('button', { name: 'Connect', exact: true }).count() === 1 && await page.getByRole('button', { name: 'Link model' }).count() === 0);

  // 2. connect through the (fake) APS sign-in round trip: SPA -> authorize -> backend callback -> SPA
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.waitForSelector('text=APS account connected.', { timeout: 20000 });
  check('APS connect round trip returns to the app signed in, with a confirmation', true);
  check('URL was cleaned (?aps=... removed)', !page.url().includes('aps='), page.url());
  await page.waitForSelector('text=APS: svc@firm.example');
  check('header shows the connected APS account', true);

  // 3. the finder
  await page.getByRole('button', { name: 'Link model' }).click();
  await dialog.locator('text=Link to a Revit cloud model').waitFor();
  check('dialog says the model is not linked yet', await dialog.locator('text=not linked yet').count() === 1);
  await dialog.locator('select').nth(0).selectOption({ label: 'Acme Account' });
  await dialog.locator('select').nth(1).selectOption({ label: 'Test Project' });
  await dialog.getByRole('button', { name: 'Find models' }).click();
  await dialog.locator('text=M-CentralPlant.rvt').waitFor();
  check('both cloud models found across folders and pages',
    await dialog.locator('text=M-CentralPlant.rvt').count() === 1 && await dialog.locator('text=E-Power.rvt').count() === 1);
  check('each model shows its Revit release', await dialog.locator('.review-row:has-text("M-CentralPlant.rvt"):has-text("Revit 2025")').count() === 1
        && await dialog.locator('.review-row:has-text("E-Power.rvt"):has-text("Revit 2026")').count() === 1);
  const oldRow = dialog.locator('.review-row', { hasText: 'Old-Model.rvt' });
  check('a model in an unsupported release (2024) cannot be linked and says why',
        await oldRow.getByRole('button', { name: 'Link' }).isDisabled() && (await oldRow.innerText()).includes('engines exist only for Revit 2025 and 2026'));
  check('GUIDs are displayed so they can be checked', await dialog.locator('text=bbbbbbbb-1111-2222-3333-444444444444').count() === 1);
  check('plain uploaded RVT explained, not listed', await dialog.locator('text=1 were plain uploaded RVTs').count() === 1 && await dialog.locator('text=Plain-Upload.rvt').count() === 0);
  check('foreign-host pagination warning is shown to the lead', await dialog.locator('text=different host').count() === 1);
  check('known hub region is used (no region picker)', await dialog.locator("text=did not report this hub's region").count() === 0);

  // 4. link
  await dialog.locator('.review-row', { hasText: 'M-CentralPlant.rvt' }).getByRole('button', { name: 'Link' }).click();
  await page.waitForSelector('text=Linked to M-CentralPlant.rvt.');
  await dialog.locator('text=Currently linked:').waitFor();
  check('dialog now shows the link (EMEA, project and model GUIDs)',
    (await dialog.innerText()).includes('EMEA') && (await dialog.innerText()).includes('aaaaaaaa-1111-2222-3333-444444444444')
    && (await dialog.locator('text=Currently linked:').innerText()).includes('Revit 2025'));

  // 5. hub without a region asks the lead to choose
  await dialog.locator('select').nth(0).selectOption({ label: 'Regionless Account' });
  await dialog.locator('select').nth(1).selectOption({ label: 'Solo Project' });
  await dialog.getByRole('button', { name: 'Find models' }).click();
  await dialog.locator('text=Solo.rvt').waitFor();
  check("hub with no region: the lead must choose the region", await dialog.locator("text=did not report this hub's region").count() === 1);
  await dialog.getByRole('button', { name: 'Close' }).click();
  await signOut();

  // 6. contributor sees none of it
  await signIn('Casey Contributor');
  check('contributor has no Link model / Connect buttons',
    await page.getByRole('button', { name: 'Link model' }).count() === 0 && await page.getByRole('button', { name: 'Connect', exact: true }).count() === 0);

  check('no unexpected browser errors', errors.length === 0, errors.join(' | '));
  await browser.close();
  console.log(`\n${results.filter(Boolean).length}/${results.length} passed`);
  process.exit(results.every(Boolean) ? 0 : 1);
})().catch(e => { console.error('FATAL', e.message); process.exit(2); });
