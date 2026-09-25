# The payload manifest

`payload.json` is what the app reads to know **what to download, from where, and what it has to
hash to**. It replaces the constants the Rust installer compiled in.

## Where it all lives

The binaries are published **first** in a **Hugging Face dataset repository**, because it is the one free host that gives an address that is both *stable* and
*overwritable*:

```
https://huggingface.co/datasets/<user>/<repo>/resolve/main/<filename>
```

Uploading a new build of a file keeps that address. That is the whole reason for the choice: the
payload changes often, and an immutable host would mean a new `payload.json` address, which means a
new `config.json`, which means **a new build of the app for every payload swap**. Here nothing moves.

`config.json` beside the executable names two of those addresses — `Payload.ManifestUrl` for
`payload.json` and `Payload.ApiDbUrl` for `api-db.json` — and neither ever has to change again.

Reading is anonymous: the repo is public, so the app sends no token. Only publishing needs a login.
Hugging Face is built for model weights, so the 141 MB of weights are unremarkable there, and the
CDN serves `Range`, so an interrupted download resumes.

ReShade is the exception in the other direction: it is fetched from `reshade.me` exactly as a person
would download it, and never mirrored.

### Publishing

```powershell
hf auth login                                          # once, with a write token
.\tools\publish-payload.ps1 -Repo <user>/<repo> -From <folder with the new files>
```

The script uploads what changed, recomputes every size and SHA-256 from the local bytes, bumps the
version of any component whose content changed, rewrites and uploads `payload.json`, and then
**downloads every published file back and checks it against its own hash**. Nothing is believed
because it was uploaded.

`-Verify` alone re-checks what is published without changing anything.

The version bump matters: the cache is keyed by `<component>/<version>`, so new bytes under an
unchanged version read as a corrupt cache to everyone who already has the old ones. The script does
it automatically; `-SetVersion runtime=0.2.18` overrides it.

### Mirrors

`mirrors` is an array of further addresses per file, tried in order when the first cannot be
reached. Every one of them is checked against the same SHA-256, so a mirror can be anywhere and is
never trusted further than its hash. The previous catbox.moe addresses are kept there as a free
fallback — but only for files whose bytes have not changed since they were uploaded there. A
mirror serving an older build fails the hash and the person is told *"the published file changed"*,
which is the wrong sentence and sends them looking for the wrong problem. So a file that is
re-cut loses its mirror line until a matching copy is uploaded: that is why `dlssnr_amd_pass1.dll`
has none as of the v0.3.0 runtime, while the weights, which did not change, keep theirs.

`owner`/`repo`/`tag` are still understood and are tried last, after `url` and every mirror, so a
GitHub release asset remains a valid place to put one of these without any code change.

## The components

| | what it is | where it comes from |
|---|---|---|
| `addon` | `amd-nr.addon64` | the dataset repo |
| `runtime` | `dlssnr_amd_pass1.dll` + `dlssnr_on_amd_weights.bin` | the dataset repo |
| `reshade` | the official 6.8.0 Addon setup, with `ReShade64.dll`/`ReShade32.dll` extracted from it | reshade.me |
| `bridge` | the 32-bit pair and the `payload.sha256` that pins it | the dataset repo |
| `x86-extras` | pinned ReShade 6.8.0.2156 x86 and d3d8to9 v1.15.1 | the dataset repo |
| `shader` | `AMD_Neural_Feed.fx`, the companion effect | the dataset repo |
| `optiscaler` | the neural-amd-opti release archive, with every file taken out of it pinned | its GitHub release |
| `opti-runtime` | the runtime build OptiScaler drives | the dataset repo |
| `lmxxf-weights` | `native-game-tiled-assets.zip`, the lmxxf runtime's weights (0.2.0 and later) | the dataset repo |
| `mochizuki` | `mochizuki-<version>.zip`: `MochizukiNrRuntime.dll`, its shaders, its prewarm list and DLSSNR-AMD's licence (`dlssnr-amd/LICENSE-DLSSNR-AMD.txt`), every file pinned (0.4.0 and later) | its `url` |
| `mochizuki-model` | `dlssnr.bin`, the mochizuki runtime's model, one version per model | its `url` |

Versions of OptiScaler past the one in `components` live under a top-level `releases` key, with
the lmxxf weights (`lmxxf-weights`) and the mochizuki runtime inside the version that needs them.
Older apps ignore that key. The add-on, bridge and shader files also carry the v0.6.6 GitHub release
assets as `mirrors`.

### mochizuki: paths two levels deep for files three levels down

The runtime reads `dlssnr-amd\shaders\runtime\x.spv`, and every app already published refuses a
whole manifest that has a path deeper than two levels -- inside `releases` too, which v0.5.x reads. So
the payload spells the folders under `dlssnr-amd` with dots: `dlssnr-amd.shaders.runtime/x.spv` in
the archive and in the cache, `dlssnr-amd\shaders\runtime\x.spv` in the game
(`Work.MochizukiDestination`). The model needs no spelling: `dlssnr-amd/dlssnr.bin`.

