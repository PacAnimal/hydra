import AppKit
import CoreWLAN
import CoreLocation

// tiny shield app — places a topmost window over the cursor park position (main screen center)
// to absorb hover events while the KVM cursor is hidden there.
// no dock icon, no menu bar. controlled via stdin: CmdHide/CmdShow/CmdDebug.
// automatically suppresses itself while a fullscreen app (e.g. a game) is running.
// runs until killed by the parent process.
//
// also handles macOS WiFi SSID detection on behalf of the Hydra parent:
//   stdin CmdWifi         — activates WiFi monitoring (idempotent); requests location auth if needed
//   stdout PfxSsid+Name   — current WiFi SSID (empty = not connected / not authorized)
//   stdout PfxWifiAuth+n  — CLAuthorizationStatus raw value (0=notDetermined 1=restricted 2=denied 3/4=authorized)
// emitted on activation and whenever SSID changes.
//
// also manages a file transfer progress panel:
//   stdin "transfer:begin;{totalBytes};{fileCount};{isSender};{b64name1|b64name2|...}"  — show panel in transferring mode (names are base64-encoded utf-8)
//   stdin "transfer:pending;{totalBytes};{fileCount};{isSender};{b64name1|b64name2|...}" — show pending state
//   stdin "transfer:start"                                                              — switch to progress bar
//   stdin "transfer:progress:{bytes}:{speed}"                                          — update progress
//   stdin "transfer:file:{base64-filename}"                                            — update current file name
//   stdin "transfer:done"                                                              — show completed, auto-close
//   stdin "transfer:error:{message}"                                                   — show error
//   stdin "transfer:close"                                                             — force close
//   stdout "transfer:cancel"                                                           — user clicked cancel

// stdin commands (C# → shield)
let CmdHide  = "0"
let CmdShow  = "1"
let CmdDebug = "2"
let CmdWifi  = "wifi"
let CmdTransferPrefix  = "transfer:"
let CmdOsdPrefix       = "osd:"

// stdout prefixes (shield → C#)
let PfxSsid             = "ssid:"
let PfxWifiAuth         = "wifiauth:"
let PfxTransferCancel   = "transfer:cancel"

// writes a line to stdout; exits cleanly if the pipe is broken (parent restarted)
func writeState(_ line: String) {
    let data = Data((line + "\n").utf8)
    let result = data.withUnsafeBytes { Darwin.write(STDOUT_FILENO, $0.baseAddress!, $0.count) }
    if result == -1 { exit(0) }
}

