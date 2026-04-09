import { useState } from 'react'
import type { FormState } from '../types'
import { serialize } from '../utils/serializer'

interface Props {
  state: FormState
}

export function ConfigFileSection({ state }: Props) {
  const [copied, setCopied] = useState(false)
  const json = serialize(state)

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
    <div className="config-panel-inner">
      <div className="config-panel-header">
        <span className="config-panel-title">hydra.conf</span>
      </div>
      <pre className="config-pre">{json}</pre>
      <div className="config-panel-footer">
        <button className="btn-copy" onClick={copy}>
          {copied ? 'Copied!' : 'Copy to Clipboard'}
        </button>
        <button className="btn-secondary" onClick={download}>Download</button>
      </div>
    </div>
  )
}
