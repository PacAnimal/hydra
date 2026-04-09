import '@testing-library/jest-dom'
import { vi } from 'vitest'

// jsdom doesn't implement HTMLDialogElement — stub globally
HTMLDialogElement.prototype.showModal = vi.fn()
HTMLDialogElement.prototype.close = vi.fn()

// stub clipboard
Object.defineProperty(navigator, 'clipboard', {
  value: { writeText: vi.fn().mockResolvedValue(undefined) },
  writable: true,
  configurable: true,
})
