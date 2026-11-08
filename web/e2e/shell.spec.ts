// The shell: welcome page, command palette, ? help, g-letter navigation, page titles, the crew, both themes.
import path from 'node:path';
import { expect, test } from './fixtures';

test('opens on the welcome page and continues to the overview', async ({ open }) => {
  const page = await open('', { welcome: true });
  await expect(page.getByRole('heading', { name: 'Raise the sails.' })).toBeVisible();
  await expect(page).toHaveTitle('Halyard');
  await page.getByRole('button', { name: /Continue with example-org\/backlog-sandbox/ }).click();
  await expect(page).toHaveURL(/#\/overview$/);
  await expect(page).toHaveTitle('Overview · Halyard');
});

test('a deep link skips the welcome page', async ({ open }) => {
  const page = await open('#/review', { welcome: true });
  await expect(page).toHaveTitle('Review · Halyard');
  await expect(page.getByRole('heading', { name: 'Raise the sails.' })).toHaveCount(0);
});

test('Ctrl+K finds and runs a command', async ({ open }) => {
  const page = await open('#/overview');
  await page.locator('main').click();
  await page.keyboard.press('Control+K');
  const box = page.getByRole('combobox', { name: 'Search pages and actions' });
  await expect(box).toBeFocused();
  await box.fill('propos');
  await expect(page.getByRole('option').first()).toContainText('Proposals');
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/#\/proposals$/);
  await expect(page.locator('dialog[open]')).toHaveCount(0);
});

test('? opens the help with every shortcut, and Esc closes it', async ({ open }) => {
  const page = await open('#/overview');
  await page.locator('main').click();
  await page.keyboard.type('?');
  const help = page.getByRole('dialog', { name: 'Help' });
  await expect(help).toBeVisible();
  await expect(help).toContainText('Open the command palette');
  await expect(help).toContainText('Go to a page');
  await page.keyboard.press('Escape');
  await expect(help).toBeHidden();
});

test('g then a letter goes to a page', async ({ open }) => {
  const page = await open('#/overview');
  await page.locator('main').click();
  await page.keyboard.press('g');
  await page.keyboard.press('h');
  await expect(page).toHaveURL(/#\/hierarchy$/);
  await expect(page).toHaveTitle('Hierarchy · Halyard');
});

test('shortcuts stay quiet while typing in a field', async ({ open }) => {
  const page = await open('#/overview');
  await page.keyboard.press('Control+K');
  await page.keyboard.press('Escape');
  const crewOrb = page.getByRole('button', { name: /Halyard crew orb/ });
  await crewOrb.focus();
  await page.keyboard.press('Enter');   // the orb opens the crew panel (the microphone is refused in tests)
  const box = page.getByRole('textbox', { name: 'Message to the crew' });
  await box.fill('g p ?');
  await expect(page).toHaveURL(/#\/overview$/);
  await expect(page.getByRole('dialog', { name: 'Help' })).toBeHidden();
});

test('the crew runs standup from the palette and answers from the scrum brief', async ({ open, problems }) => {
  const page = await open('#/overview');
  await page.locator('main').click();
  await page.keyboard.press('Control+K');
  await page.getByRole('combobox', { name: 'Search pages and actions' }).fill('run standup');
  await page.keyboard.press('Enter');
  const panel = page.getByRole('dialog', { name: /Crew: product owner and scrum master/ });
  await expect(panel).toBeVisible();
  await expect(panel).toContainText(/S\d+, day \d+/, { timeout: 15_000 });
  await expect(panel).toContainText('No model is configured');
  expect(problems).toEqual([]);
});

test('Settings has the TalkForge voice section', async ({ open }) => {
  const page = await open('#/settings');
  await expect(page.getByRole('heading', { name: 'Voice (TalkForge)' })).toBeVisible();
  await expect(page.getByText('TalkForge was not found')).toBeVisible();
});

for (const theme of ['dark', 'light'] as const) {
  test(`overview renders in the ${theme === 'dark' ? 'Night watch' : 'Chart room'} theme`, async ({ open }, info) => {
    const page = await open('#/overview');
    await page.evaluate((t) => { document.documentElement.dataset.theme = t; }, theme);
    await expect(page.getByRole('heading', { name: 'Overview' })).toBeVisible();
    await page.screenshot({ path: path.join(info.outputDir, `overview-${theme}.png`), fullPage: true });
  });
}
