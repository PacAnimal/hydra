import type { HydraConfig, NeighbourConfig, HostConfig, ScreenDefinition, ConfigConditions } from '../types'

function serializeNeighbour(n: NeighbourConfig): Record<string, unknown> {
  const out: Record<string, unknown> = {
    direction: n.direction,
    name: n.name,
  }
  if (n.sourceScreen) out.sourceScreen = n.sourceScreen
  if (n.destScreen) out.destScreen = n.destScreen
  if (n.sourceStart !== undefined && n.sourceStart !== 0) out.sourceStart = n.sourceStart
  if (n.sourceEnd !== undefined && n.sourceEnd !== 100) out.sourceEnd = n.sourceEnd
  if (n.destStart !== undefined && n.destStart !== 0) out.destStart = n.destStart
  if (n.destEnd !== undefined && n.destEnd !== 100) out.destEnd = n.destEnd
  // mirror defaults to true — omit when true
  if (n.mirror === false) out.mirror = false
  return out
}

function serializeHost(h: HostConfig): Record<string, unknown> {
  const out: Record<string, unknown> = { name: h.name }
  const neighbours = (h.neighbours ?? []).filter(n => n.name.trim())
  if (neighbours.length > 0) out.neighbours = neighbours.map(serializeNeighbour)
  if (h.deadCorners !== undefined) out.deadCorners = h.deadCorners
  return out
}

function serializeScreenDefinition(s: ScreenDefinition): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  if (s.displayName) out.displayName = s.displayName
  if (s.outputName) out.outputName = s.outputName
  if (s.platformId) out.platformId = s.platformId
  if (s.mouseScale !== undefined) out.mouseScale = s.mouseScale
  return out
}

function serializeConditions(c: ConfigConditions): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  if (c.ssid) out.ssid = c.ssid
  if (c.screenCount !== undefined) out.screenCount = c.screenCount
  return out
}

function serializeConfig(cfg: HydraConfig): Record<string, unknown> {
  const out: Record<string, unknown> = { mode: cfg.mode }
  if (cfg.name?.trim()) out.name = cfg.name.trim()
  if (cfg.lockFile?.trim()) out.lockFile = cfg.lockFile.trim()
  if (cfg.networkConfig?.trim()) out.networkConfig = cfg.networkConfig.trim()
  if (cfg.logLevel && cfg.logLevel !== 'info') out.logLevel = cfg.logLevel

  if (cfg.mouseScale !== undefined) out.mouseScale = cfg.mouseScale
  if (cfg.deadCorners !== undefined) out.deadCorners = cfg.deadCorners

  // booleans — omit when equal to default
  if (cfg.remoteOnly === true) out.remoteOnly = true
  if (cfg.autoUpdate === false) out.autoUpdate = false
  if (cfg.syncScreensaver === false) out.syncScreensaver = false
  if (cfg.debugShield === true) out.debugShield = true

  const hosts = (cfg.hosts ?? []).filter(h => h.name.trim())
  if (cfg.mode === 'Master' && hosts.length > 0) out.hosts = hosts.map(serializeHost)

  const screens = (cfg.screenDefinitions ?? []).filter(
    s => s.displayName || s.outputName || s.platformId
  )
  if (screens.length > 0) out.screenDefinitions = screens.map(serializeScreenDefinition)

  if (cfg.conditions) {
    const c = serializeConditions(cfg.conditions)
    if (Object.keys(c).length > 0) out.conditions = c
  }

  return out
}

// serialize a single config or an array of configs to a JSON string
export function serialize(configs: HydraConfig[], multiConfig: boolean): string {
  if (multiConfig) {
    return JSON.stringify(configs.map(serializeConfig), null, 2)
  }
  return JSON.stringify(serializeConfig(configs[0] ?? { mode: 'Master' }), null, 2)
}
