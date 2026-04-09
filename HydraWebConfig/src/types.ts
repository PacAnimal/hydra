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

export interface HydraProfile {
  profileName: string
  mode: Mode
  hosts?: HostConfig[]
  screenDefinitions?: ScreenDefinition[]
  mouseScale?: number
  networkConfig?: string
  remoteOnly?: boolean
  syncScreensaver?: boolean
  debugShield?: boolean
  deadCorners?: number
  conditions?: ConfigConditions
}

// form state — profiles always an array; activeIndex tracks the selected profile tab
export interface FormState {
  name?: string
  autoUpdate?: boolean
  logLevel?: LogLevel
  lockFile?: string
  profiles: HydraProfile[]
  activeIndex: number
}
