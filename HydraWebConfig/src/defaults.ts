import type { HydraProfile, HostConfig, NeighbourConfig, ScreenDefinition, FormState } from './types'

export function newNeighbour(): NeighbourConfig {
  return { direction: 'Right', name: '' }
}

export function newHost(): HostConfig {
  return { name: '', neighbours: [] }
}

export function newScreenDefinition(): ScreenDefinition {
  return {}
}

export function newProfile(): HydraProfile {
  return { profileName: '', mode: 'Master' }
}

export function newFormState(): FormState {
  return { profiles: [newProfile()], activeIndex: 0 }
}
