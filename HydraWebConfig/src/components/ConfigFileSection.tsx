import { useState, useRef, useEffect } from 'react'
import type { HydraConfig } from '../types'
import { serialize } from '../utils/serializer'

interface Props {
  configs: HydraConfig[]
  multiConfig: boolean
}

export function ConfigFileSection({ configs, multiConfig }: Props) {
  const [viewOpen, setViewOpen] = useState(false)
  const [copied, setCopied] = useState(false)
  const dialogRef = useRef<HTMLDialogElement>(null)

  const json = serialize(configs, multiConfig)

  useEffect(() => {
    if (viewOpen) dialogRef.current?.showModal()
    else dialogRef.current?.close()
  }, [viewOpen])

  const download = () => {
    const blob = new Blob([json], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = 'hydra.conf'
    a.click()
    URL.revokeObjectURL(url)
  }

  const copy = async () => {
    await navigator.clipboard.writeText(json)
    setCopied(true)
    setTimeout(() => setCopied(false), 1500)
  }

  return (
    <div className="section config-file-section">
      <h2>Config File</h2>
      <div className="config-actions">
        <button className="btn-primary" onClick={() => setViewOpen(true)}>View</button>
        <button className="btn-secondary" onClick={download}>Download</button>
      </div>

      <dialog ref={dialogRef} className="config-dialog" onClose={() => setViewOpen(false)}>
        <div className="dialog-header">
          <span>hydra.conf</span>
          <button className="btn-remove" onClick={() => setViewOpen(false)} aria-label="close">✕</button>
        </div>
        <pre className="config-pre">{json}</pre>
        <div className="dialog-footer">
          <button className="btn-copy" onClick={copy}>
            {copied ? 'Copied!' : 'Copy to Clipboard'}
          </button>
        </div>
      </dialog>
    </div>
  )
}
