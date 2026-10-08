import SwiftUI
import WebKit

@MainActor
final class PlayerSession: ObservableObject {
    let game: Game
    let input = GameInput()
    @Published var ready = false
    @Published var fraction: Double?
    @Published var error: String?
    weak var webView: WKWebView?
    init(game: Game) { self.game = game }
    func attach(_ view: WKWebView) {
        webView = view; input.webView = view; input.start(); input.setActive(true)
        reload()
    }
    func reload() {
        input.releaseAll(); ready = false; error = nil; fraction = nil
        let query = ProcessInfo.processInfo.arguments.contains("-dustoreSelfTest") ? "?selftest" : ""
        if let url = URL(string: "\(GameServer.scheme)://game/index.html" + query) { webView?.load(URLRequest(url: url)) }
    }
    func pageLoaded() { if game.kind == .web { ready = true; fraction = nil } }
    func report(_ body: Any) {
        if let value = body as? [String: Any], value["type"] as? String == "progress", let fraction = value["fraction"] as? Double {
            self.fraction = min(1, max(0, fraction)); return
        }
        let text = String(describing: body)
        SelfTest.gameReported(text)
        if text == "started" { ready = true; fraction = nil }
        else if text.hasPrefix("error: ") { fail(String(text.dropFirst(7))) }
    }
    func fail(_ detail: String) { input.releaseAll(); error = detail; fraction = nil }
    func setActive(_ active: Bool) {
        input.setActive(active)
        webView?.setAllMediaPlaybackSuspended(!active) { }
    }
    func close() { input.close(); webView?.setAllMediaPlaybackSuspended(true) { }; webView = nil }
}

struct PlayerScreen: View {
    @EnvironmentObject var library: Library
    @Environment(\.scenePhase) private var scenePhase
    let game: Game
    @StateObject private var session: PlayerSession
    @State private var menu = false
    @AppStorage("dustore.player.touch-controls") private var touchControls = true
    @AppStorage("dustore.player.input-preset") private var preset = InputPreset.arrows.rawValue
    init(game: Game) { self.game = game; _session = StateObject(wrappedValue: PlayerSession(game: game)) }
    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                Button { close() } label: { Image(systemName: "xmark").frame(width: 44, height: 44) }
                    .accessibilityLabel("Закрыть игру").accessibilityIdentifier("player.close")
                VStack(alignment: .leading, spacing: 3) {
                    Text(game.title).font(.headline).lineLimit(1)
                    Text(game.engine ?? "Веб-игра").font(.caption).foregroundColor(Theme.dim).lineLimit(1)
                }.frame(maxWidth: .infinity, alignment: .leading)
                Button { session.input.releaseAll(); menu = true } label: { Image(systemName: "slider.horizontal.3").frame(width: 44, height: 44) }
                    .accessibilityLabel("Управление и игровое меню").accessibilityIdentifier("player.menu")
            }.foregroundColor(Theme.text).padding(.horizontal, 12).background(Theme.background)
            ZStack {
                Color.black
                GameWebView(game: game, session: session)
                if let error = session.error {
                    VStack(spacing: 16) {
                        Image(systemName: "exclamationmark.triangle").font(.largeTitle).foregroundColor(Theme.warn)
                        Text("Игра не запустилась").font(.title2.bold()).foregroundColor(Theme.text)
                        Text(error).font(.subheadline).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true)
                        YellowButton(title: "Перезапустить", symbol: "arrow.clockwise") { session.reload() }
                            .accessibilityIdentifier("player.retry")
                        Button("Вернуться в библиотеку") { close() }.buttonStyle(PrimePressStyle())
                    }.padding(24).frame(maxWidth: 480).background(Theme.background).clipShape(RoundedRectangle(cornerRadius: 22)).padding(20)
                } else if !session.ready {
                    VStack(spacing: 12) {
                        Text("Запускаем «\(game.title)»").font(.headline)
                        if let value = session.fraction { ProgressView(value: value).tint(Theme.selection) }
                        else { ProgressView().tint(Theme.selection) }
                        Text(session.fraction.map { "\(Int($0 * 100))% · загрузка файлов игры" } ?? "Загружаем страницу и движок")
                            .font(.caption).foregroundColor(Theme.dim)
                    }.padding(22).frame(maxWidth: 400).background(Theme.background).clipShape(RoundedRectangle(cornerRadius: 20)).padding(20)
                    .foregroundColor(Theme.text).accessibilityElement(children: .combine)
                }
            }.frame(maxWidth: .infinity, maxHeight: .infinity)
            if touchControls && session.error == nil { TouchPad(input: session.input, preset: InputPreset(rawValue: preset) ?? .arrows) }
        }
        .background(Theme.background).statusBarHidden()
        .onAppear { session.input.preset = InputPreset(rawValue: preset) ?? .arrows }
        .onChange(of: preset) { value in session.input.preset = InputPreset(rawValue: value) ?? .arrows }
        .onChange(of: scenePhase) { phase in session.setActive(phase == .active && !menu) }
        .onDisappear { session.close() }
        .sheet(isPresented: $menu, onDismiss: { session.setActive(scenePhase == .active) }) {
            PlayerMenu(game: game, session: session, touchControls: $touchControls, preset: $preset, close: close)
        }
    }
    private func close() { session.close(); library.playing = nil }
}

