# Repository Map: KS Launcher

```
KSLauncher/
├── .gitignore
├── KSLauncher.sln
├── README.md
├── MEMORY.md
├── REPO_MAP.md
├── KSLauncher.App/
│   ├── App.xaml / App.xaml.cs
│   ├── AssemblyInfo.cs
│   ├── KSLauncher.App.csproj
│   ├── MainWindow.xaml / MainWindow.xaml.cs
│   ├── Assets/kslauncher.ico
│   └── Services/
│       ├── AuthlibInjectorService.cs
│       ├── CloudAccountService.cs
│       ├── ElyAccountService.cs
│       ├── JvmTuning.cs
│       ├── MicrosoftAccountService.cs
│       ├── MinecraftService.cs
│       ├── MinecraftVersionService.cs
│       ├── ModrinthService.cs
│       └── OfflineProfileStore.cs
└── cloudflare/
    ├── package.json / tsconfig.json / wrangler.toml
    ├── README.md
    ├── migrations/
    │   ├── 0001_init.sql
    │   └── 0002_ely_oauth.sql
    ├── site/
    │   ├── config.js
    │   ├── index.html
    │   └── KSLauncher.exe
    └── src/
        └── index.ts
```

### KSLauncher.App/App.xaml.cs
- `App`: Application entry point definition

### KSLauncher.App/MainWindow.xaml.cs
- `MainWindow`: Primary WPF window controller
- `InitializeAsync`: Restores profile, settings, versions
- `LoadVersionsAsync`: Queries Mojang manifest versions
- `RestoreProfileAsync`: Reads stored offline profile
- `RestoreSettingsAsync`: Reads saved launcher configuration
- `SaveSettingsAsync`: Persists current launcher settings
- `Play_Click`: Launches selected game instance
- `SearchMods_Click`: Queries Modrinth search catalog
- `OpenMod_Click`: Opens mod webpage externally
- `InstallMod_Click`: Downloads mod and dependencies
- `SaveOffline_Click`: Saves offline user nickname
- `MicrosoftLogin_Click`: Authenticates via Microsoft OAuth
- `ElyLogin_Click`: Authenticates via Ely.by loopback
- `CloudLogin_Click`: Logs in to KS
- `CloudRegister_Click`: Registers in KS System
- `CloudLogout_Click`: Logs out from KS
- `UploadCloudAssetAsync`: Uploads skin or cape
- `ChooseBackground_Click`: Selects custom image/video background
- `ApplyBackground`: Applies background visual media
- `ClearBackground_Click`: Resets launcher background image
- `ApplyTheme_Click`: Updates theme color configuration
- `CreateColorWheelBitmap`: Generates HSV color wheel

### KSLauncher.App/Services/AuthlibInjectorService.cs
- `AuthlibInjectorService`: Fetches authlib-injector library
- `EnsureInstalledAsync`: Downloads and verifies injector

### KSLauncher.App/Services/CloudAccountService.cs
- `CloudAccountService`: Client for KS System
- `LoginAsync`: Authenticates user with credentials
- `RegisterAsync`: Registers new user account
- `UploadPngAsync`: Uploads PNG skin or cape
- `LogoutAsync`: Revokes active session token

### KSLauncher.App/Services/ElyAccountService.cs
- `ElyAccountService`: Client for Ely.by OAuth
- `SignInAsync`: Executes loopback OAuth flow

### KSLauncher.App/Services/JvmTuning.cs
- `JvmTuning`: Memory and processor helpers

### KSLauncher.App/Services/MicrosoftAccountService.cs
- `MicrosoftAccountService`: Microsoft account authentication handler
- `SignInAsync`: Launches interactive Microsoft login

### KSLauncher.App/Services/MinecraftService.cs
- `MinecraftService`: Installs and launches instances
- `GetInstanceRoot`: Resolves instance directory path
- `LaunchAsync`: Prepares and executes Minecraft

### KSLauncher.App/Services/MinecraftVersionService.cs
- `MinecraftVersionService`: Queries Mojang version manifest
- `GetVersionsAsync`: Returns filtered Minecraft versions

### KSLauncher.App/Services/ModrinthService.cs
- `ModrinthService`: Modrinth API search client
- `SearchAsync`: Queries Modrinth project catalog
- `InstallAsync`: Downloads project into instance
- `InstallProjectAsync`: Recursively downloads project files

### KSLauncher.App/Services/OfflineProfileStore.cs
- `OfflineProfileStore`: Manages local offline profiles
- `SaveAsync`: Saves offline profile JSON
- `LoadAsync`: Reads stored offline profile

### cloudflare/src/index.ts
- `fetch`: Cloudflare Worker request dispatcher
- `encryptTicket`: Encrypts Ely.by payload (AES-GCM)
- `decryptTicket`: Decrypts Ely.by payload
- `signToken`: Creates signed JWT token
- `verifyToken`: Validates signed JWT token
- `hashPassword`: Hashes user password (PBKDF2)
- `authenticate`: Validates request bearer session
- `issueSession`: Issues session and token
