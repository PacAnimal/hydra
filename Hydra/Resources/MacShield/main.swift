import AppKit
import CoreLocation
import CoreWLAN

// tiny shield app — places a topmost window over the cursor park position (main screen center)
// to absorb hover events while the KVM cursor is hidden there.
// no dock icon, no menu bar. controlled by the Hydra parent over stdin/stdout.
// automatically suppresses itself while a fullscreen app (e.g. a game) is running.
// runs until killed by the parent process or until stdin closes.
//
// also reports the WiFi SSID and location auth, and the ARP table, on behalf of the parent, shows OSD notifications
// and a file transfer progress panel.
//
// protocol: framed messagepack, see the table in Hydra/Platform/MacOs/ShieldChannel.cs.

// serializes stdout writes: CoreWLAN and CoreLocation callbacks may arrive off the main thread
private let stdoutLock = NSLock()

// writes one framed message to stdout; exits cleanly if the pipe is broken (parent restarted)
func writeMessage(_ kind: String, _ fields: [(String, MsgPackValue)] = []) {
  let data = frame(msgPackEncode(.map([("kind", .string(kind))] + fields)))
  stdoutLock.lock()
  defer { stdoutLock.unlock() }
  var offset = 0
  while offset < data.count {
    let result = data.withUnsafeBytes { Darwin.write(STDOUT_FILENO, $0.baseAddress! + offset, $0.count - offset) }
    if result == -1 {
      if errno == EINTR { continue }
      exit(0)
    }
    offset += result
  }
}

// writes an error line to stderr — for conditions that break the protocol
func writeError(_ message: String) {
  let data = Data((message + "\n").utf8)
  _ = data.withUnsafeBytes { Darwin.write(STDERR_FILENO, $0.baseAddress!, $0.count) }
}

struct Neighbour {
  let ip: String
  let mac: Data
}

// the IPv4 ARP table's resolved entries, from the routing sysctl as arp(8) reads it. Local Network privacy leaves
// the table empty for a process without the grant, which this bundle holds as it holds Location.
// each message: rt_msghdr, then RTA_DST's sockaddr_in and RTA_GATEWAY's sockaddr_dl, each padded to 4 bytes
// nil when the sysctl failed
func readNeighbours() -> [Neighbour]? {
  var mib: [Int32] = [CTL_NET, PF_ROUTE, 0, AF_INET, NET_RT_FLAGS, RTF_LLINFO]
  var buffer: [UInt8] = []
  var length = 0
  // the table may grow between asking its size and reading it, which fails with ENOMEM
  for _ in 0..<3 {
    length = 0
    guard sysctl(&mib, 6, nil, &length, nil, 0) == 0 else { return nil }
    if length == 0 { return [] }
    buffer = [UInt8](repeating: 0, count: length + 4096)
    length = buffer.count
    if sysctl(&mib, 6, &buffer, &length, nil, 0) == 0 { break }
    if errno != ENOMEM { return nil }
    length = -1
  }
  if length < 0 { return nil }

  let header = MemoryLayout<rt_msghdr>.size
  let wanted = RTA_DST | RTA_GATEWAY
  var neighbours: [Neighbour] = []
  var offset = 0
  while offset + header <= length {
    let message = buffer.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: offset, as: rt_msghdr.self) }
    let end = offset + Int(message.rtm_msglen)
    if end <= offset + header || end > length { break }
    defer { offset = end }
    let dst = offset + header
    guard message.rtm_addrs & wanted == wanted, dst + 8 <= end, buffer[dst + 1] == UInt8(AF_INET) else { continue }
    let dstLength = Int(buffer[dst])
    let link = dst + (dstLength > 0 ? 1 + ((dstLength - 1) | 3) : 4)
    guard link + 8 <= end, buffer[link + 1] == UInt8(AF_LINK) else { continue }
    // sockaddr_dl: len, family, index (2), type, nlen, alen, slen, then the name and the address
    let mac = link + 8 + Int(buffer[link + 5])
    // an entry still resolving has no address yet
    guard buffer[link + 6] == 6, mac + 6 <= end else { continue }
    let ip = buffer[(dst + 4)..<(dst + 8)].map { String($0) }.joined(separator: ".")
    neighbours.append(Neighbour(ip: ip, mac: Data(buffer[mac..<(mac + 6)])))
  }
  return neighbours
}

