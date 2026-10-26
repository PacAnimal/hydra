export type Mode = 'Master' | 'Slave'
export type Direction = 'Left' | 'Right' | 'Up' | 'Down'
export type LogLevel = 'trace' | 'debug' | 'info' | 'warn' | 'error' | 'critical'
export type UpdateChannel = 'stable' | 'edge'
export type NetworkType = 'config' | 'embeddedStyx' | 'embeddedStyxServer'
export type ClipboardSyncMode = 'Hydra' | 'System' | 'Off'

export interface NeighbourConfig {
  id?: string
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
  id?: string
  name: string
  neighbours?: NeighbourConfig[]
  deadCorners?: number
}

export interface ScreenDefinition {
  id?: string
  displayName?: string
  outputName?: string
  platformId?: string
  mouseScale?: number
  relativeMouseScale?: number
}

export interface ConfigConditions {
  ssid?: string
  screenCount?: number
  isPluggedIn?: boolean
}

export interface EmbeddedStyxConfig {
  server: string
  password: string
}

export interface EmbeddedStyxServerConfig {
  port: number
  password: string
}

// visual canvas item — one block per (host, screen?) pair in the layout editor
export interface LayoutItem {
  id: string
  hostName: string
  screenId?: string  // optional: identifies which screen on this host (matches sourceScreen/destScreen)
  x: number          // left edge in logical pixels
  y: number          // top edge in logical pixels
  w: number          // width in logical pixels (default 1920)
  h: number          // height in logical pixels (default 1080)
  isMaster?: boolean // marks which host in the layout is the local master machine
  deadCorners?: number
}

export interface HydraProfile {
  profileName: string
  mode: Mode
  // manual hosts editor (used when layoutItems is empty, or imported configs)
  hosts?: HostConfig[]
  // visual canvas state — when non-empty, derives hosts on serialize (overrides 'hosts')
  layoutItems?: LayoutItem[]
  // visual mode toggle (true = canvas, false = manual host editor)
  visualMode?: boolean
  screenDefinitions?: ScreenDefinition[]
  mouseScale?: number
  relativeMouseScale?: number
  networkType?: NetworkType
  networkConfig?: string
  embeddedStyx?: EmbeddedStyxConfig
  embeddedStyxServer?: EmbeddedStyxServerConfig
  remoteOnly?: boolean
  syncScreensaver?: boolean
  screenLockPropagation?: boolean
  accelerateMouseWheel?: boolean
  clipboardSync?: ClipboardSyncMode
  debugShield?: boolean
  deadCorners?: number
  conditions?: ConfigConditions
}

// form state — profiles always an array; activeIndex tracks the selected profile tab
export interface FormState {
  name?: string
  autoUpdate?: boolean
  updateChannel?: UpdateChannel
  managementListener?: boolean
  logLevel?: LogLevel
  lockFile?: string
  logFile?: string
  sessionLogFile?: string
  // a size as Hydra reads one ("10 MiB", "512 KB" or bytes), or 'none' for no cap; unset is Hydra's 10 MiB
  logMaxSize?: string
  logHistory?: number
  logTrimPercent?: number
  profiles: HydraProfile[]
  activeIndex: number
}

// the root-level fields the Global Settings section edits
export type RootSettingsPatch = Partial<Pick<FormState, 'name' | 'autoUpdate' | 'updateChannel' | 'managementListener' | 'logLevel' | 'lockFile' | 'logFile' | 'sessionLogFile'
  | 'logMaxSize' | 'logHistory' | 'logTrimPercent'>>
