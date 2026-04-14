# Unity Editor Terminal

[![GitHub Sponsors](https://img.shields.io/badge/Sponsor-GitHub-pink?logo=githubsponsors)](https://github.com/sponsors/islee23520)

Standalone IMGUI-based terminal tool for the Unity Editor.

This package embeds a terminal window directly inside the Unity Editor so you can run shell commands without leaving your project context. The package is distributed as `com.linalab.unity-terminal` and currently targets Unity `6000.0`.

If this project helps your workflow, you can [support it on GitHub Sponsors](https://github.com/sponsors/islee23520).

## Features

- Open a terminal from the Unity Editor menu.
- Run an interactive POSIX shell inside the editor.
- Parse ANSI output and render it in an IMGUI terminal surface.
- Configure shell, working directory, tmux auto-attach, font, scrollback, and cursor blink settings from Unity Preferences.
- Keep terminal state, rendering, parsing, and process management separated in a small editor-only assembly.

## Installation

### Option 1: Unity Package Manager (Git URL)

Add this repository as a package:

```text
https://github.com/islee23520/unity-editor-terminal.git
```

### Option 2: Local package

Copy this package into your Unity project's `Packages/` directory.

## Requirements

- Unity `6000.0`
- A POSIX environment such as macOS or Linux
- A shell available through `$SHELL`, `/bin/zsh`, or `/bin/bash`

## Usage

After importing the package, open the terminal from:

```text
Tools > Unity Editor Terminal
```

The window is created by `Editor/TerminalEditorWindow.cs` and starts a shell using the configured working directory and shell profile.

## Configuration

Open the settings UI from the terminal toolbar or from Unity Preferences:

```text
Preferences > Linalab > Unity Terminal
```

Available settings include:

- Shell profile
- Custom shell path
- Default working directory
- tmux auto-attach
- Font family
- Font size
- Scrollback limit
- Cursor blink rate

The settings surface is implemented in:

- `Editor/TerminalSettings.cs`
- `Editor/TerminalSettingsProvider.cs`

## Project Structure

```text
unity-terminals/
├── Editor/                  # Editor-only implementation
├── package.json             # Unity package manifest
└── README.md
```

Core files:

- `Editor/TerminalEditorWindow.cs` — editor window lifecycle and input loop
- `Editor/ShellProcess.cs` — shell startup, process lifecycle, output draining
- `Editor/AnsiParser.cs` — ANSI escape sequence parsing
- `Editor/TerminalBuffer.cs` — terminal grid, cursor, and scrollback model
- `Editor/TerminalRenderer.cs` — IMGUI drawing and selection rendering

## How It Works

The runtime flow is:

```text
ShellProcess -> AnsiParser -> TerminalBuffer -> TerminalRenderer
```

This keeps shell I/O, parsing, state, and drawing responsibilities separated inside the `Linalab.Terminal.Editor` assembly.

## Limitations

- **Windows is not supported yet.** `ShellProcess.Start()` throws a `PlatformNotSupportedException` on Windows.
- **This package is shell-oriented for POSIX environments.** It assumes a shell can be resolved from `$SHELL`, `/bin/zsh`, or `/bin/bash`.
- **No repo-local test or CI workflow is present today.**
- **tmux integration is optional.** If enabled, the package attempts to auto-attach to a generated session name and falls back to the shell when tmux is unavailable.

## Development Notes

- All product code currently lives under `Editor/`.
- The assembly definition is `Editor/UnityEditorTerminal.Editor.asmdef`.
- Unity metadata files (`*.meta`) are part of the package layout but are not implementation sources.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
