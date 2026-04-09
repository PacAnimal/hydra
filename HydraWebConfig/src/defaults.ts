import type { HydraConfig, HostConfig, NeighbourConfig, ScreenDefinition, FormState } from './types'

export function newNeighbour(): NeighbourConfig {
  return { direction: 'Right', name: '' }
}

export function newHost(): HostConfig {
  return { name: '', neighbours: [] }
}

export function newScreenDefinition(): ScreenDefinition {
  return {}
}

export function newConfig(): HydraConfig {
  return { mode: 'Master' }
}

export function newFormState(): FormState {
  return { multiConfig: false, configs: [newConfig()], activeIndex: 0 }
}