// writes an error line to stderr — for conditions that break the protocol
func writeError(_ message: String) {
    let data = Data((message + "\n").utf8)
    _ = data.withUnsafeBytes { Darwin.write(STDERR_FILENO, $0.baseAddress!, $0.count) }
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

    private var locationManager: CLLocationManager?
    private var wifiActive = false // guard against duplicate "wifi" commands
    private let transferPanel = TransferPanel()
    private let osdPanel = OsdPanel()

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard let screen = NSScreen.main else {
            writeError("no main screen available — shield window cannot be created")
            return
        }
        let level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.maximumWindow)))
        let w10 = screen.frame.width * 0.2
        let h10 = screen.frame.height * 0.2
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
        w.orderFrontRegardless() // always composited so first CmdShow is instant
        window = w

        // poll for fullscreen apps every 500ms; suppress window while one is active
        Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            self?.applyState()
        }

        // read commands from parent process
        DispatchQueue.global(qos: .background).async { [weak self] in
            while let line = readLine(strippingNewline: true) {
                DispatchQueue.main.async {
                    guard let self else { return }
                    if line.hasPrefix(CmdOsdPrefix) {
                        let b64 = String(line.dropFirst(CmdOsdPrefix.count))
                        let msg = Data(base64Encoded: b64).flatMap { String(data: $0, encoding: .utf8) } ?? b64
                        self.osdPanel.show(msg)
                    } else if line.hasPrefix(CmdTransferPrefix) {
                        self.handleTransferCommand(line)
                    } else {
                        switch line {
                        case CmdShow:
                            self.desiredAbsorb = true
                            self.debugMode = false
                            self.applyState()
                            writeState(CmdShow)
                        case CmdDebug:
                            self.desiredAbsorb = true
                            self.debugMode = true
                            self.applyState()
                            writeState(CmdDebug)
                        case CmdWifi:
                            self.startWifiMonitoring()
                        default: // CmdHide
                            self.desiredAbsorb = false
                            self.debugMode = false
                            self.applyState()
                            writeState(CmdHide)
                        }
                    }
                }
            }
            // stdin closed — parent process died; exit so we don't linger as an orphan
            DispatchQueue.main.async { NSApplication.shared.terminate(nil) }
        }
    }

    // MARK: - WiFi monitoring (activated on demand via "wifi" stdin command)

    // CLLocationManager + CWWiFiClient for SSID — requires Location Services authorization
    private func startWifiMonitoring() {
        guard !wifiActive else { return }
        wifiActive = true

        let mgr = CLLocationManager()
        mgr.delegate = self
        locationManager = mgr

        let status = mgr.authorizationStatus
        writeState("\(PfxWifiAuth)\(status.rawValue)") // 0=notDetermined 1=restricted 2=denied 3=authorized 4=authorizedAlways
        switch status {
        case .notDetermined:
            mgr.requestAlwaysAuthorization()
        case .authorized, .authorizedAlways:
            startCoreWlan()
        default:
            writeState(PfxSsid)
        }
    }

    func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) {
        writeState("\(PfxWifiAuth)\(manager.authorizationStatus.rawValue)")
        switch manager.authorizationStatus {
        case .authorized, .authorizedAlways:
            startCoreWlan()
        default:
            writeState(PfxSsid)
        }
    }

    private func startCoreWlan() {
        let client = CWWiFiClient.shared()
        client.delegate = self
        do { try client.startMonitoringEvent(with: .ssidDidChange) }
        catch { writeError("CWWiFiClient startMonitoringEvent failed: \(error)") }
        reportSsid()
    }

    func ssidDidChangeForWiFiInterface(withName interfaceName: String) {
        reportSsid()
    }

    private func reportSsid() {
        let ssid = CWWiFiClient.shared().interface()?.ssid() ?? ""
        writeState("\(PfxSsid)\(ssid)")
    }

    // MARK: - Transfer panel commands

    // handles "transfer:begin;{totalBytes};{fileCount};{isSender};{b64name1|b64name2|...}"
    //          "transfer:pending;{totalBytes};{fileCount};{isSender};{b64name1|b64name2|...}"
    //          "transfer:start"
    //          "transfer:progress:{bytes}:{speed}"
    //          "transfer:file:{base64-filename}"
    //          "transfer:done"
    //          "transfer:error:{message}"
    //          "transfer:close"
    private func handleTransferCommand(_ line: String) {
        let payload = String(line.dropFirst(CmdTransferPrefix.count))
        if payload == "start" {
            transferPanel.showTransferring()
            return
        }
        if payload.hasPrefix("file:") {
            let b64 = String(payload.dropFirst("file:".count))
            let name = Data(base64Encoded: b64).flatMap { String(data: $0, encoding: .utf8) } ?? b64
            transferPanel.updateCurrentFile(name)
            return
        }
        if payload == "done" {
            transferPanel.showCompleted()
            return
        }
        if payload == "close" {
            transferPanel.close()
            return
        }
        if payload.hasPrefix("progress:") {
            let parts = payload.dropFirst("progress:".count).split(separator: ":", maxSplits: 1)
            if parts.count == 2,
               let bytes = Int64(parts[0]),
               let speed = Double(parts[1]) {
                transferPanel.updateProgress(bytes: bytes, speed: speed)
            }
            return
        }
        if payload.hasPrefix("error:") {
            let b64 = String(payload.dropFirst("error:".count))
            let msg = Data(base64Encoded: b64).flatMap { String(data: $0, encoding: .utf8) } ?? b64
            transferPanel.showError(msg)
            return
        }
        if payload.hasPrefix("begin;") || payload.hasPrefix("pending;") {
            // format: begin/pending;{totalBytes};{fileCount};{isSender};{b64name1|b64name2|...}
            // names are base64-encoded utf-8 to handle special characters
            let isBegin = payload.hasPrefix("begin;")
            let rest = payload.dropFirst(isBegin ? "begin;".count : "pending;".count)
            let parts = rest.split(separator: ";", maxSplits: 3)
            if parts.count == 4,
               let totalBytes = Int64(parts[0]),
               let fileCount = Int(parts[1]),
               let isSender = Bool(String(parts[2])) {
                let names = parts[3].split(separator: "|").compactMap { b64 -> String? in
                    guard let data = Data(base64Encoded: String(b64)) else { return nil }
                    return String(data: data, encoding: .utf8)
                }
                if isBegin {
                    transferPanel.showBegin(names: names, totalBytes: totalBytes, fileCount: fileCount, isSender: isSender)
                } else {
                    transferPanel.showPending(names: names, totalBytes: totalBytes, fileCount: fileCount, isSender: isSender)
                }
            }
            return
        }
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

// MARK: - Transfer panel

class TransferPanel: NSObject {
    private var panel: NSPanel?
    private var titleLabel: NSTextField?
    private var subtitleLabel: NSTextField?
    private var progressBar: NSProgressIndicator?
    private var statusLabel: NSTextField?
    private var cancelButton: NSButton?
    private var autoCloseTimer: Timer?

    private var totalBytes: Int64 = 0

    func showBegin(names: [String], totalBytes: Int64, fileCount: Int, isSender: Bool) {
        self.totalBytes = totalBytes
        buildPanelIfNeeded()
        autoCloseTimer?.invalidate()

        let verb = isSender ? "Sending" : "Receiving"
        let nameStr = Self.formatNames(names)
        titleLabel?.stringValue = "\(verb) \(fileCount) file\(fileCount == 1 ? "" : "s")"
        subtitleLabel?.stringValue = nameStr
        statusLabel?.stringValue = "Transferring…"
        progressBar?.isIndeterminate = false
        progressBar?.doubleValue = 0
        cancelButton?.title = "Cancel"
        cancelButton?.isHidden = false
        panel?.orderFrontRegardless()
    }

    func showPending(names: [String], totalBytes: Int64, fileCount: Int, isSender: Bool) {
        self.totalBytes = totalBytes
        buildPanelIfNeeded()
        autoCloseTimer?.invalidate()

        let verb = isSender ? "Sending" : "Receiving"
        let nameStr = Self.formatNames(names)
        titleLabel?.stringValue = "\(verb) \(fileCount) file\(fileCount == 1 ? "" : "s")"
        subtitleLabel?.stringValue = nameStr
        statusLabel?.stringValue = isSender ? "Drop to send" : "Waiting for sender…"
        progressBar?.isIndeterminate = true
        progressBar?.startAnimation(nil)
        cancelButton?.title = "Cancel"
        cancelButton?.isHidden = false
        panel?.orderFrontRegardless()
    }

    func showTransferring() {
        buildPanelIfNeeded()
        statusLabel?.stringValue = "Transferring…"
        progressBar?.isIndeterminate = false
        progressBar?.doubleValue = 0
        panel?.orderFrontRegardless()
    }

    func updateCurrentFile(_ name: String) {
        subtitleLabel?.stringValue = name
    }

    func updateProgress(bytes: Int64, speed: Double) {
        if totalBytes > 0 {
            progressBar?.doubleValue = Double(bytes) / Double(totalBytes) * 100.0
        }
        let speedStr = TransferPanel.formatSpeed(speed)
        statusLabel?.stringValue = "\(TransferPanel.formatBytes(bytes)) / \(TransferPanel.formatBytes(totalBytes))  ·  \(speedStr)"
    }

    func showCompleted() {
        progressBar?.doubleValue = 100
        statusLabel?.stringValue = "Transfer complete"
        cancelButton?.isHidden = true
        autoCloseTimer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: false) { [weak self] _ in
            self?.close()
        }
    }

    func showError(_ message: String) {
        progressBar?.isIndeterminate = false
        statusLabel?.stringValue = "Error: \(message)"
        cancelButton?.title = "Close"
    }

    func close() {
        autoCloseTimer?.invalidate()
        autoCloseTimer = nil
        panel?.orderOut(nil)
        progressBar?.doubleValue = 0  // reset while hidden so next open starts clean
    }

    private func buildPanelIfNeeded() {
        guard panel == nil else { return }

        let w: CGFloat = 360, h: CGFloat = 140
        let screen = NSScreen.main ?? NSScreen.screens[0]
        let sx = screen.visibleFrame.midX - w / 2
        let sy = screen.visibleFrame.maxY - h - 60

        let p = NSPanel(
            contentRect: NSRect(x: sx, y: sy, width: w, height: h),
            styleMask: [.titled, .nonactivatingPanel],
            backing: .buffered,
            defer: false)
        p.title = "Hydra File Transfer"
        p.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.floatingWindow)))
        p.collectionBehavior = [.canJoinAllSpaces, .stationary]
        p.isReleasedWhenClosed = false

        let cv = p.contentView!

        // title
        let tl = NSTextField(labelWithString: "")
        tl.font = NSFont.systemFont(ofSize: 13, weight: .semibold)
        tl.frame = NSRect(x: 16, y: h - 38, width: w - 32, height: 20)
        cv.addSubview(tl)
        titleLabel = tl

        // subtitle (file names)
        let sl = NSTextField(labelWithString: "")
        sl.font = NSFont.systemFont(ofSize: 11)
        sl.textColor = .secondaryLabelColor
        sl.lineBreakMode = .byTruncatingTail
        sl.frame = NSRect(x: 16, y: h - 58, width: w - 32, height: 16)
        cv.addSubview(sl)
        subtitleLabel = sl

        // progress bar
        let pb = NSProgressIndicator()
        pb.style = .bar
        pb.minValue = 0
        pb.maxValue = 100
        pb.frame = NSRect(x: 16, y: h - 88, width: w - 32, height: 16)
        cv.addSubview(pb)
        progressBar = pb

        // status label
        let stl = NSTextField(labelWithString: "")
        stl.font = NSFont.systemFont(ofSize: 10)
        stl.textColor = .secondaryLabelColor
        stl.frame = NSRect(x: 16, y: h - 106, width: w - 100, height: 14)
        cv.addSubview(stl)
        statusLabel = stl

        // cancel button
        let btn = NSButton(title: "Cancel", target: self, action: #selector(onCancel))
        btn.frame = NSRect(x: w - 88, y: 12, width: 72, height: 22)
        btn.bezelStyle = .rounded
        cv.addSubview(btn)
        cancelButton = btn

        panel = p
    }

    @objc private func onCancel() {
        writeState(PfxTransferCancel)
        close()
    }

    private static func formatNames(_ names: [String]) -> String {
        let joined = names.prefix(3).joined(separator: ", ")
        return names.count > 3 ? "\(joined) +\(names.count - 3) more" : joined
    }

    private static func formatBytes(_ bytes: Int64) -> String {
        let kb: Double = 1024, mb = kb * 1024, gb = mb * 1024
        let d = Double(bytes)
        if d >= gb { return String(format: "%.1f GB", d / gb) }
        if d >= mb { return String(format: "%.1f MB", d / mb) }
        if d >= kb { return String(format: "%.0f KB", d / kb) }
        return "\(bytes) B"
    }

    private static func formatSpeed(_ bps: Double) -> String {
        let kb: Double = 1024, mb = kb * 1024
        if bps >= mb { return String(format: "%.1f MB/s", bps / mb) }
        if bps >= kb { return String(format: "%.0f KB/s", bps / kb) }
        return String(format: "%.0f B/s", bps)
    }
}

