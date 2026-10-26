export type Mode = 'Master' | 'Slave'
export type Direction = 'Left' | 'Right' | 'Up' | 'Down'
export type LogLevel = 'trace' | 'debug' | 'info' | 'warn' | 'error' | 'critical'
export type UpdateChannel = 'stable' | 'edge'
export type NetworkType = 'config' | 'embeddedStyx' | 'embeddedStyxServer'
export type ClipboardSyncMode = 'Hydra' | 'System'

// Hydra's rebindable hotkey actions and their defaults (docs/CONFIGURATION.md, Hotkeys)
export const HOTKEY_ACTIONS: { action: string; label: string; defaultChord: string }[] = [
  { action: 'toggleCursorLock', label: 'Cursor Lock', defaultChord: 'Ctrl+Alt+Super+L' },
  { action: 'toggleRelativeMouse', label: 'Relative Mouse', defaultChord: 'Ctrl+Alt+Super+M' },
  { action: 'copyFiles', label: 'Copy Files', defaultChord: 'Ctrl+Alt+Super+C' },
  { action: 'pasteFiles', label: 'Paste Files', defaultChord: 'Ctrl+Alt+Super+V' },
  { action: 'lockSlaves', label: 'Lock Slaves', defaultChord: 'Ctrl+Alt+Super+K' },
  { action: 'missionControl', label: 'Mission Control', defaultChord: 'Ctrl+Alt+Super+Z' },
]

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
  allowSystemSleep?: boolean
  clipboardSync?: ClipboardSyncMode
  // master only
  hideCursor?: boolean
  maxMouseHz?: number
  // the keypad's NumLock under Super/Ctrl/Alt: true digits, false navigation, undefined as pressed
  modifierNumLock?: boolean
  // action name to its chords as typed, one per line (a chord's key may itself be ',' or '+'); an empty or missing
  // action keeps its default
  hotkeys?: Record<string, string>
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
  profiles: HydraProfile[]
  activeIndex: number
}

// the root-level fields the Global Settings section edits
export type RootSettingsPatch = Partial<Pick<FormState, 'name' | 'autoUpdate' | 'updateChannel' | 'managementListener' | 'logLevel' | 'lockFile' | 'logFile' | 'sessionLogFile'>>
