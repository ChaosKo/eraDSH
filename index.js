import { spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, isAbsolute, join, resolve } from 'node:path'

export const name = 'dsh-era-bridge'
export const inject = ['tools']

const here = dirname(fileURLToPath(import.meta.url))
const defaultScript = resolve(here, 'scripts', 'Invoke-Bridge.ps1')
const systemRoot = process.env.SystemRoot ?? process.env.WINDIR ?? 'C:\\Windows'
const defaultPowerShell = join(systemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe')

const ACTIONS = ['open', 'status', 'observe', 'act', 'close']

function runClient({ powershell, script, args }) {
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

export function apply(ctx, config = {}) {
  const script = isAbsolute(config.scriptPath ?? '') ? config.scriptPath : defaultScript
  const powershell = isAbsolute(config.powershellPath ?? '') ? config.powershellPath : defaultPowerShell
  const configuredSession = typeof config.sessionFile === 'string' && config.sessionFile.length > 0 ? config.sessionFile : undefined

  ctx.effect(() => ctx.tools.register({
    name: 'era_bridge',
    description:
      'Read or control a managed Era game through the local eraDSH bridge. Always observe before acting: act requires the observation id from the latest observation, and the bridge rejects stale, duplicate, or malformed actions. Text comes from the engine display buffer; screenshots are saved as files and must be viewed separately.',
    parameters: {
      type: 'object',
      properties: {
        action: { type: 'string', enum: ACTIONS, description: 'open launches the managed instance; observe reads state; act submits one input; status reports liveness; close shuts the managed instance down cleanly.' },
        sessionFile: { type: 'string', description: 'Absolute path to a session.json produced by Build-Bridge.ps1. Falls back to the plugin configuration when omitted.' },
        mode: { type: 'string', enum: ['text', 'visual', 'both', 'auto'], description: 'Observation content. auto currently behaves like both.' },
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
      render: (_args, value) => [{ type: 'text', text: JSON.stringify(value, null, 2) }],
    },
    async execute(args) {
      const action = String(args.action ?? '')
      if (!ACTIONS.includes(action)) throw new Error(`Unsupported era_bridge action: ${action}`)
      const sessionFile = args.sessionFile ?? configuredSession
      if (!sessionFile) throw new Error('era_bridge needs sessionFile (no default configured)')
      if (!isAbsolute(sessionFile)) throw new Error('sessionFile must be an absolute path')
      if (!existsSync(sessionFile)) throw new Error(`sessionFile does not exist: ${sessionFile}`)
      if (!existsSync(script)) throw new Error(`Bridge client script not found: ${script}`)

      const cli = ['-SessionFile', sessionFile, '-Action', action]
      if (args.mode) cli.push('-Mode', String(args.mode))
      if (args.limitLines != null) cli.push('-LimitLines', String(args.limitLines))
      if (args.fromLine != null) cli.push('-FromLine', String(args.fromLine))
      if (action === 'act') {
        if (!args.observationId) throw new Error('act requires observationId from the latest observation')
        cli.push('-ObservationId', String(args.observationId))
        cli.push('-Kind', String(args.kind ?? 'input'))
        if (args.value != null) cli.push('-Value', String(args.value))
        if (args.choiceId) cli.push('-ChoiceId', String(args.choiceId))
        if (args.actionId) cli.push('-ActionId', String(args.actionId))
      }

      const { code, stdout, stderr } = await runClient({ powershell, script, args: cli })
      if (code !== 0) {
        const detail = stderr.trim() || stdout.trim() || 'no diagnostic output'
        throw new Error(`era_bridge client failed (exit ${code}): ${detail.slice(0, 2000)}`)
      }
      return parseSoleJson(stdout)
    },
  }))
}
