import AppKit
import Network
import CoreWLAN
import CoreLocation

// tiny shield app — places a topmost window over the cursor park position (main screen center)
// to absorb hover events while the KVM cursor is hidden there.
// no dock icon, no menu bar. controlled via stdin: "0" = hide, "1" = show (invisible), "2" = show (visible red, debug).
// automatically suppresses itself while a fullscreen app (e.g. a game) is running.
// runs until killed by the parent process.
//
// also handles macOS network state detection on behalf of the Hydra parent:
//   stdout "ssid:NetworkName" — current WiFi SSID (empty = not connected / not authorized)
//   stdout "wired:1" / "wired:0" — wired Ethernet presence
// emitted on startup and whenever state changes.

// writes a line to stdout; exits cleanly if the pipe is broken (parent restarted)
func writeState(_ line: String) {
    let data = Data((line + "\n").utf8)
    let result = data.withUnsafeBytes { Darwin.write(STDOUT_FILENO, $0.baseAddress!, $0.count) }
    if result == -1 { exit(0) }
}

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

class ShieldDelegate: NSObject, NSApplicationDelegate, CLLocationManagerDelegate, CWEventDelegate {
    var window: NSWindow?
    var desiredAbsorb = false
    var debugMode = false

    private var pathMonitor: NWPathMonitor?
    private var locationManager: CLLocationManager?
    private var reportedWired: Bool? = nil // nil = not yet reported

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

        startNetworkMonitoring()
    }

    // MARK: - Network monitoring

    private func startNetworkMonitoring() {
        startWiredMonitoring()
        startWifiMonitoring()
    }

    // NWPathMonitor watches wired Ethernet — no permissions required
    private func startWiredMonitoring() {
        let monitor = NWPathMonitor(requiredInterfaceType: .wiredEthernet)
        monitor.pathUpdateHandler = { [weak self] path in
            let wired = path.status == .satisfied
            DispatchQueue.main.async {
                guard let self else { return }
                if self.reportedWired != wired {
                    self.reportedWired = wired
                    writeState("wired:\(wired ? 1 : 0)")
                }
            }
        }
        monitor.start(queue: DispatchQueue.global(qos: .background))
        pathMonitor = monitor
    }

    // CLLocationManager + CWWiFiClient for SSID — requires Location Services authorization
    private func startWifiMonitoring() {
        let mgr = CLLocationManager()
        mgr.delegate = self
        locationManager = mgr

        let status = mgr.authorizationStatus
        writeState("wifiauth:\(status.rawValue)") // 0=notDetermined 1=restricted 2=denied 3=authorized 4=authorizedAlways
        switch status {
        case .notDetermined:
            mgr.requestAlwaysAuthorization()
        case .authorized, .authorizedAlways:
            startCoreWlan()
        default:
            writeState("ssid:")
        }
    }

    func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) {
        writeState("wifiauth:\(manager.authorizationStatus.rawValue)")
        switch manager.authorizationStatus {
        case .authorized, .authorizedAlways:
            startCoreWlan()
        default:
            writeState("ssid:")
        }
    }

    private func startCoreWlan() {
        let client = CWWiFiClient.shared()
        client.delegate = self
        try? client.startMonitoringEvent(with: .ssidDidChange)
        reportSsid()
    }

    func ssidDidChangeForWiFiInterface(withName interfaceName: String) {
        reportSsid()
    }

    private func reportSsid() {
        let ssid = CWWiFiClient.shared().interface()?.ssid() ?? ""
        writeState("ssid:\(ssid)")
    }

    // MARK: - Shield window

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

signal(SIGPIPE, SIG_IGN)
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let delegate = ShieldDelegate()
app.delegate = delegate
app.run()
