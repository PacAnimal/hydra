import type { FormState, HydraProfile, NeighbourConfig, HostConfig, ScreenDefinition, ConfigConditions } from '../types'

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

function serializeProfile(p: HydraProfile): Record<string, unknown> {
  const out: Record<string, unknown> = { profileName: p.profileName, mode: p.mode }
  if (p.networkConfig?.trim()) out.networkConfig = p.networkConfig.trim()

  if (p.deadCorners !== undefined) out.deadCorners = p.deadCorners

  // booleans — omit when equal to default
  if (p.remoteOnly === true) out.remoteOnly = true
  if (p.syncScreensaver === false) out.syncScreensaver = false
  if (p.debugShield === true) out.debugShield = true

  const hosts = (p.hosts ?? []).filter(h => h.name.trim())
  if (p.mode === 'Master' && hosts.length > 0) out.hosts = hosts.map(serializeHost)

  // mouseScale and screenDefinitions are slave-only
  if (p.mode !== 'Master') {
    if (p.mouseScale !== undefined) out.mouseScale = p.mouseScale
    const screens = (p.screenDefinitions ?? []).filter(
      s => s.displayName || s.outputName || s.platformId
    )
    if (screens.length > 0) out.screenDefinitions = screens.map(serializeScreenDefinition)
  }

  if (p.conditions) {
    const c = serializeConditions(p.conditions)
    if (Object.keys(c).length > 0) out.conditions = c
  }

  return out
}

export function serialize(state: FormState): string {
  const out: Record<string, unknown> = {}
  // root-level globals — omit when equal to default
  if (state.name?.trim()) out.name = state.name.trim()
  if (state.autoUpdate === false) out.autoUpdate = false
  if (state.logLevel && state.logLevel !== 'info') out.logLevel = state.logLevel
  if (state.lockFile?.trim()) out.lockFile = state.lockFile.trim()
  out.profiles = state.profiles.map(serializeProfile)
  return JSON.stringify(out, null, 2)
}