Both components are installed only when somebody ticks mochizuki in the sheet, and they are
`OnDemand`: never in the first-run wizard or in Download all. v0.5.x never downloads them, since it
does not know their names. An install with the box unticked takes out, in the same transaction,
what an earlier install put in of them (`Transaction.PlanRetire`).

### A version being prepared: placeholder pins

A pin of 64 zeros (`PayloadManifest.PlaceholderSha`) stands for bytes that do not exist yet. A release
with one anywhere in it is **not offered** by v0.6.0 and later, so `payload.json` can carry the next
OptiScaler with its layout written out while its archive is still being built.
`tools/publish-payload.ps1` refuses to publish a manifest with a placeholder, and
`tools/check-release.ps1` refuses a build that carries one: an older app does not know the rule and
would offer that version and fail it.

The pins come from the files themselves:

```powershell
# the OptiScaler archive, downloaded back from its GitHub release
.\tools\pin-optiscaler.ps1 -Zip <OptiScaler-X.Y.Z-amd-nr.zip> -Published yyyy-MM-dd
# the mochizuki files, laid out as the game gets them; builds mochizuki-X.Y.Z-amd-nr.zip too
.\tools\pin-mochizuki.ps1 -From <folder with MochizukiNrRuntime.dll and dlssnr-amd\> -License <DLSSNR-AMD's LICENSE> -Version X.Y.Z-amd-nr
```

`pin-optiscaler.ps1` checks the archive's own `SHA256SUMS.txt` and refuses a file name v0.5.0 and
v0.5.1 would refuse to install. `pin-mochizuki.ps1` refuses what the runtime writes while a game runs
(`pipeline.cache`, `*.tmp`, logs), checks the prewarm list was made for these shaders, puts
DLSSNR-AMD's MIT notice in the archive beside the shaders (never among them: the prewarm list is
checked against every file in `shaders\`), and builds the same archive from the same files on any
machine.

`path` puts a file somewhere other than the component's root. The bridge and x86-extras use it to
rebuild the `files\` layout the 32-bit installer reads.

`owner`/`repo`/`tag` are still understood and are tried last, after `url` and every `mirror`, so a
release asset remains a valid place to put one of these without any code change.

## Every hash here was verified against two sources

The four in `runtime` and `x86-extras` match the constants this app's engine pins
(`Engine.RuntimeSha`, `WeightsSha`, `ReShadeSha`, `D3d8To9Sha`), and the runtime also matches the
add-on's own pin (`kRuntimeSha256`). The `addon` and `bridge` ones match
`SHA256SUMS.txt` and `payload.sha256` published in the v0.5.0 release. All eight were then
downloaded back from their published addresses and re-hashed.

## Publishing a new payload

`.\tools\publish-payload.ps1` does all of it; see **Publishing** above. By hand it is: upload the
file under its own name, update `size`, `sha256` and the component `version` here, upload this file,
and download both back to check them. A file whose address already names a folder is uploaded back
to that same path.

Never change what an existing `version` points at. The cache is keyed by version and trusts the
hash, so different bytes under an unchanged version read as corruption to everyone who already has
them. The script bumps the version for you whenever a component's content changes.

## The version menu, and what a release has to publish for it

The sheet offers a menu of add-on versions. It is read from the **GitHub releases of
`zmodelerlover/dlss5-neural-amd`** -- one call to the REST API per launch, cached in
`%AppData%\AmdNrInstaller\cache\releases.json`, and nothing else in the download path ever touches
that API. The files still come from release asset addresses.

A release is offered only when it publishes what pins it and what the route installs:

| Asset | Needed for |
|---|---|
| `SHA256SUMS.txt` | every release. Without it nothing pins the assets and the version is left out entirely |
| `amd-nr.addon64` | the 64-bit routes |
| `amd-nr.addon32`, `amd-nr-host64.exe`, `payload.sha256` | the 32-bit bridge routes |

All of them **loose, beside the archive** -- not only inside it. That is what lets the app know what
a version contains, and pin each file's size and hash, without downloading anything first: the size
comes from the API's record of the asset and the hash from `SHA256SUMS.txt`.

v0.5.0 published the bridge pair only inside `dlss5-neural-amd-v0.5.0.zip`, so the menu starts at
v0.5.1 (`AddonReleases.Earliest`), the first release with the four assets loose. From there a new
version needs no change here at all.

Cutting a release, then, ends with the add-on's own script, which gathers the loose files and writes
the sums that pin them:

```powershell
.\tools\release-assets.ps1
gh release upload <tag> (Get-Content release\upload.txt)
```

It does not gather `AMD_Neural_Feed.fx`; v0.6.6 carried it because it was added by hand.

The runtime, the weights, ReShade and `d3d8to9` are not versioned with the add-on and are not in a
release: whichever version is chosen, those still come from this manifest.

## Where it is published now

`zmodelerlover/amd-nr` — https://huggingface.co/datasets/zmodelerlover/amd-nr

The eight files, plus `payload.json` and `api-db.json`. Every one was downloaded back from its
published address and re-hashed. The old catbox.moe addresses are kept as `mirrors`, and the
fallback was exercised: a dead primary address falls through to the mirror and still passes its
hash, and a mirror serving other bytes is refused.
