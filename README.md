# FFX Randomizer

A randomizer for **FINAL FANTASY X** in the Steam release of *FINAL FANTASY X/X-2 HD Remaster*, with a one-click Windows app. It is a C# port of [FFXRando](https://github.com/nyterage/FFXRando) by nyterage.

![A tour of the app: picking enemy and item options, then the Presets and History tabs](docs/images/tour.gif)

## Getting started

1. Download `FFX-Randomizer-vX.Y.Z.exe` from [Releases](../../releases). It's a single self-contained file with nothing else to install.
2. Close the game, then run the exe. Windows SmartScreen may warn because the app isn't signed; click **More info → Run anyway**.
3. Pick your options and click **Randomize**.
4. Launch the game and start a **New Game**. Saves made before randomizing keep their old data.

The app finds your Steam copy automatically. If it can't, it asks you for the game folder (the one that contains `FFX.exe`).

| Pick your options | Randomize, then play |
|---|---|
| ![The Enemies tab with item drops, steals and elemental affinities switched on](docs/images/enemies.png) | ![The finished screen after randomizing, with the seed, what changed and a button to launch the game](docs/images/finished.png) |

The **Tidus starts with 9999 HP** option makes it easy to confirm the randomizer is working: it shows in the very first fight in Zanarkand.

![The first battle in Zanarkand, with Tidus at 9999 HP](docs/images/in-game-tidus-9999.png)

### Undoing it

Click **Restore original** to put the game back exactly as Steam installed it. A Steam update or **Verify integrity of game files** also removes the randomization; just randomize again afterwards.

## What it can randomize

| Area | Status |
|---|---|
| Tidus starts with 9999 HP (quick check that randomizing works) | ✅ |
| Enemies: item drops, steals, bribes, gil and AP | ✅ |
| Enemies: elemental affinities | ✅ |
| Items & Shops: treasure and field items (optionally key items, Celestial items, Brotherhood) | ✅ |
| Items & Shops: item shops, prices, always-sell-spheres | ✅ |
| Items & Shops: customization items | ✅ |
| Enemy equipment drops, gear abilities, weapon stats, gear shops | Coming soon |
| Party stats, starting Overdrive mode | Coming soon |
| Aeon stats and power-up items | Coming soon |
| Sphere Grid | Coming soon |
| Random encounters, enemy stats | Coming soon |

Options that aren't ported yet appear in the app marked **Coming soon**.

The Withered Bouquet and Flint chests in Baaj Temple never move, because the story needs both to light the fire.

### Presets, seeds and History

- **Presets** save a set of options (not a seed), so every run from a preset is a new game. Built-in presets are read-only; duplicate one to make your own. Change options and click **Update preset** to save changes to the loaded preset.
- **Set seed** makes a specific game. The same seed, options and app version always produce the same game.
- **History** records every run with its seed and options. **Use settings** reloads the options with a new seed, and **Replay this seed** recreates the exact game.
- The **spoiler log** is JSON. It lists every change: each enemy's new drops, steals, bribe and elements; every chest; and every shop's stock.

| Presets | History |
|---|---|
| ![The Presets tab with your presets and the built-in ones](docs/images/presets.png) | ![The History tab listing past runs with their seeds, and buttons to reuse the settings or replay the seed](docs/images/history.png) |

![The Items & Shops tab with treasure and item shops switched on](docs/images/items.png)

## How it works

The 2026 Steam update to the HD Remaster isn't supported yet by the usual mod loaders (Fahrenheit, External File Loader). This app instead writes the randomized files straight into `data/FFX_Data.vbf`:

- New file data is appended to the end of the archive and the archive header is rewritten to point at it. The original data is never overwritten.
- Before the first change, the original header is backed up to `%LOCALAPPDATA%\FFX Randomizer\backup`. Restoring writes that header back and trims the archive to its original size, which returns it to the original byte for byte.
- Every run is checked before anything is written: valid item IDs, quantities in range, only the intended bytes changed, and the spoiler matching the files. After writing, every changed file is read back and compared.

Settings, presets, history, spoiler logs and the backup live in `%LOCALAPPDATA%\FFX Randomizer`.

## Building from source

Requires the .NET 10 SDK on Windows.

```
cd app
dotnet build FfxRandomizer.slnx
dotnet test FfxRandomizer.Tests          # tests that need the game run against your Steam install, read-only
dotnet publish FfxRandomizer.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Projects in `app/`:

- `FfxRandomizer.Core`: VBF reading and patching, game data, randomization features, presets and history.
- `FfxRandomizer.App`: the WPF app.
- `FfxRandomizer.Cli` (`ffxr`): developer commands, listed below.
- `FfxRandomizer.Tests`: unit tests, plus bulk-seed tests against the real game data.

### Developer commands

```
ffxr status                               # where the game is and whether it's randomized
ffxr apply [--seed X] <option-id>...      # randomize without the app, e.g. ffxr apply rewards drops steals
ffxr restore
ffxr crosscheck [--seeds N] [--sabotage] <option-id>...
```

`crosscheck` randomizes in memory only and never touches the game. It decodes the output with [FFXDataParser](https://github.com/Karifean/FFXDataParser), an independent parser, and compares every enemy, chest and shop with the spoiler. `--sabotage` deliberately corrupts a few values to show the check catches them. It needs Java, a local build of FFXDataParser (`FFXDATAPARSER_DIR`, default `C:\Github\Personal\FFXDataParser`), and an extracted `ffx_ps2` folder (`FFXR_VANILLA_ROOT`). `tools/ffxdataparser.ps1` runs the parser on its own.

The original C++ FFXRando source is kept in `src/` as the reference for porting.

## Credits

- [FFXRando](https://github.com/nyterage/FFXRando) by nyterage: the randomization logic this app ports (MIT).
- [FFXDataParser](https://github.com/Karifean/FFXDataParser) by Karifean: game data research, and the independent check used in development.
- [VBFTool](https://github.com/topher-au/VBFTool) by topher-au: the VBF archive format.
- The [Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) project and the Cid's Salvage Ship community.
