#!/usr/bin/env python3
"""
Keystone Desktop — App Packager

Reads an app's keystone.config.json, assembles a distributable macOS .app bundle.
This is the keystone-desktop equivalent of electron-builder.

Usage:
  python3 package.py /path/to/app                    # Package with config defaults
  python3 package.py /path/to/app --mode bundled      # Self-contained bundle
  python3 package.py /path/to/app --dmg               # Also create DMG
  python3 package.py /path/to/app --engine /path      # Explicit engine location
"""

import subprocess
import os
import sys
import json
import shutil
import argparse
from pathlib import Path

SCRIPT_DIR = Path(__file__).parent
ENGINE_ROOT = SCRIPT_DIR.parent  # keystone/


def run(cmd, cwd=None, check=True):
    print(f"  $ {' '.join(str(c) for c in cmd) if isinstance(cmd, list) else cmd}")
    return subprocess.run(cmd, cwd=cwd, check=check, shell=isinstance(cmd, str))


def discover_services(svc_dir: Path) -> list:
    """Discover service modules in a directory. Returns [(name, abs_path), ...]."""
    result = []
    if not svc_dir.exists():
        return result
    for entry in sorted(svc_dir.iterdir()):
        if entry.is_file() and entry.suffix in ('.ts', '.tsx'):
            result.append((entry.stem, str(entry)))
        elif entry.is_dir():
            idx = entry / "index.ts"
            if idx.exists():
                result.append((entry.name, str(idx)))
    return result


# ─── Config loading ──────────────────────────────────────────────────────────

def load_config(app_root: Path) -> dict:
    """Load keystone.config.json (or keystone.json fallback) with JSONC support."""
    for name in ["keystone.config.json", "keystone.json"]:
        path = app_root / name
        if path.exists():
            text = path.read_text()
            # Strip single-line comments (// ...) for JSONC support
            import re
            text = re.sub(r'^\s*//.*$', '', text, flags=re.MULTILINE)
            config = json.loads(text)
            print(f"  Config: {path.name}")
            return config
    print(f"  ERROR: No keystone.config.json or keystone.json found in {app_root}")
    sys.exit(1)


# ─── Engine discovery ────────────────────────────────────────────────────────

def find_engine(app_root: Path, explicit: str = None) -> Path:
    """Locate Keystone Desktop (framework runtime). Returns path to engine root."""
    if explicit:
        p = Path(explicit)
        if p.exists():
            return p
        print(f"  ERROR: Explicit engine path not found: {explicit}")
        sys.exit(1)

    candidates = [
        # 1. Source checkout (building from source — this script lives in keystone/tools/)
        ENGINE_ROOT,
        # 2. Vendored in app
        app_root / "keystone-desktop",
        # 3. Global cache (version from engine's version.txt)
    ]

    for c in candidates:
        # Check for dotnet publish output
        for config_mode in ["Release", "Debug"]:
            src = c / "Keystone.App" / "bin" / config_mode / "net10.0-macos" / "osx-arm64" / "Keystone.app" / "Contents"
            if src.exists() and (src / "MacOS").exists():
                print(f"  Engine: {c} ({config_mode})")
                return c

    # 3. Global cache — check version.txt
    version_file = ENGINE_ROOT / "version.txt"
    if version_file.exists():
        version = version_file.read_text().strip()
        cache = Path.home() / ".keystone" / "engines" / version
        if cache.exists():
            print(f"  Engine: {cache} (cached)")
            return cache

    print("  ERROR: Keystone Desktop not found.")
    print("  Build the framework first: cd keystone && python3 build.py")
    print("  Or specify: --engine /path/to/engine")
    sys.exit(1)


def find_engine_contents(engine: Path, debug=False) -> Path:
    """Find the dotnet publish Contents/ directory within the engine."""
    config_mode = "Debug" if debug else "Release"
    # Try requested mode first, then fallback
    for mode in [config_mode, "Release", "Debug"]:
        src = engine / "Keystone.App" / "bin" / mode / "net10.0-macos" / "osx-arm64" / "Keystone.app" / "Contents"
        if src.exists():
            return src
    return None


