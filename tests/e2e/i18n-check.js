// 0.14.4: проверка перевода веб-интерфейса (i18n.js) без браузера — у каждой
// строки t('…') / tf('…') / plural('…') есть английский вариант, и в коде нет
// русского текста вне t() (кроме комментариев). Запуск: node i18n-check.js;
// тот же разбор — в specs/i18n.spec.js.
const fs = require('fs');
const path = require('path');

// В репозитории — исходники; в пакете стенда (без src/) — опубликованный wwwroot.
const ROOT = [path.resolve(__dirname, '../../src/Watcher.Server/wwwroot'), path.resolve(__dirname, '../../wsrv/wwwroot')]
  .find(p => fs.existsSync(path.join(p, 'i18n.en.js')));
const files = () => [
  ...fs.readdirSync(ROOT).filter(f => f.endsWith('.js') && !f.startsWith('i18n')).map(f => path.join(ROOT, f)),
  ...fs.readdirSync(path.join(ROOT, 'views')).filter(f => f.endsWith('.js')).map(f => path.join(ROOT, 'views', f)),
];

function loadEn() {
  const src = fs.readFileSync(path.join(ROOT, 'i18n.en.js'), 'utf8');
  return JSON.parse(src.slice(src.indexOf('{'), src.lastIndexOf('}') + 1).replace(/,\s*}$/, '}'));
}

// Строковый литерал JS (одинарные/двойные кавычки) — только без ${} внутри.
const LIT = String.raw`'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)"`;
const unq = s => s.replace(/\\(.)/g, '$1');

function check() {
  const en = loadEn();
  const missing = new Set(), stray = [];
  for (const f of files()) {
    const rel = path.relative(ROOT, f);
    const lines = fs.readFileSync(f, 'utf8').split('\n');
    lines.forEach((line, i) => {
      if (line.includes('i18n-ok')) return;   // данные/регулярка, не текст интерфейса
      const code = line.replace(/\/\*.*?\*\//g, '').replace(/^\s*\/\/.*$/, '').replace(/\s\/\/\s.*$/, '');
      let rest = code;
      for (const m of code.matchAll(new RegExp(String.raw`\b(?:t|tf)\(\s*(?:${LIT})`, 'g'))) {
        const key = unq(m[1] ?? m[2]);
        if (!(key in en)) missing.add(key);
        rest = rest.replace(m[0], '');
      }
      for (const m of code.matchAll(new RegExp(String.raw`\bplural\([^,]+,\s*(?:${LIT})`, 'g'))) {
        const key = unq(m[1] ?? m[2]);
        if (!(key in en)) missing.add(key);
      }
      rest = rest.replace(new RegExp(String.raw`\bplural\([^)]*\)`, 'g'), '');
      if (/[А-Яа-яЁё]/.test(rest)) stray.push(`${rel}:${i + 1}: ${line.trim().slice(0, 140)}`);
    });
  }
  return { missing: [...missing], stray };
}

module.exports = { check };

if (require.main === module) {
  const { missing, stray } = check();
  for (const k of missing) console.log('нет перевода: ' + k);
  for (const s of stray) console.log('текст вне t(): ' + s);
  console.log(`итого: без перевода ${missing.length}, вне t() ${stray.length}`);
  process.exit(missing.length || stray.length ? 1 : 0);
}
