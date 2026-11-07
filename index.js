import { spawn } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { homedir } from 'node:os'
import { dirname, isAbsolute, join, resolve } from 'node:path'

export const name = 'dsh-era-bridge'
export const inject = ['tools']

// Everything the tool runs ships inside this package, so the plugin never depends
// on the checkout's location and never has to be told where a repository is.
const here = dirname(fileURLToPath(import.meta.url))
const clientScript = resolve(here, 'scripts', 'Invoke-Bridge.ps1')
const buildScript = resolve(here, 'scripts', 'Build-Bridge.ps1')
const systemRoot = process.env.SystemRoot ?? process.env.WINDIR ?? 'C:\\Windows'
const defaultPowerShell = join(systemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe')

// The last session built through this tool, remembered per DSH home so a caller
// can keep acting after a turn boundary without re-passing sessionFile. The DSH
// host process does not always export DSH_HOME, so fall back to the default home.
const dshHome = process.env.DSH_HOME && process.env.DSH_HOME.length > 0
  ? process.env.DSH_HOME
  : join(homedir(), '.dsh')
const stateRoot = join(dshHome, 'era-bridge')
const stateFile = join(stateRoot, 'last-session.json')

const ACTIONS = ['build', 'open', 'status', 'observe', 'act', 'advance', 'close']
const RUNTIME_MODES = ['inplace', 'copy']

function run({ powershell, script, args }) {
  return new Promise((resolvePromise, reject) => {
    const child = spawn(powershell, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', script, ...args], {
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    })
    let stdout = ''
    let stderr = ''
    child.stdout.setEncoding('utf8')
    child.stderr.setEncoding('utf8')
    child.stdout.on('data', chunk => { if (stdout.length < 4_000_000) stdout += chunk })
    child.stderr.on('data', chunk => { if (stderr.length < 65_536) stderr += chunk })
    child.on('error', reject)
    child.on('close', code => resolvePromise({ code, stdout, stderr }))
  })
}

function parseSoleJson(stdout) {
  const text = stdout.trim()
  const start = text.indexOf('{')
  const end = text.lastIndexOf('}')
  if (start < 0 || end <= start) throw new Error('Bridge returned no JSON result')
  return JSON.parse(text.slice(start, end + 1))
}

function readRememberedSession() {
  try {
    if (!existsSync(stateFile)) return undefined
    const parsed = JSON.parse(readFileSync(stateFile, 'utf8'))
    if (typeof parsed.sessionFile !== 'string' || !parsed.sessionFile) return undefined
    if (!existsSync(parsed.sessionFile)) return undefined
    return parsed.sessionFile
  } catch {
    return undefined
  }
}

function rememberSession(result) {
  try {
    mkdirSync(stateRoot, { recursive: true })
    writeFileSync(stateFile, JSON.stringify({
      sessionFile: result.sessionFile,
      gameRoot: result.gameRoot,
      runtimeMode: result.runtimeMode,
      builtAt: new Date().toISOString(),
    }, null, 2))
  } catch {
    // Remembering is a convenience; a read-only home must not fail the build.
  }
}

