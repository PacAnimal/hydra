import { Component } from 'react'
import type { ReactNode, ErrorInfo } from 'react'

interface Props {
  children: ReactNode
  onReset: () => void
}

interface State {
  error: Error | null
}

export class CanvasErrorBoundary extends Component<Props, State> {
  constructor(props: Props) {
    super(props)
    this.state = { error: null }
  }

  static getDerivedStateFromError(error: Error): State {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('LayoutCanvas crashed:', error, info)
  }

  render() {
    if (this.state.error) {
      return (
        <div className="canvas-error">
          <p>The canvas crashed unexpectedly.</p>
          <button
            className="btn-ghost"
            onClick={() => {
              this.setState({ error: null })
              this.props.onReset()
            }}
          >
            Reset canvas
          </button>
        </div>
      )
    }
    return this.props.children
  }
}
