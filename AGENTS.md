# PROJECT KNOWLEDGE BASE

**Generated:** 2026-04-14
**Commit:** dbb7939
**Branch:** main

## OVERVIEW
Unity package for an IMGUI-based terminal inside the Unity Editor. Nearly all product code lives in the editor-only `Editor/` assembly; the root mostly carries package metadata and lightweight Sisyphus state.

## STRUCTURE
```text
unity-terminals/
├── Editor/                  # Main Unity Editor package implementation; see Editor/AGENTS.md
├── .sisyphus/rag/           # Local knowledge-state/config; not product code
├── package.json             # Unity package manifest
├── Editor.meta              # Unity metadata; never treat as source
└── package.json.meta        # Unity metadata; never treat as source
```

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Package identity and Unity version | `package.json` | Package name `com.linalab.unity-terminal`, Unity `6000.0` |
| Main implementation | `Editor/` | All C# source is editor-only |
| Open the terminal window | `Editor/TerminalEditorWindow.cs` | Menu entry: `Tools/Unity Editor Terminal` |
| Shell/process behavior | `Editor/ShellProcess.cs` | Starts shell, drains output, tmux attach logic |
| ANSI parsing | `Editor/AnsiParser.cs` | Converts shell output into buffer mutations |
| Terminal state model | `Editor/TerminalBuffer.cs` | Grid, scrollback, cursor, resize behavior |
| Rendering and selection | `Editor/TerminalRenderer.cs` | Draws terminal contents in IMGUI |
| User preferences | `Editor/TerminalSettings.cs` / `Editor/TerminalSettingsProvider.cs` | `EditorPrefs` keys and Preferences UI |
| Local Sisyphus memory state | `.sisyphus/rag/config.json` | Tooling state only; usually not part of product changes |

## CODE MAP
| File | Role |
|------|------|
| `Editor/TerminalEditorWindow.cs` | Editor window lifecycle, polling loop, toolbar, input handoff |
| `Editor/ShellProcess.cs` | Shell abstraction and process orchestration via `IShellProcess` |
| `Editor/TerminalBuffer.cs` | Core terminal model via `ITerminalBuffer` |
| `Editor/AnsiParser.cs` | Escape-sequence parser feeding the buffer |
| `Editor/TerminalRenderer.cs` | IMGUI rendering, scrollback view, selection/cursor drawing |
| `Editor/TerminalInputHandler.cs` | Key-event translation to terminal input sequences |
| `Editor/TerminalSettings.cs` | Centralized settings and workspace/tmux helpers |
| `Editor/TerminalSettingsProvider.cs` | Unity Preferences UI |
| `Editor/TerminalCell.cs` | Cell/color/cursor value types and palette data |
| `Editor/UnityEditorTerminal.Editor.asmdef` | Editor-only assembly boundary |

## CONVENTIONS
- Keep product code under the `Linalab.Terminal.Editor` namespace unless the package grows beyond a single editor-only assembly.
- Treat `Editor/UnityEditorTerminal.Editor.asmdef` as the assembly boundary; new source outside `Editor/` changes package shape and should be deliberate.
- Centralize user settings in `TerminalSettings`; it owns `EditorPrefs` keys, defaults, clamping, and workspace/tmux naming.
- The runtime data flow is layered: `ShellProcess` -> `AnsiParser` -> `TerminalBuffer` -> `TerminalRenderer`.
- Unity metadata files (`*.meta`) are present but are not implementation sources.

## ANTI-PATTERNS (THIS PROJECT)
- Do not document or score `.meta` files as if they were real code.
- Do not create subdirectory AGENTS files just because a hidden/tooling folder exists; `.sisyphus/rag` is state/config, not a development module.
- Do not scatter package-wide instructions into `Editor/`; keep cross-cutting package identity and high-level navigation in this root file.
- Do not claim repo-local build, test, or CI workflows that are not present; this repo currently has no test suite or workflow files.

## UNIQUE STYLES
- Small package, shallow hierarchy: root routes; `Editor/` specializes.
- The package is Unity-first rather than CLI-first: the operational entrypoint is a Unity menu item, not a shell script.
- Preference discovery includes shell-aware heuristics such as zsh font inference and tmux session naming.

## COMMANDS
```text
Primary runtime entry: open the package in Unity Editor, then use Tools > Unity Editor Terminal.
Repo-local automation: none detected for build/test/lint/CI.
```

## NOTES
- `ShellProcess.Start()` currently throws on Windows; treat this package as POSIX-editor-shell oriented unless that behavior changes.
- `.gitignore` is effectively empty at the moment; do not infer established ignore conventions from it.
- Recent commit history is feature-oriented (`feat:` / `chore:`) and already describes the package in logical slices.