export function apply(ctx, config = {}) {
  const script = isAbsolute(config.scriptPath ?? '') ? config.scriptPath : clientScript
  const builder = isAbsolute(config.buildScriptPath ?? '') ? config.buildScriptPath : buildScript
  const powershell = isAbsolute(config.powershellPath ?? '') ? config.powershellPath : defaultPowerShell
  const configuredSession = typeof config.sessionFile === 'string' && config.sessionFile.length > 0 ? config.sessionFile : undefined
  const defaultRuntimeMode = RUNTIME_MODES.includes(config.runtimeMode) ? config.runtimeMode : 'inplace'

  ctx.effect(() => ctx.tools.register({
    name: 'era_bridge',
    description:
      'Build a session and read or control a managed Era game through the local eraDSH bridge. Start with action=build (gameRoot is the Era game folder); every other action then works without sessionFile because the last build is remembered. Always observe before acting: act requires the observation id from the latest observation, and the bridge rejects stale, duplicate, or malformed actions. Text comes from the engine display buffer; screenshots are saved as files and must be viewed separately.',
    parameters: {
      type: 'object',
      properties: {
        action: { type: 'string', enum: ACTIONS, description: 'build prepares a session for a game folder; open launches the managed instance; observe reads state; act submits one input; advance presses Enter repeatedly while the game is only narrating (stops when a button, a value, a text entry, a non-waiting state or maxSteps is reached); status reports liveness; close shuts the managed instance down cleanly.' },
        maxSteps: { type: 'integer', minimum: 1, maximum: 200, description: 'advance only: how many Enter presses to allow before returning (default 20). The reply says steps and stoppedBecause (input-required, not-waiting-input, max-steps, stalled).' },
        gameRoot: { type: 'string', description: 'Absolute path of the Era game folder. Required by build; the scripts that operate it ship inside this plugin, so no repository path is ever needed.' },
        engineExecutable: { type: 'string', description: 'Optional launcher file name inside gameRoot; omitted, the engine is detected automatically.' },
        runtimeMode: { type: 'string', enum: RUNTIME_MODES, description: 'inplace (default) runs the engine from the game folder and copies nothing; copy mirrors the folder into the plugin runtime directory first.' },
        recreate: { type: 'boolean', description: 'build only: discard and rebuild an existing runtime directory.' },
        sessionFile: { type: 'string', description: 'Absolute path to a session.json. Falls back to the plugin configuration, then to the session this tool built last.' },
        mode: { type: 'string', enum: ['text', 'visual', 'both', 'auto'], description: 'Observation content. auto currently behaves like both.' },
        includeLines: { type: 'boolean', description: 'Include the per-line array. It duplicates text one-for-one, so observations omit it unless this is true.' },
        sinceCursor: { type: 'integer', description: 'Continue from a line cursor: pass nextCursor from the previous page to read the next block of lines, or cursor to follow the tail. cursorExpired is true when the engine already dropped those rows; the reply then carries the current window instead.' },
        sinceObservationId: { type: 'string', description: 'Incremental observe: pass the observation id you already hold and the reply carries only the difference (delta.kind = append/changed/none, or full when the base is unknown or too large). The observation id still hashes the whole screen, so actions remain safe.' },
        noise: { type: 'string', enum: ['filter', 'raw'], description: 'filter (default) drops engine loading chatter (読み込み/ロード/Loading lines, bare .erb/.erh/.csv listings) and collapses long separator runs, reporting counts in filteredNoiseLines and collapsedSeparators; raw returns the display buffer verbatim.' },
        limitLines: { type: 'integer', minimum: 1, maximum: 200, description: 'Maximum display-buffer lines to return.' },
        fromLine: { type: 'integer', minimum: 0, description: 'Zero-based display-buffer line to start from; omit for the newest page.' },
        observationId: { type: 'string', description: 'Observation id the action is based on. Required for act.' },
        kind: { type: 'string', enum: ['choose', 'input', 'continue'], description: 'choose presses a listed button; input submits a number or text; continue answers an EnterKey/AnyKey wait.' },
        value: { type: 'string', description: 'Number or single-line text for input.' },
        choiceId: { type: 'string', description: 'Button id from the observation for choose.' },
        actionId: { type: 'string', description: 'Optional GUID for idempotency. Do not change it to retry an uncertain action.' },
      },
      required: ['action'],
      additionalProperties: false,
    },
    output: {
      schema: { type: 'object', additionalProperties: true },
      // Compact on purpose: pretty-printing the same observation costs about 28% more
      // context for no information the model can use.
      render: (_args, value) => [{ type: 'text', text: JSON.stringify(value) }],
    },
    async execute(args) {
      const action = String(args.action ?? '')
      if (!ACTIONS.includes(action)) throw new Error(`Unsupported era_bridge action: ${action}`)

      if (action === 'build') {
        const gameRoot = String(args.gameRoot ?? '')
        if (!gameRoot) throw new Error('build requires gameRoot: the absolute path of the Era game folder')
        if (!isAbsolute(gameRoot)) throw new Error('gameRoot must be an absolute path')
        if (!existsSync(gameRoot) || !statSync(gameRoot).isDirectory()) throw new Error(`gameRoot is not a directory: ${gameRoot}`)
        if (!existsSync(builder)) throw new Error(`Bridge build script not found: ${builder}`)

        const cli = ['-GameRoot', gameRoot, '-RuntimeMode', String(args.runtimeMode ?? defaultRuntimeMode)]
        if (args.engineExecutable) cli.push('-EngineExecutable', String(args.engineExecutable))
        if (args.recreate === true) cli.push('-Recreate')

        const { code, stdout, stderr } = await run({ powershell, script: builder, args: cli })
        if (code !== 0) {
          const detail = (stderr.trim() || stdout.trim() || 'no diagnostic output').slice(0, 2000)
          throw new Error(`era_bridge build failed (exit ${code}): ${detail}`)
        }
        const built = parseSoleJson(stdout)
        if (!built.sessionFile) throw new Error('era_bridge build returned no sessionFile')
        rememberSession(built)
        return built
      }

      const sessionFile = args.sessionFile ?? configuredSession ?? readRememberedSession()
      if (!sessionFile) throw new Error('era_bridge has no session yet: call action="build" with gameRoot first')
      if (!isAbsolute(sessionFile)) throw new Error('sessionFile must be an absolute path')
      if (!existsSync(sessionFile)) throw new Error(`sessionFile does not exist: ${sessionFile}`)
      if (!existsSync(script)) throw new Error(`Bridge client script not found: ${script}`)

      const cli = ['-SessionFile', sessionFile, '-Action', action]
      if (args.mode) cli.push('-Mode', String(args.mode))
      if (args.limitLines != null) cli.push('-LimitLines', String(args.limitLines))
      if (args.fromLine != null) cli.push('-FromLine', String(args.fromLine))
      if (args.includeLines === true) cli.push('-IncludeLines')
      if (args.sinceObservationId) cli.push('-SinceObservationId', String(args.sinceObservationId))
      if (args.sinceCursor != null) cli.push('-SinceCursor', String(args.sinceCursor))
      if (action === 'advance' && args.maxSteps != null) cli.push('-MaxSteps', String(args.maxSteps))
      if (args.noise) cli.push('-Noise', String(args.noise))
      if (action === 'act') {
        if (!args.observationId) throw new Error('act requires observationId from the latest observation')
        cli.push('-ObservationId', String(args.observationId))
        cli.push('-Kind', String(args.kind ?? 'input'))
        if (args.value != null) cli.push('-Value', String(args.value))
        if (args.choiceId) cli.push('-ChoiceId', String(args.choiceId))
        if (args.actionId) cli.push('-ActionId', String(args.actionId))
      }

      const { code, stdout, stderr } = await run({ powershell, script, args: cli })
      if (code !== 0) {
        const detail = stderr.trim() || stdout.trim() || 'no diagnostic output'
        throw new Error(`era_bridge client failed (exit ${code}): ${detail.slice(0, 2000)}`)
      }
      return parseSoleJson(stdout)
    },
  }))
}
