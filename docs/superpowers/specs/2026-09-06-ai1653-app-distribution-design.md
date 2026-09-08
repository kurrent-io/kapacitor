# AI-1653 — App distribution: CLI bundling, signing/notarization, DMG, auto-update (desktop supervisor slice 3)

**Date:** 2026-09-06
**Status:** Approved design.
**Issue:** AI-1653. Umbrella spec: [2026-07-31-desktop-supervisor-app-design.md](2026-07-31-desktop-supervisor-app-design.md) §7 "Onboarding & distribution" and §11 (top project risk).
**Prior slices:** control IPC + consent (AI-1623/AI-1648), supervision IPC (AI-1649), app shell (AI-1650), tray/agents/stop (AI-1651), consent prompts + Activity (AI-1652), daemon lifecycle + PATH shim (AI-1654), onboarding wizard (AI-1655). AI-1654 reserved the bundle-relative `CliResolver` arm and recorded the stable-in-bundle-path constraint this slice honours; AI-1655 fixed the CLI floor (`0.12.0-beta.1`) and promised a build-time floor assertion in the app package build, delivered here.

## 1. Problem

Every desktop-supervisor slice so far runs only from source behind the `KCAP_APP_CLI_PATH` dev seam. Nothing produces an installable artifact: the app is an unbundled `dotnet run` executable named "Avalonia Application" in the macOS application menu, with a 96px icon and no bundle id; the CLI and daemon it supervises come from wherever PATH points. The umbrella spec asks for a signed, notarized DMG that bundles the `kcap` AOT binary (the Docker Desktop model), and an auto-update that never leaves the app and its bundled binary mismatched. The release engineering for that (Apple signing, notarization, a download host, an update feed) is new surface for the team.

## 2. Decisions

