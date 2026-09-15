# AMD-NR ReShade Installer

A manager for [dlss5-neural-amd](https://github.com/zmodelerlover/dlss5-neural-amd) — the ReShade
add-on that runs DLSS 5 neural rendering on Radeon cards.

It does what the terminal installer did, with two things it could not: it **downloads the runtime
and the weights itself**, so nobody has to find a Discord channel first, and it shows the state of
a game folder instead of asking you to know it.

**Status: the install engine is ported and tested; the interface is not written yet.** What works
today is `AmdNr.Core` plus its suite — preflight, install, uninstall, the transactional journal and
the architecture detection, all ported from the Rust installer in the add-on's own repository.

## What it installs

The same files, verified the same way:

| | |
|---|---|
| `dlss5-neural.addon64` | the add-on, from its GitHub release |
| `dlssnr_amd_pass1.dll` | the neural runtime |
| `dlssnr_on_amd_weights.bin` | the weights, 141 MB |

Every one is pinned by SHA-256 and checked after download and again before a byte is copied into a
game folder. The add-on hashes the runtime at load and refuses anything else, because the
integration is fixed offsets into one specific binary — so a mismatch is caught here, in words,
rather than by the add-on failing later with the game already open.

**Neither the runtime nor the weights are in this repository**, and they never will be: the weights
are NVIDIA-derived and the runtime comes from a third-party project with its own distribution terms.

## How an install is kept undoable

Ported verbatim from the Rust engine, because these were paid for the hard way:

- **Pre-flight is a gate, not a panel.** Game still open and holding a file, folder needing
  administrator rights, no room for the weights, ReShade missing or installed twice, and the
  `DisabledAddons=` line ReShade writes into its own ini — nothing is written until those pass.
- **The journal lands before the writes it describes**, through `MoveFileEx` with write-through, so
  an interrupted install is recoverable rather than half-applied.
- **Everything displaced is backed up**, and uninstall puts it back instead of deleting filenames it
  recognises. A file you changed after installing is kept, with a warning.
- **The manifest is a compatibility surface.** It is accepted only when re-encoding reproduces it
  byte for byte, which is what makes hand-editing detectable — and what keeps installs written by
  the older C++ and Rust installers readable. The captured literal in the test suite is the guard.
- **Bitness is detected, never asked**, by reading the PE header; five of the ten
  API-by-architecture combinations do not exist and are never offered.

## Building

```powershell
dotnet test
```

.NET 10 SDK, Windows. The engine is Windows-only on purpose: reparse-point refusal, the
write-through commit and the PE machine check are the substance of it.

Set `AMDNR_TEST_PAYLOAD_DIR` to a folder holding the three real files to include the end-to-end
round trip; without it that one test skips and the other fifty-eight still run.

## Credits

The network itself is **[DLSS-NR-on-AMD](https://github.com/danielblnc/DLSS-NR-on-AMD)** by
**danielblnc** — the runtime and the weights this installs are its work, not reimplemented and not
redistributed here.

The install engine is ported from the Rust installer in
[dlss5-neural-amd](https://github.com/zmodelerlover/dlss5-neural-amd) (MIT).

The shape of the application — a manager with its own content repository, per-component version
pinning and a language file per locale — follows what
[Optiscaler-Client](https://github.com/Optiscaler-Client/Optiscaler-Client) does for OptiScaler.
That project is GPL-3.0 and **none of its code is used here**; this is an independent
implementation of the same idea for a different add-on.

## License

MIT, in `LICENSE`.
