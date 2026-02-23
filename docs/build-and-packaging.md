# Keystone Desktop Build & Packaging System

Keystone Desktop is a native macOS application framework combining three processes:
- **C# (.NET 10)** — native AppKit runtime with Metal GPU rendering via Skia and a plugin system
- **Bun (TypeScript)** — backend services, web component compilation, and asset bundling
- **WebKit (WKWebView)** — web frontend rendering and component display

The build system cleanly separates framework compilation from application packaging, enabling both fast iteration and reproducible distribution builds.

## Quick Start

### Web-Only App (No C# Required)

```bash
# Create a new web-only app
python3 tools/create-app.py my-app

# Build and run
cd my-app
python3 build.py --run

# Package for distribution
python3 build.py --package
```

### Full Native App (With C# Plugins)

```bash
# Create with native C# scaffolding
python3 tools/create-app.py my-app --native

# Build plugins + run
cd my-app
python3 build.py --run

# Package with bundled plugins
python3 build.py --package --mode bundled
```

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [keystone.config.json Reference](#keystoneconfigjson-reference)
3. [Framework Build](#framework-build)
4. [Application Packaging](#application-packaging)
5. [Plugin Modes](#plugin-modes)
6. [Entitlements](#entitlements)
7. [Runtime Config Transformation](#runtime-config-transformation)
8. [App Build Scripts](#app-build-scripts)
9. [Creating Applications](#creating-applications)
10. [Development Workflow](#development-workflow)
11. [Native Libraries](#native-libraries)
12. [Engine Discovery](#engine-discovery)

---

## Architecture Overview

### Build Pipeline

The build pipeline has two distinct phases:

1. **Framework Build** (`keystone/build.py`)
   - Compiles Rust native libraries (layout engine)
   - Builds C# core libraries
   - Publishes `Keystone.app` bundle to `keystone/Keystone.App/bin/Release/net10.0-macos/osx-arm64/`
   - Result: Reusable runtime binary + framework libraries

2. **Application Packaging** (`keystone/tools/package.py` or app-specific `build.py`)
   - Loads app manifest from `keystone.config.json`
   - Copies framework runtime to new bundle
   - Compiles Bun TypeScript to single-file executable
   - Assembles plugins (bundled or side-by-side)
   - Creates macOS code signature
   - Result: Distributable `MyApp.app` bundle

### Three Application Modes

**TypeScript-Only** — No C# assemblies, no plugins:
- Framework runtime handles WebKit display + Bun subprocess
- Use `bun/web/app.ts` for UI
- Fastest iteration, minimal footprint

**Native with Side-by-Side Plugins** — C# assemblies optional, plugins external:
- Plugins in `dylib/` folder outside bundle (same directory as `keystone.config.json`)
- Runtime loads from `../../../../dylib/` (relative path in bundle config)
- Hot-reload enabled by default
- Best for development iteration

**Native with Bundled Plugins** — C# assemblies + plugins in app bundle:
- All plugins compiled into `Resources/dylib/` within bundle
- Hot-reload disabled
- Proper for distribution

---

## keystone.config.json Reference

The config manifest controls app identity, windows, plugins, scripts, and build settings.

**Config Search Order:** `keystone.config.json` → `keystone.json` (backwards compatibility)

**Format:** JSON with JSONC support (comments allowed, trailing commas permitted)

### Top-Level Fields

#### Identity Section

```json
{
  "name": "My App",
  "id": "com.example.myapp",
  "version": "1.0.0"
}
```

| Field | Type | Default | Purpose |
|-------|------|---------|---------|
| `name` | string | "Keystone App" | Display name in macOS menu bar, Dock, and App Store |
| `id` | string | "com.keystone.app" | Bundle identifier (reverse DNS format) |
| `version` | string | "1.0.0" | Semantic version (shown in About dialog) |

#### App Assembly (C#)

```json
{
  "appAssembly": "dylib/AppCore.dll"
}
```

| Field | Type | Default | Purpose |
|-------|------|---------|---------|
| `appAssembly` | string | null | Path to primary C# assembly implementing ICorePlugin interface |

When set, packager copies the DLL into the bundle under `Resources/`. At runtime, plugin loader treats it as the main app assembly, giving it special privileges (like direct access to `ApplicationRuntime`).

#### Plugins Section

```json
{
  "plugins": {
    "enabled": true,
    "dir": "dylib",
    "hotReload": true,
    "debounceMs": 200,
    "watchPatterns": ["*.dll"],
    "userDir": null,
    "extensionDir": null,
    "nativeDir": "dylib",
    "allowExternalSignatures": false
  }
}
```

| Field | Type | Default | Purpose |
|-------|------|---------|---------|
| `enabled` | bool | true | Enable/disable plugin system |
| `dir` | string | "dylib" | Primary plugin directory (relative to app root) |
| `hotReload` | bool | true | Watch `dir` for DLL changes and reload at runtime |
| `debounceMs` | int | 200 | Milliseconds to wait before reloading after file change |
| `watchPatterns` | array | ["*.dll"] | Glob patterns to watch (e.g., `["*.dll", "*.so"]`) |
| `userDir` | string | null | Publisher-managed plugin directory. Loaded in addition to `dir`. For signed plugin updates pushed outside the app bundle (same signing team). Supports `~`, `$APP_SUPPORT`, absolute, or relative paths. |
| `extensionDir` | string | null | Community/third-party extension directory. Loaded after `dir` and `userDir`. Requires `allowExternalSignatures = true` for plugins signed by other teams. Supports `~`, `$APP_SUPPORT`, absolute, or relative paths. |
| `nativeDir` | string | null | App-specific native library directory (Go/Rust/C dylibs); registered as search path at startup |
| `allowExternalSignatures` | bool | false | Allow plugins signed by other Developer IDs (adds `com.apple.security.cs.disable-library-validation` entitlement; **incompatible with Mac App Store**) |

#### Scripts Section

```json
{
  "scripts": {
    "enabled": true,
    "dir": "scripts",
    "hotReload": true,
    "webDir": null,
    "autoCreateDir": true
  }
}
```

| Field | Type | Default | Purpose |
|-------|------|---------|---------|
| `enabled` | bool | true | Enable/disable C# CSX scripts |
| `dir` | string | "scripts" | Directory for CSX script files |
| `hotReload` | bool | true | Watch `dir` for changes and recompile |
| `webDir` | string | null | Optional web scripts directory (relative to app root, not script dir) |
| `autoCreateDir` | bool | true | Auto-create directory if missing |

#### Bun Runtime Section

```json
{
  "bun": {
    "enabled": true,
    "root": "bun",
    "compiledExe": null
  }
}
```

| Field | Type | Default | Purpose |
|-------|------|---------|---------|
| `enabled` | bool | true | Start Bun subprocess at app launch |
| `root` | string | "bun" | Directory containing `package.json` and TypeScript source |
| `compiledExe` | string | null | **Set by packager only.** Name of compiled Bun executable (relative to MacOS/ directory) |

The `compiledExe` field is computed during packaging. Set `keystone.config.ts` in your Bun root to configure Bun's internal behavior (ports, routes, etc.).

#### Windows Section

```json
{
  "windows": [
    {
      "component": "app",
      "title": "Main Window",
      "width": 1024,
      "height": 700,
      "spawn": true,
      "toolbar": {
        "items": [
          { "label": "Refresh", "action": "refresh_data", "icon": "refresh" }
        ]
      }
    }
  ]
}
```

| Field | Type | Default | Purpose |
|-------|------|---------|---------|
| `component` | string | "" | Component name from Bun or plugin system to display |
| `title` | string | null | Window title bar text |
| `width` | float | 800 | Initial width in points |
| `height` | float | 600 | Initial height in points |
| `spawn` | bool | true | Auto-create at startup |
| `toolbar` | object | null | Toolbar item declarations |

#### Menus Section

```json
{
  "menus": {
    "File": [
      { "title": "New", "action": "file:new", "shortcut": "Cmd+N" },
      { "title": "Open", "action": "file:open", "shortcut": "Cmd+O" }
    ]
  }
}
```

Define per-menu item lists. Each item has `title`, `action`, and optional `shortcut`.

#### Process Recovery Section

```json
{
  "processRecovery": {
    "bunAutoRestart": true,
    "bunMaxRestarts": 5,
    "bunRestartBaseDelayMs": 500,
    "bunRestartMaxDelayMs": 30000,
    "webViewAutoReload": true,
    "webViewReloadDelayMs": 200
  }
}
```

| Field | Type | Default | Purpose |
|-------|------|---------|---------|
| `bunAutoRestart` | bool | true | Automatically restart Bun if it crashes |
| `bunMaxRestarts` | int | 5 | Max restart attempts before giving up |
| `bunRestartBaseDelayMs` | int | 500 | Initial restart delay (doubles each attempt) |
| `bunRestartMaxDelayMs` | int | 30000 | Max delay cap per restart |
| `webViewAutoReload` | bool | true | Auto-reload WKWebView on content process crash |
| `webViewReloadDelayMs` | int | 200 | Delay before reloading |

#### Other Fields

```json
{
  "iconDir": "icons",
  "pluginDir": "dylib",
  "scriptDir": "scripts"
}
```

| Field | Type | Default | Purpose |
|-------|------|---------|---------|
| `iconDir` | string | "icons" | Directory containing `AppIcon.icns` and icon assets |
| `pluginDir` | string | null | **Deprecated.** Use `plugins.dir` instead |
| `scriptDir` | string | null | **Deprecated.** Use `scripts.dir` instead |

#### Build Section (Packaging Configuration)

The `build` section is **write-only** (by packager) and **never read by runtime**. It contains packaging metadata that is stored in the bundled config for reference only.

```json
{
  "build": {
    "pluginMode": "side-by-side",
    "category": "public.app-category.finance",
    "outDir": "dist",
    "signingIdentity": null,
    "dmg": false,
    "minimumSystemVersion": "15.0",
    "extraResources": []
  }
}
```

| Field | Type | Default | Purpose |
|-------|------|---------|---------|
| `pluginMode` | string | "side-by-side" | Plugin layout: "side-by-side" or "bundled" |
| `category` | string | "public.app-category.utilities" | macOS app category (LSApplicationCategoryType) |
| `outDir` | string | "dist" | Output directory for packaged .app |
| `signingIdentity` | string | null | Developer ID cert name (null = ad-hoc signature) |
| `dmg` | bool | false | Create DMG after packaging |
| `minimumSystemVersion` | string | "15.0" | Minimum macOS version (LSMinimumSystemVersion) |
| `extraResources` | array | [] | Additional directories/files to copy into Resources/ |

### Example: Full Configuration

```json
{
  // App identity
  "name": "My App",
  "id": "com.example.myapp",
  "version": "1.0.0",

  // C# assembly
  "appAssembly": "dylib/AppCore.dll",

  // Plugins
  "plugins": {
    "dir": "dylib",
    "hotReload": true,
    "nativeDir": "dylib",
    "userDir": "$APP_SUPPORT/plugins",
    "extensionDir": "$APP_SUPPORT/extensions",
    "allowExternalSignatures": true
  },

  // Scripts
  "scripts": {
    "dir": "scripts"
  },

  // Web runtime
  "bun": {
    "root": "bun"
  },

  // Windows
  "windows": [
    {
      "component": "app",
      "title": "My App",
      "width": 1400,
      "height": 900,
      "spawn": true
    }
  ],

  // Icon assets
  "iconDir": "icons",

  // Build settings (stripped at runtime)
  "build": {
    "pluginMode": "side-by-side",
    "category": "public.app-category.finance",
    "outDir": "dist",
    "signingIdentity": null,
    "dmg": false
  }
}
```

---

## Framework Build

Build Keystone Desktop from source. This produces the reusable framework binary that all apps use.

### Requirements

- macOS 15+ (Sequoia)
- .NET 10 SDK
- Rust toolchain (for native libs)
- Xcode Command Line Tools

### Building

```bash
cd keystone/

# Full build (Rust + C# + publish)
python3 build.py

# Clean + build
python3 build.py --clean

# Incremental (skip Rust, rebuild only C# → publish)
python3 build.py --no-rust

# Build steps individually
python3 build.py --rust-only
python3 build.py --core-only
python3 build.py --app-only

# Debug configuration
python3 build.py --debug
```

### Build Phases

**Phase 1: Rust Native Libraries**
```bash
cd rust_ffi/
cargo build -p keystone-layout --release
# Output: libkeystone_layout.dylib → keystone/dylib/native/
```

**Phase 2: C# Core Libraries**
- `Keystone.Core` — Rendering, colors, colors palette, plugin interfaces
- `Keystone.Core.Platform` — Native AppKit bindings, layout engine FFI
- `Keystone.Core.Graphics.Skia` — Metal GPU contexts, Skia window management
- `Keystone.Core.Management` — Plugin/script hot-reload, Bun bridge
- `Keystone.Core.Runtime` — Application runtime, window manager
- `Keystone.Toolkit` — Utility types and helpers

**Phase 3: Publish App Bundle**
```
dotnet publish Keystone.App/Keystone.App.csproj \
  -c Release -r osx-arm64 --self-contained
```

Output structure:
```
keystone/Keystone.App/bin/Release/net10.0-macos/osx-arm64/
└── Keystone.app
    └── Contents
        ├── MacOS/
        │   ├── Keystone.App (main executable)
        │   └── libkeystone_layout.dylib
        └── Resources/
            ├── bun/ (engine runtime)
            ├── AppIcon.icns
            └── ...
```

### Output Files

After successful build:
- Framework binary: `Keystone.App/bin/Release/net10.0-macos/osx-arm64/Keystone.app`
- Native libs: `dylib/native/*.dylib`

The packager expects to find the framework binary in one of these locations (in order):
1. Local source checkout: `keystone/Keystone.App/bin/Release/...`
2. Vendored in app: `app-root/keystone-desktop/Keystone.App/bin/Release/...`
3. Global cache: `~/.keystone/engines/{version}/Keystone.App/bin/Release/...`

---

## Application Packaging

Package an app into a distributable `MyApp.app` bundle.

### Standalone Packager

The framework provides a standalone packager (`keystone/tools/package.py`) that apps invoke.

```bash
# Basic packaging
python3 tools/package.py /path/to/app

# Custom engine location
python3 tools/package.py /path/to/app --engine /path/to/keystone

# Override plugin mode
python3 tools/package.py /path/to/app --mode bundled

# Debug configuration
python3 tools/package.py /path/to/app --debug

# Create DMG
python3 tools/package.py /path/to/app --dmg

# Allow external plugin signatures
python3 tools/package.py /path/to/app --allow-external

# All together
python3 tools/package.py /path/to/app \
  --engine /path/to/keystone \
  --mode bundled \
  --dmg \
  --allow-external
```

### Packaging Steps

The packager executes these steps in order:

1. **Load Config** — Read `keystone.config.json` (or `keystone.json`)
2. **Find Engine** — Locate framework binary (local → vendored → cached → error)
3. **Create Bundle** — Prepare directory structure
4. **Info.plist** — Template substitution (`{{BUNDLE_NAME}}`, `{{BUNDLE_ID}}`, etc.)
5. **Framework Runtime** — Copy `Keystone.app/Contents/MacOS/` + `MonoBundle/`
6. **App Icon** — Copy `icons/AppIcon.icns` → `Resources/`
7. **Plugins** — Either bundle into `Resources/dylib/` or note external dir
8. **App Assembly** — Copy C# entry point (if `appAssembly` set)
9. **Compile Bun** — `bun build --compile host.ts → MacOS/{SafeName}`
10. **Scripts** — Copy `scripts/` directory
11. **Extra Resources** — Copy files listed in `build.extraResources`
12. **Icon Directory** — Copy full `icons/` tree
13. **Runtime Config** — Generate `Resources/keystone.config.json` (stripped + transformed)
14. **Entitlements** — Hardened runtime base, patch in external-signatures if needed
15. **Code Signing** — `codesign --deep --sign` with selected identity + entitlements
16. **Quarantine Flag** — Remove `com.apple.quarantine` attribute
17. **DMG** — Optionally create DMG volume

### CLI Flags

```bash
--engine PATH          Explicit path to Keystone Desktop root
--mode MODE            Override build.pluginMode: "bundled" or "side-by-side"
--allow-external       Allow plugins from other Developer IDs (sets allowExternalSignatures)
--dmg                  Create DMG after packaging
--debug                Use Debug configuration for framework binaries
```

### Output

Packaged bundle at path from config's `build.outDir` (default: `dist/`):
```
dist/
└── MyApp.app
    └── Contents/
        ├── MacOS/
        │   ├── MyApp (executable)
        │   ├── libkeystone_layout.dylib
        │   └── ...
        ├── Resources/
        │   ├── keystone.config.json (runtime config)
        │   ├── bun/ (compiled Bun runtime)
        │   ├── dylib/ (if bundled mode)
        │   ├── icons/
        │   └── ...
        └── Info.plist (generated from template)
```

---

## Plugin Modes

Choose between two plugin architectures based on distribution strategy.

### Side-by-Side Mode (Development Default)

Plugins remain external to the app bundle.

**File Layout:**
```
my-app/
├── keystone.config.json
├── dylib/               ← plugins stay here
│   ├── Plugin1.dll
│   └── Plugin2.dll
└── dist/
    └── MyApp.app        ← no dylib/ inside
        └── Contents/Resources/
            └── keystone.config.json
```

**Runtime Config Path:**
```json
{
  "plugins": {
    "dir": ".no-bundled-plugins",
    "userDir": "../../../../dylib",  // Relative to Resources/
    "hotReload": true,
    "allowExternalSignatures": false
  }
}
```

**Advantages:**
- Hot-reload enabled (edit DLL → reload instantly)
- Smaller bundle size
- Plugins shared across app instances
- Best for development

**Disadvantages:**
- Plugins must be shipped separately
- Distribution more complex (installer creates symlink or copies dylib/)

### Bundled Mode (Distribution Default)

Plugins compiled into the app bundle.

**File Layout:**
```
my-app/
├── keystone.config.json
└── dylib/               ← built once
    ├── Plugin1.dll
    └── Plugin2.dll

python3 build.py --package --mode bundled

dist/
└── MyApp.app
    └── Contents/Resources/
        ├── keystone.config.json
        └── dylib/        ← plugins copied here
            ├── Plugin1.dll
            └── Plugin2.dll
```

**Runtime Config Path:**
```json
{
  "plugins": {
    "dir": "dylib",       // Relative to Resources/
    "hotReload": false,
    "allowExternalSignatures": false
  }
}
```

**Advantages:**
- Single file distribution (MyApp.app is self-contained)
- No external dependencies
- Proper for App Store and DMG distribution

**Disadvantages:**
- No hot-reload
- Larger bundle size
- Rebuild required for any plugin change

### Hybrid: Bundled + External Directories

For distribution with publisher updates and/or community extensions:

```json
{
  "plugins": {
    "dir": "dylib",
    "userDir": "~/Library/Application Support/MyApp/plugins",
    "extensionDir": "~/Library/Application Support/MyApp/extensions",
    "hotReload": false,
    "allowExternalSignatures": true
  }
}
```

| Directory | Trust Level | Signing | Use Case |
|-----------|-------------|---------|----------|
| `dir` | App-bundled | Same team | Core app plugins |
| `userDir` | Publisher-managed | Same team | Signed plugin updates pushed outside the bundle |
| `extensionDir` | Community | Any team | Third-party extensions (requires `allowExternalSignatures`) |

The runtime loads all three directories in order: `dir` -> `userDir` -> `extensionDir`. All directories support hot-reload if enabled.

---

## Entitlements

All Keystone apps use **hardened runtime** entitlements (Developer ID distribution). Like Electron, there is no App Store target.

### Base Entitlements

**File:** `Keystone.App/entitlements.base.plist`

| Entitlement | Reason |
|-------------|--------|
| `network.client` + `network.server` | Bun ↔ WebKit localhost IPC, outgoing requests |
| `files.user-selected.read-write` | Open/save dialogs |
| `cs.allow-jit` | WebKit/JavaScriptCore JIT |
| `cs.allow-unsigned-executable-memory` | .NET CLR JIT (runtime is always JIT-based, not AOT) |

### External Signatures

When `allowExternalSignatures` is enabled, the packager patches in:

```xml
<key>com.apple.security.cs.disable-library-validation</key>
<true/>
```

This allows loading plugin DLLs signed by other Developer IDs — required for community/third-party extensions via `extensionDir`.

```bash
python3 package.py /path/to/app --allow-external
```

---

## Runtime Config Transformation

The packager transforms the source config before bundling.

### Transformation Steps

**1. Load source config** from `keystone.config.json`

```json
{
  "name": "My App",
  "plugins": {
    "dir": "dylib",
    "nativeDir": "dylib"
  },
  "build": {
    "pluginMode": "side-by-side"
  }
}
```

**2. Strip build section** — never shipped to runtime

```json
// "build" removed entirely
```

**3. Adjust plugin paths for bundle layout**

For **side-by-side mode**:
```json
{
  "plugins": {
    "dir": ".no-bundled-plugins",
    "userDir": "../../../../dylib",    // Relative from Resources/ to app-root/dylib/
    "nativeDir": "../../../../dylib"   // Same for native libs
  }
}
```

For **bundled mode**:
```json
{
  "plugins": {
    "dir": "dylib",                    // Relative from Resources/
    "hotReload": false,
    "userDir": null                    // No user dir
  }
}
```

**4. Set compiled Bun executable**

If Bun was compiled:
```json
{
  "bun": {
    "compiledExe": "MyApp"    // Relative to MacOS/
  }
}
```

**5. Write to bundle**

```
dist/MyApp.app/Contents/Resources/keystone.config.json
```

Runtime loads this transformed config, never the original.

---

## App Build Scripts

Applications provide a `build.py` script that orchestrates plugin builds and calls the packager.

### Template App Build Script

```python
#!/usr/bin/env python3
python3 build.py              # Build plugins + setup Bun
python3 build.py --run        # Build + run directly
python3 build.py --package    # Build + package to MyApp.app
python3 build.py --plugins    # Rebuild plugins only
python3 build.py --clean      # Clean build artifacts
python3 build.py --debug      # Debug mode
```

### Example App Build

**File:** `/path/to/app/build.py`

```bash
# Build all plugins (AppCore + libraries + services + windows)
python3 build.py

# Build and run immediately
python3 build.py --run

# Package for distribution (bundled mode)
python3 build.py --package --mode bundled

# With code signing
python3 build.py --package --mode bundled --dmg

# Rebuild only plugin DLLs
python3 build.py --plugins-only

# Skip everything but packaging (use existing dylib/)
python3 build.py --no-plugins --package
```

### Responsibility Chain

1. **App build.py:**
   - Builds app assemblies (if `app/` exists)
   - Builds plugin DLLs (if `plugins/` exists)
   - Calls framework packager

2. **Framework packager (package.py):**
   - Locates engine binary
   - Assembles bundle
   - Handles entitlements, signing, DMG

3. **App-specific logic:**
   - Custom build steps (Go binaries, Python scripts, etc.)
   - Plugin post-processing
   - Artifact organization

---

## Creating Applications

Use the scaffolding tool to bootstrap new apps.

### Web-Only Application (TypeScript + Bun)

```bash
python3 tools/create-app.py my-app
cd my-app
python3 build.py --run
```

**Generated Structure:**
```
my-app/
├── keystone.config.json         ← app manifest
├── build.py                      ← build orchestrator
├── bun/
│   ├── package.json
│   ├── tsconfig.json
│   ├── web/
│   │   └── app.ts              ← main component (edit this!)
│   └── services/               ← background services
├── icons/                       ← app icon (SVG/ICNS)
└── dist/                        ← output (created by packaging)
```

**First Steps:**
1. Edit `bun/web/app.ts` to design your UI
2. Run `python3 build.py --run` to see live changes
3. Edit `keystone.config.json` for app name/icon
4. Package with `python3 build.py --package`

### Native Application (With C# Plugins)

```bash
python3 tools/create-app.py my-app --native
cd my-app
python3 build.py --run
```

**Generated Structure:**
```
my-app/
├── keystone.config.json
├── build.py
├── app/                         ← C# entry point
│   ├── MyApp.Core.csproj
│   └── Program.cs
├── plugins/                     ← plugin projects
│   ├── MyPluginA/
│   │   └── MyPluginA.csproj
│   └── MyPluginB/
│       └── MyPluginB.csproj
├── bun/
│   ├── web/app.ts
│   └── services/
├── dylib/                       ← compiled DLLs (generated)
└── icons/
```

**First Steps:**
1. Implement `ICorePlugin` in `app/Program.cs`
2. Create window plugins in `plugins/`
3. Edit `bun/web/app.ts` for UI
4. Run `python3 build.py --run`
5. Package with `python3 build.py --package --mode bundled`

### Custom App Identifiers

```bash
python3 tools/create-app.py my-app \
  --name "My App" \
  --id "com.mycompany.myapp"
```

**Placeholder Substitutions:**
| Placeholder | Computed From |
|-------------|---------------|
| `{{APP_NAME}}` | `--name` flag or slug |
| `{{APP_ID}}` | `--id` flag or `com.keystone.{slug}` |
| `{{APP_NAMESPACE}}` | PascalCase slug (e.g., `MyApp`) |
| `{{APP_SLUG}}` | Kebab-case slug (e.g., `my-app`) |
| `{{KEYSTONE_VERSION}}` | From engine's `version.txt` |

---

## Development Workflow

### Running in Development

Set the `KEYSTONE_ROOT` environment variable to tell the engine where to find config and plugins:

```bash
export KEYSTONE_ROOT=/path/to/app
open path/to/keystone/Keystone.app
```

Or via app build script:
```bash
python3 build.py --run
```

**Discovery:**
1. Check `KEYSTONE_ROOT` env var
2. Check first CLI argument (if directory exists)
3. Scan upward from exe dir for `keystone.config.json` or `dylib/`
4. Fall back to exe dir

### Hot-Reload Workflow

When `plugins.hotReload == true`:

1. Edit plugin source code
2. Build plugin DLL to `dylib/`
3. FileSystemWatcher detects change
4. App unloads old AssemblyLoadContext
5. Loads new DLL (usually instant)
6. Plugin state is **not preserved** (fresh instance)

Watch patterns default to `*.dll` but can be customized:
```json
{
  "plugins": {
    "watchPatterns": ["*.dll", "*.so"]
  }
}
```

### Debugging

Logs go to:
```bash
cat /tmp/keystone.log
```

Or set custom log path:
```bash
export KEYSTONE_LOG=/path/to/my.log
python3 build.py --run
```

View in real-time:
```bash
tail -f /tmp/keystone.log
```

### Iterative Development Cycle

1. **Start app:** `python3 build.py --run`
2. **Edit code** (UI in `bun/web/app.ts`, logic in C# plugins)
3. **For Bun changes:** Kill and restart (auto-compile on next run)
4. **For plugin DLL:** Rebuild with `python3 build.py --plugins` (hot-reload)
5. **For config:** Edit `keystone.config.json`, restart app

---

## Native Libraries

Apps can use native libraries (Rust, Go, C dylibs) alongside plugins.

### App-Specific Native Directory

Register a native library search path:

```json
{
  "plugins": {
    "nativeDir": "dylib"
  }
}
```

At startup, `Program.cs` registers this directory:
```csharp
if (config.Plugins.NativeDir is { } nativeDir)
{
    var fullPath = Path.IsPathRooted(nativeDir)
        ? nativeDir
        : Path.Combine(rootDir, nativeDir);
    if (Directory.Exists(fullPath))
        NativeLibraryLoader.AddSearchPath(fullPath);
}
```

In C# code, load dylibs normally:
```csharp
[DllImport("my_rust_lib")]
private static extern int some_function();
```

The runtime searches:
1. App-specific `nativeDir` (from config)
2. Framework `MacOS/` directory (libkeystone_layout.dylib, etc.)
3. Standard system paths

### Packaging Native Libraries

**Development (side-by-side):**
```
dylib/
├── libmy_rust.dylib           ← dev builds
└── libstrategy_engine.dylib
```

In config:
```json
{
  "plugins": {
    "nativeDir": "dylib"
  }
}
```

**Distribution (bundled):**
The packager copies `plugins.nativeDir` into the bundle:

```json
{
  "plugins": {
    "nativeDir": "dylib"
  },
  "build": {
    "pluginMode": "bundled"
  }
}
```

Result:
```
MyApp.app/Contents/Resources/
└── dylib/
    ├── libmy_rust.dylib
    └── libstrategy_engine.dylib
```

At runtime, the config is rewritten:
```json
{
  "plugins": {
    "nativeDir": "dylib"    ← Relative to Resources/
  }
}
```

### Cross-Compilation for Different Architectures

Current framework builds for `osx-arm64` (Apple Silicon). For Intel Macs:

```bash
# Requires macOS 13+ with Xcode supporting x86_64
dotnet publish Keystone.App.csproj \
  -c Release -r osx-x64 --self-contained
```

To build multi-architecture:
```bash
# Build both
python3 build.py --app-only
python3 build.py --app-only -r osx-x64

# Package with universal flag in plist
```

Native libraries (Rust/Go) must also be cross-compiled:
```bash
cargo build --release --target x86_64-apple-darwin
```

---

## Engine Discovery

The packager locates the Keystone framework using a flexible search strategy.

### Discovery Order

1. **Explicit `--engine` flag** (highest priority)
   ```bash
   python3 package.py /path/to/app --engine /path/to/keystone
   ```

2. **Source checkout** (if `package.py` lives in `keystone/tools/`)
   ```
   keystone/Keystone.App/bin/Release/net10.0-macos/osx-arm64/
   ```

3. **Vendored in app**
   ```
   app-root/keystone-desktop/Keystone.App/bin/Release/net10.0-macos/osx-arm64/
   ```

4. **Global cache** (read from `keystone/version.txt`)
   ```
   ~/.keystone/engines/{version}/Keystone.App/bin/Release/net10.0-macos/osx-arm64/
   ```

5. **Auto-download** (if cached version is missing)
   ```
   https://github.com/khayzz13/keystone-desktop/releases/download/{version}/{tarball}
   ```

### Vendoring the Engine

For reproducible builds or offline distribution, vendor the engine:

```bash
# Copy framework into app repo
mkdir -p my-app/keystone-desktop
cp -r /path/to/keystone/* my-app/keystone-desktop/

# Now package.py will find it automatically
python3 tools/package.py my-app
```

### Global Cache

Downloaded engines are cached per version:

```
~/.keystone/engines/
├── 0.1.0/
│   └── Keystone.App/bin/Release/...
├── 0.2.0/
│   └── Keystone.App/bin/Release/...
└── ...
```

Cached engines are reused across all apps. To clear:

```bash
rm -rf ~/.keystone/engines/0.1.0
```

### Specifying Framework Version

Apps pin a framework version via `version.txt` (populated during scaffolding):

```bash
cat my-app/keystone/version.txt
# Output: 0.1.0
```

The template build script uses this to download the right engine:

```python
KEYSTONE_VERSION = "{{KEYSTONE_VERSION}}"
ENGINE_CACHE = Path.home() / ".keystone" / "engines" / KEYSTONE_VERSION
```

To update framework version:
1. Edit `keystone/version.txt` in the framework repo
2. Re-scaffold apps, or manually update `build.py` placeholder

---

## Troubleshooting

### Engine Binary Not Found

```
ERROR: Keystone Desktop not found.
Build the framework first: cd keystone && python3 build.py
Or specify: --engine /path/to/engine
```

**Solution:**
- Build framework: `cd keystone && python3 build.py`
- Or explicitly point: `python3 package.py /path/to/app --engine /path/to/keystone`

### Config File Not Found

```
ERROR: No keystone.config.json or keystone.json found in /path/to/app
```

**Solution:**
- Ensure `keystone.config.json` exists in app root
- Check spelling (case-sensitive)
- Validate JSON syntax: `python3 -m json.tool keystone.config.json`

### Plugin Hot-Reload Not Working

Check:
1. `plugins.enabled == true` in config
2. `plugins.hotReload == true` in config
3. Plugin DLLs exist in `plugins.dir`
4. Running with `KEYSTONE_ROOT` env var set (for dev)

To debug:
```bash
tail -f /tmp/keystone.log | grep -i "plugin\|reload"
```

### Bun Compilation Failure

```
WARNING: host.ts not found — Bun runtime not compiled
```

**Solution:**
- Ensure `bun/` directory exists
- Check for `package.json` in bun root
- Verify TypeScript source compiles: `cd bun && bun build`

### Code Signing Issues

```
Error: The identity used to sign the bundle is not valid.
```

**Solution:**
- For development: use ad-hoc `-` (default)
- For distribution: specify cert ID: `--engine ... --mode bundled` + valid `signingIdentity` in config
- List available certs: `security find-identity -v -p codesigning`

### Plugins Not Loading at Runtime

Check:
1. `plugins.enabled == true`
2. `plugins.dir` points to correct location
3. Plugin DLLs exist: `ls dylib/*.dll`
4. Logs: `tail -f /tmp/keystone.log | grep -i "plugin\|loader"`

---

## Advanced Topics

### Multi-Architecture Distribution

For universal binaries (Apple Silicon + Intel):

```bash
# Build for arm64
python3 build.py

# Build for x64
python3 build.py --app-only -r osx-x64

# Create universal app (requires lipo tool)
lipo -create \
  Keystone.App/bin/Release/net10.0-macos/osx-arm64/Keystone.app/Contents/MacOS/Keystone.App \
  Keystone.App/bin/Release/net10.0-macos/osx-x64/Keystone.App/Contents/MacOS/Keystone.App \
  -output Keystone.App/Contents/MacOS/Keystone.App-universal
```

### Custom Build Categories

App category shown in App Store and Spotlight:

```json
{
  "build": {
    "category": "public.app-category.finance"
  }
}
```

Common categories:
- `public.app-category.utilities`
- `public.app-category.finance`
- `public.app-category.productivity`
- `public.app-category.developer-tools`
- Full list: [Apple Documentation](https://developer.apple.com/documentation/bundleresources/information_property_list/lsapplicationcategorytype)

### Extra Resources

Copy additional files into the bundle:

```json
{
  "build": {
    "extraResources": [
      "data/db.sqlite",
      "docs/",
      "config/defaults.json"
    ]
  }
}
```

These are copied to `Resources/` at package time.

### Minimum System Version

Specify macOS requirement:

```json
{
  "build": {
    "minimumSystemVersion": "14.5"
  }
}
```

Updated in Info.plist: `LSMinimumSystemVersion`

---

## Summary

The Keystone Desktop build system provides:

- **Clear separation** — Framework build vs. app packaging
- **Flexible plugin modes** — Development hot-reload vs. distribution bundled
- **Config-driven** — Single manifest controls all packaging
- **Native integration** — C# + TypeScript + native dylibs seamlessly
- **Reproducible builds** — Pinned framework versions, vendored dependencies
- **Safe signing** — Hardened runtime entitlements, ad-hoc by default

For most apps: scaffold with `create-app.py`, edit `build.py` as needed, then package with `--mode bundled` for distribution.

