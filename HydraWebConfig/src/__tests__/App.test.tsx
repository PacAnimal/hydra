import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../App'

describe('App', () => {
  it('renders without crashing', () => {
    render(<App />)
    expect(screen.getByText('Hydra')).toBeInTheDocument()
  })

  it('shows Master and Slave toggle buttons', () => {
    render(<App />)
    expect(screen.getByRole('button', { name: /master/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /slave/i })).toBeInTheDocument()
  })

  it('shows Visual and Manual toggle in Master mode', () => {
    render(<App />)
    expect(screen.getByRole('button', { name: /visual/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /manual/i })).toBeInTheDocument()
  })

  it('shows canvas add button in visual mode', () => {
    render(<App />)
    expect(screen.getByText('+ Add Screen')).toBeInTheDocument()
  })

  it('hides canvas and shows host editor in Manual mode', async () => {
    const user = userEvent.setup()
    render(<App />)
    await user.click(screen.getByRole('button', { name: /manual/i }))
    expect(screen.getByText('+ Add Host')).toBeInTheDocument()
  })

  it('hides hosts section in Slave mode', async () => {
    const user = userEvent.setup()
    render(<App />)
    await user.click(screen.getByRole('button', { name: /slave/i }))
    expect(screen.queryByText('+ Add Host')).not.toBeInTheDocument()
    expect(screen.queryByText('+ Add Screen')).not.toBeInTheDocument()
  })

  it('can add a screen block in visual mode', async () => {
    const user = userEvent.setup()
    render(<App />)
    await user.click(screen.getByText('+ Add Screen'))
    // add form should appear
    expect(screen.getByPlaceholderText(/e.g. desktop/i)).toBeInTheDocument()
  })

  it('can add and see a host in manual mode', async () => {
    const user = userEvent.setup()
    render(<App />)
    await user.click(screen.getByRole('button', { name: /manual/i }))
    await user.click(screen.getByText('+ Add Host'))
    expect(screen.getByText('Host 1')).toBeInTheDocument()
  })

  it('resets to initial state', async () => {
    const user = userEvent.setup()
    render(<App />)
    await user.click(screen.getByRole('button', { name: /manual/i }))
    await user.click(screen.getByText('+ Add Host'))
    await user.click(screen.getByText('Reset'))
    expect(screen.queryByText('Host 1')).not.toBeInTheDocument()
  })

  it('always shows profile tabs', () => {
    render(<App />)
    expect(screen.getByText('Profile 1')).toBeInTheDocument()
  })
})
