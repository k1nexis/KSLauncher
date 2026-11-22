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
│       ├── CurseForgeService.cs
│       ├── ElyAccountService.cs
│       ├── InstanceManager.cs
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
- `UnifiedModItem`: Unified mod project record
- `MainWindow`: Primary WPF window controller
- `InitializeAsync`: Restores profile, settings, versions
- `LoadVersionsAsync`: Queries Mojang manifest versions
- `RestoreProfileAsync`: Reads stored offline profile
- `RestoreSettingsAsync`: Reads saved launcher configuration
- `SaveSettingsAsync`: Persists current launcher settings
- `Play_Click`: Launches selected game instance
- `GameLanguageBox_SelectionChanged`: Switches game download language
- `LoadLibraryInstances`: Refreshes library instance list
- `CreateInstance_Click`: Creates custom named instance
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
- `ChooseBackground_Click`: Selects background image/video
- `ApplyBackground`: Displays background and adapts
- `AdaptThemeColorsFromImage`: Extracts palette from background
- `ClearBackground_Click`: Resets launcher background image
- `ApplyTheme_Click`: Updates theme color configuration
- `CreateColorWheelBitmap`: Generates HSV color wheel

### KSLauncher.App/Services/CurseForgeService.cs
- `CurseForgeProject`: CurseForge mod project representation
- `CurseForgeService`: CurseForge Core API client
- `SearchAsync`: Queries CurseForge mod search
- `InstallAsync`: Downloads file from CurseForge

### KSLauncher.App/Services/InstanceManager.cs
- `GameInstanceInfo`: Detailed instance representation record
- `InstanceMetadata`: Persistent JSON instance data
- `InstanceManager`: Manages filesystem game instances
- `GetInstances`: Reads available instance profiles
- `CreateInstance`: Creates new custom instance
- `TouchInstance`: Updates last played timestamp
- `DeleteInstance`: Removes instance directory safely

### KSLauncher.App/Services/MinecraftService.cs
- `MinecraftService`: Installs and launches instances
- `GetInstanceRoot`: Resolves instance directory path
- `LaunchAsync`: Prepares and executes Minecraft
- `RepairInstanceAsync`: Repairs broken instance files

### cloudflare/site/index.html
- `applyLang`: Switches website language dynamically
- `updateFormTexts`: Updates registration/login form labels
