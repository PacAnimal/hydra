import type { HydraProfile, HostConfig, NeighbourConfig, ScreenDefinition, ConfigConditions, Direction, Mode, LogLevel, FormState } from '../types'

const VALID_MODES: Mode[] = ['Master', 'Slave']
const VALID_DIRECTIONS: Direction[] = ['Left', 'Right', 'Up', 'Down']
const VALID_LOG_LEVELS: LogLevel[] = ['trace', 'debug', 'info', 'warn', 'error', 'critical']

function coerceMode(v: unknown): Mode {
  if (typeof v === 'string') {
    const m = VALID_MODES.find(x => x.toLowerCase() === v.toLowerCase())
    if (m) return m
  }
  throw new Error(`invalid mode: ${JSON.stringify(v)}`)
}

function coerceDirection(v: unknown): Direction {
  if (typeof v === 'string') {
    const d = VALID_DIRECTIONS.find(x => x.toLowerCase() === v.toLowerCase())
    if (d) return d
  }
  throw new Error(`invalid direction: ${JSON.stringify(v)}`)
}

function coerceLogLevel(v: unknown): LogLevel | undefined {
  if (v === undefined || v === null) return undefined
  if (typeof v === 'string') {
    // support short aliases matching C# LogLevelConverter
    const map: Record<string, LogLevel> = {
      trce: 'trace', trace: 'trace',
      dbug: 'debug', debug: 'debug',
      info: 'info', information: 'info',
      warn: 'warn', warning: 'warn',
      fail: 'error', error: 'error',
      crit: 'critical', critical: 'critical',
    }
    const mapped = map[v.toLowerCase()]
    if (mapped) return mapped
    const direct = VALID_LOG_LEVELS.find(x => x === v.toLowerCase())
    if (direct) return direct
  }
  return undefined
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function parseNeighbour(obj: any): NeighbourConfig {
  return {
    direction: coerceDirection(obj.direction),
    name: String(obj.name ?? ''),
    sourceScreen: obj.sourceScreen ?? undefined,
    destScreen: obj.destScreen ?? undefined,
    sourceStart: obj.sourceStart !== undefined ? Number(obj.sourceStart) : undefined,
    sourceEnd: obj.sourceEnd !== undefined ? Number(obj.sourceEnd) : undefined,
    destStart: obj.destStart !== undefined ? Number(obj.destStart) : undefined,
    destEnd: obj.destEnd !== undefined ? Number(obj.destEnd) : undefined,
    mirror: obj.mirror !== undefined ? Boolean(obj.mirror) : undefined,
  }
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function parseHost(obj: any): HostConfig {
  return {
    name: String(obj.name ?? ''),
    neighbours: Array.isArray(obj.neighbours) ? obj.neighbours.map(parseNeighbour) : [],
    deadCorners: obj.deadCorners !== undefined ? Number(obj.deadCorners) : undefined,
  }
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function parseScreenDefinition(obj: any): ScreenDefinition {
  return {
    displayName: obj.displayName ?? undefined,
    outputName: obj.outputName ?? undefined,
    platformId: obj.platformId ?? undefined,
    mouseScale: obj.mouseScale !== undefined ? Number(obj.mouseScale) : undefined,
  }
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function parseConditions(obj: any): ConfigConditions {
  return {
    ssid: obj.ssid ?? undefined,
    screenCount: obj.screenCount !== undefined ? Number(obj.screenCount) : undefined,
  }
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function parseProfile(obj: any): HydraProfile {
  if (typeof obj !== 'object' || obj === null || Array.isArray(obj)) {
    throw new Error('profile must be a JSON object')
  }
  return {
    profileName: typeof obj.profileName === 'string' ? obj.profileName : '',
    mode: coerceMode(obj.mode),
    hosts: Array.isArray(obj.hosts) ? obj.hosts.map(parseHost) : undefined,
    screenDefinitions: Array.isArray(obj.screenDefinitions)
      ? obj.screenDefinitions.map(parseScreenDefinition)
      : undefined,
    mouseScale: obj.mouseScale !== undefined ? Number(obj.mouseScale) : undefined,
    networkConfig: obj.networkConfig ?? undefined,
    remoteOnly: obj.remoteOnly !== undefined ? Boolean(obj.remoteOnly) : undefined,
    syncScreensaver: obj.syncScreensaver !== undefined ? Boolean(obj.syncScreensaver) : undefined,
    debugShield: obj.debugShield !== undefined ? Boolean(obj.debugShield) : undefined,
    deadCorners: obj.deadCorners !== undefined ? Number(obj.deadCorners) : undefined,
    conditions: obj.conditions ? parseConditions(obj.conditions) : undefined,
  }
}

export function deserialize(json: string): FormState {
  let parsed: unknown
  try {
    parsed = JSON.parse(json)
  } catch {
    throw new Error('invalid JSON')
  }

  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    throw new Error('config must be a JSON object with a "profiles" array')
  }

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const root = parsed as any
  if (!Array.isArray(root.profiles) || root.profiles.length === 0) {
    throw new Error('config must have a non-empty "profiles" array')
  }

  return {
    name: root.name ?? undefined,
    autoUpdate: root.autoUpdate !== undefined ? Boolean(root.autoUpdate) : undefined,
    logLevel: coerceLogLevel(root.logLevel),
    lockFile: root.lockFile ?? undefined,
    profiles: root.profiles.map(parseProfile),
    activeIndex: 0,
  }
}
