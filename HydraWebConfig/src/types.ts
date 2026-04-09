export type Mode = 'Master' | 'Slave'
export type Direction = 'Left' | 'Right' | 'Up' | 'Down'
export type LogLevel = 'trace' | 'debug' | 'info' | 'warn' | 'error' | 'critical'

export interface NeighbourConfig {
  direction: Direction
  name: string
  sourceScreen?: string
  destScreen?: string
  sourceStart?: number
  sourceEnd?: number
  destStart?: number
  destEnd?: number
  mirror?: boolean
}

export interface HostConfig {
  name: string
  neighbours?: NeighbourConfig[]
  deadCorners?: number
}

export interface ScreenDefinition {
  displayName?: string
  outputName?: string
  platformId?: string
  mouseScale?: number
}

export interface ConfigConditions {
  ssid?: string
  screenCount?: number
}

export interface HydraConfig {
  mode: Mode
  name?: string
  hosts?: HostConfig[]
  screenDefinitions?: ScreenDefinition[]
  mouseScale?: number
  logLevel?: LogLevel
  networkConfig?: string
  remoteOnly?: boolean
  autoUpdate?: boolean
  syncScreensaver?: boolean
  debugShield?: boolean
  deadCorners?: number
  lockFile?: string
  conditions?: ConfigConditions
}

// form state — always an array internally; single-config mode just uses configs[0]
export interface FormState {
  multiConfig: boolean
  configs: HydraConfig[]
  activeIndex: number
}
