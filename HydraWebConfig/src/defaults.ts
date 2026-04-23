import type { HydraProfile, HostConfig, NeighbourConfig, ScreenDefinition, FormState, LayoutItem } from './types'

export function newNeighbour(): NeighbourConfig {
  return { id: `n-${Date.now()}-${Math.random().toString(36).slice(2)}`, direction: 'Right', name: '' }
}

export function newHost(): HostConfig {
  return { id: `h-${Date.now()}-${Math.random().toString(36).slice(2)}`, name: '', neighbours: [] }
}

export function newScreenDefinition(): ScreenDefinition {
  return { id: `s-${Date.now()}-${Math.random().toString(36).slice(2)}` }
}

export function newLayoutItem(hostName: string, screenId?: string, col = 0, row = 0): LayoutItem {
  return { id: `layout-${Date.now()}-${Math.random().toString(36).slice(2)}`, hostName, screenId, col, row }
}

export function newProfile(): HydraProfile {
  return { profileName: '', mode: 'Master', visualMode: true, layoutItems: [] }
}

export function newFormState(): FormState {
  return { profiles: [newProfile()], activeIndex: 0 }
}
