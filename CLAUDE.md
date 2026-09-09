# CLAUDE.md — DebugAssistance

DebugAssistance is a RimWorld mod split across a C# backend and a Vue/TypeScript frontend that talk over an in-process HTTP API:

- **Backend** (`Source/DebugAssistance/`): runs inside RimWorld. `Source/DebugAssistance/Web/DebugAssistanceServer.cs` hosts the HTTP server; `Source/DebugAssistance/Web/*Endpoints.cs` define the routes; `Source/DebugAssistance/Web/Dtos/*.cs` define the request/response shapes.
- **Frontend** (`web/`): a Vue 3 + TypeScript SPA. `web/src/api.ts` is the only place that calls the backend's HTTP endpoints; `web/src/types.ts` declares the TypeScript shapes those calls consume.
- **Served build output** (`Site/`): the committed, built-and-checked-in output of `web/`. `web/vite.config.ts` sets `outDir: '../Site'`, and `Source/DebugAssistance/Web/DebugAssistanceServer.cs` serves static files straight out of the mod's `Site/` folder at runtime — there is no build step in the mod's own load process. `.github/workflows/ci.yml` and `release.yml` package `Site` as an extra file alongside the compiled assembly.

## Keeping backend and frontend in sync

There is no code generation between the two sides — DTOs and TypeScript types are hand-mirrored. Whenever you add, change, or remove a feature that touches the API surface, update all of the following together, in the same change:

1. **DTOs** (`Source/DebugAssistance/Web/Dtos/*.cs`) — the C# request/response shapes.
2. **Endpoint** (`Source/DebugAssistance/Web/*Endpoints.cs`) — the route(s) that use those DTOs.
3. **Frontend types** (`web/src/types.ts`) — the TypeScript shape matching each DTO field-for-field (names, optionality, and nesting need to match what the JSON serializer actually produces, not just the C# property names).
4. **Frontend API client** (`web/src/api.ts`) — the function(s) that call the endpoint and parse its response into those types.
5. **Frontend components** (`web/src/components/`, `web/src/App.vue`) — wherever the changed data is consumed or displayed.
6. **Tests on both sides** — a new/changed C# endpoint or DTO needs backend tests (see the root `CLAUDE.SHARED.md`'s testing section, included below); a new/changed frontend consumer needs a matching `web/test/*.test.ts`.

When removing a feature, remove it from both sides in the same change — a DTO or endpoint left behind after the frontend stops calling it (or a frontend type/component left behind after the endpoint is removed) is dead code, not a safety net.

### Translated frontend strings

The frontend does not hard-code its own UI strings. `Source/DebugAssistance/Web/TranslationsEndpoint.cs` reads every `DebugAssistance.FrontEnd.*` key out of `Common/Languages/English/Keyed/FrontEnd.xml` (and any other language's translation of that same file) and serves them from `/api/translations`; `web/src/translations.ts` fetches them once at startup and looks strings up through its `t(key, ...args)` helper. When adding or changing user-facing frontend text:

- Add/change the `DebugAssistance.FrontEnd.<Name>` key in `Common/Languages/English/Keyed/FrontEnd.xml` (and in every other language's `Keyed/FrontEnd.xml` present in this mod — see the shared translations rule below).
- Use `t('DebugAssistance.FrontEnd.<Name>', ...)` in the Vue component rather than a literal string; `{0}`, `{1}`, ... placeholders are filled in by the `args` passed to `t()`, the same way RimWorld's own `.Translate(args)` fills them in server-side.
- Never add a key to `web/src/translations.ts` directly — it only ever holds what `/api/translations` returns.

## Building and testing the frontend

- Install/dev/build/test from within `web/`: `npm ci`, `npm run dev`, `npm run build`, `npm test`, `npm run typecheck`, `npm run lint`.
- `npm run build` writes straight into `../Site`, overwriting it (`emptyOutDir: true`). After any change under `web/`, rebuild and commit the resulting `Site/` changes alongside the source change — `Site/` is checked-in build output, not generated during CI or at mod load, so a stale `Site/` means the shipped mod doesn't reflect the frontend source.
- CI (`.github/build_hooks/pre_build_mod_hook/action.yml`) runs `npm run lint`, `npm run typecheck`, and `npm test` against `web/`, but does **not** run `npm run build` or verify `Site/` is up to date — that's on you before committing.

@CLAUDE.SHARED.md
