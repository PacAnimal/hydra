import AppKit

// tiny shield app — places a topmost window over the cursor park position (main screen center)
// to absorb hover events while the KVM cursor is hidden there.
// no dock icon, no menu bar. controlled via stdin: "0" = hide, "1" = show (invisible), "2" = show (visible red, debug).
// automatically suppresses itself while a fullscreen app (e.g. a game) is running.
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
    var desiredAbsorb = false
    var debugMode = false

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

        // poll for fullscreen apps every 500ms; suppress window while one is active
        Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            self?.applyState()
        }

        // read commands from parent process: "0" = pass-through, "1" = absorb, "2" = absorb + visible (debug)
        DispatchQueue.global(qos: .background).async { [weak self] in
            while let line = readLine(strippingNewline: true) {
                DispatchQueue.main.async {
                    guard let self else { return }
                    switch line {
                    case "1":
                        self.desiredAbsorb = true
                        self.debugMode = false
                    case "2":
                        self.desiredAbsorb = true
                        self.debugMode = true
                    default: // "0"
                        self.desiredAbsorb = false
                        self.debugMode = false
                    }
                    self.applyState()
                }
            }
        }
    }

    func applyState() {
        guard let w = window else { return }
        let absorb = desiredAbsorb && !isFullscreenAppActive()
        if absorb {
            w.backgroundColor = debugMode ? .red : .clear
            w.alphaValue = debugMode ? 0.2 : 0.01
            w.ignoresMouseEvents = false
        } else {
            w.backgroundColor = .clear
            w.alphaValue = 0.01
            w.ignoresMouseEvents = true
        }
    }

    // any of these options indicate an app has taken over the display (e.g. a fullscreen game)
    func isFullscreenAppActive() -> Bool {
        let opts = NSApp.currentSystemPresentationOptions
        return opts.contains(.fullScreen)
            || opts.contains(.hideMenuBar)
            || opts.contains(.autoHideMenuBar)
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let delegate = ShieldDelegate()
app.delegate = delegate
app.run()
