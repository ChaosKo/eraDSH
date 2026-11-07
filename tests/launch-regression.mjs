import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, existsSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { run } from '../index.js'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const ps = 'C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe'
const compiler = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe'
const directory = mkdtempSync(join(tmpdir(), 'era launch 中文 '))
const checks = []
const owned = []
function check(name, predicate) { assert.ok(predicate, name); checks.push(name) }
async function invoke(sessionFile, action, extra = []) {
  const started = performance.now()
  const result = await run({ powershell: ps, script: join(root, 'scripts/Invoke-Bridge.ps1'), args: ['-SessionFile', sessionFile, '-Action', action, ...extra], timeoutMs: 10000 })
  assert.equal(result.code, 0, result.stderr || result.stdout)
  return { value: JSON.parse(result.stdout.trim()), elapsedMs: Math.round(performance.now() - started) }
}
try {
  const fixture = join(directory, 'sleep engine.exe')
  const compiled = spawnSync(compiler, ['/nologo', '/target:winexe', '/reference:System.Web.Extensions.dll', '/out:' + fixture, join(root, 'tests/launch-fixture.cs')], { encoding: 'utf8' })
  assert.equal(compiled.status, 0, compiled.stdout + compiled.stderr)
  for (const launchMode of ['startup-hook', 'reflection']) {
    const artifact = join(directory, launchMode)
    mkdirSync(artifact)
    const sessionFile = join(artifact, 'session 中文.json')
    writeFileSync(sessionFile, JSON.stringify({ sessionId: launchMode, artifactDirectory: artifact, hostPath: fixture, enginePath: fixture, gameRoot: directory, runtimeDirectory: directory, launchMode, hookPath: 'test-hook-value', pipeName: 'era-no-server-' + crypto.randomUUID(), token: 'fixture-only', responseTimeoutSeconds: 20 }))
    const opened = await invoke(sessionFile, 'open')
    const pid = opened.value.processId
    owned.push(pid)
    check(launchMode + ': receipt-without-observation', opened.value.launched && opened.value.pending && opened.value.canAct === false && !opened.value.observationId)
    check(launchMode + ': tool-returned-while-child-alive', opened.elapsedMs < 5000 && process.kill(pid, 0))
    check(launchMode + ': instance-recorded', JSON.parse(readFileSync(join(artifact, 'bridge-instance.json'), 'utf8').trim()).processId === pid)
    const observed = await invoke(sessionFile, 'observe')
    check(launchMode + ': absent-ipc-is-pending', observed.value.ok && observed.value.pending && observed.value.canAct === false && observed.value.phase === 'starting')
    check(launchMode + ': bounded-observe', observed.elapsedMs < 5000)
    const rejected = await invoke(sessionFile, 'act', ['-ObservationId', 'not-an-observation', '-Kind', 'continue'])
    check(launchMode + ': absent-ipc-does-not-submit', !rejected.value.ok && rejected.value.submitted === false && rejected.value.code === 'NOT_READY')
    const fixtureResult = JSON.parse(readFileSync(join(artifact, 'fixture.json'), 'utf8'))
    check(launchMode + ': unicode-argument-and-working-directory', fixtureResult.sessionFile === sessionFile && fixtureResult.directory.toLowerCase() === directory.toLowerCase())
    if (launchMode === 'startup-hook') check('hook-environment-isolated-to-child', fixtureResult.hook === 'test-hook-value')
    process.kill(pid)
    owned.splice(owned.indexOf(pid), 1)
    const exited = await invoke(sessionFile, 'status')
    check(launchMode + ': exited-is-not-pending', exited.value.exited && exited.value.phase === 'exited' && exited.value.pending === false)
    console.log(JSON.stringify({ launchMode, openMs: opened.elapsedMs, observeMs: observed.elapsedMs }))
  }
  const slow = join(directory, 'slow.ps1')
  writeFileSync(slow, 'Start-Sleep -Seconds 10')
  await assert.rejects(run({ powershell: ps, script: slow, args: [], timeoutMs: 300 }), /deadline exceeded/)
  checks.push('client-deadline-is-enforced')
  // The native launcher itself must not alter this Node process's hook environment.
  check('parent-environment-unchanged', process.env.DOTNET_STARTUP_HOOKS !== 'test-hook-value')
  console.log(JSON.stringify({ passed: true, checks }, null, 2))
} finally {
  for (const pid of owned) { try { process.kill(pid) } catch {} }
  // If opening failed after process creation, recover only this test's executable.
  spawnSync(ps, ['-NoProfile', '-NonInteractive', '-Command', `Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq '${join(directory, 'sleep engine.exe').replaceAll("'", "''")}' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }`], { encoding: 'utf8' })
  try { rmSync(directory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 }) }
  catch (error) { console.error('fixture cleanup:', error.message) }
}
