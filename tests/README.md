# Tests

These scripts drive a **running** bridge host through `scripts/Invoke-Bridge.ps1`.
Build a session first, then pass its `session.json` path:

```powershell
$build = ..\scripts\Build-Bridge.ps1 | ConvertFrom-Json
..\scripts\Invoke-Bridge.ps1 -SessionFile $build.sessionFile -Action open -Mode text
```

| Script | Scope | Portable |
| --- | --- | --- |
| `Test-BridgeTransport.ps1` | Wrong credential, oversized request, service survival | yes |
| `Test-BridgeBounds.ps1` | Synthetic limits: text budget, button cap, generation filtering, stale buttons | yes (32-bit PowerShell) |
| `Test-TitleCredits.ps1` | Title-screen credits toggle, replay protection, restore | no — encodes one game's menu values |
| `Test-NewGamePath.ps1` | Title → new game → setup → prologue → main screen | no — encodes one game's flow |
| `Measure-ObservationCost.ps1` | Display-buffer growth, per-step response size, audit growth | no — encodes one game's menu values |

The last two are kept because they are the evidence behind
[TEST-REPORT.md](../TEST-REPORT.md). They assert button values that only exist
in the game build this project was developed against, so they are protocol
demonstrations, not a generic test suite. Running them against another game will
fail on an assertion or refuse an action; neither should be treated as a bridge
defect.

`Test-BridgeBounds.ps1` loads the x86 host assembly without executing the engine
entry point, so it must run under 32-bit PowerShell:

```powershell
& "$env:WINDIR\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -STA `
  -ExecutionPolicy Bypass -File .\Test-BridgeBounds.ps1 `
  -HostPath $build.hostPath -OutputPath (Join-Path $build.artifactDirectory 'bounds-results.json')
```

Every script writes its results into the session artifact directory; nothing
touches the original game folder.

## Synthetic suites (no game, no real engine)

| Suite | Run | Scope |
| --- | --- | --- |
| Core (`.NET 8` hook) | `dotnet run --project tests/core-tests.csproj -c Release` | input epochs, stale/duplicate actions, deadlines, bounded UI work, delta budget, compact rendering |
| Framework (`net48` host) | `tests\framework-regression.ps1` | the same authorization chain for the reflection adapter, plus compact rendering parity |

The compact-rendering checks read `fixtures/ocg-duel-screen.txt`, a real eraOCG2
duel screen captured from a session log. It is the fixture behind the measured
55.8% text reduction in [observation-cost.md](../docs/observation-cost.md#10-表现层压缩rendercompact2026-11-09);
if the rendering rules change, update both the numbers and the assertions.
