import { useState, useRef, useCallback } from 'react'

interface Props {
  onImport: (json: string) => string | null
}

export function DropZone({ onImport }: Props) {
  const [dragging, setDragging] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const inputRef = useRef<HTMLInputElement>(null)

  const handleFile = useCallback((file: File) => {
    const reader = new FileReader()
    reader.onload = e => {
      const text = e.target?.result
      if (typeof text === 'string') {
        const err = onImport(text)
        setError(err)
      }
    }
    reader.readAsText(file)
  }, [onImport])

  const onDragOver = (e: React.DragEvent) => {
    e.preventDefault()
    setDragging(true)
  }

  const onDragLeave = () => setDragging(false)

  const onDrop = (e: React.DragEvent) => {
    e.preventDefault()
    setDragging(false)
    const file = e.dataTransfer.files[0]
    if (file) handleFile(file)
  }

  const onFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (file) handleFile(file)
    e.target.value = ''
  }

  return (
    <>
      {dragging && (
        <div
          className="drop-overlay"
          onDragOver={onDragOver}
          onDragLeave={onDragLeave}
          onDrop={onDrop}
        >
          <span>Drop hydra.conf to import</span>
        </div>
      )}
      <div
        className="drop-target"
        onDragOver={onDragOver}
        onDragLeave={onDragLeave}
        onDrop={onDrop}
      >
        <button className="btn-ghost" onClick={() => inputRef.current?.click()}>
          Import Config
        </button>
        <input
          ref={inputRef}
          type="file"
          accept=".conf,.json"
          style={{ display: 'none' }}
          onChange={onFileChange}
          aria-label="import config file"
        />
        {error && <span className="error">{error}</span>}
      </div>
    </>
  )
}
