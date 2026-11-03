# Hintsmith

Normal font hinting for Avalonia 11 and 12, with runtime control.
Hintsmith uses AsmResolver to rewrite `Avalonia.Skia.dll` at build time. The
default is a global `Normal` override; applications can change it or restore
Avalonia's original behavior through a static API. No Avalonia source changes
are required.

## Installation

Add a regular package reference to the **final application project**:

```xml
<PackageReference Include="Hintsmith" Version="0.3.0" />
```

Keep your existing project SDK and Avalonia references. NuGet automatically
imports `build/Hintsmith.props` and `build/Hintsmith.targets`; the same package
provides `Hintsmith.dll` as a normal compile/runtime asset. There is no custom
SDK, restore-time package injection, or `deps.json` patching.

Build targets intentionally do not flow through transitive dependencies. Each
final application that should enable hinting needs a direct reference. Do not
exclude the package's `build`, `compile`, or `runtime` assets. Central Package
Management works through the usual `PackageVersion` entry.

To use an unpublished local build:

```powershell
dotnet pack src/Hintsmith -c Release -o artifacts/feed
```

Add the absolute path to `artifacts/feed` to the application's `NuGet.Config`.

**Migrating from 0.2.x:** remove `Hintsmith/...` from the project's `Sdk`
attribute and any `msbuild-sdks` entry for Hintsmith in `global.json`, then add
the package reference above. Remove `HintsmithVersion`; the package reference
now selects both the runtime and build task version.

## Runtime API

```csharp
using Hintsmith;

// Normal is already the default.
Hinting.SetHinting(HintingMode.Normal);
Hinting.SetHinting(HintingMode.None);
Hinting.SetHinting(HintingMode.Slight);
Hinting.SetHinting(HintingMode.Full);

// Restores Full on Avalonia 11 and the original TextHintingMode mapping on 12.
Hinting.RestoreAvaloniaDefaults();
var current = Hinting.Current;
```

Changes are thread-safe and take effect on the next actual text draw. They do
not schedule UI updates: request a redraw on the UI thread, and refresh retained
composition or bitmap caches as necessary. Recreate the relevant text layout
if glyph bounds must also be updated. Existing layouts are not recalculated.
Prefer setting the initial policy before creating the UI.

## Build configuration

| Property | Default | Purpose |
| --- | --- | --- |
| `HintsmithEnabled` | `true` | Set to `false` to disable weaving. Runtime APIs still compile but do not affect an unmodified Avalonia assembly. |
| `HintsmithSymbols` | `Preserve` | Rewrite available Portable PDBs, or explicitly select `Strip`. |
| `HintsmithPdbPath` | Empty | An external Portable PDB matching the original DLL. Otherwise, adjacent files and build items are searched. |
| `HintsmithTaskAssembly` | Bundled task | Override the task assembly path for local development. |

The build host requires .NET SDK 10 or newer. The application may target earlier
modern .NET frameworks. The runtime library targets `netstandard2.0` and has no
Avalonia, SkiaSharp, or AsmResolver dependency. The package also contains a
`net472` MSBuild task; verification uses `dotnet` MSBuild.

## How it works

Hintsmith borrows Switchyard's MSBuild item replacement and AsmResolver approach,
but does not need Switchyard's project SDK: it only processes assets that NuGet
has already restored. Build integration lives in `build/`, not `buildTransitive/`,
so applications explicitly opt in.

Original NuGet files remain read-only. Woven assemblies are cached under
`obj/<configuration>/<TFM>/<RID>/hintsmith/<content hash>/`. The hash includes the
input DLL, runtime library, task assembly, symbol mode, and external PDB bytes.
Unchanged builds reuse their output. A process-independent mutex, staging
directory, and PDB-before-DLL commit order protect cache writes. Missing generated
PDBs are rebuilt. Switching to stripped or embedded symbols removes stale sidecars
from build and publish output.

