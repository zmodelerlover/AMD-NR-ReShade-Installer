# The payload manifest

`payload.json` is what the app reads to know **what to download, from where, and what it has to
hash to**. It replaces the constants the Rust installer compiled in.

It is published at the root of the content repository (`AMD-NR-Extras`) and read from
`raw.githubusercontent.com`, which is CDN-backed and — unlike the REST API — not rate-limited per
address. The files themselves come from release asset URLs, which support `Range`, so a 141 MB
download resumes instead of restarting. **Nothing in the download path spends a GitHub API call**;
only the app's own update check does.

## The four components

| | what it is | published by |
|---|---|---|
| `addon` | `dlss5-neural.addon64` | the add-on's own release, so the app never mirrors it |
| `runtime` | `dlssnr_amd_pass1.dll` + `dlssnr_on_amd_weights.bin` | the content repository |
| `bridge` | the 32-bit pair and `payload.sha256` that pins it | the add-on's release |
| `x86-extras` | pinned ReShade 6.8.0.2156 x86 and d3d8to9 v1.15.1 | the content repository |

`path` puts a file somewhere other than the component's root. The bridge and x86-extras use it to
rebuild the `files\` layout the 32-bit installer reads, because GitHub flattens asset names.

## Every hash here was verified against two sources

The four in `runtime` and `x86-extras` match the constants in the add-on's own `installer/src/engine.rs`
(`RUNTIME_SHA`, `WEIGHTS_SHA`, `RESHADE_SHA`, `D3D8TO9_SHA`). The `addon` and `bridge` ones match
`SHA256SUMS.txt` and `payload.sha256` published in the v0.5.0 release.

## Publishing a new payload

1. Put the files on the content repository as release assets under a new tag.
2. Update `version`, `size`, `sha256` and `tag` here.
3. Push to `main`. The app picks it up on the next launch; the cache is keyed by version, so an old
   build stays usable until its component is deleted.

Never change an existing tag's assets in place. The cache trusts the hash, so a changed file under
an unchanged version reads as corruption to everyone who already has it.
