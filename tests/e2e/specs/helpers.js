const fs = require('fs');
const path = require('path');
const base = require('@playwright/test');
const F = require('../fixtures.js');

// Каждый тест падает на любой ошибке JS, нарушении CSP или неожиданном HTTP
// 4xx/5xx — даже если сам сценарий «прошёл». Ожидаемые ошибки тест объявляет
// через page.expectHttpError(status, urlPart).
const test = base.test.extend({
  page: async ({ page }, use) => {
    const problems = [];
    const expected = [];
    page.expectHttpError = (status, urlPart) => expected.push({ status, urlPart });
    page.on('pageerror', e => problems.push('pageerror: ' + e.message));
    page.on('console', m => {
      // «Failed to load resource» дублирует response-событие ниже, там решаем.
      if (m.type() === 'error' && !m.text().startsWith('Failed to load resource')) problems.push('console: ' + m.text());
    });
    page.on('response', r => {
      if (r.status() < 400) return;
      const url = r.url();
      if (!expected.some(e => e.status === r.status() && url.includes(e.urlPart)))
        problems.push(`http ${r.status()} ${r.request().method()} ${url}`);
    });
    page.on('dialog', d => d.accept());
    await use(page);
    base.expect(problems, 'ошибки в браузере').toEqual([]);
    base.expect(await page.evaluate(() => window.__xss), 'исполнился внедрённый скрипт').toBeUndefined();
  },
});

// Сессия роли, сохранённая seed.js (вход через форму — только в auth.spec.js).
async function login(page, username = 'admin') {
  const state = JSON.parse(fs.readFileSync(path.join(F.authDir, username + '.json'), 'utf8'));
  await page.context().addCookies(state.cookies);
  await page.goto('/');
  await page.locator('.topnav a').first().waitFor();
}

// Настоящий вход через форму login.html.
async function loginViaForm(page, username = 'admin', password = F.adminPassword) {
  await page.goto('/login.html');
  await page.fill('#username', username);
  await page.fill('#password', password);
  await page.click('#submit');
  await page.waitForURL(url => url.pathname === '/');
  await page.locator('.topnav a').first().waitFor();
}

const navItems = page => page.locator('.topnav a').allTextContents();

module.exports = { test, expect: base.expect, login, loginViaForm, navItems, F };
