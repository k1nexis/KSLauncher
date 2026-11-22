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
│   ├── LogWindow.xaml / LogWindow.xaml.cs
│   ├── Assets/kslauncher.ico
│   └── Services/
│       ├── AccountManager.cs
│       ├── AuthlibInjectorService.cs
│       ├── CloudAccountService.cs
│       ├── CurseForgeService.cs
│       ├── ElyAccountService.cs
│       ├── GameLogService.cs
│       ├── InstanceManager.cs
│       ├── JavaService.cs
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

### KSLauncher.App/LogWindow.xaml.cs
- `LogWindow`: Real-time game log console window
- `CopyLogs_Click`: Copies output to clipboard
- `ClearLogs_Click`: Clears console text buffer

### KSLauncher.App/MainWindow.xaml.cs
- `UnifiedModItem`: Unified mod project record
- `MainWindow`: Primary WPF window controller
- `InitializeAsync`: Restores profile, settings, versions
- `LoadVersionsAsync`: Queries Mojang manifest versions
- `RestoreProfileAsync`: Reads stored offline profile
- `RestoreSettingsAsync`: Reads saved launcher configuration
- `SaveSettingsAsync`: Persists current launcher settings
- `Play_Click`: Launches selected game instance
- `ViewLogs_Click`: Opens live log console
- `GameLanguageBox_SelectionChanged`: Switches game download language
- `LoadLibraryInstances`: Refreshes library instance list
- `CreateInstance_Click`: Creates custom named instance with mods
- `LaunchLibraryInstance_Click`: Selects and plays instance
- `RepairInstance_Click`: Repairs broken instance files
- `QuickRepair_Click`: Home screen quick repair
- `RunRepairWorkflowAsync`: Executes assembly repair workflow
- `OpenInstanceFolder_Click`: Opens explorer in instance
- `DeleteInstance_Click`: Deletes selected instance folder
- `ModSearchBox_KeyDown`: Triggers search on Enter
- `ModSourceBox_SelectionChanged`: Switches mod platform provider
- `SearchMods_Click`: Queries Modrinth or CurseForge
- `InstallMod_Click`: Installs mod to instance
- `LoadJavaRuntimes`: Queries detected Java runtimes
- `BrowseJava_Click`: Opens file browser for javaw
- `JavaRuntimeBox_SelectionChanged`: Configures active Java runtime
- `LoadSavedAccounts`: Loads saved profile list
- `SelectSavedAccount_Click`: Switches active user profile
- `RemoveSavedAccount_Click`: Removes saved user profile
- `ChooseBackground_Click`: Selects background image/video
- `ApplyBackground`: Displays background and adapts
- `AdaptThemeColorsFromImage`: Extracts palette from background
- `ClearBackground_Click`: Resets launcher background image
- `ApplyTheme_Click`: Updates theme color configuration
- `CreateColorWheelBitmap`: Generates HSV color wheel

### KSLauncher.App/Services/AccountManager.cs
- `SavedAccount`: Persistent profile model record
- `AccountManager`: Manages saved accounts list
- `GetAccounts`: Reads saved account array
- `SaveAccount`: Persists or updates account
- `SetActiveAccount`: Switches active saved profile
- `RemoveAccount`: Deletes saved account profile

### KSLauncher.App/Services/CurseForgeService.cs
- `CurseForgeProject`: CurseForge mod project representation
- `CurseForgeInstallResult`: Result with dependencies list
- `CurseForgeService`: CurseForge Core API client
- `SearchAsync`: Queries CurseForge mod search
- `InstallAsync`: Downloads file with dependencies
- `DownloadModWithDependenciesAsync`: Recursive dependencies downloader

### KSLauncher.App/Services/GameLogService.cs
- `GameLogService`: Live process stream logger
- `AttachToProcess`: Hooks stdout and stderr streams
- `Append`: Formats and appends message
- `GetFullLogText`: Returns full concatenated log

### KSLauncher.App/Services/InstanceManager.cs
- `GameInstanceInfo`: Detailed instance representation record
- `InstanceMetadata`: Persistent JSON instance data
- `InstanceManager`: Manages filesystem game instances
- `GetInstances`: Reads available instance profiles
- `CreateInstance`: Creates new custom instance
- `TouchInstance`: Updates last played timestamp
- `DeleteInstance`: Removes instance directory safely

### KSLauncher.App/Services/JavaService.cs
- `JavaRuntimeInfo`: Java runtime version descriptor
- `JavaService`: System Java environment detector
- `DetectInstalledJavas`: Scans registry and folders
- `ProbeJavaVersion`: Detects version via CLI

### KSLauncher.App/Services/MinecraftService.cs
- `MinecraftService`: Installs and launches instances
- `GetInstanceRoot`: Resolves instance directory path
- `LaunchAsync`: Prepares and executes Minecraft
- `RepairInstanceAsync`: Repairs broken instance files

### KSLauncher.App/Services/ModrinthService.cs
- `ModrinthProject`: Modrinth project descriptor
- `ModrinthInstallResult`: Result with dependencies list
- `ModrinthService`: Modrinth API search client
- `SearchAsync`: Queries Modrinth project catalog
- `InstallAsync`: Downloads project into instance
- `ExtractModpackAsync`: Unpacks mrpack index and overrides

### cloudflare/src/index.ts
- `fetch`: Cloudflare Worker request dispatcher
- `validatePngGeometry`: Checks PNG dimensions for cosmetics
- `cleanupExpired`: Purges expired tokens and states
- `scheduled`: Cron event handler for cleanup
- `encryptTicket`: Encrypts Ely.by payload (AES-GCM)
- `decryptTicket`: Decrypts Ely.by payload
- `signToken`: Creates signed JWT token
- `verifyToken`: Validates signed JWT token
- `hashPassword`: Hashes user password (PBKDF2)
- `authenticate`: Validates request bearer session
- `issueSession`: Issues session and token

### cloudflare/site/index.html
- `setDownloadLang`: Sets direct download language choice
- `applyLang`: Switches website language dynamically
- `loadCabinet`: Renders personal dashboard with cosmetics
- `uploadCosmetic`: Uploads skin or cape directly
