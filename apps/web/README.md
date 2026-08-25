# Web diagnostics

## Назначение

`apps/web` — optional Vite/TypeScript диагностический интерфейс. Это не
основной Desktop UI и не замена WinUI приложению.

## Навигация

- `index.html` — entry document.
- `src/` — компоненты и API-клиент (если включены в текущую сборку).
- `package.json` / `vite.config.ts` — dev/build configuration.
- `Dockerfile` — optional web image.

## Эксплуатация

```powershell
npm ci --prefix apps/web
npm run build --prefix apps/web
```

Web должен обращаться только к разрешённому API gateway и не хранить токены,
PCM или transcript в браузерном localStorage. Для рабочего пользователя
используйте `apps/desktop`; web включается только отдельным диагностическим
профилем.
