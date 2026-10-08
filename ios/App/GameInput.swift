import SwiftUI
import WebKit
import GameController

/// Maps touch and physical-controller actions to actual keyboard events in the web game.
/// Compatibility depends on the game's bindings; this is not an emulator or a promised universal pad.
enum InputPreset: String, CaseIterable, Identifiable {
    case arrows, wasd
    var id: String { rawValue }
    var title: String { self == .arrows ? "Стрелки" : "WASD" }
    var directions: [String] { self == .arrows ? ["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"] : ["KeyW", "KeyS", "KeyA", "KeyD"] }
}

@MainActor
final class GameInput: ObservableObject {
    @Published private(set) var controllerName: String?
    @Published private(set) var keyboardConnected = false
    @Published private(set) var pressed = Set<String>()
    weak var webView: WKWebView?
    var preset: InputPreset = .arrows { didSet { if oldValue != preset { releaseAll() } } }
    private var owners: [String: Set<String>] = [:]
    private var observers: [NSObjectProtocol] = []
    private var controllers: [GCController] = []
    private var accepting = true
    func start() {
        guard observers.isEmpty else { return }
        for name in [NSNotification.Name.GCControllerDidConnect, .GCControllerDidDisconnect, .GCKeyboardDidConnect, .GCKeyboardDidDisconnect] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.refreshDevices() }
            })
        }
        refreshDevices()
    }
    private func refreshDevices() {
        releaseAll()
        for controller in controllers { controller.extendedGamepad?.valueChangedHandler = nil }
        controllers = GCController.controllers()
        keyboardConnected = GCKeyboard.coalesced != nil
        controllerName = controllers.first?.vendorName
        for controller in controllers {
            controller.extendedGamepad?.valueChangedHandler = { [weak self, weak controller] pad, _ in
                let x = abs(pad.dpad.xAxis.value) > abs(pad.leftThumbstick.xAxis.value) ? pad.dpad.xAxis.value : pad.leftThumbstick.xAxis.value
                let y = abs(pad.dpad.yAxis.value) > abs(pad.leftThumbstick.yAxis.value) ? pad.dpad.yAxis.value : pad.leftThumbstick.yAxis.value
                let a = pad.buttonA.isPressed, b = pad.buttonB.isPressed, xButton = pad.buttonX.isPressed, yButton = pad.buttonY.isPressed
                Task { @MainActor in
                    guard let self, let controller else { return }
                    var keys = Set<String>()
                    let directions = self.preset.directions
                    if y > 0.35 { keys.insert(directions[0]) }; if y < -0.35 { keys.insert(directions[1]) }
                    if x < -0.35 { keys.insert(directions[2]) }; if x > 0.35 { keys.insert(directions[3]) }
                    if a { keys.insert("Space") }; if b { keys.insert("KeyE") }; if xButton { keys.insert("Escape") }; if yButton { keys.insert("Enter") }
                    self.set(keys, owner: "controller-\(ObjectIdentifier(controller).hashValue)")
                }
            }
        }
    }
    func set(_ keys: Set<String>, owner: String) {
        guard accepting else { return }
        owners[owner] = keys
        sync()
    }
    func hold(_ code: String, down: Bool, owner: String) { set(down ? [code] : [], owner: owner) }
    private func sync() {
        let next = owners.values.reduce(into: Set<String>()) { $0.formUnion($1) }
        guard next != pressed else { return }
        pressed = next
        send(next)
    }
    private func send(_ keys: Set<String>) {
        webView?.callAsyncJavaScript("window.__dustoreInput && window.__dustoreInput(keys)", arguments: ["keys": keys.sorted()], in: nil, in: .page) { _ in }
    }
    func releaseAll() { owners.removeAll(); pressed.removeAll(); send([]) }
    func setActive(_ active: Bool) { releaseAll(); accepting = active }
    func close() {
        releaseAll(); accepting = false
        for controller in controllers { controller.extendedGamepad?.valueChangedHandler = nil }
        controllers.removeAll()
        observers.forEach { NotificationCenter.default.removeObserver($0) }; observers.removeAll()
        webView = nil
    }
    static let bridgeScript = #"""
    (() => {
      const held = new Set();
      const map = {ArrowUp:['ArrowUp',38],ArrowDown:['ArrowDown',40],ArrowLeft:['ArrowLeft',37],ArrowRight:['ArrowRight',39],KeyW:['w',87],KeyA:['a',65],KeyS:['s',83],KeyD:['d',68],Space:[' ',32],KeyE:['e',69],Escape:['Escape',27],Enter:['Enter',13]};
      function fire(code, down) {
        if (!map[code]) return;
        const [key, number] = map[code];
        const canvas = document.querySelector('canvas');
        if (down && canvas) { if (!canvas.hasAttribute('tabindex')) canvas.tabIndex = 0; canvas.focus({preventScroll:true}); }
        const target = document.activeElement && document.activeElement !== document.body ? document.activeElement : (canvas || document.body || document);
        const event = new KeyboardEvent(down ? 'keydown' : 'keyup', {key, code, keyCode:number, which:number, bubbles:true, cancelable:true, repeat:false});
        try { Object.defineProperty(event, 'keyCode', {get:() => number}); Object.defineProperty(event, 'which', {get:() => number}); } catch (_) {}
        target.dispatchEvent(event);
      }
      window.__dustoreInput = keys => {
        const next = new Set(Array.isArray(keys) ? keys.filter(code => map[code]) : []);
        for (const code of held) if (!next.has(code)) { fire(code, false); held.delete(code); }
        for (const code of next) if (!held.has(code)) { fire(code, true); held.add(code); }
        return held.size;
      };
      window.addEventListener('blur', () => window.__dustoreInput([]));
      document.addEventListener('visibilitychange', () => { if (document.hidden) window.__dustoreInput([]); });
    })();
    """#
}

/// UIKit delivers independent press/release/cancel events for simultaneous touch controls.
struct HoldKey: UIViewRepresentable {
    let title: String, label: String, code: String
    let input: GameInput
    func makeUIView(context: Context) -> HoldKeyControl {
        let control = HoldKeyControl()
        control.onChange = { [weak input] down in input?.hold(code, down: down, owner: "touch-" + code) }
        control.accessibilityLabel = label
        control.accessibilityHint = "Удерживайте для действия. VoiceOver отправляет короткое нажатие."
        control.title.text = title
        return control
    }
    func updateUIView(_ view: HoldKeyControl, context: Context) {
        view.title.text = title
        view.onChange = { [weak input] down in input?.hold(code, down: down, owner: "touch-" + code) }
    }
    static func dismantleUIView(_ view: HoldKeyControl, coordinator: ()) { view.release() }
}

@MainActor
final class HoldKeyControl: UIControl {
    let title = UILabel()
    var onChange: ((Bool) -> Void)?
    private var held = false
    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = UIColor(Theme.background).withAlphaComponent(0.87)
        layer.cornerRadius = 15; layer.borderWidth = 1; layer.borderColor = UIColor(Theme.selection).withAlphaComponent(0.35).cgColor
        isAccessibilityElement = true; accessibilityTraits = .button
        title.textAlignment = .center; title.font = UIFont.preferredFont(forTextStyle: .headline)
        title.adjustsFontForContentSizeCategory = true; title.textColor = UIColor(Theme.text)
        title.translatesAutoresizingMaskIntoConstraints = false
        addSubview(title)
        NSLayoutConstraint.activate([title.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 5), title.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -5), title.centerYAnchor.constraint(equalTo: centerYAnchor)])
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    private func press() {
        guard !held else { return }
        held = true; backgroundColor = UIColor(Theme.selection).withAlphaComponent(0.7); title.textColor = UIColor(Theme.background)
        onChange?(true)
        if UserDefaults.standard.object(forKey: Preferences.haptics) as? Bool ?? true { UIImpactFeedbackGenerator(style: .soft).impactOccurred(intensity: 0.4) }
    }
    func release() {
        guard held else { return }
        held = false; backgroundColor = UIColor(Theme.background).withAlphaComponent(0.87); title.textColor = UIColor(Theme.text)
        onChange?(false)
    }
    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) { press() }
    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) { release() }
    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) { release() }
    override func didMoveToWindow() { if window == nil { release() } }
    override func accessibilityActivate() -> Bool {
        press()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { [weak self] in self?.release() }
        return true
    }
}
