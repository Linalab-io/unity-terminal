# Unity Editor Terminal

[![GitHub Sponsors](https://img.shields.io/badge/Sponsor-GitHub-pink?logo=githubsponsors)](https://github.com/sponsors/islee23520)

Standalone terminal tool for the Unity Editor that lets you run shell commands without leaving your project context. The package is distributed as `com.linalab.unity-terminal` and currently targets Unity `6000.0`.

If this project helps your workflow, you can [support it on GitHub Sponsors](https://github.com/sponsors/islee23520).

## Features

- Open a terminal from the Unity Editor menu.
- Run an interactive POSIX shell inside the editor.
- Parse ANSI output and render it in a UI Toolkit terminal surface.
- Resize the in-editor terminal grid from the live Unity container size.
- Configure shell, font, scrollback, and cursor blink settings from Unity Preferences.

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

The terminal starts a shell using the configured working directory and shell profile.

## Configuration

Open the settings UI from the terminal toolbar or from Unity Preferences:

```text
Preferences > Linalab > Unity Terminal
```

Available settings include:

- Shell profile
- Custom shell path
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
- `Editor/TerminalSurfaceElement.cs` — UI Toolkit terminal surface, selection handling, and composition/cursor overlays

## How It Works

The runtime flow is:

```text
ShellProcess -> AnsiParser -> TerminalBuffer -> TerminalRenderer -> TerminalSurfaceElement
```

This keeps shell I/O, parsing, state, and drawing responsibilities separated inside the `Linalab.Terminal.Editor` assembly.

## Limitations

- **Windows is not supported yet.** `ShellProcess.Start()` throws a `PlatformNotSupportedException` on Windows.
- **This package is shell-oriented for POSIX environments.** It assumes a shell can be resolved from `$SHELL`, `/bin/zsh`, or `/bin/bash`.
- **Headless EditMode coverage is backend-focused.** The automated smoke tests validate shell/process, ANSI parsing, buffer updates, and resize semantics, but not final on-screen rendering quality.

## Testing

Editor tests now live under:

- `Tests/Editor/Linalab.Terminal.Tests.Editor.asmdef`
- `Tests/Editor/TerminalSmokeTests.cs`

Current automated coverage includes:

- shell startup through `ShellProcess`
- stdout/stderr drain flow into `AnsiParser`
- visible text landing in `TerminalBuffer`
- buffer resize preservation and cursor clamping

Example headless EditMode run:

```text
Unity -runTests -batchmode -nographics -projectPath <PROJECT> -testPlatform EditMode -testFilter TerminalSmokeTests
```

Because Unity blocks opening the same project in two editor instances, run the batchmode test against a closed project or a temporary clone when your main editor session is already open.

## Manual QA

Use `docs/manual-qa-checklist.md` for interactive verification of:

- terminal window launch
- prompt visibility
- keyboard input and paste
- selection/copy behavior
- resize and clipping behavior
- restart/clear actions

## Development Notes

- All product code currently lives under `Editor/`.
- The assembly definition is `Editor/UnityEditorTerminal.Editor.asmdef`.
- Test coverage lives in `Tests/Editor/` via `Linalab.Terminal.Tests.Editor.asmdef`.
- Unity metadata files (`*.meta`) are part of the package layout but are not implementation sources.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
