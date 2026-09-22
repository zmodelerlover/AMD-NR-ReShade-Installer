# The payload manifest

`payload.json` is what the app reads to know **what to download, from where, and what it has to
hash to**. It replaces the constants the Rust installer compiled in.

## Where it all lives

The binaries are **not on GitHub**, by decision. They are published in a **Hugging Face dataset
repository**, because it is the one free host that gives an address that is both *stable* and
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

## The five components

| | what it is | where it comes from |
|---|---|---|
| `addon` | `amd-nr.addon64` | the dataset repo |
| `runtime` | `dlssnr_amd_pass1.dll` + `dlssnr_on_amd_weights.bin` | the dataset repo |
| `reshade` | the official 6.8.0 Addon setup, with `ReShade64.dll`/`ReShade32.dll` extracted from it | reshade.me |
| `bridge` | the 32-bit pair and the `payload.sha256` that pins it | the dataset repo |
| `x86-extras` | pinned ReShade 6.8.0.2156 x86 and d3d8to9 v1.15.1 | the dataset repo |

`path` puts a file somewhere other than the component's root. The bridge and x86-extras use it to
rebuild the `files\` layout the 32-bit installer reads.

`owner`/`repo`/`tag` are still understood and are tried last, after `url` and every `mirror`, so a
release asset remains a valid place to put one of these without any code change.

## Every hash here was verified against two sources

The four in `runtime` and `x86-extras` match the constants in the add-on's own `installer/src/engine.rs`
(`RUNTIME_SHA`, `WEIGHTS_SHA`, `RESHADE_SHA`, `D3D8TO9_SHA`). The `addon` and `bridge` ones match
`SHA256SUMS.txt` and `payload.sha256` published in the v0.5.0 release. All eight were then
downloaded back from their published addresses and re-hashed.

## Publishing a new payload

`.\tools\publish-payload.ps1` does all of it; see **Publishing** above. By hand it is: upload the
file under its own name, update `size`, `sha256` and the component `version` here, upload this file,
and download both back to check them.

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

v0.5.0 published the bridge pair only inside `dlss5-neural-amd-v0.5.0.zip`, so it is offered for a
64-bit route from GitHub and for a 32-bit one from this manifest, which pins those two files itself.
From v0.5.1 the four assets go up loose and a new version needs no change here at all.

Cutting a release, then, ends with:

```powershell
gh release upload v0.5.1 `
  build\amd-nr.addon64 `
  build-x86bridge\amd-nr.addon32 `
  build-x86bridge\amd-nr-host64.exe `
  release\payload.sha256 `
  SHA256SUMS.txt
```

The runtime, the weights, ReShade and `d3d8to9` are not versioned with the add-on and are not in a
release: whichever version is chosen, those still come from this manifest.

## Where it is published now

`zmodelerlover/amd-nr` — https://huggingface.co/datasets/zmodelerlover/amd-nr

The eight files, plus `payload.json` and `api-db.json`. Every one was downloaded back from its
published address and re-hashed. The old catbox.moe addresses are kept as `mirrors`, and the
fallback was exercised: a dead primary address falls through to the mirror and still passes its
hash, and a mirror serving other bytes is refused.
