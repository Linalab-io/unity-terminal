# EDITOR MODULE KNOWLEDGE BASE

## OVERVIEW
Editor-only Unity assembly implementing the terminal window, shell bridge, ANSI parser, buffer model, rendering, input translation, and user preferences.

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Open/restart/clear terminal window | `TerminalEditorWindow.cs` | Owns menu item, update polling, toolbar, input routing |
| Shell startup and process lifecycle | `ShellProcess.cs` | Detects shell, starts process, drains stdout/stderr, tmux attach |
| Buffer mutations and scrollback | `TerminalBuffer.cs` | Grid state, cursor movement, resize, erase, scroll region |
| Parse escape sequences | `AnsiParser.cs` | CSI/escape handling, SGR state, device-status responses |
| Draw terminal contents | `TerminalRenderer.cs` | IMGUI drawing, visible grid sizing, selection, cursor |
| Translate Unity key events | `TerminalInputHandler.cs` | Maps Unity input to terminal control sequences |
| Persist settings | `TerminalSettings.cs` | `EditorPrefs` keys, defaults, workspace directory, shell profile |
| Expose preferences UI | `TerminalSettingsProvider.cs` | Unity Preferences panel |
| Value types and palette data | `TerminalCell.cs` | Cells, colors, flags, cursor structs |
| Assembly boundary | `UnityEditorTerminal.Editor.asmdef` | Editor-only platform include |

## CONVENTIONS
- Keep code in the `Linalab.Terminal.Editor` namespace and inside the editor-only asmdef unless package architecture intentionally expands.
- Preserve the main flow: window reads shell output, parser mutates buffer, renderer reads buffer; avoid shortcuts that blur these boundaries.
- Add or change persisted settings through `TerminalSettings` first, then expose them in `TerminalSettingsProvider`.
- Respect the interface seams `IShellProcess` and `ITerminalBuffer`; they are the clearest local boundaries in an otherwise tightly-coupled module.
- Treat resize, scrollback, and cursor behavior as buffer concerns, not renderer concerns.

## ANTI-PATTERNS
- Do not write directly to the shell from rendering code; shell I/O should stay behind `ShellProcess` and input handlers.
- Do not add ad hoc `EditorPrefs` keys outside `TerminalSettings`.
- Do not assume Windows support because detection code exists; `Start()` currently blocks Windows execution.
- Do not bypass `AnsiParser` by mutating terminal state from raw shell output handlers.
- Do not move logic into `.meta` or package-root files that belongs inside this assembly.

## LOCAL GOTCHAS
- `TerminalEditorWindow` polls every ~16 ms and resizes the shell only after renderer grid changes settle.
- `TerminalSettings.GetWorkspaceDirectory()` defaults to the Unity project root via `Application.dataPath`.
- tmux session names are derived from workspace path plus a stable hash; keep that helper centralized.
- zsh/font heuristics read `~/.zshrc`; be careful when changing startup-font behavior.