# ─── Packager ────────────────────────────────────────────────────────────────

def package(app_root: Path, engine: Path, debug=False, mode_override=None,
            dmg_override=None, allow_external_override=None):
    """Package an app into a distributable .app bundle."""

    config = load_config(app_root)
    build_cfg = config.get("build", {})

    # Resolve build settings (CLI overrides > config > defaults)
    app_name = config.get("name", "Keystone App")
    app_id = config.get("id", "com.keystone.app")
    app_version = config.get("version", "1.0.0")
    safe_name = app_name.replace(" ", "")

    plugin_mode = mode_override or build_cfg.get("pluginMode", "side-by-side")
    category = build_cfg.get("category", "public.app-category.utilities")
    out_dir = app_root / build_cfg.get("outDir", "dist")
    signing_identity = build_cfg.get("signingIdentity") or os.environ.get("KEYSTONE_SIGNING_IDENTITY")
    require_signing_identity = bool(build_cfg.get("requireSigningIdentity", False))
    notarize = bool(build_cfg.get("notarize", False))
    notary_profile = build_cfg.get("notaryProfile") or os.environ.get("KEYSTONE_NOTARY_PROFILE")
    create_dmg = dmg_override if dmg_override is not None else build_cfg.get("dmg", False)
    min_version = build_cfg.get("minimumSystemVersion", "15.0")
    extra_resources = build_cfg.get("extraResources", [])

    plugins_cfg = config.get("plugins", {})
    plugins_enabled = plugins_cfg.get("enabled", True) if isinstance(plugins_cfg, dict) else False
    allow_external = allow_external_override if allow_external_override is not None else (
        plugins_cfg.get("allowExternalSignatures", False) if isinstance(plugins_cfg, dict) else False)
    plugins_dir_name = plugins_cfg.get("dir", "dylib") if isinstance(plugins_cfg, dict) else "dylib"
    user_dir_configured = bool(plugins_cfg.get("userDir", "")) if isinstance(plugins_cfg, dict) else False
    extension_dir_configured = bool(plugins_cfg.get("extensionDir", "")) if isinstance(plugins_cfg, dict) else False
    has_external_dirs = user_dir_configured or extension_dir_configured

    bundle_path = out_dir / f"{safe_name}.app"
    bundle_contents = bundle_path / "Contents"
    bundle_macos = bundle_contents / "MacOS"
    bundle_resources = bundle_contents / "Resources"

    print(f"\n{'='*60}")
    print(f"  Packaging {app_name} v{app_version}")
    print(f"  Mode: {plugin_mode}")
    print(f"  Bundle: {bundle_path}")
    print(f"{'='*60}")

    # Clean previous build
    if bundle_path.exists():
        shutil.rmtree(bundle_path)
    bundle_macos.mkdir(parents=True, exist_ok=True)
    bundle_resources.mkdir(parents=True, exist_ok=True)

    # ── 1. Info.plist ────────────────────────────────────────────────────────

    template_path = engine / "Keystone.App" / "Info.plist.template"
    if template_path.exists():
        plist = template_path.read_text()
        plist = plist.replace("{{BUNDLE_NAME}}", app_name)
        plist = plist.replace("{{BUNDLE_ID}}", app_id)
        plist = plist.replace("{{BUNDLE_VERSION}}", app_version)
        plist = plist.replace("{{BUNDLE_EXECUTABLE}}", "Keystone.App")
        plist = plist.replace("{{BUNDLE_CATEGORY}}", category)
        # Patch minimum system version if different from template default
        if min_version != "15.0":
            plist = plist.replace(
                "<string>15.0</string><!-- LSMinimumSystemVersion -->",
                f"<string>{min_version}</string>")
        (bundle_contents / "Info.plist").write_text(plist)
        print(f"  Info.plist: {app_name} ({app_id})")
    else:
        static = engine / "Keystone.App" / "Info.plist"
        if static.exists():
            shutil.copy2(static, bundle_contents / "Info.plist")

    # ── 2. Framework runtime (MacOS/ + MonoBundle/) ──────────────────────────

    src_contents = find_engine_contents(engine, debug)
    if src_contents:
        # Skip engine bun/ inside Resources — step 6 builds bun/ from scratch with
        # pre-bundled assets and compiled exes. The raw .ts engine files are dead weight.
        skip = {"_CodeSignature", "Info.plist", "PkgInfo"}
        for item in src_contents.iterdir():
            if item.name in skip:
                continue
            dst = bundle_contents / item.name
            if item.is_dir():
                if dst.exists():
                    shutil.rmtree(dst)
                if item.name == "Resources":
                    shutil.copytree(item, dst, ignore=shutil.ignore_patterns("bun"))
                else:
                    shutil.copytree(item, dst)
            else:
                shutil.copy2(item, dst)
        print(f"  Framework: copied")
    else:
        print(f"  WARNING: Framework not built — run 'python3 build.py' in the engine directory first")

    # ── 3. App icon ──────────────────────────────────────────────────────────

    icon_dir = app_root / config.get("iconDir", "icons")
    icon_file = icon_dir / "AppIcon.icns"
    if icon_file.exists():
        shutil.copy2(icon_file, bundle_resources / "AppIcon.icns")
        print(f"  Icon: AppIcon.icns")

    # ── 4. Plugins ───────────────────────────────────────────────────────────

    dylib_src = app_root / plugins_dir_name
    has_bundled_plugins = False
    has_plugins = False

    if plugins_enabled:
        if plugin_mode == "bundled" and dylib_src.exists():
            shutil.copytree(dylib_src, bundle_resources / "dylib")
            has_bundled_plugins = True
            has_plugins = True
            print(f"  Plugins: bundled ({plugins_dir_name}/)")
        elif plugin_mode == "side-by-side":
            has_plugins = dylib_src.exists() or has_external_dirs
            if dylib_src.exists():
                print(f"  Plugins: side-by-side ({plugins_dir_name}/ stays external)")
            if user_dir_configured:
                print(f"  Plugins: userDir configured")
            if extension_dir_configured:
                print(f"  Plugins: extensionDir configured")
        else:
            has_plugins = dylib_src.exists() or has_external_dirs

    # ── 5. App assembly ──────────────────────────────────────────────────────

    app_assembly = config.get("appAssembly")
    if app_assembly:
        bundle_dest = bundle_resources / app_assembly
        if not bundle_dest.exists():
            src = app_root / app_assembly
            if src.exists():
                bundle_dest.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(src, bundle_dest)
                print(f"  App assembly: {app_assembly}")
            else:
                print(f"  WARNING: appAssembly not found: {src}")

    # ── 6. Bun runtime ──────────────────────────────────────────────────────
    # Pre-bundle web components into JS/CSS, compile host.ts into single-file exe.
    # The bundle ships pre-built assets — no raw .ts source, no Bun.build() at runtime.

    bun_cfg = config.get("bun", {})
    compiled_exe_name = None
    compiled_worker_name = None
    pre_built_web = False
    if isinstance(bun_cfg, dict) and bun_cfg.get("enabled", True):
        bun_root = app_root / bun_cfg.get("root", "bun")
        bundle_bun = bundle_resources / bun_cfg.get("root", "bun")
        bundle_bun.mkdir(parents=True, exist_ok=True)

        # 6a. Pre-bundle web components (JS/CSS output only — no .ts source in bundle)
        bun_config_ts = bun_root / "keystone.config.ts"
        web_dir_name = "web"
        web_components = {}

        # Read bun-side config: extract web entries + resolve full config for distribution.
        # The resolved config is written as JSON so the compiled exe doesn't need .ts at runtime.
        resolved_bun_config = None
        if bun_config_ts.exists():
            try:
                extract_script = f"""
                const {{ resolveConfig }} = require("{bun_root}/node_modules/@keystone/sdk/config.ts");
                const mod = require("{bun_config_ts}");
                const cfg = mod.default ?? mod;
                const resolved = resolveConfig(cfg);
                console.log(JSON.stringify({{
                    webDir: cfg.web?.dir ?? "web",
                    components: cfg.web?.components ?? {{}},
                    resolved: resolved,
                }}));
                """
                result = subprocess.run(["bun", "-e", extract_script],
                    capture_output=True, text=True, cwd=bun_root)
                if result.returncode == 0 and result.stdout.strip():
                    parsed = json.loads(result.stdout.strip())
                    web_dir_name = parsed.get("webDir", "web")
                    web_components = parsed.get("components", {})
                    resolved_bun_config = parsed.get("resolved")
            except Exception as e:
                print(f"  WARNING: Could not parse bun config: {e}")

        # Auto-discover .ts/.tsx in web dir if no explicit entries
        web_src_dir = bun_root / web_dir_name
        if not web_components and web_src_dir.exists():
            for f in sorted(web_src_dir.iterdir()):
                if f.suffix in (".ts", ".tsx") and f.is_file():
                    name = f.stem
                    web_components[name] = f"./{web_dir_name}/{f.name}"

        bundle_web_dir = bundle_bun / web_dir_name
        bundle_web_dir.mkdir(parents=True, exist_ok=True)

        if web_components and bun_root.exists():
            print(f"  Pre-bundling {len(web_components)} web component(s)...")
            for name, entry in web_components.items():
                entry_abs = bun_root / entry.lstrip("./")
                if not entry_abs.exists():
                    print(f"    WARNING: {entry} not found, skipping {name}")
                    continue
                # Use Bun.build() JS API — supports naming with [ext] for JS+CSS output
                bundle_script = f"""
                const result = await Bun.build({{
                    entrypoints: ["{entry_abs}"],
                    outdir: "{bundle_web_dir}",
                    target: "browser",
                    format: "esm",
                    naming: "{name}.[ext]",
                }});
                if (!result.success) {{
                    for (const log of result.logs) console.error(log.message);
                    process.exit(1);
                }}
                console.log("{name}: " + result.outputs.length + " file(s)");
                """
                run(["bun", "-e", bundle_script], cwd=bun_root)
                pre_built_web = True

        # 6b. (Services are compiled directly into the exe — see steps 6e/6f)
        workers_cfg = config.get("workers", []) or []

        # 6c. Write pre-resolved bun config as JSON
        #     The compiled exe loads this directly — no .ts evaluation needed at runtime.
        if resolved_bun_config:
            # Inject preBuilt flag into the resolved config
            resolved_bun_config["web"]["preBuilt"] = True
            resolved_json = bundle_bun / "keystone.resolved.json"
            resolved_json.write_text(json.dumps(resolved_bun_config, indent=2))
            print(f"  Bun config: keystone.resolved.json (pre-resolved)")

        # 6e. Compile host.ts → single-file executable (services baked in)
        host_ts = bun_root / "node_modules" / "keystone-desktop" / "host.ts"
        if not host_ts.exists():
            host_ts = engine / "bun" / "host.ts"

        if host_ts.exists():
            compiled_exe_name = safe_name
            compiled_exe_path = bundle_macos / compiled_exe_name

            # Discover main host services — static imports get compiled into the exe
            services_dir_name = "services"
            if resolved_bun_config:
                services_dir_name = resolved_bun_config.get("services", {}).get("dir", "services")
            main_services = discover_services(bun_root / services_dir_name)

            if main_services:
                # Generate wrapper that statically imports services then boots host.ts
                wrapper = bun_root / "_compiled_host_entry.ts"
                lines = []
                for i, (name, path) in enumerate(main_services):
                    lines.append(f'import * as _svc{i} from "{path}";')
                lines.append('(globalThis as any).__KEYSTONE_COMPILED_SERVICES__ = {')
                for i, (name, path) in enumerate(main_services):
                    lines.append(f'  "{name}": _svc{i},')
                lines.append('};')
                lines.append(f'await import("{host_ts}");')
                wrapper.write_text('\n'.join(lines))

                svc_names = ", ".join(n for n, _ in main_services)
                print(f"  Compiling Bun -> {compiled_exe_name} (services: {svc_names})...")
                try:
                    run(["bun", "build", "--compile", str(wrapper),
                         "--outfile", str(compiled_exe_path)])
                finally:
                    wrapper.unlink(missing_ok=True)
            else:
                print(f"  Compiling Bun -> {compiled_exe_name}...")
                run(["bun", "build", "--compile", str(host_ts),
                     "--outfile", str(compiled_exe_path)])
        else:
            print(f"  WARNING: host.ts not found — Bun runtime not compiled")

        # 6f. Compile worker-host.ts → standalone worker executable (services baked in)
        compiled_worker_name = None
        if workers_cfg:
            worker_host_ts = bun_root / "node_modules" / "keystone-desktop" / "worker-host.ts"
            if not worker_host_ts.exists():
                worker_host_ts = engine / "bun" / "worker-host.ts"

            if worker_host_ts.exists():
                compiled_worker_name = safe_name + "-worker"
                compiled_worker_path = bundle_macos / compiled_worker_name

                # Discover services for all workers
                workers_services = {}
                for w in workers_cfg:
                    w_name = w.get("name", "")
                    svc_dir = w.get("servicesDir", "")
                    if w_name and svc_dir:
                        svcs = discover_services(bun_root / svc_dir)
                        if svcs:
                            workers_services[w_name] = svcs

                if workers_services:
                    # Generate wrapper that statically imports all worker services
                    wrapper = bun_root / "_compiled_worker_entry.ts"
                    lines = []
                    all_vars = []
                    for w_name, svcs in workers_services.items():
                        for i, (svc_name, path) in enumerate(svcs):
                            var = f"_w_{w_name}_{i}"
                            lines.append(f'import * as {var} from "{path}";')
                            all_vars.append((w_name, svc_name, var))

                    lines.append('(globalThis as any).__KEYSTONE_COMPILED_SERVICES__ = {')
                    by_worker = {}
                    for w_name, svc_name, var in all_vars:
                        by_worker.setdefault(w_name, []).append((svc_name, var))
                    for w_name, svcs in by_worker.items():
                        lines.append(f'  "{w_name}": {{')
                        for svc_name, var in svcs:
                            lines.append(f'    "{svc_name}": {var},')
                        lines.append('  },')
                    lines.append('};')
                    lines.append(f'await import("{worker_host_ts}");')
                    wrapper.write_text('\n'.join(lines))

                    total = sum(len(s) for s in workers_services.values())
                    names = ", ".join(f"{w}({len(s)})" for w, s in workers_services.items())
                    print(f"  Compiling Worker -> {compiled_worker_name} ({total} services: {names})...")
                    try:
                        run(["bun", "build", "--compile", str(wrapper),
                             "--outfile", str(compiled_worker_path)])
                    finally:
                        wrapper.unlink(missing_ok=True)
                else:
                    print(f"  Compiling Worker -> {compiled_worker_name}...")
                    run(["bun", "build", "--compile", str(worker_host_ts),
                         "--outfile", str(compiled_worker_path)])
            else:
                print(f"  WARNING: worker-host.ts not found — workers not compiled")

    # ── 7. Scripts ───────────────────────────────────────────────────────────

    scripts_cfg = config.get("scripts", {})
    scripts_dir_name = scripts_cfg.get("dir", "scripts") if isinstance(scripts_cfg, dict) else "scripts"
    scripts_dir = app_root / scripts_dir_name
    if scripts_dir.exists() and any(scripts_dir.iterdir()):
        shutil.copytree(scripts_dir, bundle_resources / "scripts")
        print(f"  Scripts: {scripts_dir_name}/")

    # ── 8. Extra resources ───────────────────────────────────────────────────

    for extra in extra_resources:
        src = app_root / extra
        if src.exists():
            dst = bundle_resources / Path(extra).name
            if src.is_dir():
                shutil.copytree(src, dst, dirs_exist_ok=True)
            else:
                dst.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(src, dst)
            print(f"  Extra: {extra}")

    # ── 9. Icons directory ───────────────────────────────────────────────────

    if icon_dir.exists():
        shutil.copytree(icon_dir, bundle_resources / "icons", dirs_exist_ok=True)

    # ── 10. Runtime config ───────────────────────────────────────────────────
    # Build the config that goes INTO the bundle. Strip build section,
    # adjust plugin paths for the bundle layout.

    runtime_config = dict(config)
    runtime_config.pop("build", None)

    if plugins_enabled and plugin_mode == "side-by-side":
        rt_plugins = dict(runtime_config.get("plugins", {}))
        rt_plugins["dir"] = ".no-bundled-plugins"
        # Compute relative path from Contents/Resources/ → app_root/dylib/
        # Bundle is at: app_root/dist/App.app/Contents/Resources/
        # dylib/ is at: app_root/dylib/
        # So: ../../../../dylib
        rt_plugins["userDir"] = "../../../../" + plugins_dir_name
        rt_plugins["hotReload"] = True
        rt_plugins["allowExternalSignatures"] = allow_external
        if rt_plugins.get("nativeDir"):
            rt_plugins["nativeDir"] = "../../../../" + rt_plugins["nativeDir"]
        # extensionDir: absolute/~ paths are preserved as-is (they point to
        # user-writable locations outside the bundle). Relative paths get
        # the same ../../../../ treatment as userDir.
        ext_dir = rt_plugins.get("extensionDir")
        if ext_dir and not ext_dir.startswith(("/", "~", "$")):
            rt_plugins["extensionDir"] = "../../../../" + ext_dir
        runtime_config["plugins"] = rt_plugins
    elif plugins_enabled and plugin_mode == "bundled":
        rt_plugins = dict(runtime_config.get("plugins", {}))
        rt_plugins["hotReload"] = False
        rt_plugins.pop("userDir", None)
        # extensionDir is preserved — community plugins are always external,
        # even in bundled mode. Only absolute/~/$ paths make sense here.
        rt_plugins["allowExternalSignatures"] = allow_external
        runtime_config["plugins"] = rt_plugins

    if compiled_exe_name or pre_built_web or compiled_worker_name:
        rt_bun = dict(runtime_config.get("bun", {})) if isinstance(runtime_config.get("bun"), dict) else {}
        if compiled_exe_name:
            rt_bun["compiledExe"] = compiled_exe_name
        if compiled_worker_name:
            rt_bun["compiledWorkerExe"] = compiled_worker_name
        if pre_built_web:
            rt_bun["preBuiltWeb"] = True
        runtime_config["bun"] = rt_bun

    config_name = "keystone.config.json"
    (bundle_resources / config_name).write_text(json.dumps(runtime_config, indent=2))
    print(f"  Config: {config_name}")

    # ── 11. Entitlements & signing ───────────────────────────────────────────
    # Hardened runtime with .NET CLR JIT + WebKit JIT entitlements.
    # Optionally patches in disable-library-validation for third-party plugins.

    entitlements_src = engine / "Keystone.App" / "entitlements.base.plist"
    entitlements_path = bundle_resources / "entitlements.plist"
    tier = "hardened-runtime"

    if entitlements_src.exists():
        entitlements_text = entitlements_src.read_text()

        # External signatures: allow loading DLLs signed by other teams.
        if allow_external:
            patch = "    <key>com.apple.security.cs.disable-library-validation</key>\n    <true/>\n"
            entitlements_text = entitlements_text.replace("</dict>", patch + "</dict>")
            tier += " + external-signatures"

        entitlements_path.write_text(entitlements_text)
    else:
        entitlements_path = None

    identity = signing_identity or "-"
    is_adhoc = identity == "-"

    if require_signing_identity and is_adhoc:
        print("  ERROR: requireSigningIdentity=true but no signing identity was configured.")
        print("  Set build.signingIdentity or KEYSTONE_SIGNING_IDENTITY.")
        sys.exit(1)

    if notarize and is_adhoc:
        print("  ERROR: Notarization requires a real Developer ID signing identity (ad-hoc '-' is not valid).")
        print("  Set build.signingIdentity or KEYSTONE_SIGNING_IDENTITY.")
        sys.exit(1)

    if notarize and not notary_profile:
        print("  ERROR: Notarization enabled but no notary profile configured.")
        print("  Set build.notaryProfile or KEYSTONE_NOTARY_PROFILE (xcrun notarytool keychain profile name).")
        sys.exit(1)

    if is_adhoc:
        print("  NOTE: Using ad-hoc signature ('-'). Suitable for local/dev, not trusted distribution.")

    sign_cmd = ["codesign", "--force", "--deep", "--sign", identity]
    if not is_adhoc:
        # Required for hardened runtime behavior expected in production distributions.
        sign_cmd += ["--options", "runtime", "--timestamp"]
    if entitlements_path:
        sign_cmd += ["--entitlements", str(entitlements_path)]
    sign_cmd.append(str(bundle_path))
    print(f"  Signing ({tier}, {'ad-hoc' if is_adhoc else identity})...")
    run(sign_cmd)

    # Verify signature integrity immediately after signing.
    print("  Verifying signature...")
    run(["codesign", "--verify", "--strict", "--deep", "--verbose=2", str(bundle_path)])
    spctl_result = run(["spctl", "-a", "-t", "exec", "-vv", str(bundle_path)], check=False)
    if not is_adhoc and spctl_result.returncode != 0:
        print("  ERROR: Gatekeeper assessment failed for signed bundle.")
        sys.exit(spctl_result.returncode or 1)
    if is_adhoc and spctl_result.returncode != 0:
        print("  NOTE: Gatekeeper rejected ad-hoc signature (expected for local/dev builds).")

    run(["xattr", "-dr", "com.apple.quarantine", str(bundle_path)], check=False)

    # ── 12. DMG ──────────────────────────────────────────────────────────────

    dmg_path = None
    if create_dmg:
        dmg_path = out_dir / f"{safe_name}.dmg"
        if dmg_path.exists():
            dmg_path.unlink()
        print(f"  Creating DMG...")
        run(["hdiutil", "create", "-volname", app_name,
             "-srcfolder", str(bundle_path), "-ov", "-format", "UDZO",
             str(dmg_path)])
        print(f"  DMG: {dmg_path}")

    # ── 13. Optional notarization ────────────────────────────────────────────
    # Notarize final distribution artifact when configured.
    if notarize:
        target = dmg_path if dmg_path else bundle_path
        print(f"  Notarizing: {target.name} (profile={notary_profile})...")
        run([
            "xcrun", "notarytool", "submit", str(target),
            "--keychain-profile", notary_profile,
            "--wait",
        ])
        print(f"  Stapling ticket: {target.name}")
        run(["xcrun", "stapler", "staple", str(target)])
        if target != bundle_path:
            # Best effort: staple app too when distributing inside a DMG.
            run(["xcrun", "stapler", "staple", str(bundle_path)], check=False)

    # ── Done ─────────────────────────────────────────────────────────────────

    print(f"\n{'='*60}")
    print(f"  {bundle_path}")
    if plugin_mode == "side-by-side":
        print(f"  Plugins: {dylib_src} (external, hot-reload)")
    print(f"{'='*60}\n")
    return bundle_path


# ─── CLI ─────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="Package a Keystone app into a distributable .app bundle")
    parser.add_argument("app_root", type=str,
                        help="Path to the app directory (contains keystone.config.json)")
    parser.add_argument("--debug", action="store_true",
                        help="Use Debug configuration for framework binaries")
    parser.add_argument("--mode", choices=["bundled", "side-by-side"],
                        help="Override build.pluginMode")
    parser.add_argument("--dmg", action="store_true", default=None,
                        help="Create DMG (overrides build.dmg)")
    parser.add_argument("--allow-external", action="store_true", default=None,
                        help="Allow externally-signed plugins")
    parser.add_argument("--engine", type=str,
                        help="Explicit path to Keystone Desktop")

    args = parser.parse_args()
    app_root = Path(args.app_root).resolve()

    if not app_root.exists():
        print(f"ERROR: App directory not found: {app_root}")
        sys.exit(1)

    engine = find_engine(app_root, args.engine)

    package(
        app_root=app_root,
        engine=engine,
        debug=args.debug,
        mode_override=args.mode,
        dmg_override=args.dmg if args.dmg else None,
        allow_external_override=args.allow_external if args.allow_external else None,
    )


if __name__ == "__main__":
    main()
