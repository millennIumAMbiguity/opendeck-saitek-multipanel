# Saitek Pro Flight Multi Panel for OpenDeck

[OpenDeck](https://github.com/nekename/OpenDeck) device plugin for the **Logitech/Saitek Pro Flight Multi Panel** (PZ70, USB `06A3:0D06`).
Buttons, switches, the selector knob and the trim wheel become OpenDeck keys and dials; the button LEDs and the two 5-digit LCD rows are driven by OpenDeck and by a small local API.

Written in C# / .NET 10, compiled to a native binary (~6 MB, ~20 MB RAM). Windows and Linux.

> Linux builds are produced by CI and the system monitor was tested under WSL, but the panel itself has only been tested on Windows so far.

## Layout in OpenDeck

The panel shows up as **Saitek Pro Flight Multi Panel** with 2×6 keys and 6 dials:

| Key | 1 | 2 | 3 | 4 | 5 | 6 |
|---|---|---|---|---|---|---|
| Row 1 | AP | HDG | NAV | IAS | ALT | VS |
| Row 2 | APR | REV | A/T ARM | A/T OFF | FLAPS UP | FLAPS DN |

- Buttons: key down/up as pressed.
- Switches (auto-throttle, flaps): the key is held while the switch is in that position. `A/T OFF` is the inverse of `A/T ARM`, so each position can run its own action.
- Dials 1–5 = **ALT, VS, IAS, HDG, CRS**: the big knob turns the dial that matches the selector position.
- Dial 6 = **pitch trim wheel**.

### Button LEDs

The 8 buttons (AP…REV) light up from their key image: a bright image turns the LED on, a dark image or an empty key turns it off.
Identical images are skipped; changed ones are decoded once and a 24×24 grid is sampled.
Threshold (mean brightness 0–255, default 40): `MULTIPANEL_LED_THRESHOLD` environment variable.

### Actions

**Counter** (dial): the knob changes a number shown on the LCD and as the dial title. Saved in the action settings.

| Dial | Step / fast step | Range | Display |
|---|---|---|---|
| ALT | 100 / 1000 | 0 – 99999 | upper row (ALT and VS positions) |
| VS | 100 / 500 | -9999 – 9999 | lower row (ALT and VS positions) |
| IAS | 1 / 10 | 0 – 999 | IAS position |
| HDG, CRS | 1 / 10 | 0 – 359, wraps, 3 digits | own position |

**System Monitor** (dial): CPU load %, RAM used/free (%, MB, GB), GPU load %, GPU memory MB.
Windows reads GetSystemTimes, GlobalMemoryStatusEx and the PDH GPU counters; Linux reads `/proc` and amdgpu sysfs or NVIDIA's NVML.
Sampling only runs while the value is on screen (selector on that dial's position; ALT and VS share the display), otherwise nothing runs at all.

In IAS/HDG/CRS the panel only shows the last 3 digits of the upper row; that's the hardware.

### LCD API for other programs

Other plugins and scripts can drive the display over HTTP on `localhost:47070` (`MULTIPANEL_API_PORT` to change).
The [War Thunder plugin](https://github.com/millennIumAMbiguity/opendeck-warthunder) uses this.

```sh
curl localhost:47070/state                                                   # selector position + LEDs
curl -X POST localhost:47070/display -d '{"upper":"12500","lower":"-850"}'  # all selector positions
curl -X POST localhost:47070/display -d '{"mode":"ias","upper":250}'        # only when selector = IAS
curl -X POST localhost:47070/display -d '{"leds":5}'                        # LED override (bit0=AP..bit7=REV), null = back to images
curl -X POST localhost:47070/dial -d '{"dial":3,"text":"270"}'              # like a dial action on knob dial 0-4 (row optional)
curl -N localhost:47070/watch                                               # stream of selector changes, one JSON line each
curl -X POST localhost:47070/clear
```

Digits `0-9`, space and `-` (minus only on the lower row). That's everything the hardware can show: a sweep of all other codes (`--sweep`) only produced blanks,
and the ALT/VS/IAS/HDG/CRS labels are switched by the panel itself from the selector.

## Install

From OpenDeck's plugin store, or download `io.github.millenniumambiguity.saitekmultipanel.sdPlugin.zip` from the [releases](../../releases) and install it from file in OpenDeck.

**Linux**: allow access to the panel without root once:

```sh
sudo cp 40-saitek-multipanel.rules /etc/udev/rules.d/   # from the plugin folder
sudo udevadm control --reload-rules && sudo udevadm trigger
```

## Build

```powershell
.\scripts\install.ps1          # Windows: native build + install into %APPDATA%\opendeck\plugins (needs VS "Desktop development with C++"; -NoAot to skip)
```

```sh
./scripts/build-linux.sh       # Linux: native build (needs clang, zlib1g-dev; --no-aot to skip) + install into ~/.config/opendeck/plugins
```

Restart OpenDeck after installing. Releases are built by GitHub Actions: push a tag matching the manifest version (`v0.1.0`).

Diagnostics (close OpenDeck first): `saitek-multipanel --diag` prints panel input and writes a test pattern, `--metrics` prints system readings, `--sweep` steps through LCD codes with the knob.

Log: `%LOCALAPPDATA%\opendeck\logs\plugins\io.github.millenniumambiguity.saitekmultipanel.sdPlugin.log` (Linux: `~/.local/share/opendeck/logs/plugins/`).
Put an empty `debug` file in the installed plugin folder (or set `MULTIPANEL_DEBUG=1`) to log every event.

## Protocol

Input: 3-byte report of bit flags (selector, knob, buttons, switches, trim). Output: feature report with report id 0, 5 upper digits, 5 lower digits and an LED byte, zero-padded to 13 bytes on Windows.
Thanks to [DCSFlightpanels](https://github.com/DCS-Skunkworks/DCSFlightpanels) for documenting it.

## License

MIT, see [LICENSE](LICENSE). Third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
