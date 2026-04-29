import { useRef } from 'react'

interface Props {
  start: number
  end: number
  label: string
  onChange: (start: number, end: number) => void
}

export function RangeSlider({ start, end, label, onChange }: Props) {
  const trackRef = useRef<HTMLDivElement>(null)

  function pctFromPointer(clientX: number): number {
    const rect = trackRef.current!.getBoundingClientRect()
    return Math.round(Math.max(0, Math.min(100, (clientX - rect.left) / rect.width * 100)))
  }

  function onPointerDown(e: React.PointerEvent) {
    e.preventDefault()
    ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
  }

  function onPointerUp(e: React.PointerEvent) {
    ;(e.currentTarget as HTMLElement).releasePointerCapture(e.pointerId)
  }

  function onStartMove(e: React.PointerEvent) {
    if (!(e.currentTarget as HTMLElement).hasPointerCapture(e.pointerId)) return
    onChange(Math.min(pctFromPointer(e.clientX), end - 1), end)
  }

  function onEndMove(e: React.PointerEvent) {
    if (!(e.currentTarget as HTMLElement).hasPointerCapture(e.pointerId)) return
    onChange(start, Math.max(pctFromPointer(e.clientX), start + 1))
  }

  const isDefault = start === 0 && end === 100

  return (
    <div className="range-slider-wrap">
      <div className="range-slider-label">
        <span>{label}</span>
        <span className="range-slider-vals">{isDefault ? 'full' : `${start}%–${end}%`}</span>
      </div>
      <div className="range-slider-track" ref={trackRef}>
        <div className="range-slider-fill" style={{ left: `${start}%`, width: `${Math.max(0, end - start)}%` }} />
        <div
          className="range-slider-handle"
          style={{ left: `${start}%` }}
          onPointerDown={onPointerDown}
          onPointerMove={onStartMove}
          onPointerUp={onPointerUp}
          title={`start: ${start}%`}
        />
        <div
          className="range-slider-handle"
          style={{ left: `${end}%` }}
          onPointerDown={onPointerDown}
          onPointerMove={onEndMove}
          onPointerUp={onPointerUp}
          title={`end: ${end}%`}
        />
      </div>
    </div>
  )
}