The `HookV1` ABI uses only BCL types and numeric arguments. Each text blob cache
lookup captures the global policy once, encodes it in the cache key, and passes
it through to font creation. Cache keys and font settings therefore agree even
if another thread changes the policy during the draw. There is no runtime
reflection, dynamic proxy, or JIT detour. `HookV1` is the weaving ABI, not an
application-facing API.

| Avalonia implementation | Weaving strategy |
| --- | --- |
| 11.0 dictionary with a shared closure font | Encode the edging cache key, intercept the closure's hinting setter, and decode edging before use. |
| 11.1/11.2 dictionary with `CreateFont` | Encode the edging key and separate the policy from the original edging in `CreateFont`. |
| 11.3 array cache | Apply the same encoding and expand the array. Each mode/edging combination has its own entry, released by the original disposal path. |
| 12 `TextOptions` cache | Carry the effective policy in reserved byte values within the method; intercept `Hinting` and `ForceAutoHinting` setters. |

On Avalonia 12, `Slight` retains forced autohinting; other modes do not force it.
Avalonia 11 keeps its existing autohinting behavior. Edging, subpixel positioning,
baseline snapping, and layout algorithms retain their original behavior.

Builds replace `ReferenceCopyLocalPaths`. Publishing also intercepts
`_ResolvedCopyLocalPublishAssets` and `ResolvedFileToPublish`, covering NativeAOT's
early input selection as well as trimming, ReadyToRun, and single-file bundling.
Only `Hintsmith.dll` is added to the application's runtime dependencies; the build
task and its dependencies stay in the package's `tasks/` directory.

## Portable PDBs

`HintsmithSymbols=Preserve` supports external and embedded Portable PDBs. External
symbols are found beside the DLL or in MSBuild file items; `HintsmithPdbPath` can
point to a matching file extracted from a symbol package. Embedded symbols take
precedence and remain embedded. Hintsmith does not download `.snupkg` files or
contact symbol servers. Assemblies without available symbols are still woven,
and their stale symbol references are removed.

PDB identities and available checksums are validated before rewriting. Mismatched,
damaged, non-portable, or unsupported debug data causes Preserve mode to fail
instead of producing misleading source mappings. To opt out explicitly:

```xml
<PropertyGroup>
  <HintsmithSymbols>Strip</HintsmithSymbols>
</PropertyGroup>
```

Original instruction identities are recorded before weaving. After final branch
and macro optimization, the symbol writer rebuilds:

- Sequence points and local scope boundaries, with hidden points for injected hooks.
- Local signature tokens and type-system table row counts from the emitted DLL.
- State-machine yield/resume/catch offsets and hoisted local scopes.
- Document paths and hashes, local names/slots/constants, imports and aliases,
  SourceLink, and EmbeddedSource.
- PDB identity, CodeView data, and the SHA-256 checksum, preserving unrelated PE
  debug directory entries.

AsmResolver modifies IL; `System.Reflection.Metadata` rebuilds Portable PDBs.
Edit-and-Continue data is removed from affected methods. **Hot Reload of the
woven Avalonia.Skia assembly is not supported.** Symbols provide source mappings
for debugging and exception line numbers, but future compiler extensions may
require additional support. Traditional Windows PDBs are unsupported.

## Repository and verification

```text
src/Hintsmith/          Runtime API and NuGet package, including build/ imports
src/Hintsmith.Tasks/    MSBuild task, IL weaving, and Portable PDB rewriting
tests/                 Contract tests, runtime probes, and symbol fixtures
eng/                   Verification scripts
.github/workflows/     Manual NuGet publishing workflow
```

The weaver recognizes implementation shapes rather than hard-coding each patch
version. Unknown signatures, cache layouts, missing hook locations, and repeated
weaving fail the build. Future Avalonia internals may change; sampled versions
are not a guarantee for every 11/12 release.

Verified on Windows x64 with .NET 10:

