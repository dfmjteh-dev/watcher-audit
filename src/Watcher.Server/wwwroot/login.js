// Этап E1 (roadmap-next.md): вынесено из login.html под CSP без 'unsafe-inline'.
// Публичный (AuthGate.PublicPaths) — грузится до входа, без Preact; язык — i18n.js.
import { t, getLang, setLang } from './i18n.js';

// Подписи с data-t — на выбранном языке; переключатель RU/EN внизу.
document.title = t('Watcher — вход');
for (const el of document.querySelectorAll('[data-t]')) el.textContent = t(el.dataset.t);
for (const b of document.querySelectorAll('[data-lang]')) {
  b.classList.toggle('current', b.dataset.lang === getLang());
  b.addEventListener('click', () => { if (b.dataset.lang !== getLang()) setLang(b.dataset.lang); });
}

const form = document.getElementById('login-form');
const errorEl = document.getElementById('error');
const submitBtn = document.getElementById('submit');

// 0.13: версия сервера внизу — «Watcher 0.13.0». Нет ответа — остаётся «Watcher».
fetch('/api/login').then(r => r.ok ? r.json() : null).then(d => {
  if (d && d.version) document.getElementById('version').textContent = 'Watcher ' + d.version;
}).catch(() => {});

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  errorEl.textContent = '';
  submitBtn.disabled = true;
  try {
    const r = await fetch('/api/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        username: document.getElementById('username').value,
        password: document.getElementById('password').value,
      }),
    });
    if (r.ok) {
      // Глубокая ссылка, открытая без сессии (#/hosts/…), переживает редирект на
      // вход фрагментом — возвращаемся туда же. Всегда '/' + hash этой же
      // страницы, увести на чужой origin так нельзя.
      location.href = '/' + location.hash;
      return;
    }
    errorEl.textContent = r.status === 429 ? t('Слишком много попыток входа — подождите несколько минут') : t('Неверный логин или пароль');
  } catch {
    errorEl.textContent = t('Сервер недоступен');
  } finally {
    submitBtn.disabled = false;
  }
});
