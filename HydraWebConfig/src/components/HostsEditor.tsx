import type { HostConfig, NeighbourConfig } from '../types'
import { HostCard } from './HostCard'

interface Props {
  hosts: HostConfig[]
  onAdd: () => void
  onRemove: (hi: number) => void
  onUpdate: (hi: number, patch: Partial<HostConfig>) => void
  onAddNeighbour: (hi: number) => void
  onRemoveNeighbour: (hi: number, ni: number) => void
  onUpdateNeighbour: (hi: number, ni: number, patch: Partial<NeighbourConfig>) => void
}

export function HostsEditor({ hosts, onAdd, onRemove, onUpdate, onAddNeighbour, onRemoveNeighbour, onUpdateNeighbour }: Props) {
  return (
    <>
      {hosts.map((h, hi) => (
        <HostCard
          key={h.id ?? hi}
          host={h}
          index={hi}
          onChange={patch => onUpdate(hi, patch)}
          onRemove={() => onRemove(hi)}
          onAddNeighbour={() => onAddNeighbour(hi)}
          onRemoveNeighbour={ni => onRemoveNeighbour(hi, ni)}
          onUpdateNeighbour={(ni, patch) => onUpdateNeighbour(hi, ni, patch)}
        />
      ))}
      <button className="btn-add" onClick={onAdd}>+ Add Host</button>
    </>
  )
}
