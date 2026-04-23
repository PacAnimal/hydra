import type {
  HydraProfile, HostConfig, NeighbourConfig, ScreenDefinition, ConfigConditions,
  Direction, Mode, LogLevel, FormState, EmbeddedStyxConfig, EmbeddedStyxServerConfig, NetworkType,
} from '../types'
import { inferLayoutFromHosts } from './layout'

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

function optNum(v: unknown): number | undefined {
  return v !== undefined ? Number(v) : undefined
}
function optBool(v: unknown): boolean | undefined {
  return v !== undefined ? Boolean(v) : undefined
}
function optStr(v: unknown): string | undefined {
  return typeof v === 'string' ? v : undefined
}

function parseNeighbour(obj: Record<string, unknown>): NeighbourConfig {
  return {
    direction: coerceDirection(obj.direction),
    name: String(obj.name ?? ''),
    sourceScreen: optStr(obj.sourceScreen),
    destScreen: optStr(obj.destScreen),
    sourceStart: optNum(obj.sourceStart),
    sourceEnd: optNum(obj.sourceEnd),
    destStart: optNum(obj.destStart),
    destEnd: optNum(obj.destEnd),
    mirror: optBool(obj.mirror),
  }
}

function parseHost(obj: Record<string, unknown>): HostConfig {
  return {
    name: String(obj.name ?? ''),
    neighbours: Array.isArray(obj.neighbours) ? obj.neighbours.map(parseNeighbour) : [],
    deadCorners: optNum(obj.deadCorners),
  }
}

function parseScreenDefinition(obj: Record<string, unknown>): ScreenDefinition {
  return {
    displayName: optStr(obj.displayName),
    outputName: optStr(obj.outputName),
    platformId: optStr(obj.platformId),
    mouseScale: optNum(obj.mouseScale),
    relativeMouseScale: optNum(obj.relativeMouseScale),
  }
}

function parseConditions(obj: Record<string, unknown>): ConfigConditions {
  return {
    ssid: optStr(obj.ssid),
    screenCount: optNum(obj.screenCount),
  }
}

function parseEmbeddedStyx(obj: Record<string, unknown>): EmbeddedStyxConfig {
  return {
    server: String(obj.server ?? ''),
    password: String(obj.password ?? ''),
  }
}

function parseEmbeddedStyxServer(obj: Record<string, unknown>): EmbeddedStyxServerConfig {
  return {
    port: Number(obj.port ?? 0),
    password: String(obj.password ?? ''),
  }
}

function parseProfile(obj: Record<string, unknown>): HydraProfile {
  if (typeof obj !== 'object' || obj === null || Array.isArray(obj)) {
    throw new Error('profile must be a JSON object')
  }

  const hosts = Array.isArray(obj.hosts) ? obj.hosts.map(parseHost) : undefined

  // determine network type from which field is present
  let networkType: NetworkType | undefined
  if (obj.embeddedStyxServer) networkType = 'embeddedStyxServer'
  else if (obj.embeddedStyx) networkType = 'embeddedStyx'
  else if (obj.networkConfig) networkType = 'config'

  return {
    profileName: typeof obj.profileName === 'string' ? obj.profileName : '',
    mode: coerceMode(obj.mode),
    hosts,
    // infer visual layout from hosts on import
    layoutItems: hosts ? inferLayoutFromHosts(hosts) : [],
    visualMode: true,
    screenDefinitions: Array.isArray(obj.screenDefinitions)
      ? obj.screenDefinitions.map(parseScreenDefinition)
      : undefined,
    mouseScale: optNum(obj.mouseScale),
    relativeMouseScale: optNum(obj.relativeMouseScale),
    networkType,
    networkConfig: optStr(obj.networkConfig),
    embeddedStyx: obj.embeddedStyx ? parseEmbeddedStyx(obj.embeddedStyx as Record<string, unknown>) : undefined,
    embeddedStyxServer: obj.embeddedStyxServer ? parseEmbeddedStyxServer(obj.embeddedStyxServer as Record<string, unknown>) : undefined,
    remoteOnly: optBool(obj.remoteOnly),
    syncScreensaver: optBool(obj.syncScreensaver),
    debugShield: optBool(obj.debugShield),
    deadCorners: optNum(obj.deadCorners),
    conditions: obj.conditions ? parseConditions(obj.conditions as Record<string, unknown>) : undefined,
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

  const root = parsed as Record<string, unknown>
  if (!Array.isArray(root.profiles) || root.profiles.length === 0) {
    throw new Error('config must have a non-empty "profiles" array')
  }

  return {
    name: optStr(root.name),
    autoUpdate: optBool(root.autoUpdate),
    logLevel: coerceLogLevel(root.logLevel),
    lockFile: optStr(root.lockFile),
    logFile: optStr(root.logFile),
    sessionLogFile: optStr(root.sessionLogFile),
    profiles: root.profiles.map(parseProfile),
    activeIndex: 0,
  }
}