// a datagram to the discard port has the OS resolve the address, and has macOS ask for Local Network access the first
// time; whether it arrives does not matter
func poke(_ address: String) {
  var target = sockaddr_in()
  target.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
  target.sin_family = sa_family_t(AF_INET)
  target.sin_port = in_port_t(9).bigEndian
  guard inet_pton(AF_INET, address, &target.sin_addr) == 1 else { return }
  let fd = socket(AF_INET, SOCK_DGRAM, 0)
  guard fd >= 0 else { return }
  defer { close(fd) }
  var byte: UInt8 = 0
  _ = withUnsafePointer(to: &target) {
    $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { sendto(fd, &byte, 1, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
  }
}

// full-coverage view that participates in hit testing and swallows all mouse events
class AbsorberView: NSView {
  override var acceptsFirstResponder: Bool { true }
  override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

  override func updateTrackingAreas() {
    super.updateTrackingAreas()
    for area in trackingAreas { removeTrackingArea(area) }
    addTrackingArea(
      NSTrackingArea(
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
  private var wifiActive = false  // guard against duplicate "wifi" commands
  private let transferPanel = TransferPanel()
  private let osdPanel = OsdPanel()

  func applicationDidFinishLaunching(_ notification: Notification) {
    guard let screen = NSScreen.main else {
      writeError("no main screen available — shield window cannot be created")
      return
    }
    let level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.maximumWindow)))
    // cover the whole screen; the actual screen is chosen per-absorb from the cursor's location
    let frame = screen.frame

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
    w.ignoresMouseEvents = true  // pass-through until a "show" message arrives
    w.acceptsMouseMovedEvents = true
    w.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
    w.contentView = AbsorberView(frame: NSRect(origin: .zero, size: frame.size))
    w.orderFrontRegardless()  // always composited so the first show is instant
    window = w

    // poll for fullscreen apps every 500ms; suppress window while one is active
    Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
      self?.applyState()
    }

    // read framed messages from the parent process
    DispatchQueue.global(qos: .background).async { [weak self] in
      var reader = FrameReader()
      var chunk = [UInt8](repeating: 0, count: 64 * 1024)
      readLoop: while true {
        let n = chunk.withUnsafeMutableBytes { Darwin.read(STDIN_FILENO, $0.baseAddress!, $0.count) }
        if n < 0 && errno == EINTR { continue }
        if n <= 0 { break }
        reader.append(Data(chunk[0..<n]))
        while true {
          let payload: Data?
          do { payload = try reader.next() } catch {
            // the length prefix is the only framing, so an oversized one leaves nothing to resync on
            writeError("shield frame rejected: \(error)")
            break readLoop
          }
          guard let payload else { break }
          let message: MsgPackValue
          do { message = try msgPackDecode(payload) } catch {
            writeError("shield frame could not be decoded: \(error)")
            continue
          }
          DispatchQueue.main.async { self?.handleMessage(message) }
        }
      }
      // stdin closed — parent process died; exit so we don't linger as an orphan
      DispatchQueue.main.async { NSApplication.shared.terminate(nil) }
    }
  }

  // MARK: - Messages from the parent

  private func handleMessage(_ message: MsgPackValue) {
    guard let kind = message["kind"]?.stringValue else {
      writeError("shield message has no kind; ignored")
      return
    }
    switch kind {
    case "shield":
      guard let seq = message["seq"]?.int64Value,
        let state = message["state"]?.stringValue, ["hide", "show", "debug"].contains(state)
      else {
        writeError("shield message is malformed; ignored")
        return
      }
      desiredAbsorb = state != "hide"
      debugMode = state == "debug"
      applyState()
      writeMessage("ack", [("seq", .int(seq))])
    case "wifi":
      startWifiMonitoring()
    case "neighbours":
      guard let seq = message["seq"]?.int64Value else {
        writeError("neighbours message is malformed; ignored")
        return
      }
      let gateways = message["gateways"]?.arrayValue?.compactMap { $0.stringValue } ?? []
      DispatchQueue.global(qos: .utility).async {
        gateways.forEach(poke)
        // no entries says the table could not be read, which Hydra takes as unknown rather than empty
        guard let neighbours = readNeighbours() else {
          writeMessage("ack", [("seq", .int(seq))])
          return
        }
        let entries = neighbours.map { MsgPackValue.map([("ip", .string($0.ip)), ("mac", .binary($0.mac))]) }
        writeMessage("ack", [("seq", .int(seq)), ("entries", .array(entries))])
      }
    case "osd":
      if let text = message["text"]?.stringValue { osdPanel.show(text) }
    case "transferBegin":
      guard let totalBytes = message["totalBytes"]?.int64Value,
        let fileCount = message["fileCount"]?.int64Value,
        let isSender = message["isSender"]?.boolValue
      else {
        writeError("transferBegin is malformed; ignored")
        return
      }
      let names = message["names"]?.arrayValue?.compactMap { $0.stringValue } ?? []
      transferPanel.showBegin(names: names, totalBytes: totalBytes, fileCount: Int(fileCount), isSender: isSender)
    case "transferFile":
      if let name = message["name"]?.stringValue { transferPanel.updateCurrentFile(name) }
    case "transferProgress":
      if let bytes = message["bytes"]?.int64Value, let speed = message["speed"]?.doubleValue {
        transferPanel.updateProgress(bytes: bytes, speed: speed)
      }
    case "transferDone":
      transferPanel.showCompleted()
    case "transferError":
      transferPanel.showError(message["message"]?.stringValue ?? "")
    case "transferClose":
      transferPanel.close()
    case "echo":
      writeMessage("echo", [("payload", message["payload"] ?? .nil)])
    default:
      writeError("unknown shield message kind '\(kind)'; ignored")
    }
  }

  // MARK: - WiFi monitoring (activated on demand via the "wifi" message)

  // CLLocationManager + CWWiFiClient for SSID — requires Location Services authorization
  private func startWifiMonitoring() {
    guard !wifiActive else { return }
    wifiActive = true

    let mgr = CLLocationManager()
    mgr.delegate = self
    locationManager = mgr

    let status = mgr.authorizationStatus
    writeWifiAuth(status)
    switch status {
    case .notDetermined:
      mgr.requestAlwaysAuthorization()
    case .authorized, .authorizedAlways:
      startCoreWlan()
    default:
      writeSsid(nil)
    }
  }

  func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) {
    writeWifiAuth(manager.authorizationStatus)
    switch manager.authorizationStatus {
    case .authorized, .authorizedAlways:
      startCoreWlan()
    default:
      writeSsid(nil)
    }
  }

  // 0=notDetermined 1=restricted 2=denied 3=authorized 4=authorizedAlways
  private func writeWifiAuth(_ status: CLAuthorizationStatus) {
    writeMessage("wifiAuth", [("status", .int(Int64(status.rawValue)))])
  }

  // nil when not connected or not authorized
  private func writeSsid(_ name: String?) {
    writeMessage("ssid", [("name", name.map { .string($0) } ?? .nil)])
  }

  private func startCoreWlan() {
    let client = CWWiFiClient.shared()
    client.delegate = self
    do { try client.startMonitoringEvent(with: .ssidDidChange) } catch {
      writeError("CWWiFiClient startMonitoringEvent failed: \(error)")
    }
    reportSsid()
  }

  func ssidDidChangeForWiFiInterface(withName interfaceName: String) {
    reportSsid()
  }

  private func reportSsid() {
    let ssid = CWWiFiClient.shared().interface()?.ssid()
    writeSsid(ssid?.isEmpty == false ? ssid : nil)
  }

  // MARK: - Shield window

  func applyState() {
    guard let w = window else { return }
    let absorb = desiredAbsorb && !isFullscreenAppActive()
    if absorb {
      positionOverCursorScreen(w)
      w.backgroundColor = debugMode ? .red : .clear
      w.alphaValue = debugMode ? 0.2 : 0.01
      w.ignoresMouseEvents = false
      w.orderFrontRegardless()
    } else {
      w.backgroundColor = .clear
      w.alphaValue = 0.01
      w.ignoresMouseEvents = true
    }
  }

  // cover the full screen that currently contains the cursor, so local hover is absorbed on whichever
  // monitor the KVM parks the cursor on — and it follows display reconfiguration (dock/undock, clamshell).
  // previously the window frame was fixed once at launch to NSScreen.main, which after a display change
  // could leave it stranded on the wrong monitor entirely, absorbing nothing where the cursor actually is.
  private func positionOverCursorScreen(_ w: NSWindow) {
    let mouse = NSEvent.mouseLocation
    guard
      let screen = NSScreen.screens.first(where: { NSMouseInRect(mouse, $0.frame, false) })
        ?? NSScreen.main ?? NSScreen.screens.first
    else { return }
    if w.frame != screen.frame { w.setFrame(screen.frame, display: false) }
  }

  // only a genuine fullscreen app (e.g. a game) should suppress the shield. deliberately does NOT key off
  // menu-bar hiding — that is a common desktop setting and would wrongly suppress the shield in normal use.
  func isFullscreenAppActive() -> Bool {
    NSApp.currentSystemPresentationOptions.contains(.fullScreen)
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

  func updateCurrentFile(_ name: String) {
    subtitleLabel?.stringValue = name
  }

  func updateProgress(bytes: Int64, speed: Double) {
    if totalBytes > 0 {
      progressBar?.doubleValue = Double(bytes) / Double(totalBytes) * 100.0
    }
    let speedStr = TransferPanel.formatSpeed(speed)
    statusLabel?.stringValue =
      "\(TransferPanel.formatBytes(bytes)) / \(TransferPanel.formatBytes(totalBytes))  ·  \(speedStr)"
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

    let w: CGFloat = 360
    let h: CGFloat = 140
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
    writeMessage("transferCancel")
    close()
  }

  private static func formatNames(_ names: [String]) -> String {
    let joined = names.prefix(3).joined(separator: ", ")
    return names.count > 3 ? "\(joined) +\(names.count - 3) more" : joined
  }

  private static func formatBytes(_ bytes: Int64) -> String {
    let kb: Double = 1024
    let mb = kb * 1024
    let gb = mb * 1024
    let d = Double(bytes)
    if d >= gb { return String(format: "%.1f GB", d / gb) }
    if d >= mb { return String(format: "%.1f MB", d / mb) }
    if d >= kb { return String(format: "%.0f KB", d / kb) }
    return "\(bytes) B"
  }

  private static func formatSpeed(_ bps: Double) -> String {
    let kb: Double = 1024
    let mb = kb * 1024
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

  override init() {
    super.init()
    buildWindowIfNeeded()
  }

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
    let screen =
      NSScreen.screens.first { $0.frame.contains(mouse) } ?? NSScreen.main ?? NSScreen.screens[0]
    let sx = screen.frame.midX - wW / 2
    let sy = screen.frame.minY + screen.frame.height * 0.1

    osdView?.setContent(attrStr)
    w.setFrame(NSRect(x: sx, y: sy, width: wW, height: wH), display: false)
    // fill the content view (autoresizingMask handles it after the frame change)

    w.alphaValue = 1.0
    w.orderFrontRegardless()

    dismissTimer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: false) { [weak self] _ in
      NSAnimationContext.runAnimationGroup(
        { ctx in
          ctx.duration = 0.3
          self?.window?.animator().alphaValue = 0.0
        },
        completionHandler: {
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
    return NSAttributedString(
      string: text,
      attributes: [
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