// MARK: - OSD notification

// custom view that draws outlined text centered in its bounds
class OsdView: NSView {
    private var content: NSAttributedString?

    func setContent(_ str: NSAttributedString) {
        content = str
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let str = content else { return }
        let sz = str.size()
        let pt = NSPoint(x: (bounds.width - sz.width) / 2, y: (bounds.height - sz.height) / 2)

        // shadow pass: draw twice to accumulate opacity without widening the spread
        NSGraphicsContext.current?.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor(white: 0, alpha: 0.95)
        shadow.shadowBlurRadius = 4
        shadow.shadowOffset = .zero
        shadow.set()
        str.draw(at: pt)
        str.draw(at: pt)
        NSGraphicsContext.current?.restoreGraphicsState()

        // clean pass on top: covers shadow that bled into the glyph interior
        str.draw(at: pt)
    }
}

class OsdPanel: NSObject {
    private var window: NSWindow?
    private var osdView: OsdView?
    private var dismissTimer: Timer?

    func show(_ message: String) {
        buildWindowIfNeeded()
        guard let w = window else { return }

        dismissTimer?.invalidate()
        dismissTimer = nil

        let attrStr = makeAttributedString(message)
        // use size() for measurement — works without a layout context
        // add extra padding for the stroke (which extends outside the glyph bounding box)
        let textSz = attrStr.size()
        let strokePad: CGFloat = 6
        let padH: CGFloat = 16
        let padV: CGFloat = 12
        let wW = ceil(textSz.width) + padH * 2 + strokePad * 2
        let wH = ceil(textSz.height) + padV * 2 + strokePad * 2

        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { $0.frame.contains(mouse) } ?? NSScreen.main ?? NSScreen.screens[0]
        let sx = screen.frame.midX - wW / 2
        let sy = screen.frame.minY + screen.frame.height * 0.1

        osdView?.setContent(attrStr)
        w.setFrame(NSRect(x: sx, y: sy, width: wW, height: wH), display: false)
        // fill the content view (autoresizingMask handles it after the frame change)

        w.alphaValue = 1.0
        w.orderFrontRegardless()

        dismissTimer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: false) { [weak self] _ in
            NSAnimationContext.runAnimationGroup({ ctx in
                ctx.duration = 0.3
                self?.window?.animator().alphaValue = 0.0
            }, completionHandler: {
                self?.window?.orderOut(nil)
            })
        }
    }

    private func buildWindowIfNeeded() {
        guard window == nil else { return }

        let w = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 100, height: 50),
            styleMask: .borderless,
            backing: .buffered,
            defer: false)
        w.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.floatingWindow)))
        w.backgroundColor = .clear
        w.isOpaque = false
        w.hasShadow = false
        w.ignoresMouseEvents = true
        w.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
        w.isReleasedWhenClosed = false

        let view = OsdView(frame: w.contentView?.bounds ?? NSRect(x: 0, y: 0, width: 100, height: 50))
        view.autoresizingMask = [.width, .height]
        w.contentView?.addSubview(view)
        osdView = view
        window = w
    }

    private func makeAttributedString(_ text: String) -> NSAttributedString {
        let font = NSFont.systemFont(ofSize: 28, weight: .bold)
        return NSAttributedString(string: text, attributes: [
            .font: font,
            .foregroundColor: NSColor(white: 0.96, alpha: 1.0),
        ])
    }
}

signal(SIGPIPE, SIG_IGN)
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let delegate = ShieldDelegate()
app.delegate = delegate
app.run()
