# Unity Editor Terminal Manual QA Checklist

Use this checklist in an interactive Unity Editor session for behaviors that cannot be validated reliably in `-batchmode -nographics`.

## Preconditions

- Unity project is open on macOS or Linux
- package `com.linalab.unity-terminal` is installed
- a POSIX shell is available through `$SHELL`, `/bin/zsh`, or `/bin/bash`
- terminal opens from `Tools > Unity Editor Terminal`

## 1. Window launch and initial prompt

- Open `Tools > Unity Editor Terminal`
- Confirm the window appears without exceptions in Console
- Confirm a shell prompt becomes visible
- Confirm the working directory matches the expected project/workspace root

Pass criteria:
- no startup exception
- prompt is readable and not clipped
- terminal accepts focus

## 2. Basic keyboard input

- Type `echo QA_INPUT_OK`
- Press Enter
- Confirm the command text appears once
- Confirm output contains `QA_INPUT_OK`

Pass criteria:
- no duplicated keystrokes
- Enter submits exactly once
- output is appended at the bottom

## 3. Paste and interrupt behavior

- Copy `printf 'PASTE_OK\n'`
- Paste with the platform shortcut
- Confirm pasted text executes correctly after Enter
- Run a long-lived command such as `sleep 5`
- Send interrupt with Ctrl+C or Cmd/Ctrl+C path used by the terminal

Pass criteria:
- paste does not garble text
- interrupt returns to the shell prompt

## 4. Selection and copy

- Print a few lines of output
- Drag to select part of the output
- Copy selected text
- Paste into another editor field and verify contents

Pass criteria:
- mouse drag selection matches expected text
- copied text preserves line breaks sensibly
- copy does not inject extra characters

## 5. Resize behavior

- Resize the terminal window narrower and wider
- Resize it shorter and taller
- After each resize, run `stty size` and note the reported rows/columns
- Run `printf '1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZ\n'`

Pass criteria:
- shell remains interactive after resize
- `stty size` changes consistently with visible grid changes
- existing visible content stays coherent
- no severe clipping, overdraw, or stale characters remain after resize

## 6. Rendering and clipping regression check

- Observe prompt baseline, command text, and output lines
- Look specifically for top-edge clipping or vertical offset issues
- Scroll through multiple lines of output
- Verify cursor position aligns with glyphs

Pass criteria:
- no obvious baseline drift
- no top-clipped text
- cursor and text align on the same row

## 7. Toolbar actions

- Click `Clear`
- Confirm visible buffer clears and terminal remains usable
- Click `Restart`
- Confirm a fresh shell session appears
- Open `Settings`
- Change font size, then verify the terminal updates

Pass criteria:
- clear does not break input
- restart yields a usable prompt
- settings changes apply without exceptions

## 8. Stress smoke

- Run a command with ANSI color, such as `printf '\033[31mRED\033[0m\n'`
- Run enough output to exceed one screen, such as `seq 1 200`
- Scroll and then return to bottom

Pass criteria:
- ANSI-colored text remains readable
- scrollback is navigable
- returning to bottom restores the live prompt view

## Notes to capture if a check fails

Record:
- Unity version
- operating system version
- exact shell in use
- exact command typed
- whether the failure is input, parser, buffer, resize, or rendering related
- screenshot or short screen recording when the bug is visual
