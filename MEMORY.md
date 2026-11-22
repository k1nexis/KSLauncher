# Project Memory: KS Launcher

## Architecture
- **KSLauncher.App** (C# 12 / WPF / .NET 8): Native Windows launcher.
  - `MainWindow.xaml/.cs`: Frameless shell, multi-tab UI (Home with Changelog block & live log button, Library with custom instances, mod selection with auto-dependencies & repair, Mods with dual platform, Accounts with Saved Accounts manager, Settings with Java selector), dynamic theme (adaptive wallpaper colors, color wheel, HSV tints, parallel image/video background), language switcher (ru/en).
  - `MinecraftService`: Orchestrates CmlLib.Core, installs modloaders (Fabric, Quilt, Forge, NeoForge), builds JVM args, repairs broken instances (`RepairInstanceAsync`), configures game language, integrates `JavaService` and `GameLogService`, launches instances in `%AppData%\KSLauncher\instances\<ver>-<loader>`.
  - `InstanceManager`: Manages custom named instances in `%AppData%\KSLauncher\instances` with metadata in `instance.json`.
  - `JavaService`: Detects installed JRE/JDKs (Java 8, 17, 21), handles custom `javaw.exe` selection.
  - `GameLogService`: Captures live game stdout/stderr, notifies on crash exit.
  - `LogWindow.xaml/.cs`: Dedicated live log console window with copy/scroll features.
  - `AccountManager`: Manages persistent saved accounts (Offline, Microsoft, Ely.by) in `saved-accounts.json` with 1-click active switching.
  - `ModrinthService`: Search & download from Modrinth API v2, recursive dependency resolution, `.mrpack` unpacking.
  - `CurseForgeService`: Search & download from CurseForge Core API v1 with recursive dependency downloading.
  - `OfflineProfileStore`: Manages offline nicknames and UUIDs in `%AppData%\KSLauncher\offline-profile.json`.
  - `MicrosoftAccountService`: Interactive Microsoft -> Xbox -> Java auth via memory-backed account manager.
  - `ElyAccountService`: OAuth loopback handoff via Cloudflare Worker bridge; uses `AuthlibInjectorService`.
  - `AuthlibInjectorService`: Fetches authlib-injector from GitHub releases with SHA-256 verification.
  - `CloudAccountService`: HTTP client for KS System backend (register, login, logout, cosmetics upload).
- **cloudflare/** (TypeScript / Cloudflare Workers + D1 + Pages + R2):
  - `src/index.ts`: Worker API for authentication (PBKDF2 310k), JWT sessions, cosmetics metadata with PNG geometry validation (64x64/64x32), automated D1 cleanup of expired tokens/states, Ely.by OAuth bridge (AES-GCM encrypted tickets), download streaming.
  - `migrations/`: D1 schema for `users`, `sessions`, `cosmetics`, `ely_oauth_states`, `ely_oauth_tickets`.
  - `site/`: Pages personal dashboard with bilingual RU/EN support, direct download language selection, web skin/cape upload and portable `.exe` download.

## Project Rules & Constraints
- **Secrets**: Do not hardcode or commit API secrets or tokens. Use `wrangler secret put` for worker secrets (`TOKEN_SECRET`, `ELY_CLIENT_ID`, `ELY_CLIENT_SECRET`).
- **Compatibility**: Preserve CmlLib integration, isolated instance folder structure (`<version>-<loader>`), and settings schema in `launcher-settings.json`.
- **KS System vs Minecraft**: KS System is an account/cosmetics ecosystem; it does not distribute Minecraft licenses or authenticate online-mode servers.
- **Ely.by vs KS System**: Ely.by OAuth bridge is isolated; client secrets never enter the desktop app.