| Avalonia | Assembly TFMs checked | Runtime and native drawing |
| --- | --- | --- |
| 11.0.0 | net6.0, netstandard2.0 | Passed |
| 11.1.0 | net6.0, net8.0, netstandard2.0 | Passed |
| 11.2.0 | net6.0, net8.0, netstandard2.0 | Passed |
| 11.3.0 | net6.0, net8.0, netstandard2.0 | Passed |
| 12.0.0 | net8.0, net10.0 | Passed |
| 12.1.4 | net8.0, net10.0 | Passed |
| Adjacent source build 12.2.999 | net10.0 | Passed |

Tests cover all four modes, default restoration, cache switching on the same
glyph run, preserved edging/subpixel settings, native `SKCanvas` raster drawing,
input integrity, assembly identity, and rejection of unsupported or repeated
weaving. IL checks cover every listed TFM; runtime probes use package assets
selected by .NET 10.

Symbol tests cover external/embedded PDBs, multiple documents, scopes, constants,
imports, SourceLink, corrupted inputs, Strip, absent symbols, cache invalidation,
missing sidecar recovery, and publishing. They also throw inside a woven method
and verify the original source line reported by .NET.

Package integration tests install a uniquely versioned local package through
`PackageReference`, then exercise restore, incremental builds, disabling and
reenabling weaving, and `publish --no-build`. The full publish matrix covers
single-file, ReadyToRun, trimming, and NativeAOT on Avalonia 12.0.0. The probe uses
reflection and roots Avalonia assemblies; its trim/AOT warnings and successful
runs do not prove that every application is trim-safe.

```powershell
# Compatibility, symbols, installed package integration, and ordinary publishing.
pwsh ./eng/verify.ps1

# Full Windows publish matrix; requires the native C++ toolchain for NativeAOT.
pwsh ./eng/verify.ps1 -FullPublish

# Optionally include a local Avalonia source build.
pwsh ./eng/verify.ps1 -CurrentAvaloniaRoot ../Avalonia/src/Skia/Avalonia.Skia/bin/Release/net10.0
```

## Publishing

The [Publish NuGet Package workflow](.github/workflows/publish-nuget.yml) follows
the manual publishing structure used by Material3Expressive.Avalonia. It verifies
the full Windows matrix, packs a Release build using `Version` from
`Directory.Build.props`, uploads the package, and publishes that artifact from a
separate job using `NuGet/login@v1`. Only the publishing job can request an OIDC
token. No long-lived NuGet API key is required.

Configure a [NuGet trusted publishing policy](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
with these values:

| Setting | Value |
| --- | --- |
| Repository owner / repository | The GitHub owner and repository hosting Hintsmith |
| Workflow file | `publish-nuget.yml` (filename only) |
| Environment | Leave empty; the workflow does not use a GitHub environment |
| Package scope | `Hintsmith` |

Set the desired package version, push the source, and manually run the workflow
on that ref. The `nuget_user` input defaults to `Summpot`; change it if the
policy belongs to a different account.
It is a NuGet.org profile name, not an email address. Existing versions are skipped.
Verification packages are written to a separate directory and never uploaded for
publication.

## Limitations

- Only Avalonia.Skia glyph run rendering is modified. Other backends, custom Skia
  font drawing, and native controls are unaffected.
- Visible results depend on Skia's platform font backend, the font, size, and
  scale. `Normal` is not always visibly different from `Full`.
- Avalonia's public-key identity is retained for friend assembly access, but the
  invalid strong-name signature flag is cleared. .NET Framework/GAC deployments
  requiring strong-name verification are unsupported.
- Source debugging requires matching Portable PDBs and Preserve mode.
- Separate runtime copies in multiple `AssemblyLoadContext` instances have
  independent policies. Combining multiple weavers on Avalonia.Skia is untested.
- Linux, macOS, mobile, browser, and every Avalonia patch release have not been
  individually runtime-tested.

References: [Skia hinting](https://api.skia.org/SkFontTypes_8h.html),
[AsmResolver CIL editing](https://docs.washi.dev/asmresolver/guides/dotnet/managed-method-bodies.html),
[NuGet build imports](https://learn.microsoft.com/en-us/nuget/concepts/msbuild-props-and-targets).
