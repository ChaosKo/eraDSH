// Real-engine regression, exclusively against a full private copy (never original saves).
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync, statSync } from 'node:fs'
import { createHash } from 'node:crypto'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'
import { apply, run } from '../index.js'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const games = {
  ocg: { source: 'C:/Data/Game/Eras/eraOCG2_20261009', engine: 'Emuera.NET 1824+v22+EMv18+EEv52fix.exe' },
  meiq: { source: 'C:/Data/Game/Eras/eraMeiQGaiden', engine: 'Emuera1821.exe' },
  tohok: { source: 'E:/yaminosekai/eratohoK', engine: 'emuera1824中文版.exe' },
  fl: { source: 'C:/Data/Game/Eras/erafl', engine: 'Emuera.exe' },
  tw: { source: 'C:/Data/Game/Eras/eratw-sub-modding-main', engine: 'Emuera.NET 1824+v22+EMv18+EEv53 NAudio.exe' },
}
const name = process.argv[2] || 'ocg'
const game = games[name]
assert.ok(game && existsSync(game.source), `Unknown or missing game: ${name}`)
const ps = 'C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe'
const testRoot = join(root, 'runtime', 'regression', name)
const gameRoot = join(testRoot, 'game')
const markerPath = join(testRoot, 'fixture.json')
const excluded = new Set(['.git', 'sav', 'dat', 'debug', 'emuera.log', 'time.log'])
function protectedHashes() {
  const hashes = {}
  function scan(path, relative) {
    if (!existsSync(path)) return
    if (statSync(path).isDirectory()) for (const e of readdirSync(path)) scan(join(path, e), relative + '/' + e)
    else hashes[relative] = createHash('sha256').update(readFileSync(path)).digest('hex')
  }
  for (const e of readdirSync(game.source)) if (['sav', 'dat', 'debug', 'profile', 'emuera.config', 'setting.json', 'emuera.log', 'time.log'].includes(e.toLowerCase())) scan(join(game.source, e), e)
  return hashes
}
const original = protectedHashes()
mkdirSync(testRoot, { recursive: true })
if (!existsSync(gameRoot)) {
  cpSync(game.source, gameRoot, { recursive: true, filter: source => source === game.source || !excluded.has(source.slice(game.source.length + 1).split(/[\\/]/)[0].toLowerCase()) })
  writeFileSync(markerPath, JSON.stringify({ source: game.source, createdUtc: new Date().toISOString() }))
} else {
  assert.ok(existsSync(markerPath), 'Refusing an unmanaged test directory')
  assert.equal(JSON.parse(readFileSync(markerPath, 'utf8')).source, game.source)
}
if (process.argv.includes('--prepare-only')) {
  assert.deepEqual(protectedHashes(), original)
  console.log(JSON.stringify({ prepared: true, name, gameRoot, protectedFiles: Object.keys(original).length }))
  process.exit(0)
}
function soleJson(text) { return JSON.parse(text.slice(text.indexOf('{'), text.lastIndexOf('}') + 1)) }
let tool
apply({ effect: fn => fn(), tools: { register: definition => { tool = definition; return () => {} } } })
let built, pid
const events = []
const checks = []
function check(name, condition) { assert.ok(condition, name); checks.push(name) }
function event(kind, data) { const value = { kind, ...data }; events.push(value); console.log(JSON.stringify(value)) }
async function invoke(args) {
  const start = performance.now()
  const result = await tool.execute({ ...args, sessionFile: built.sessionFile })
  const elapsedMs = Math.round(performance.now() - start)
  event(args.action, { elapsedMs, ok: result.ok, code: result.code, phase: result.phase ?? result.observation?.phase, pending: result.pending ?? result.observation?.pending, canAct: result.canAct ?? result.observation?.canAct, state: result.state ?? result.observation?.state, input: result.input ?? result.observation?.input, text: (result.text ?? result.observation?.text ?? '').slice(-1800), buttons: (result.buttons ?? result.observation?.buttons ?? []).slice(0, 12).map(b => ({ id: b.id, label: b.label, value: b.value })), submitted: result.submitted, stoppedBecause: result.stoppedBecause, reason: result.reason })
  return { result, elapsedMs }
}
try {
  const compiled = await run({ powershell: ps, script: join(root, 'scripts/Build-Bridge.ps1'), args: ['-GameRoot', gameRoot, '-EngineExecutable', game.engine, '-RuntimeMode', 'inplace'], timeoutMs: 180000 })
  assert.equal(compiled.code, 0, compiled.stderr || compiled.stdout)
  built = soleJson(compiled.stdout)
  const opened = await invoke({ action: 'open' })
  pid = opened.result.processId
  check('open-receipt', opened.result.launched && opened.result.pending && opened.result.canAct === false)
  check('open-returned-with-engine-alive', opened.elapsedMs < 8000 && process.kill(pid, 0))
  let submitted = 0, decisions = 0, pendingObserved = 0, sameEpochAction = false
  const deadline = performance.now() + 150000
  for (let i = 0; i < 240 && performance.now() < deadline; i++) {
    let { result: observation } = await invoke({ action: 'observe', mode: 'text', limitLines: 40 })
    if (observation.pending || !observation.canAct) {
      pendingObserved++
      check(`non-actionable-observation-${i}`, observation.canAct === false)
      // Prove even a syntactically valid action cannot become a delayed Enter.
      if (observation.observationId && pendingObserved === 1) {
        const { result: rejected } = await invoke({ action: 'act', observationId: observation.observationId, kind: 'continue' })
        check('pending-action-is-rejected', rejected.ok === false && rejected.submitted !== true)
      }
      await delay(250)
      continue
    }
    check('actionable-is-waiting', observation.state === 'WaitInput' && Boolean(observation.inputEpoch))
    // Keep a delivered observation across another capture and a timer/repaint opportunity.
    const earlier = observation
    await delay(300)
    const { result: latest } = await invoke({ action: 'observe', mode: 'text', limitLines: 20 })
    const stable = latest.canAct && latest.inputEpoch === earlier.inputEpoch
    observation = stable ? earlier : latest
    if (!observation.canAct) continue
    const inputKind = observation.input?.kind
    let action
    if (['EnterKey', 'AnyKey', 'PrimitiveMouseKey'].includes(inputKind)) action = { kind: 'continue' }
    else {
      const options = (observation.buttons ?? []).filter(b => b.active && b.choosable && !/終了|退出|离开游戏|セーブ|保存|存档|ロード|读档|读取/.test(b.label))
      const choice = options.find(b => /最初|新游戏|新規|从头|开始游戏|最初から|NEW GAME/i.test(b.label)) ?? options[0]
      if (choice) action = { kind: 'choose', choiceId: choice.id }
      else if (inputKind === 'StrValue' || inputKind === 'StrButton') action = { kind: 'input', value: 'BridgeTest' }
      else if (inputKind === 'IntValue' || inputKind === 'IntButton') action = { kind: 'input', value: '0' }
      else break
      decisions++
    }
    const { result: acted } = await invoke({ action: 'act', observationId: observation.observationId, ...action, mode: 'text' })
    check(`input-${submitted}-submitted`, acted.ok && acted.submitted)
    submitted++
    if (stable) sameEpochAction = true
    // A token authorizes one input only, even if identical text is redrawn next.
    if (submitted === 1) {
      const { result: replay } = await invoke({ action: 'act', observationId: observation.observationId, ...action, mode: 'text' })
      check('consumed-observation-rejected', replay.ok === false && replay.submitted !== true)
    }
    if (submitted >= 4 && decisions >= 1) break
  }
  check('at-least-two-real-inputs', submitted >= 2)
  check('stable-input-survives-new-capture', sameEpochAction)
  event('summary', { name, submitted, decisions, pendingObserved, checks })
} finally {
  if (built && pid) {
    try { await invoke({ action: 'close' }) } catch (e) { event('close-error', { message: e.message }) }
    // Test-only last resort: exact PID + creation-time + image-path identity, never a name kill.
    const recordPath = join(built.artifactDirectory, 'bridge-instance.json')
    if (existsSync(recordPath)) {
      const record = JSON.parse(readFileSync(recordPath, 'utf8').trim())
      const command = `$ErrorActionPreference='Stop'; $p=Get-Process -Id ${record.processId} -ErrorAction SilentlyContinue; if($p -and $p.Path -eq '${built.hostPath.replaceAll("'", "''")}' -and $p.StartTime.ToUniversalTime().ToString('o') -eq '${record.startTimeUtc}') { if(-not $p.WaitForExit(5000)) { Stop-Process -Id $p.Id -Force } }; exit 0`
      const cleanup = spawnSync(ps, ['-NoProfile', '-NonInteractive', '-Command', command], { encoding: 'utf8', timeout: 10000 })
      check('test-process-cleanup', cleanup.status === 0)
    }
  }
  check('original-protected-files-unchanged', JSON.stringify(protectedHashes()) === JSON.stringify(original))
  writeFileSync(join(testRoot, 'latest-results.json'), JSON.stringify({ name, checks, events, artifactDirectory: built?.artifactDirectory, finishedUtc: new Date().toISOString() }, null, 2))
}
