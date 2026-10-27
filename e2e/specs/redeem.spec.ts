import { test, expect } from '@playwright/test';
import { ADMIN, adminToken, signIn } from './support';

test('add a campaign, redeem a code, see the coupon, revoke it', async ({ page }) => {
  const run = Date.now().toString(36).toUpperCase();
  const code = `E2E-${run}`;
  const reference = `CUS-${run}`;

  await signIn(page, ADMIN);
  await page.getByLabel('Campaign code').fill(code);
  await page.getByLabel('Redemption cap').fill('8');
  await page.getByRole('button', { name: 'Add campaign' }).click();

  const remaining = page.getByTestId('selected-remaining');
  await expect(page.getByRole('heading', { name: `Redeem a code from ${code}` })).toBeVisible();
  await expect(remaining).toHaveText('8');

  await page.getByLabel('Customer reference').fill(reference);
  await page.getByLabel('Uses', { exact: true }).fill('3');
  await page.getByRole('button', { name: 'Redeem' }).click();

  const row = page.locator(`tr[data-reference="${reference}"]`);
  await expect(row.getByTestId('status')).toHaveText('Redeemed', { timeout: 20_000 });
  await expect(row.getByTestId('coupon')).toHaveText(/CP-[0-9A-F]{12}/);
  await expect(remaining).toHaveText('5');

  // Nothing is kept in the browser: a fresh load reads the same state from the API.
  // Nothing sensitive is stored either: the refresh cookie is HttpOnly, so a reload signs back in through it.
  expect(await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }))).toBe(
    '{}',
  );
  await page.reload();
  await expect(page.getByTestId('whoami')).toContainText(ADMIN.email);
  await page.getByRole('button', { name: new RegExp(`^${code}`) }).click();
  const reloaded = page.locator(`tr[data-reference="${reference}"]`);
  await expect(reloaded.getByTestId('status')).toHaveText('Redeemed');
  await expect(page.getByTestId('selected-remaining')).toHaveText('5');

  await reloaded.getByRole('button', { name: 'Revoke' }).click();
  await expect(reloaded.getByTestId('status')).toHaveText('Revoked');
  await expect(page.getByTestId('selected-remaining')).toHaveText('8');
  // The issued coupon is not rolled back.
  await expect(reloaded.getByTestId('coupon')).toHaveText(/CP-[0-9A-F]{12}/);
  const headers = { Authorization: `Bearer ${await adminToken(page.request)}` };
  const redemptions = await page.request.get(`/api/redemptions?campaignCode=${code}`, { headers });
  const [redemption] = await redemptions.json();
  const coupon = await page.request.get(`/api/coupons/${redemption.id}`, { headers });
  expect((await coupon.json()).status).toBe('Issued');
});
