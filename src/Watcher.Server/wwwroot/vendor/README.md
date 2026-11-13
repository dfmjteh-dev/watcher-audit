# Сторонние браузерные библиотеки (Задача E1, `docs/roadmap-next.md`)

Лежат в репе, а не берутся с CDN: сервер и станции часто в LAN без
интернета, а CSP (`script-src 'self'`) не пускает чужие источники.
Бандлера нет — файлы отдаются как есть, ES-модулями.

| Файл | Пакет | Откуда в npm-пакете | Лицензия |
|---|---|---|---|
| `preact.module.js` | `preact@10.29.8` | `dist/preact.module.js` | MIT, `LICENSE-preact` |
| `hooks.module.js` | `preact@10.29.8` | `hooks/dist/hooks.module.js` | MIT, `LICENSE-preact` |
| `htm.module.js` | `htm@3.1.1` | `dist/htm.module.js` | Apache-2.0, `LICENSE-htm` |
| `xterm.module.js` | `@xterm/xterm@6.0.0` | `lib/xterm.mjs` | MIT, `LICENSE-xterm` |
| `xterm.css` | `@xterm/xterm@6.0.0` | `css/xterm.css` | MIT, `LICENSE-xterm` |

Две правки относительно оригинала, обе механические:
- `hooks.module.js`: `from"preact"` → `from"./preact.module.js"` (голый
  спецификатор без import map браузер не разрешит, а инлайн import map — это
  инлайн-скрипт, его не пустит CSP);
- удалены строки `//# sourceMappingURL=...` (сами .map не кладём).

Обновление: `npm pack preact@<версия> htm@<версия>`, распаковать, повторить
две правки выше, обновить таблицу и хеши.

## xterm.js (Задача E4 — плеер консоли)

Грузится только плеером консоли (`views/termplayer.js`, динамический
`import`), не на каждой странице. Правки относительно `lib/xterm.mjs` — под
CSP без `'unsafe-inline'` (`style-src 'self'`), без них браузер режет стили
терминала (размеры ячеек, цвета, полосу прокрутки) и пишет нарушения CSP:

- три `X.createElement("style")` (тема DOM-рендерера, размеры строк, стиль
  полосы прокрутки) → `__watcherCspStyle(X)`: функция добавлена в начало
  файла и возвращает инертный `<template>`, у которого `textContent` пишет в
  constructable stylesheet (`document.adoptedStyleSheets`) — это CSSOM, CSP
  его не ограничивает. `remove()` убирает и таблицу стилей;
- `_addStyle(t,e){t.setAttribute("style",…)}` (truecolor-цвета ячеек) →
  `t.style.cssText+=…` — тоже CSSOM вместо атрибута;
- удалена строка `//# sourceMappingURL=...`.

`WebUiTests.Vendored_xterm_is_patched_for_strict_csp` падает, если после
обновления правку забыли повторить; e2e (`console.spec.js`) — если правка
перестала работать (тест падает на любом нарушении CSP).

SHA-256 после правок:

```
8e0e4efa954c9306af73bd0e4f82b713f6c7d5309c152306af94a68af004ce7b  hooks.module.js
ab33dd3f38059b9be4d5f5350128eefb2356639c4e0bbe9d9e8b3ba75847e9e4  htm.module.js
712517dd2a42c3c22b5ab0264ad7e9a2b1e8be6cf97fa00dbedd98203617e616  preact.module.js
c18827504db9a6d0424c1c414a62ab2db6c3bb8e2641bf3a3c2b9206667efa45  xterm.module.js
854a7c0fb70e8b1a083c16797ab827299fb18744f5ad34f227b48337e33293c6  xterm.css
```