| # | Decision |
|---|----------|
| 1 | **Velopack** (library 1.2.0 + the `vpk` tool at the same version) packs, signs, notarizes and updates the bundle. It is .NET-native, needs no NativeAOT of the app, replaces the `.app` with one atomic `renamex_np` swap, reads a static feed, and covers Windows later (AI-1657). Rejected: Sparkle 2 (no maintained .NET binding — an Objective-C shim, a bundled framework with XPC services, and macOS-only); a hand-rolled updater (we would own atomic replacement and rollback while still writing every pack/sign/notarize step). |
| 2 | **One build, signed once, shared by npm and the app.** The daemon is signed immediately after its AOT publish and *before* its digest is computed; the CLI embeds that digest and is signed after its own publish; neither is ever re-signed. npm and the app ship the same signed bytes and there is one digest. The coupling is deliberate: an expired certificate fails the whole release instead of shipping an app whose CLI refuses its daemon. Rejected: a second, app-only CLI build (a second osx-arm64 AOT publish per release and two digests for one version). |
| 3 | **DMG only** as the first-install artifact: drag-to-Applications, built by our own steps around Velopack (`--noInst`), signed and notarized with the single Developer ID *Application* certificate. Velopack's `.pkg` would need a second (Installer) certificate. |
| 4 | **Self-hosted feed on Cloudflare R2, fronted by the kurrent.io Worker.** One bucket, prefix `desktop/osx-arm64/`; public URLs under `https://www.kurrent.io/download/desktop/osx-arm64/`. The website links to version-free alias DMGs on the same prefix. The Worker route and R2 binding are a kcap-web change (§8). Rejected: GitHub Releases as the store (downloads visibly from github.com, the `latest` alias skips prereleases); Velopack Flow (paid, vendor account). |
| 5 | **One Velopack channel (`osx-arm64`) holding every release; stable-vs-beta is decided in the app.** A `PrereleaseFilteringSource` drops prerelease feed entries when the installed version is itself stable, and keeps them when the installed version is a prerelease. One pack, one notarization, one upload per tag. Rejected: two channels (a stable tag would need two packs and two notarizations so beta installs also receive it). |
| 6 | **Everything in `Contents/MacOS`.** `kcap`, `kcap-daemon` and `libpty_shim.dylib` sit beside the app executable and its runtime, so the CLI finds the daemon and the daemon finds the shim as siblings exactly as in the npm layout. The stable path the shim symlink and the LaunchAgent bake is `/Applications/Kurrent Capacitor.app/Contents/MacOS/kcap`. Rejected: `Contents/Helpers` (we would assemble the bundle ourselves for no functional gain). |
| 7 | **Self-contained JIT publish of the app**, not trimmed, not AOT. Avalonia and ReactiveUI run under JIT today; AOT of the app is a separate investigation with its own trimming warnings. |
| 8 | **The first app release is the first tag after `v0.12.0-beta.1`.** That tag is cut first (via kcap-server's `release.sh`, whose version regex accepts a prerelease suffix) so MinVer yields `0.12.0-beta.1.N` on every branch and PR-built bundles satisfy the floor without a synthetic version override. |
| 9 | **Update UX: background download, one prompt when ready, the app restart is the user's.** Automatic checks are silent on failure. The daemon's restart is the daemon's own: its `RestartCoordinator` already polls its binary every 15 s and restarts itself the moment it is idle after the file changes — the path an npm upgrade takes today, and the bundle swap changes the same path. The lifecycle slice's skew dialog covers only a daemon that stays busy, after a short post-update grace (§7.4). The daemon gains no update logic beyond `--version`. |
| 10 | **Install-location guard at startup.** A bundle outside `/Applications` or `~/Applications` (DMG volume, Downloads, App Translocation) gets a modal with Move-to-Applications or Quit before anything else runs, because the shim and the LaunchAgent bake the CLI path and Velopack cannot swap a bundle on a read-only volume. |

## 3. The bundle

### 3.1 Identity

- Bundle: `Kurrent Capacitor.app`; `CFBundleIdentifier` `io.kurrent.capacitor`; `CFBundleName`/`CFBundleDisplayName` `Kurrent Capacitor`; executable `Kurrent Capacitor` (the existing `AssemblyName`).
- Velopack pack id `KurrentCapacitor` (no spaces allowed), authors `Kurrent`, title `Kurrent Capacitor`, channel `osx-arm64`. The pack id names the cache (`~/Library/Caches/velopack/KurrentCapacitor/packages`) and the log (`~/Library/Logs/velopack_KurrentCapacitor.log`).
- `src/Capacitor.App/Packaging/Info.plist` is a committed template passed to `vpk` via `--plist`, with `{VERSION}` (full SemVer, `CFBundleVersion`) and `{SHORT_VERSION}` (numeric core, `CFBundleShortVersionString`) substituted by the workflow. It also carries `CFBundleIconFile` = `kcap-icon.icns`, `NSHighResolutionCapable`, `NSPrincipalClass` `NSApplication`, `LSMinimumSystemVersion` `15.0` (.NET 10's macOS floor is Sequoia), and `LSApplicationCategoryType` `public.app-category.developer-tools`. Owning the plist is why the tray icon memory's "unbundled app with no bundle id" problem goes away here.
- `App.axaml` gets `Name="Kurrent Capacitor"`: Avalonia's default `Application.Name` is what macOS shows as "Avalonia Application" in the application menu, in dev runs and in the bundle alike.

### 3.2 Contents/MacOS

The app's self-contained `osx-arm64` publish output (`dotnet publish src/Capacitor.App -c Release -r osx-arm64 --self-contained -p:MinVerVersionOverride=<version>`), plus three files copied verbatim from the release matrix's `release-osx-arm64` artifact: `kcap`, `kcap-daemon`, `libpty_shim.dylib`. Velopack adds `UpdateMac` and the `sq.version` manifest (in `Resources`, symlinked from `MacOS`). Executable bits are restored after the artifact round-trip (`actions/upload-artifact` drops them) before anything runs or is signed.

### 3.3 Icons

`src/Capacitor.App/Assets/kcap-icon.svg` is the single source: the product mark from kcap-web's `public/favicon.svg` (dark rounded square `#100D14`, two light bars, the arc). `scripts/render-app-icons.sh` renders it with `rsvg-convert` and macOS's `iconutil` into two committed outputs: `kcap-icon.png` at 512px (replacing the 96px file; `ProductIcon`, `MainWindow.axaml` and the tray composite already load this path) and `kcap-icon.icns` for the bundle and dock. CI never rasterizes; the rendered files are checked in. `TrayIconRenderer` is unchanged — it keeps compositing the status overlay over the new base bitmap.

### 3.4 `CliResolver` bundle arm

Resolution order becomes: `KCAP_APP_CLI_PATH` override (unchanged semantics, including "set but missing → null") → `Path.Combine(AppContext.BaseDirectory, "kcap")` when that file exists → bare `kcap` (PATH). Under `dotnet run` the sibling does not exist, so dev behaviour is unchanged. The bundle arm returns an absolute path, which is what turns the AI-1654 shim offer on (it links only to a rooted path). The arm is platform-neutral in shape; only the macOS bundle produces the sibling today.

### 3.5 CLI provenance

`src/Capacitor.Cli/InstallProvenance.cs`: the CLI is **app-bundled** when `Environment.ProcessPath` has a `.app/Contents/MacOS/` segment and `Contents/Info.plist` exists beside it (pure function over a path and a file-exists seam). Effects, all in the CLI:

- `UpdateNotice.IsHumanFacing` returns false when bundled: the exit-time nag is meaningless (the app owns updates and its channel may lag npm).
- `kcap update` (any flags) prints `This kcap is bundled with the Kurrent Capacitor desktop app; updates arrive through the app ("Check for Updates…" in the menu bar).` and exits 0. `--check` keeps its JSON contract but reports `newer: false`.
- `kcap status`'s version line gains `(bundled with Kurrent Capacitor)` and skips the npm advisory.

Nothing else changes: hooks, MCP registrations, the LaunchAgent and the shim all use the resolved absolute bundle path or PATH exactly as they do for an npm install.

## 4. Signing and notarization

### 4.1 Order

1. Matrix leg `osx-arm64`, after "Publish daemon AOT binary": import the certificate into a temporary keychain; sign `kcap-daemon` and `libpty_shim.dylib`.
2. "Compute daemon digest" hashes the **signed** daemon. From here the daemon is never re-signed.
3. "Publish CLI AOT binary" embeds that digest; sign `kcap`.
4. The signed trio flows into the npm package, the release archive and the `release-osx-arm64` artifact as today.
5. `app-macos` job: publish the app; sign its main executable and every other Mach-O in the publish output (the runtime dylibs, `createdump`) with the app entitlements; copy the signed trio in unchanged.
6. `vpk pack --signDisableDeep`: Velopack signs `UpdateMac` and the outer bundle only, notarizes a zip of the bundle, staples it, runs `spctl --assess`, and writes the portable zip, the full package, any delta, and `releases.osx-arm64.json`.
7. DMG: extract the stapled bundle from the portable zip, `hdiutil create` (volume `Kurrent Capacitor`, the app plus an `Applications` symlink, no background art), `codesign --timestamp` the DMG, `notarytool submit --wait`, `stapler staple`, `spctl --assess -t open --context context:primary-signature`.

`codesign` for every nested binary we sign: `codesign --force --timestamp --options runtime --entitlements <plist> --sign "$IDENTITY" --keychain "$KEYCHAIN" <file>` (the same flags Velopack uses), in `scripts/sign-macos.sh`. The script refuses an empty identity or keychain rather than skipping.

### 4.2 Entitlements

Three committed plists under `src/Capacitor.App/Packaging/`:

- `app.entitlements.plist` — the app executable and every runtime dylib: `com.apple.security.cs.allow-jit`, `com.apple.security.cs.allow-unsigned-executable-memory`, `com.apple.security.cs.allow-dyld-environment-variables`, `com.apple.security.cs.disable-library-validation` (Microsoft's documented set for a JIT .NET app under hardened runtime).
- `cli.entitlements.plist` — `kcap`: `com.apple.security.cs.disable-library-validation` only. The on-demand `e_sqlite3` it downloads for OpenCode import is not signed by our team, and library validation would refuse to load it.
- `daemon.entitlements.plist` — `kcap-daemon` and `libpty_shim.dylib`: empty dictionary. The daemon's only native import is our own signed shim; vendor CLIs it spawns are separate processes.

The outer bundle is signed by Velopack with `app.entitlements.plist` (passed as `--signEntitlements`); `UpdateMac` gets Velopack's own default entitlements.

### 4.3 Secrets and keychain

Repository secrets: `APPLE_CERTIFICATE_P12` (base64 Developer ID Application `.p12`), `APPLE_CERTIFICATE_PASSWORD`, `APPLE_SIGNING_IDENTITY` (`Developer ID Application: <name>` — without the team suffix, which `codesign` rejects when given via Velopack), `APPLE_NOTARY_KEY_P8` (base64 App Store Connect API key), `APPLE_NOTARY_KEY_ID`, `APPLE_NOTARY_ISSUER_ID`. Per job that signs:

```bash
security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security set-keychain-settings -lut 21600 "$KEYCHAIN"
security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security import cert.p12 -P "$APPLE_CERTIFICATE_PASSWORD" -A -t cert -f pkcs12 -k "$KEYCHAIN"
security list-keychain -d user -s "$KEYCHAIN"
xcrun notarytool store-credentials kcap-notary --key key.p8 --key-id "$APPLE_NOTARY_KEY_ID" --issuer "$APPLE_NOTARY_ISSUER_ID" --keychain "$KEYCHAIN"
```

`KEYCHAIN` is `$RUNNER_TEMP/kcap-signing.keychain-db`; `KEYCHAIN_PASSWORD` is a per-run random string. Every signing step fails when its secret is empty; a tag never falls back to unsigned.

### 4.4 Post-sign smoke

After signing on the runner: run `kcap --version --no-update-check` and `kcap-daemon --version` from the signed files, and after `vpk pack` run the bundled `kcap` once more from inside the extracted bundle. The daemon has no version flag today; this slice adds `--version`, handled before any config, environment or profile work, printing the same version string the CLI prints and exiting 0 — the one daemon change in the slice.

Those prove the AOT binaries launch under hardened runtime. The JIT app is proved separately: the extracted signed bundle is launched with `KCAP_CONFIG_DIR` pointing at a scratch directory and `KCAP_APP_UPDATE_URL` at an unroutable address, and must still be running 15 s later (a JIT or executable-memory entitlement mistake kills the process within the first second). What these smokes cannot prove — a real PTY spawn through the signed daemon and shim, and a cold-cache OpenCode import loading the downloaded `e_sqlite3` under `kcap`'s library-validation exemption — is an explicit first-release gate in §10, not an inferred pass.

## 5. Feed and website

### 5.1 R2 layout

Bucket from the `R2_BUCKET` secret, prefix `desktop/osx-arm64/`. Immutable per-version objects: `KurrentCapacitor-<version>-osx-arm64-full.nupkg`, `KurrentCapacitor-<version>-osx-arm64-delta.nupkg` when a baseline exists, `Kurrent-Capacitor-<version>-osx-arm64.dmg`. Mutable objects: `releases.osx-arm64.json` (the feed manifest Velopack merges on every upload), `KurrentCapacitor-osx-arm64-Portable.zip` (Velopack names its portable zip without a version and overwrites it each release; nothing links to it and the feed does not reference it), and the two DMG aliases the workflow promotes: `Kurrent-Capacitor-osx-arm64-beta.dmg` for the highest version of any kind, `Kurrent-Capacitor-osx-arm64.dmg` for the highest stable version. `--keepMaxReleases` is never passed: old packages stay, a user mid-download is never cut off.

Velopack's `download`/`upload` commands default to channel `osx`, so every invocation names the channel and the output directory explicitly:

```bash
vpk download s3 --channel osx-arm64 --outputDir releases --endpoint "$R2_ENDPOINT" --bucket "$R2_BUCKET" --prefix desktop/osx-arm64 --keyId "$R2_ACCESS_KEY_ID" --secret "$R2_SECRET_ACCESS_KEY"
vpk upload   s3 --channel osx-arm64 --outputDir releases --endpoint "$R2_ENDPOINT" --bucket "$R2_BUCKET" --prefix desktop/osx-arm64 --keyId "$R2_ACCESS_KEY_ID" --secret "$R2_SECRET_ACCESS_KEY"
```

The download runs before packing so a delta can be generated; on an empty bucket it is allowed to fail with a logged line (a credentials failure surfaces again at upload, which is not tolerant). `vpk download` always fetches the channel's highest full package, and Velopack refuses to pack a version at or below its baseline, so a stable maintenance release cut after a higher beta (0.12.2 after 0.13.0-beta.1) would be refused although it is the highest stable version. **Baseline selection** (`scripts/desktop-baseline.sh`, with a `.test.sh`): `vpk download` writes only the selected `.nupkg` into `releases/`, no manifest, so the script reads the baseline's version from that package's embedded `.nuspec` (`unzip -p` on the one `*-full.nupkg` present); if that version is not strictly below the candidate, the package is deleted and the pack runs without a baseline — a full package only, no delta. No package present is a no-op. The test uses fixture packages (a zip with a nuspec) laid out exactly as the download leaves them: one full `.nupkg`, no adjacent JSON. The feed still orders correctly: a stable install sees 0.12.2 as its highest stable, a beta install keeps 0.13.0-beta.1.

Whether a version is already published is decided by explicit checks, never by that download. Before packing, `app-macos` asks the bucket for `desktop/osx-arm64/KurrentCapacitor-<version>-osx-arm64-full.nupkg` (`aws s3api head-object`) and fails if it exists — the same refusal `verify-release-immutable` applies to npm, and the answer is a new version, never a re-pack (§6.1). That early check cannot see a second build of the same version that is still in flight (a tag re-created while the first run builds), so `app-publish` repeats the decision inside its concurrency group before any upload: `scripts/verify-desktop-immutables.sh` downloads every immutable object of the candidate version that already exists in the bucket and compares its SHA-256 with the artifact's; identical bytes are a retry and proceed, different bytes fail the job. Velopack's own equal-or-greater refusal at pack time is a second line, not the gate. The upload merges the manifest with what is already in the bucket; the AWS CLI with `--endpoint-url "$R2_ENDPOINT"` uploads the versioned DMG and promotes the aliases. **Alias promotion is conditional:** `scripts/promote-desktop-aliases.sh` reads the freshly merged `releases.osx-arm64.json` and copies the candidate DMG onto an alias only when the candidate is the highest version of that alias's class in the manifest, so an older tag published late, or a re-run, can never regress an alias. Publication is serialized per bucket by the workflow's concurrency group (§6.1), which closes the read-merge-write race on the manifest.

### 5.2 Public URLs and the kcap-web contract

The app polls `https://www.kurrent.io/download/desktop/osx-arm64/` (`UpdateFeed.BaseUrl`), overridable by `KCAP_APP_UPDATE_URL`. The website links to `https://www.kurrent.io/download/desktop/osx-arm64/Kurrent-Capacitor-osx-arm64.dmg` (and the `-beta` alias until a stable exists). The kcap-web issue asks for:

- A Worker route `GET /download/desktop/*` served from an R2 binding on the bucket. The object key is the URL path with the leading `/download/` removed: `/download/desktop/osx-arm64/releases.osx-arm64.json` → `desktop/osx-arm64/releases.osx-arm64.json`. Response headers: the object's content type; `Cache-Control: no-cache` on `releases.*.json`, `max-age=300` on the alias DMGs and the portable zip (the mutable objects of §5.1), `public, max-age=31536000, immutable` on versioned objects; 404 on a missing object. Acceptance for that issue: the public URL of a published manifest returns the same bytes as the bucket object.
- `GET /download/mac` → 302 to the stable alias (the beta alias until a stable exists).
- The path must stay worker-first (it needs the logging every page request already gets) and must not join the `run_worker_first` exclusion list.

R2 bucket, API token, binding and route are one-time setup outside this repo.

## 6. Workflows

### 6.1 `release.yml`

- Matrix leg `osx-arm64` (steps conditioned on `startsWith(matrix.rid, 'osx-')`): keychain import; sign daemon + shim before the digest; sign the CLI after its publish; post-sign smoke. The leg writes the digest it embedded to `daemon.sha256`, and the signed CLI's hash to `kcap.sha256`, inside the `release-osx-arm64` artifact, because a matrix job's outputs cannot carry a per-leg value.
- New job `app-macos` (`needs: build`, `runs-on: macos-latest`), in parallel with `publish-npm` and `github-release`: download `release-osx-arm64`, restore executable bits, keychain import + notary profile, `dotnet tool install -g vpk --version <pinned>`, refuse an already-published version (§5.1 head-object check), publish the app, sign its binaries, assemble the pack dir, substitute the plist, `vpk download s3` (tolerant) followed by `scripts/desktop-baseline.sh` (§5.1), `vpk pack` (§6.3), extract the bundle, `scripts/assert-app-cli-version.sh` (§6.4), `scripts/assert-bundle-digest.sh` (§6.5), in-bundle smokes (§4.4), `scripts/build-dmg.sh`, sign/notarize/staple/assess the DMG. It publishes nothing: its whole `releases/` directory, the DMG, and both hash files carried over from `release-osx-arm64` (`daemon.sha256`, `kcap.sha256`) go up as the `app-osx-arm64-signed` artifact — the only input `app-publish` reads, so everything its checks need, retries included, travels inside it. The `verify-npm-trio` test runs against that exact artifact layout.
- New job `app-publish` (`needs: [app-macos, publish-npm, github-release]`, `runs-on: ubuntu-latest`, `concurrency: { group: desktop-publish, cancel-in-progress: false }`): download `app-osx-arm64-signed`, `scripts/verify-npm-trio.sh` (below), `scripts/verify-desktop-immutables.sh` (§5.1: existing objects of this version must be byte-identical to the artifact), `vpk upload s3`, upload the versioned DMG, `scripts/promote-desktop-aliases.sh`, then `gh release upload --clobber` of the DMG onto the GitHub Release that `github-release` created, so the DMG is discoverable from the repo too. The concurrency group serializes every publication into the bucket across tag runs, so two runs can never interleave their manifest read-merge-write.
- **Retry semantics.** A failure inside `app-publish` is retried by re-running that job alone: the immutables check passes on identical bytes, it re-uploads the exact signed bytes from the artifact, the manifest merge is idempotent for an entry that is already present, and alias promotion is conditional (§5.1). A failure inside `app-macos` before anything was published is retried by re-running it. Re-packing a version that is already in the bucket is refused by Velopack's own equal-or-greater check (non-interactive, so a hard failure): a version whose feed entry exists is never rebuilt, because a second signing pass produces different bytes under the same immutable keys — cut a new version, the same rule `verify-release-immutable` enforces for npm.
- **The app ships only the trio npm shipped.** Decision 2 promises the same signed bytes on both channels, and the early absence checks cannot guarantee it: two runs of one version can both pass them, one win npm and the other win the bucket, and the channels would then carry different signatures and digests. So `app-publish` depends on its run's `publish-npm`, and `scripts/verify-npm-trio.sh` (with a `.test.sh`) downloads `@kurrent/kcap-darwin-arm64@<version>` from the registry and asserts that the tarball's `bin/kcap-daemon` and `bin/kcap` hash to the artifact's `daemon.sha256` and `kcap.sha256`. A run that lost the npm race fails here and publishes no app; the run whose bytes are on npm is the only one that can publish the bundle. The dependency is one-way: neither `publish-npm` nor `github-release` depends on the app jobs, so an app-side failure leaves the npm release and the GitHub Release intact.

### 6.2 `ci.yml`

New job `app-bundle`, display name `App bundle (osx-arm64)`, on `macos-latest`, no secrets. It checks out with `fetch-depth: 0` and `fetch-tags: true`: the other CI jobs use the default shallow checkout, under which MinVer cannot see the floor tag and would compute a below-floor default version. It computes the version once with `minver --tag-prefix v` (`minver-cli` pinned to the same MinVer version as `Directory.Packages.props`; the standalone tool does not read `MinVerTagPrefix` from `Directory.Build.props`, so the prefix is passed explicitly) and passes it as `MinVerVersionOverride` to the daemon, CLI and app publishes and as `--packVersion` to `vpk pack`, so all four agree. Steps: publish daemon and CLI for `osx-arm64` with the digest handshake the release uses, publish the app, `vpk pack --noInst` without any signing options, `scripts/assert-app-cli-version.sh` against that version, `scripts/assert-bundle-digest.sh` against the digest the job computed, run the bundled `kcap --version --no-update-check`, upload the portable zip as a workflow artifact (`app-osx-arm64-unsigned`). A reviewer runs it after `xattr -d com.apple.quarantine`; being a packed bundle it polls the real feed and, carrying a prerelease version, would accept a signed release as an update — expected, not a defect.

`release.yml`'s "Wait for CI" regexp is corrected at the same time. Today it is `^(Build and test|AOT publish check.*)$`, and the test jobs are named `Build and test (ubuntu-latest)` and `Build and test (windows-latest)`, which that pattern does not match — a tag currently waits for the AOT checks only. The new pattern is `^(Build and test \(.*\)|AOT publish check \(.*\)|App bundle \(osx-arm64\))$`, so a red unit-test leg or a red bundle job blocks the release. Adding the bundle job to branch protection is a repository setting outside this PR.

### 6.3 The `vpk pack` invocation

```bash
vpk pack --packId KurrentCapacitor --packVersion "$VERSION" --packTitle "Kurrent Capacitor" --packAuthors Kurrent \
  --mainExe "Kurrent Capacitor" --packDir publish/app --plist Info.plist --icon src/Capacitor.App/Assets/kcap-icon.icns \
  --channel osx-arm64 --noInst --outputDir releases \
  --signAppIdentity "$APPLE_SIGNING_IDENTITY" --signEntitlements src/Capacitor.App/Packaging/app.entitlements.plist \
  --signDisableDeep --notaryProfile kcap-notary --keychain "$KEYCHAIN"
```

`VERSION` is the tag without build metadata (`vpk` requires plain SemVer2). On PRs the invocation ends at `--outputDir releases`: both signing lines (`--signAppIdentity`, `--signEntitlements`, `--signDisableDeep`, `--notaryProfile`, `--keychain`) are omitted, and no Apple secret is referenced anywhere in the job.

### 6.4 `scripts/assert-app-cli-version.sh`

Takes the bundled `kcap` path and the expected version. Runs `kcap --version --no-update-check`, requires exactly one line `kcap <version>`, compares to the expected version ignoring `+build` metadata, then checks the version satisfies the floor read from `KcapCliCompatibility.Floor` in `src/Capacitor.App/Services/KcapCliCompatibility.cs` (the same grep-the-constant pattern the release already uses for the SQLite pin). The comparison implements SemVer precedence for the MinVer shapes this repo produces; a sibling `.test.sh` pins it, including "a prerelease of the floor version is below the floor" and "height-suffixed betas are above". A mismatch or below-floor result fails the job.

### 6.5 `scripts/assert-bundle-digest.sh`

Takes the extracted bundle and the digest file. The version checks above cannot see the daemon-digest invariant: `kcap --version` never touches `DaemonDigest`, and the release's existing digest assertion runs on the npm staging copy, not on the packed bundle. This script asserts, on the final bundle: the SHA-256 of `Contents/MacOS/kcap-daemon` equals the recorded digest, and the packed `Contents/MacOS/kcap` embeds that digest and not the 64-zero placeholder. The embedding is proven through the CLI's own app-managed start gate rather than a byte search (NativeAOT does not lay a literal out as plain UTF-16 on disk): a copy of the packed `kcap` beside a foreign daemon must exit 43 with `daemon_start_reason=package_inconsistent`, and the packed pair must get past the gate under an empty consent seed, which makes any daemon that spawns refuse to boot. A sibling `.test.sh` pins it with a stand-in CLI: a substituted daemon fails, a placeholder or stale digest fails, a CLI that never refuses fails, and the matching pair passes.

## 7. App-side update flow

### 7.1 Bootstrap

`VelopackApp.Build().SetAutoApplyOnStartup(false).OnRestarted(...).Run()` is the first statement of `Program.Main`, before `BuildAvaloniaApp()`. Velopack runs install/update hooks and exits from inside `Run()`; anything placed before it would execute during those operations. Auto-apply is switched off deliberately: with it on, `Run()` applies the newest cached package before the install-location guard or the prerelease filter can run — from a DMG volume, or a beta package cached under the shared app id onto a stable install. Pending packages are applied by the coordinator instead (§7.3). `OnRestarted` fires on the first launch after Velopack applied an update; the app records that fact (`UpdateRelaunch`) for the grace window in §7.4.

### 7.2 `IAppUpdater`

```csharp
public interface IAppUpdater {
    bool IsAvailable { get; }                   // false outside a packed bundle
    string? InstalledVersion { get; }
    Task<UpdateCandidate?> CheckAsync(CancellationToken ct);
    Task DownloadAsync(UpdateCandidate candidate, IProgress<int> progress, CancellationToken ct);
    UpdateCandidate? PendingRestart { get; }    // UpdateManager.UpdatePendingRestart: a downloaded package awaiting a relaunch
    void ApplyOnExit(UpdateCandidate candidate); // WaitExitThenApplyUpdates(asset, silent: true, restart: true)
    void ApplyNow(UpdateCandidate candidate);    // ApplyUpdatesAndRestart(asset): exits the process itself
}
```

`VelopackAppUpdater` wraps `UpdateManager(new PrereleaseFilteringSource(new SimpleWebSource(UpdateFeed.Resolve(getEnv)), allowPrerelease))`, where `allowPrerelease` is whether `UpdateManager.CurrentVersion` is a prerelease. `IsAvailable` is `UpdateManager.IsInstalled`. Outside a packed bundle (dev runs) the updater is inert; `KCAP_APP_UPDATE_URL` changes only the feed, never availability.

`PrereleaseFilteringSource` implements Velopack's `IUpdateSource`: `GetReleaseFeed` delegates and, when prereleases are not allowed, removes assets whose version has a prerelease part; `DownloadReleaseEntry` delegates unchanged.

### 7.3 `UpdateCoordinator`

Owns the schedule and the UX; runs only when `IsAvailable`.

- **Schedule:** first check 30 s after the daemon graph is built (never during the wizard), then every 4 h; a "Check for Updates…" tray item (between "Install command-line tool…" and the Quit separator) runs one immediately. One check in flight at a time; a manual check coalesces onto a running one.
- **Found:** download in the background. On completion, one prompt through the app's serialized dialog lane: "Kurrent Capacitor `<version>` is ready. Restart now / Later".
- **Ready is terminal for the run.** Once a package is downloaded, the coordinator stops checking and downloading for the rest of the run: Velopack's download cleans every other cached package, including on a failed download, so a later automatic attempt at a newer version could delete the package the tray still offers. A manual "Check for Updates…" in this state reports that `<version>` is ready and offers the restart; it starts no download.
- **Later:** the tray item reads "Restart to update to `<version>`" for the rest of the run and the prompt does not repeat for that version. The downloaded package stays cached and is applied at the next launch by the **startup pending-apply** step: in `StartAsync`, after the install-location guard has passed and before the gate or wizard, the coordinator reads `PendingRestart` and, if the package is newer than the running version and passes the same prerelease rule as the feed filter, calls `ApplyNow` — nothing has been built yet, so there is nothing to tear down. An ineligible cached package (a prerelease under a stable install) is ignored, never applied. The step is skipped on an update relaunch (`UpdateRelaunch`, §7.1): Velopack relaunches the old version with the same marker when applying fails (a denied elevation, for instance) and the failed package stays cached, so applying it again automatically would loop forever with no UI ever reached — the same guard Velopack's own bootstrap carries. After such a relaunch the normal schedule finds the update again and the retry is the user's.
- **Restart now** (or the tray item): the coordinator records the candidate as the run's pending apply and invokes the app's existing quit path (`QuitCommand`). The updater is launched **last**: `ApplyOnExit` is called from the final step of the shutdown sequence, after workspace drain, re-auth settlement, the bounded quiesce and every disposal, immediately before the platform shutdown call. Velopack's updater waits at most 60 s for the process to exit, and the app's own quiesce can legitimately take that long, so launching it any earlier could let a slow teardown outlive the wait and skip the swap.
- **Failures:** automatic checks and downloads fail silently (Velopack logs to `~/Library/Logs/velopack_KurrentCapacitor.log`) and retry on the next interval; a manual check reports "You're up to date (`<version>`)" or "Could not check for updates" in a dialog.
- **Never** during the wizard, never over the skew dialog, never blocking startup.

### 7.4 After the relaunch

The app now carries a newer CLI than the running daemon, and two mechanisms already exist for that:

- **The daemon restarts itself when idle.** Its `RestartCoordinator` polls `Environment.ProcessPath` every 15 s; the bundle swap changes the size and mtime of the file at that path, which queues a restart-after-update that fires as soon as no hosted agent or eval is running. An app-managed daemon is supervised, so it exits and launchd's `KeepAlive` respawns it on the new binary. This is the same path an npm upgrade takes today, so headless behaviour is untouched. Agents survive the app's own restart by construction (the daemon is a LaunchAgent).
- **The skew dialog covers a daemon that stays busy.** AI-1654's skew detection (`Connected` with a differing daemon version) classifies the unit as **same-binary** (the plist's path equals the new install path) and offers "Restart daemon to update"; declining is remembered per `(daemonVersion, cliVersion)` as today. A decline governs only the forced restart: the daemon's own idle restart still applies later, which is what a headless daemon does too.

Two amendments to the lifecycle controller make the pair coherent:

- **Post-update grace.** When the run started as an update relaunch (`UpdateRelaunch` from §7.1), skew offers are held for 45 s from startup — the 15 s poll plus daemon boot, with margin. A held trigger is not dropped: the controller retains the latest evidence that reached it, the snapshot version from a `Connected` or the hello version from a `daemon_incompatible` (an incompatible daemon never produces a snapshot, and the attach client deduplicates a repeated reason/version pair, so that event may never come again). When the window closes, the check runs once more with the retained evidence, revalidated against a fresh status. An idle daemon has restarted itself by then and the versions match, so no dialog appears; a busy one, compatible or not, still mismatches and the dialog appears as today. Outside an update relaunch there is no hold.
- **Version revalidation on accept.** The gate's revalidation before the mutation already re-checks the takeover classification; it now also re-reads the daemon version and treats an accept whose versions have meanwhile become equal (the daemon restarted itself while the dialog was open) as stale — no `service install --replace` runs for nothing, and the decline claim is retracted exactly as for any other stale outcome.

### 7.5 Install-location guard

`InstallLocation.Classify(bundleRoot, home)` → `Applications | UserApplications | DmgVolume | Translocated | Other`, from the path only. Runs in `StartAsync` before the gate, only when `AppContext.BaseDirectory` is inside a `.app` bundle. `Applications` and `UserApplications` pass. Anything else shows a modal owned by the app (no daemon graph yet): "Move Kurrent Capacitor to your Applications folder to continue." with **Move to Applications** and **Quit**. Move never writes the final path directly: it copies the bundle with `ditto` into a staging sibling on the same volume (`/Applications/Kurrent Capacitor.app.staging-<random>`), verifies the copy is structurally complete (`Contents/Info.plist` and the main executable present), then promotes it to `/Applications/Kurrent Capacitor.app` with `renamex_np(RENAME_EXCL)` through a `LibraryImport` (the app already builds with unsafe blocks for one), which fails if anything exists at that path — a plain `rename` would silently replace an empty directory another installer had just created. A bundle already at the final path before the move refuses up front (the dialog then says to open that copy). Any failure deletes only the staging directory, so a partial copy can never be mistaken for an installed app on the next launch. Success `open -n`s the promoted copy and exits 0; failure shows the error and keeps Quit.

## 8. Out-of-repo work

Tracked outside this PR, all required before the first app release:

1. Apple Developer Program enrollment; Developer ID Application certificate exported as `.p12`; App Store Connect API key (Developer role suffices for notarization). Loaded as the §4.3 secrets.
2. R2 bucket and an API token scoped to it; `R2_ENDPOINT`, `R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY`, `R2_BUCKET` as secrets.
3. kcap-web: the §5.2 route, binding and redirect (issue in that repo, drafted from §5.2).
4. Cut `v0.12.0-beta.1` from main before this PR merges (decision 8).

## 9. Error handling

- Release side, all fail closed and loud: empty signing or notary secret; bundled CLI version ≠ tag; version below floor; packed daemon digest ≠ embedded digest; hardened-runtime crash in a post-sign smoke; notarization rejection (Velopack prints the notary log); `spctl` refusal; a version already present in the channel at pack time; the npm-published trio differing from the artifact's; an immutable object of the version already present with different bytes; upload failure. Retries follow §6.1: publication is re-run from the signed artifact, never re-signed; aliases move only forward.
- Client side: the updater never blocks startup, never touches the daemon, never applies an ineligible cached package, and never surfaces an automatic failure; the guard never overwrites an existing bundle and never leaves a partial one at the final path.
- CLI side: provenance detection is a pure path test; a false negative (bundle without `Info.plist`) merely restores today's npm-oriented messaging.

## 10. Testing

**Unit (TUnit):**

- `CliResolver`: bundle sibling chosen when present; env override still wins; missing sibling falls through to `kcap`; override-set-but-missing still returns null.
- `InstallProvenance`: bundled/not from path shapes; `UpdateNotice.IsHumanFacing` false when bundled; `kcap update` bundled output and exit 0; `--check` JSON.
- `PrereleaseFilteringSource`: stable install drops prereleases; prerelease install keeps them; empty feed.
- `UpdateCoordinator` with a fake `IAppUpdater` and `FakeTimeProvider`: inert when unavailable; initial delay and interval; download-then-prompt; Later semantics and tray label; restart records the pending apply and quits, and `ApplyOnExit` is invoked exactly once from the shutdown sequence's final step — pinned with the existing recording-list shutdown test, including a quiesce that outlasts 60 s; startup pending-apply applies an eligible cached package, ignores a cached prerelease under a stable install, never runs before the guard, and is skipped on an update relaunch (a failed apply relaunching the old version with the same cached package continues into the normal UI); once a package is ready no further check or download runs and a manual check reports the ready state; manual check outcomes; coalescing.
- `DaemonLifecycleController`: skew offers held during the post-update grace and re-run once at its end with the retained evidence (idle daemon restarted → no dialog; still-busy daemon → dialog; an incompatible-only daemon that never produced a snapshot → exactly one offer after the grace); no hold outside an update relaunch; an accept whose versions became equal while the dialog was open is stale (no mutation, claim retracted).
- `InstallLocation.Classify` for the five shapes, including `~/Applications` and translocation paths; the move: partial copy leaves nothing at the final path; a destination that appears mid-move, whether an empty directory or a populated bundle, fails the promotion and cleans the staging copy.
- `scripts/assert-app-cli-version.test.sh` pins the version comparison; `scripts/assert-bundle-digest.test.sh` the digest gate (§6.5); `scripts/promote-desktop-aliases.test.sh` the promotion rule (older candidate never regresses an alias; a beta never becomes the stable alias; a re-run is a no-op); `scripts/desktop-baseline.test.sh` the baseline rule on the real download layout (a lower full package is kept; an equal or higher one, such as a beta above a stable patch, is deleted; no package is a no-op); `scripts/verify-npm-trio.test.sh` the npm identity gate (matching hashes pass; a tarball whose daemon or CLI differs fails; a version absent from the registry fails); `scripts/verify-desktop-immutables.test.sh` the publication gate (absent objects pass, identical bytes pass, different bytes fail); `scripts/render-app-icons.sh` has no test (its outputs are committed and reviewed by eye).

**CI:** the `app-bundle` job (§6.2) is the automated end-to-end of bundling without signing, on a full-history checkout of an untagged descendant of the floor tag.

**First-release gate (manual, accepted risk, umbrella §10)** — run on a clean Mac before the first DMG is linked from the website, and repeated whenever an entitlements file changes:

1. Gatekeeper accepts the DMG and the app; the guard fires from the DMG volume and Move works; the application menu says "Kurrent Capacitor" with the new icon.
2. The shim links into the bundle; the LaunchAgent starts the bundled daemon; `kcap update` from a terminal prints the bundled message.
3. **Entitlements under real load:** launch an agent from the app so the signed daemon spawns a PTY through the signed shim; run `kcap import --opencode` with an empty native cache so the bundled `kcap` downloads and loads `e_sqlite3` under its library-validation exemption.
4. Cut the next beta: the app finds it, downloads, prompts, relaunches on the new version; with no agent running, the daemon restarts itself within the grace window and no skew dialog appears; with an agent running, the dialog appears after the grace and declining leaves the old daemon until it goes idle. Verify the delta package was produced for the second release, that the beta alias moved while the stable alias did not, and that the bundle's `kcap-daemon` hashes to the same value as the npm package's for that version.

## 11. Scope boundaries

- **This PR:** everything in §3–§7, the daemon's `--version` flag (§4.4), the two lifecycle-controller amendments (§7.4), the corrected "Wait for CI" regexp (§6.2), `README.md` gains a "Desktop app" install section (DMG, first-run guard, where updates come from), `docs/CHANGES.md` gains an entry, help text unchanged except `kcap update`'s bundled message.
- **kcap-web:** §5.2. **Infra:** §8.
- **Deferred:** delta hosting is enabled but only verifiable once a second release exists; Intel Macs, Windows and Linux bundles (AI-1657 for Windows); a beta opt-in toggle (AI-1656 settings); LaunchAgent cleanup on app uninstall; trimming or AOT of the app publish; DMG background art.

## 12. Risks

- **Velopack + a space in the executable name** (`Kurrent Capacitor`): Velopack quotes paths, but this is verified by the PR bundle job before any signed release.
- **Hardened runtime under NativeAOT** for `kcap`/`kcap-daemon`: expected to need no JIT entitlements; the post-sign smoke is the proof, and adding an entitlement is a one-line change.
- **Notarization latency**: minutes normally, occasionally longer; `app-macos` is off the npm path so it delays only the app.
- **Certificate expiry couples npm to Apple** (decision 2): a visible, fixable release failure, accepted over shipping two digests for one version.
- **Update on a bundle not owned by the user** (installed by an admin): Velopack escalates via `osascript`; the prompt is Velopack's, not ours.
