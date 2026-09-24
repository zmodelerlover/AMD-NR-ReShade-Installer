# AMD-NR ReShade Installer

A manager for [dlss5-neural-amd](https://github.com/zmodelerlover/dlss5-neural-amd) — the ReShade
add-on that runs DLSS 5 neural rendering on Radeon cards.

It does what the terminal installer did, with two things it could not: it **downloads the runtime
and the weights itself**, so nobody has to find a Discord channel first, and it shows the state of
a game folder instead of asking you to know it.

One executable. It finds your games across Steam, Epic, GOG, EA, Ubisoft, Battle.net and Xbox — or
takes a folder you keep games in — works out which renderer each one uses and says why, downloads
and checks everything including ReShade itself, installs it, and takes it all back out. **Report a
problem** collects the logs, the folder listing and what ReShade wrote into one zip that goes
nowhere until you hand it over.

Radeon **RDNA3 or RDNA4** with **HIP 7** (`amdhip64_7.dll`, from a current Adrenalin driver). The
first-run wizard checks both, the **This machine** page keeps showing them, and the app fetches the
rest itself.

## What it installs

The same files, verified the same way:

| | |
|---|---|
| `amd-nr.addon64` | the add-on, from its GitHub release |
| `dlssnr_amd_pass1.dll` | the neural runtime |
| `dlssnr_on_amd_weights.bin` | the weights, 141 MB |
| ReShade 6.8.0, full add-on support | under the proxy name the route loads, or the one you pick |
| `AMD_Neural_Feed.fx` | the companion effect, in `reshade-shaders\Shaders\` |
| `amd-nr.addon32`, `amd-nr-host64.exe` | 32-bit games: the add-on inside the game, and the 64-bit process that runs the network for it |
| `dxgi.dll`, `d3d8to9.dll` | 32-bit games: the pinned 32-bit ReShade, and the D3D8-to-D3D9 layer D3D8 games go through |

Every one is pinned by SHA-256 and checked after download and again before a byte is copied into a
game folder. The add-on hashes the runtime at load and refuses anything else, because the
integration is fixed offsets into one specific binary — so a mismatch is caught here, in words,
rather than by the add-on failing later with the game already open.

**Neither the runtime nor the weights are in this repository**, and they never will be: the weights
are NVIDIA-derived and the runtime comes from a third-party project with its own distribution terms.

## D3D12 games: the OptiScaler route

On D3D12 a ReShade add-on finds the game's depth but not its motion vectors, so the network gets
colour and depth and estimates the motion. A D3D12 game that offers DLSS, FSR or XeSS has a better way in: the
[OptiScaler AMD neural rendering build](https://github.com/MatheusFerreiraS/neural-amd-opti), which
takes over the game's upscaler call and runs the same network inside it, with the game's own depth
and motion vectors. For a game detected as D3D12, and for one that runs both D3D11 and D3D12 and
ships an upscaler, that route is the one selected. Every ReShade route stays in the list.

| | |
|---|---|
| `dxgi.dll` or `winmm.dll` | OptiScaler, from the neural-amd-opti release archive |
| `OptiScaler\`, `OptiScaler.ini` | its FidelityFX, XeSS and Agility libraries, and its configuration |
| `dlssnr_amd_pass1-3.dll` | the neural runtime 0.3.1, once per pass |
| `dlssnr_on_amd_weights.bin` | the same weights as the add-on |
| `LmxxfNrRuntime.dll`, `lmxxf-modules\`, `shaders\` | the second runtime OptiScaler 0.2.0 can drive, lmxxf's open-source port (RDNA4) |
| `native-game-tiled-assets\` | its weights, about 590 MB |

The game's OptiScaler panel has an **OptiScaler version** menu, beside the name it loads as, like the
add-on's on the ReShade side: every version the payload list carries, newest first, remembered per
game. The lmxxf files come with 0.2.0 and later only.

No ReShade is installed on this route. Its files are downloaded when the route is installed, not
in the first-run wizard, and the install goes through the same transaction, manifest and backups as
every other route. In game, OptiScaler opens with Insert; the network is switched on under its
Neural tab, and lmxxf is picked under NR runtime there.

## How an install is kept undoable

Ported verbatim from the Rust engine, because these were paid for the hard way:

- **Pre-flight is a gate, not a panel.** Game still open and holding a file, folder needing
  administrator rights, no room for the weights, ReShade missing, and the `DisabledAddons=` line
  ReShade writes into its own ini — nothing is written until those pass.
- **A second ReShade is refused outright**, under any proxy name, not just the one this route
  loads. Two of them in one process and the game does not start at all: no window, and nothing
  written to any log to say why. It is checked over every name because the file that collides is
  by definition the one the route was not looking for.
- **The journal lands before the writes it describes**, through `MoveFileEx` with write-through, so
  an interrupted install is recoverable rather than half-applied.
- **Everything displaced is backed up**, and uninstall puts it back. Uninstall takes out every file
  under a name only this project uses, whether the install recorded it or it was copied in by hand,
  and a ReShade only when it is a build this app pins; anybody else's file stays. The settings files
  left at the end are named, and you choose whether they go too.
- **The manifest is a compatibility surface.** It is accepted only when re-encoding reproduces it
  byte for byte, which is what makes hand-editing detectable — and what keeps installs written by
  the older C++ and Rust installers readable. The captured literal in the test suite is the guard.
- **Bitness is read from the PE header, not asked** — and the file it was read from is shown, with
  a way to point at a different one. Detection picks the game's binary out of the folder and a
  folder that keeps a launcher in the root and the game in `Bin64` is picked wrong; the routes that
  match the detected width lead the list, and the rest stay reachable, because a list that hides
  every working route is a dead end exactly when the guess was wrong.

## When something goes wrong

- **A download that fails says why**: the address never answered, went quiet halfway, the disk is
  full, or an antivirus took the file. A file the payload list gives a mirror for is fetched from
  the mirror next. When the failure is the network, **Clear DNS and try again** empties Windows' DNS
  cache — no administrator rights needed — and retries.
- **Download logs**, on the This machine page, saves one zip with a log of every download: each
  address tried, its name lookup, the proxy, what it answered and how many bytes arrived. It also
  checks every address again on the spot. Nothing is sent anywhere.
- **Import files…** takes the files from a folder you downloaded them into yourself — found by name,
  or by size under any name, and kept only if they hash to their pin.
- **Updates** install in place: **Download update** fetches the new executable and replaces this one
  only if it matches the SHA-256 the release publishes. A release that does not publish its sums
  offers **Download from GitHub** instead.
- A game on a drive that is unplugged stays in the list and comes back with the drive. A
  `games.json` that cannot be read is set aside, not overwritten. A second copy of the app brings
  the first one forward instead of opening beside it.

## Building

```powershell
dotnet test
powershell -ExecutionPolicy Bypass -File tools\gate.ps1 -Ui
```

.NET 10 SDK, Windows. The engine is Windows-only on purpose: reparse-point refusal, the
write-through commit and the PE machine check are the substance of it.

`tools\gate.ps1` is what every commit passes: build, tests, a 500-line limit per file, and with
`-Ui` a headless render of every page in English and Portuguese (`tools\uishot`) plus the game sheet
driven through install, route switching, uninstall, update and a failed download. It runs against a
throwaway `AMDNR_HOME` and never opens a window.

Set `AMDNR_TEST_PAYLOAD_DIR` to a folder holding the three real files to include the end-to-end
round trip; without it that one test skips and the rest of the suite still runs.

## Credits

The network itself is **[DLSS-NR-on-AMD](https://github.com/danielblnc/DLSS-NR-on-AMD)** by
**danielblnc** — the runtime and the weights this installs are its work, not reimplemented and not
redistributed here.

The OptiScaler route installs [neural-amd-opti](https://github.com/MatheusFerreiraS/neural-amd-opti),
an OptiScaler fork (GPL-3.0) that carries the bridge into the same runtime. Its release archive is
downloaded as published; none of its code is part of this app. The lmxxf runtime it ships is
[lmxxf/dlss5-on-amd-9070xt-porting](https://github.com/lmxxf/dlss5-on-amd-9070xt-porting) (MIT);
its weights are NVIDIA-derived and, like the others, are not in this repository.

The install engine is ported from the Rust installer in
[dlss5-neural-amd](https://github.com/zmodelerlover/dlss5-neural-amd) (MIT).

The shape of the application — a manager with its own content repository, per-component version
pinning and a language file per locale — follows what
[Optiscaler-Client](https://github.com/Optiscaler-Client/Optiscaler-Client) does for OptiScaler.
That project is GPL-3.0 and **none of its code is used here**; this is an independent
implementation of the same idea for a different add-on.

## License

MIT, in `LICENSE`.
