const { chromium } = require('playwright-core');

const results = [];
const check = (name, ok, extra = '') => { results.push(!!ok); console.log((ok ? 'PASS ' : 'FAIL ') + name + (!ok && extra ? `  [${extra}]` : '')); };

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await (await browser.newContext()).newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error' && !/401|403|409/.test(m.text())) errors.push(m.text()); });
  const url = 'http://localhost:5080/';

  const signIn = async (name) => {
    await page.goto(url);
    await page.getByRole('button', { name: new RegExp(name) }).click();
    await page.waitForSelector('text=Review & Sync to Revit');
  };
  const signOut = async () => { await page.click('button[title="Sign out"]'); await page.waitForSelector('text=Development sign-in'); };

  // 1. Not signed in -> login screen, no data
  await page.goto(url);
  await page.waitForSelector('text=Development sign-in');
  check('anonymous visitor sees the login screen', await page.locator('text=Casey Contributor').count() === 1);
  check('no equipment visible before sign-in', await page.locator('text=AHU-01').count() === 0);

  // 2. Viewer (no role): can read but not stage
  await signIn('Val Viewer');
  await page.waitForSelector('tbody tr');
  check('viewer sees equipment', await page.locator('tbody tr').count() >= 4);
  await page.locator('tbody tr', { hasText: 'CH-01' }).click();
  const stageBtn = page.getByRole('button', { name: /Stage Changes/ });
  await page.locator('.prop-item', { hasText: 'PO NUMBER' }).locator('input').fill('PO-UI-1');
  check('viewer: Stage button disabled', await stageBtn.isDisabled());
  await signOut();

  // 3. Contributor stages a change
  await signIn('Casey Contributor');
  await page.locator('tbody tr', { hasText: 'CH-01' }).click();
  await page.locator('.prop-item', { hasText: 'PO NUMBER' }).locator('input').fill('PO-UI-1');
  await page.getByRole('button', { name: /Stage Changes/ }).click();
  await page.waitForSelector('text=awaiting BIM Lead approval');
  check('contributor staged a change', true);
  await page.getByRole('button', { name: /Review & Sync/ }).click();
  check('contributor sees "Only BIM Leads can approve" notice', await page.locator('text=Only BIM Leads can approve').count() === 1);
  check('contributor has no Approve button', await page.getByRole('button', { name: 'Approve', exact: true }).count() === 0);
  check('dispatch disabled for contributor', await page.getByRole('button', { name: /Authorize & Sync to Revit/ }).isDisabled());
  await page.getByRole('button', { name: 'Close' }).click();
  await signOut();

  // 4. Lead: sees Approve; approves; dispatches
  await signIn('Lee BIM Lead');
  await page.getByRole('button', { name: /Review & Sync/ }).click();
  const approve = page.getByRole('button', { name: 'Approve', exact: true });
  check('lead sees Approve for the contributor request', await approve.count() >= 1);
  await approve.first().click();
  await page.waitForSelector('text=Change approved');
  check('lead approved', true);
  await page.getByRole('button', { name: /Authorize & Sync to Revit/ }).click();
  await page.waitForSelector('text=Sync queued');
  check('lead queued the sync (returns immediately)', true);
  await page.getByRole('button', { name: 'Close' }).click();
  await page.waitForSelector('tbody tr:has-text("CH-01"):has-text("Syncing")', { timeout: 8000 }).catch(() => {});
  await page.waitForFunction(() => [...document.querySelectorAll('tbody tr')].some(r => r.innerText.includes('CH-01') && r.innerText.includes('Synced to RVT')), null, { timeout: 40000 });
  check('CH-01 shows Synced to RVT once the background sync completes (no reload)', true);
  await signOut();

  // 4b. a change that fails in the sync stays visible, explained, and retryable
  await signIn('Casey Contributor');
  await page.locator('tbody tr', { hasText: 'CT-01' }).click();
  await page.locator('.prop-item', { hasText: 'PO NUMBER' }).locator('input').fill('FAIL-UI-1');
  await page.getByRole('button', { name: /Stage Changes/ }).click();
  await page.waitForSelector('text=awaiting BIM Lead approval');
  await signOut();
  await signIn('Lee BIM Lead');
  await page.getByRole('button', { name: /Review & Sync/ }).click();
  await page.getByRole('button', { name: 'Approve', exact: true }).first().click();
  await page.waitForSelector('text=Change approved');
  await page.getByRole('button', { name: /Authorize & Sync to Revit/ }).click();
  await page.waitForSelector('text=Sync queued');
  await page.waitForSelector('text=/Sync (failed|finished with problems)/', { timeout: 40000 });
  check('failed sync is announced to the user', true);
  await page.getByRole('button', { name: 'Close' }).click().catch(() => {});
  await page.getByRole('button', { name: /Review & Sync/ }).click();
  check('the failed change is back in Approved with its reason', await page.locator('text=Last sync attempt failed: MockFailure').count() === 1);
  check('...and can be retried (Sync button enabled)', await page.getByRole('button', { name: /Authorize & Sync to Revit/ }).isEnabled());
  check('recent syncs list shows both jobs', await page.locator('text=Recent syncs').count() === 1 && await page.locator('.review-row:has-text("Failed"), .review-row:has-text("Succeeded")').count() >= 2);
  await page.getByRole('button', { name: 'Close' }).click();
  check('CT-01 is not shown as synced (value was not written)', !(await page.locator('tbody tr', { hasText: 'CT-01' }).innerText()).includes('Synced to RVT'));

  // 4c. connectivity check: lead-only, no changes, result announced
  await page.getByRole('button', { name: /Review & Sync/ }).click();
  check('lead sees a Test connection button', await page.getByRole('button', { name: 'Test connection' }).count() === 1);
  await page.getByRole('button', { name: 'Test connection' }).click();
  await page.waitForSelector('text=Connectivity check queued');
  await page.getByRole('button', { name: 'Close' }).click();
  await page.waitForSelector('text=Connectivity check passed', { timeout: 40000 });
  check('connectivity check result is announced', true);
  await page.getByRole('button', { name: /Review & Sync/ }).click();
  check('recent syncs lists it as a Connectivity check', await page.locator('.review-row:has-text("Connectivity check")').count() === 1);
  await page.getByRole('button', { name: 'Close' }).click();
  await signOut();
  await signIn('Casey Contributor');
  await page.getByRole('button', { name: /Review & Sync/ }).click();
  check('contributor does not see Test connection', await page.getByRole('button', { name: 'Test connection' }).count() === 0);
  await page.getByRole('button', { name: 'Close' }).click();
  await signOut();
  await signIn('Lee BIM Lead');

  // 5. Own request: lead stages and cannot review it
  await page.locator('tbody tr', { hasText: 'P-01' }).click();
  await page.locator('.prop-item', { hasText: 'PO NUMBER' }).locator('input').fill('PO-LEAD-1');
  await page.getByRole('button', { name: /Stage Changes/ }).click();
  await page.waitForSelector('text=awaiting BIM Lead approval');
  await page.getByRole('button', { name: /Review & Sync/ }).click();
  check('lead cannot review own request (UI hint, API also enforces)',
    await page.locator('text=another BIM Lead must review it').count() === 1);
  await page.getByRole('button', { name: 'Close' }).click();

  // 6. Expired / invalid token -> bounced to login
  await page.evaluate(() => sessionStorage.setItem('mel.token', 'bad.token.value'));
  await page.reload();
  await page.waitForSelector('text=Development sign-in');
  check('invalid token sends user back to login', true);

  check('no unexpected browser errors', errors.length === 0, errors.join(' | '));
  await browser.close();
  console.log(`\n${results.filter(Boolean).length}/${results.length} passed`);
  process.exit(results.every(Boolean) ? 0 : 1);
})().catch(e => { console.error('FATAL', e.message); process.exit(2); });
