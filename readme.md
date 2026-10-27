# FluxWave

A fast, lightweight serial monitor for Linux, built with C#, Raylib, and Dear ImGui.
It's designed for ESP32, ESP8266, Arduino, and similar boards, with ANSI color
support, a wrong-baud-rate detector, and a hot-reloading JSON config.

![FluxWave screenshot](https://raw.githubusercontent.com/PradoshGame/FluxWave/main/image.png)

## Features

- **Live baud switching:** change the baud rate while connected, with common presets (including 74880 for ESP8266 boot ROM and 250000 for Marlin) or a custom value
- **Wrong-baud detection:** warns when recent output looks garbled and offers one-click suggestions for other rates
- **ANSI colors:** 16, 256, and truecolor foreground colors, bold, and basic cursor/erase sequences, so colored logs and shell-style output render properly
- **Live prompts:** unfinished lines (like an `esp32>` prompt) show up immediately instead of waiting for a newline
- **No accidental resets:** DTR and RTS are left low on connect, so many ESP32/Arduino boards don't reboot when you open the port
- **Send bar** with selectable line endings: none, `\n`, `\r`, or `\r\n`
- **Log tools:** timestamps with millisecond precision, text filter, pause, autoscroll, clear, copy, and save to file
- **Settings that just work:** autosaved to `settings.json` and hot-reloaded when you edit the file
- **Frame pacing:** syncs to your monitor's refresh rate and caps the frame rate if the driver ignores VSync

## Requirements

**To run:**
- Linux x86-64 with OpenGL 3.3+
- Permission to access serial ports (see [Serial port permissions](#serial-port-permissions))

**To build and install:**
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- `git`, `make`, and `sudo`

## Installation

```sh
git clone https://github.com/PradoshGame/FluxWave.git
cd FluxWave
make install
```

This publishes a self-contained single-file build, installs it to `/opt/fluxwave`,
symlinks it to `/usr/local/bin/fluxwave`, and adds a desktop entry. Run it with:

```sh
fluxwave
```

To change the install location: `make install PREFIX=/some/path`

### Serial port permissions

If no ports show up, or connecting fails with "permission denied", add yourself to
the group that owns the serial devices, then log out and back in:

```sh
sudo usermod -aG uucp $USER      # Arch / Manjaro
sudo usermod -aG dialout $USER   # Debian / Ubuntu
```

## Usage

1. Pick a port (use **Refresh** after plugging in a board).
2. Choose a baud rate. The default is 115200.
3. Click **Connect**.
4. Type in the bar at the bottom and press **Enter** to send. Choose the line ending in the dropdown next to it.

| Control | What it does |
|---|---|
| Autoscroll | Follow new output; scroll up to stop following |
| Timestamps | Show `HH:mm:ss.fff` before each line |
| Show sent (`>`) | Show or hide what you've sent |
| Pause | Freeze the display. Incoming data is buffered, not lost |
| Baud check | Enable or disable the wrong-baud warning |
| Clear / Copy / Save | Clear the log, copy it, or write it to a file |
| Filter | Show only lines containing the text (case-insensitive) |

Copy and Save only include lines that are currently visible (after the filter and
"Show sent" are applied). The log keeps the most recent 5000 lines.

### Wrong baud rate banner

If at least 25% of the characters in two consecutive 128-character windows look
like framing noise, FluxWave shows a banner with other likely baud rates to try.
It clears itself once the data looks clean again. Click **Dismiss** to hide it.

## Configuration

Settings live in `~/.config/.fluxwave/settings.json`. The file is created on first
run, autosaved when you change something in the UI, and **reloaded automatically**
when you edit it by hand. If the JSON is invalid, FluxWave shows an error and won't
overwrite your file until it's fixed.

```json
{
  "accent": "#4DA6FF",
  "logPrefix": "serial",
  "autoScroll": true,
  "timestamps": true,
  "showSent": true,
  "baudCheck": true,
  "baud": 115200,
  "lineEnding": "lf",
  "lastPort": ""
}
```

| Key | Description |
|---|---|
| `accent` | UI and sent-text color as `#RGB` or `#RRGGBB` |
| `logPrefix` | Saved logs are named `<logPrefix>_yyyyMMdd_HHmmss.log` |
| `autoScroll`, `timestamps`, `showSent`, `baudCheck` | Same as the checkboxes in the UI |
| `baud` | Baud rate (50 to 10,000,000) |
| `lineEnding` | `none`, `lf`, `cr`, or `crlf` |
| `lastPort` | Port to preselect on startup |

Saved logs go to `~/.config/.fluxwave/logs/`. Clicking the toast after saving
copies the file path to your clipboard.

## Makefile targets

| Command | Description |
|---|---|
| `make run` | Run in debug mode |
| `make build` | Debug build |
| `make publish` | Release build into `./publish` |
| `make install` | Publish and install system-wide |
| `make uninstall` | Remove the app (your settings are kept) |
| `make reinstall` | Uninstall, then install |
| `make clean` | Delete build output |

## Limitations

- Only foreground ANSI colors are supported. Background colors are ignored.
- The terminal emulation is line-based, so full-screen programs (e.g. `htop`, `vim`) won't render correctly.
- Tested with Linux x86-64 only.