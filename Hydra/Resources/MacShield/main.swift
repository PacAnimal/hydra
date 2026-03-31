import AppKit

// tiny shield app — places a topmost window over the cursor park position (main screen center)
// to absorb hover events while the KVM cursor is hidden there.
// no dock icon, no menu bar. controlled via stdin: "0" = hide, "1" = show (invisible), "2" = show (visible red, debug).
// runs until killed by the parent process.

// full-coverage view that participates in hit testing and swallows all mouse events
class AbsorberView: NSView {
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        for area in trackingAreas { removeTrackingArea(area) }
        addTrackingArea(NSTrackingArea(
            rect: bounds,
            options: [.mouseEnteredAndExited, .mouseMoved, .activeAlways],
            owner: self,
            userInfo: nil))
    }

    override func mouseEntered(with event: NSEvent) {}
    override func mouseExited(with event: NSEvent) {}
    override func mouseMoved(with event: NSEvent) {}
    override func mouseDown(with event: NSEvent) {}
    override func mouseUp(with event: NSEvent) {}
}

class ShieldDelegate: NSObject, NSApplicationDelegate {
    var window: NSWindow?

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard let screen = NSScreen.main else { return }
        let level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.maximumWindow)))
        let w10 = screen.frame.width * 0.1
        let h10 = screen.frame.height * 0.1
        let frame = NSRect(x: screen.frame.midX - w10 / 2, y: screen.frame.midY - h10 / 2, width: w10, height: h10)

        let w = NSWindow(
            contentRect: frame,
            styleMask: .borderless,
            backing: .buffered,
            defer: false,
            screen: screen)
        w.level = level
        w.backgroundColor = .clear
        w.alphaValue = 0.01
        w.isOpaque = false
        w.hasShadow = false
        w.ignoresMouseEvents = true // pass-through until "1" command received
        w.acceptsMouseMovedEvents = true
        w.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
        w.contentView = AbsorberView(frame: NSRect(origin: .zero, size: frame.size))
        w.orderFrontRegardless() // always composited so first "1" is instant
        window = w

        // read commands from parent process: "0" = pass-through, "1" = absorb, "2" = absorb + visible (debug)
        DispatchQueue.global(qos: .background).async { [weak self] in
            while let line = readLine(strippingNewline: true) {
                DispatchQueue.main.async {
                    guard let w = self?.window else { return }
                    switch line {
                    case "1":
                        w.backgroundColor = .clear
                        w.alphaValue = 0.01
                        w.ignoresMouseEvents = false
                    case "2":
                        w.backgroundColor = .red
                        w.alphaValue = 0.2
                        w.ignoresMouseEvents = false
                    default: // "0"
                        w.backgroundColor = .clear
                        w.alphaValue = 0.01
                        w.ignoresMouseEvents = true
                    }
                }
            }
        }
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let delegate = ShieldDelegate()
app.delegate = delegate
app.run()
