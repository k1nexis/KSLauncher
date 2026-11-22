# Project Memory: KS Launcher

## Architecture
- **KSLauncher.App** (C# 12 / WPF / .NET 8): Native Windows launcher.
  - `MainWindow.xaml/.cs`: Frameless shell, multi-tab UI (Home, Library, Mods, Accounts, Settings), dynamic theme (color wheel, HSV tints, custom background image/video).
  - `MinecraftService`: Orchestrates CmlLib.Core, installs modloaders (Fabric, Quilt, Forge, NeoForge), builds JVM args, launches instances in `%AppData%\KSLauncher\instances\<ver>-<loader>`.
  - `MinecraftVersionService`: Loads official Mojang manifest, filters releases/snapshots.
  - `ModrinthService`: Search & download mods/modpacks/resourcepacks/shaders, recursive dependency resolution, SHA-512 verify, path traversal checks.
  - `OfflineProfileStore`: Manages offline nicknames and UUIDs in `%AppData%\KSLauncher\offline-profile.json`.
  - `MicrosoftAccountService`: Interactive Microsoft -> Xbox -> Java auth via memory-backed account manager.
  - `ElyAccountService`: OAuth loopback handoff via Cloudflare Worker bridge; uses `AuthlibInjectorService`.
  - `AuthlibInjectorService`: Fetches authlib-injector from GitHub releases with SHA-256 verification.
  - `CloudAccountService`: HTTP client for KS System backend (register, login, logout, cosmetics upload).
- **cloudflare/** (TypeScript / Cloudflare Workers + D1 + Pages + R2):
  - `src/index.ts`: Worker API for authentication (PBKDF2 310k), JWT sessions, cosmetics metadata, Ely.by OAuth bridge (AES-GCM encrypted tickets), download streaming.
  - `migrations/`: D1 schema for `users`, `sessions`, `cosmetics`, `ely_oauth_states`, `ely_oauth_tickets`.
  - `site/`: Pages landing page with registration/login and portable `.exe` download.

## Project Rules & Constraints
- **Secrets**: Do not hardcode or commit API secrets or tokens. Use `wrangler secret put` for worker secrets (`TOKEN_SECRET`, `ELY_CLIENT_ID`, `ELY_CLIENT_SECRET`).
- **Compatibility**: Preserve CmlLib integration, isolated instance folder structure (`<version>-<loader>`), and settings schema in `launcher-settings.json`.
- **KS System vs Minecraft**: KS System is an account/cosmetics ecosystem; it does not distribute Minecraft licenses or authenticate online-mode servers.
- **Ely.by vs KS System**: Ely.by OAuth bridge is isolated; client secrets never enter the desktop app.
- **Git**: Git is initialized, initial commit pending. Keep work verifiable with `dotnet build` and `npx tsc --noEmit`.
