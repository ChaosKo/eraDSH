# Legacy probes

Read-only research scripts from the first phase of this project, when the
approach was to drive an already-running Emuera window from outside the process
(Win32 messages, UI Automation, `PrintWindow`).

That route was tested and **rejected**:

- The engine draws its own text, so UIA and `WM_GETTEXT` return no game body.
- `SetForegroundWindow` was refused by the system, so synthetic key input could
  not be committed reliably.
- Input typed into the RichEdit box was cleared without ever selecting a menu
  entry.

The project moved to the in-process bridge (`bridge/EraBridgeHost.cs`), which
reads the engine display buffer directly. These files are kept only as a record
of what was tried; they are **not a supported path**.

`Test-TitleInfo.ps1` and `Test-CharacterInput.ps1` are deliberately reduced to a
single failure statement: their original bodies depended on one game build and on
a menu assumption that later turned out to be wrong (see
[TEST-REPORT.md](../TEST-REPORT.md)).

`NativeWindow.ps1` is the shared Win32/UIA helper used by the other probes and is
ASCII-only on purpose, because Windows PowerShell 5.1 misreads non-BOM sources.

Files here resolve the game folder through `scripts/Common.ps1`, the same
configuration the supported scripts use, so no machine path is stored in the
repository.
