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
| `dlssnr_amd_pass1.dll` | the neural runtime, or a supporter build you supply yourself (below) |
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
| `MochizukiNrRuntime.dll`, `dlssnr-amd\` | only when ticked: the third runtime OptiScaler 0.4.0 can drive, mochizuki's Vulkan port (RDNA4, experimental), with its shaders, prewarm list and model (`dlssnr.bin`, about 141 MB) |

The game's OptiScaler panel has an **OptiScaler version** menu, beside the name it loads as, like the
add-on's on the ReShade side: every version the payload list carries, newest first, remembered per
game. The lmxxf files come with 0.2.0 and later only.

A version that carries mochizuki shows **NR runtime** under it: a box, off by default and remembered
per game, that installs mochizuki too. danielblnc stays the NR runtime: `OptiScaler.ini` goes in as
the package has it, and the report says to pick mochizuki under NR runtime in OptiScaler's Neural
tab. Unticked, the next install takes back out what the app put in of it; with no choice on record
(a game added again, another PC) the box follows the folder. It is offered only on an RDNA4 card when
the app can tell which card it is, and says so when it cannot. mochizuki files copied in by hand
before are recorded as yours and stay yours.

No ReShade is installed on this route. Its files are downloaded when the route is installed, not
in the first-run wizard, and the install goes through the same transaction, manifest and backups as
every other route. In game, OptiScaler opens with Insert; the network is switched on under its
Neural tab, and lmxxf is picked under NR runtime there.

## danielblnc's supporter builds: your own files

Some of danielblnc's runtime builds go to his supporters only, 0.5.1 among them. **This app does not
distribute them, now or later, and neither does this repository or its payload.** Somebody who has
one supplies it themselves. While the payload list names one (`user_runtimes` in
`payload/payload.json`), the game's sheet has a block of its own under the route, on every ReShade
route and on OptiScaler where a release runs the build, titled **danielblnc's runtime**, with two
cards and the one that goes in lit: **Public Release** (the version the app downloads and checks) and
**Local Supporter Build** (a newer runtime than the download), and
under them that the supporter build is not distributed. Picking the supporter card takes the copy
already on this machine, or asks right there for your `version.dll` or the `dlssnr_on_amd_setup.exe`
it came in; the DLL is read out of the setup without running it. Under the card the block says which
runtime goes in: "Using 0.5.1 from your file", or why not (no copy on this machine yet, or an add-on or
OptiScaler version that does not run it, with the version that does). The Public Release card goes back.

- The file is checked by SHA-256 against the build the list names. On the ReShade routes, 64-bit and
  the 32-bit bridge, it is then patched in place, each change only over the bytes it expects, and it
  goes in as `dlssnr_amd_pass1.dll` only if the result hashes to the patched SHA-256 the list gives;
  the add-on accepts it by that hash from the version the list names (v0.7.2 for 0.5.1). On the
  OptiScaler route it goes in unpatched, as its runtime passes, from the first OptiScaler release that
  runs it (0.4.5-amd-nr for 0.5.1).
- The checked original is kept in `%AppData%\AmdNrInstaller\runtimes\`, named by its hash, so other
  games do not ask for it again. It is hashed again every time it is installed, and it is never
  uploaded or written into a log or a report. Emptying the download cache leaves it; delete that
  folder to forget it.
- A game folder that already holds danielblnc's own `version.dll` of that build is used as the source
  too. On a ReShade route that `version.dll` is his standalone loader, and beside the add-on it would
  be two drivers on one runtime, so the install moves it to the backup, as the OptiScaler route does,
  and uninstall puts it back, with his weights.
- The choice is remembered per game, and until it is made it follows the folder: an install on the
  build stays on it when updated, and is not called out of date for running a runtime other than the
  download. On a game with no choice made and nothing of his in the folder, a build this machine kept a
  copy of is preselected, and the block says so. An add-on version that does not run the build gets the
  download instead, and the report says so. Until the list carries the build's patch, it goes in on the
  OptiScaler route only.
- danielblnc's standalone runtime is found by its hash under any name in the game folder, not only as
  `version.dll`. Loaded under another name by a chain loader somebody else put there (NBA 2K27 had a
  loader as `version.dll` and his 0.3.0 as `dlssnr_ver.dll`), it hooks the same DXGI and D3D12 calls
  ReShade does, and the two together can keep the game from starting. The ReShade routes then stop in
  the pre-flight, name the file and what loads it, and touch neither: remove his setup with his own
  setup or uninstaller, or use the OptiScaler route, which only warns. When that file is a supporter
  build, it can be picked as your files first. His setup's files with none of his runtime loaded are a
  warning.

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
- **The manifest is a compatibility surface.** It is written byte for byte in the layout the older
  C++ and Rust installers read, and the captured literal in the test suite is the guard. It is read
  as JSON with every field checked (names, hashes, backups inside the backup folder), so the same
  record reformatted by an editor still reads.
- **An install clears what would stop it.** A record that cannot be read, an install cut off halfway,
  or another API of the same route in the folder: the install takes out what is this app's first, the
  way Uninstall does, keeping your settings, and goes in fresh. A record that cannot be read is kept
  beside it as `.unreadable`. A file of ours replaced by hand is backed up and replaced, and a backup
  deleted since only means there is no original to put back.
- **Bitness is read from the PE header, not asked** — and the file it was read from is shown, with
  a way to point at a different one. Detection picks the game's binary out of the folder and a
  folder that keeps a launcher in the root and the game in `Bin64` is picked wrong; the routes that
  match the detected width lead the list, and the rest stay reachable, because a list that hides
  every working route is a dead end exactly when the guess was wrong.

## Around the install

- **Last session**, on a game's page: whether NR ran the last time the game did, read from the logs
  the add-on and the runtimes write beside it, however the game was started. It gives the frames
  processed, the network's time per frame and the runtime, or else what went wrong: a crash (with the
  runtime's own `CRASH:` line), a runtime that did not start and why, ReShade leaving the add-on out,
  or a session where no frame went through the network.
- **What's new**, under the version menus and on the Settings page: the release notes of the add-on,
  OptiScaler or app version picked, shown once after the app updates itself.
- **NR settings**: a game's NR settings out to one file and back in, to keep a tuning or take
  someone else's. The ReShade route carries `amd-nr.ini` and `dlssnr_on_amd.ini`; the OptiScaler route
  carries the fork's sections of `OptiScaler.ini` only, written back key by key, so nobody's frame
  generation or spoofing comes with them. What an import replaces is kept in `backups\settings\` first.
- **The library**: a grid of covers or a list beside the open game's page; filters (installed, with
  an update, not installed, emulators) and three orders (name, recently played, recently added); a
  name and cover art of your own per game; games taken out of the list stay out of the next scan
  until Settings brings them back; and an auto-update switch per game, off until you turn it on.
- **Twenty languages**: English, Português (Brasil), Español, Français, Deutsch, Italiano, Polski,
  Română, Magyar, Hrvatski, Lietuvių, Русский, Українська, Türkçe, हिन्दी, 简体中文, 日本語, 한국어, ไทย
  and العربية (right to left), plus Pirate English for fun.

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
its weights are NVIDIA-derived and, like the others, are not in this repository. The mochizuki
runtime is neural-amd-opti's build of [mochizuki0323/DLSSNR-AMD](https://github.com/mochizuki0323/DLSSNR-AMD)
(MIT), and its licence goes into the game folder with it, as `dlssnr-amd\LICENSE-DLSSNR-AMD.txt`;
its model is NVIDIA-derived too, and is not in this repository either.

The install engine is ported from the Rust installer in
[dlss5-neural-amd](https://github.com/zmodelerlover/dlss5-neural-amd) (MIT).

The shape of the application — a manager with its own content repository, per-component version
pinning and a language file per locale — follows what
[Optiscaler-Client](https://github.com/Optiscaler-Client/Optiscaler-Client) does for OptiScaler.
That project is GPL-3.0 and **none of its code is used here**; this is an independent
implementation of the same idea for a different add-on.

## License

MIT, in `LICENSE`.