private struct TouchPad: View {
    @ObservedObject var input: GameInput
    let preset: InputPreset
    var body: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 24) { directions; Spacer(minLength: 8); actions }
            HStack(spacing: 10) { directions; Spacer(minLength: 2); actions }
        }
        .padding(.horizontal, 18).padding(.vertical, 10).background(Theme.inset)
        .onDisappear { input.releaseAll() }
        .accessibilityElement(children: .contain).accessibilityLabel("Экранное управление")
    }
    private var directions: some View {
        VStack(spacing: 4) {
            key("↑", "Вверх", preset.directions[0])
            HStack(spacing: 4) { key("←", "Влево", preset.directions[2]); key("↓", "Вниз", preset.directions[1]); key("→", "Вправо", preset.directions[3]) }
        }
    }
    private var actions: some View {
        VStack(spacing: 4) {
            HStack(spacing: 4) { key("Esc", "Escape · игровое меню", "Escape"); key("↵", "Enter · подтвердить", "Enter") }
            HStack(spacing: 4) { key("E", "Клавиша E · действие", "KeyE"); key("␣", "Пробел · прыжок или действие", "Space") }
        }
    }
    private func key(_ title: String, _ label: String, _ code: String) -> some View {
        HoldKey(title: title, label: label, code: code, input: input).frame(width: 48, height: 46)
            .accessibilityIdentifier("pad." + code)
    }
}

private struct PlayerMenu: View {
    let game: Game
    @ObservedObject var session: PlayerSession
    @Binding var touchControls: Bool
    @Binding var preset: String
    let close: () -> Void
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    Card {
                        DisplayTitle(text: game.title, size: 25)
                        Chip(text: game.engine ?? "Веб-игра")
                        Text("Игровое меню не приостанавливает логику веб-игры. Зажатые клавиши освобождены, воспроизведение медиа временно приостановлено.")
                            .font(.subheadline).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true)
                    }
                    Card {
                        Toggle("Экранные кнопки", isOn: $touchControls).tint(Theme.selection)
                        Picker("Направления", selection: $preset) { ForEach(InputPreset.allCases) { Text($0.title).tag($0.rawValue) } }
                            .pickerStyle(.segmented)
                        Text("Направления — стрелки или WASD. Действия — пробел, E, Enter и Escape. Поддержка зависит от раскладки самой игры.")
                            .font(.subheadline).foregroundColor(Theme.muted)
                        Label(session.input.controllerName.map { "Геймпад: \($0)" } ?? "Геймпад не подключён", systemImage: "gamecontroller")
                            .font(.subheadline).foregroundColor(Theme.selection)
                        Text("A → пробел · B → E · X → Escape · Y → Enter. Bluetooth-клавиатура работает напрямую через WebKit.")
                            .font(.caption).foregroundColor(Theme.dim)
                    }
                    Button("Перезапустить игру") { session.reload(); dismiss() }.buttonStyle(PrimePressStyle()).frame(maxWidth: .infinity)
                    Button("Закрыть игру", role: .destructive) { dismiss(); close() }.buttonStyle(PrimePressStyle()).frame(maxWidth: .infinity)
                }.padding(20).frame(maxWidth: 700).frame(maxWidth: .infinity)
            }.background(Theme.background)
            .navigationTitle("Управление").navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Готово") { dismiss() } } }
        }.preferredColorScheme(.dark).onAppear { session.setActive(false) }
    }
}
