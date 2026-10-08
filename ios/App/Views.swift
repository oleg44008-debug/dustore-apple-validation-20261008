import SwiftUI
import UniformTypeIdentifiers

private struct Destination: Identifiable {
    let id: Int, title: String, symbol: String
    static let all = [Destination(id: 1, title: "Библиотека", symbol: "square.grid.2x2"), Destination(id: 0, title: "Магазин", symbol: "bag"), Destination(id: 2, title: "eX", symbol: "arrow.left.arrow.right"), Destination(id: 3, title: "Настройки", symbol: "slider.horizontal.3")]
}

struct RootView: View {
    @StateObject private var library = Library.shared
    @State private var tab = 1
    @State private var importing = false
    @Environment(\.horizontalSizeClass) private var sizeClass
    @AppStorage(Preferences.motion) private var motion = MotionPreference.full.rawValue
    private var sidebar: Bool { UIDevice.current.userInterfaceIdiom == .pad && sizeClass == .regular }
    var body: some View {
        ZStack {
            Group {
                if sidebar {
                    NavigationSplitView {
                        List {
                            Section { Image("BrandMark").resizable().scaledToFit().frame(width: 46, height: 46).accessibilityHidden(true); Text("DUSTORE").font(.headline); EditionMark() }
                            ForEach(Destination.all) { item in
                                Button { tab = item.id } label: {
                                    Label(item.title, systemImage: item.symbol).foregroundColor(tab == item.id ? Theme.selection : Theme.text).padding(.vertical, 8)
                                }.listRowBackground(tab == item.id ? Theme.selection.opacity(0.12) : Color.clear)
                                    .accessibilityValue(tab == item.id ? "Выбрано" : "").accessibilityIdentifier("tab.\(item.id)")
                            }
                        }.scrollContentBackground(.hidden).background(Theme.background)
                            .navigationTitle("DustoreX").navigationSplitViewColumnWidth(min: 190, ideal: 225, max: 270)
                    } detail: { screen(tab).toolbar(.hidden, for: .navigationBar) }.navigationSplitViewStyle(.balanced)
                } else {
                    TabView(selection: $tab) {
                        StoreScreen().tabItem { Label("Магазин", systemImage: "bag") }.tag(0)
                        LibraryScreen(importGame: { importing = true }, openStore: { tab = 0 }).tabItem { Label("Библиотека", systemImage: "square.grid.2x2") }.tag(1)
                        ExScreen(importGame: { importing = true }, openStore: { tab = 0 }).tabItem { Label("eX", systemImage: "arrow.left.arrow.right") }.tag(2)
                        SettingsScreen().tabItem { Label("Настройки", systemImage: "slider.horizontal.3") }.tag(3)
                    }
                }
            }.accessibilityHidden(library.transfer != nil)
            if let transfer = library.transfer { TransferPanel(state: transfer, openLibrary: { tab = 1 }).zIndex(2) }
        }
        .tint(Theme.selection).environmentObject(library).preferredColorScheme(.dark)
        .transaction { if motion == MotionPreference.off.rawValue { $0.disablesAnimations = true } }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.zip, .folder, .item], allowsMultipleSelection: false) { result in
            switch result {
            case .success(let urls):
                guard let url = urls.first else { return }
                Task { await library.add(from: url, title: url.deletingPathExtension().lastPathComponent, copyPickedSource: true) }
            case .failure(let error):
                if (error as NSError).code != NSUserCancelledError { library.message = error.localizedDescription }
            }
        }
        .alert("DustoreX", isPresented: Binding(get: { library.message != nil }, set: { if !$0 { library.message = nil } })) { Button("Понятно", role: .cancel) {} } message: { Text(library.message ?? "") }
        .fullScreenCover(item: $library.playing) { game in PlayerScreen(game: game).environmentObject(library) }
    }
    @ViewBuilder private func screen(_ value: Int) -> some View {
        switch value {
        case 0: StoreScreen()
        case 2: ExScreen(importGame: { importing = true }, openStore: { tab = 0 })
        case 3: SettingsScreen()
        default: LibraryScreen(importGame: { importing = true }, openStore: { tab = 0 })
        }
    }
}

struct TransferPanel: View {
    @EnvironmentObject var library: Library
    let state: TransferState
    let openLibrary: () -> Void
    @AccessibilityFocusState private var focused: Bool
    var body: some View {
        GeometryReader { geometry in
            ZStack {
                Theme.background.opacity(0.96).ignoresSafeArea()
                ScrollView {
                    Card {
                        HStack(spacing: 12) {
                            Image(systemName: symbol).font(.title2).foregroundColor(state.outcome == .failed ? Theme.warn : Theme.selection).accessibilityHidden(true)
                            Eyebrow(text: "eX · " + Edition.name)
                        }
                        DisplayTitle(text: state.title, size: 27).accessibilityFocused($focused).accessibilityIdentifier("transfer.title")
                        Text(state.source).font(.headline).foregroundColor(Theme.text).fixedSize(horizontal: false, vertical: true)
                        if state.isActive {
                            if let fraction = state.fraction {
                                ProgressView(value: fraction).tint(Theme.selection).accessibilityLabel(state.phase.title)
                                Text("\(Int(fraction * 100))%").font(.caption.monospacedDigit()).foregroundColor(Theme.selection)
                            } else { ProgressView().tint(Theme.selection).accessibilityLabel(state.phase.title) }
                        }
                        if let detail = state.detail { Text(detail).font(.subheadline).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true).accessibilityIdentifier("transfer.detail") }
                        if state.outcome == .running {
                            Button("Отменить перенос", role: .cancel) { library.cancelTransfer() }.buttonStyle(PrimePressStyle()).accessibilityIdentifier("transfer.cancel")
                        } else if state.outcome == .cancelling {
                            Text("Завершаем текущий блок и освобождаем файлы…").font(.caption).foregroundColor(Theme.dim)
                        } else {
                            if let game = state.game, game.playable {
                                YellowButton(title: "Играть", symbol: "play.fill") { library.dismissTransfer(); library.play(game) }.accessibilityIdentifier("transfer.play")
                            }
                            if state.canRetry { YellowButton(title: state.outcome == .cancelled ? "Продолжить загрузку" : "Повторить", symbol: "arrow.clockwise") { library.retryTransfer() }.accessibilityIdentifier("transfer.retry") }
                            Button(state.outcome == .completed ? "Открыть библиотеку" : "Готово") { library.dismissTransfer(); openLibrary() }.buttonStyle(PrimePressStyle()).accessibilityIdentifier("transfer.dismiss")
                        }
                    }.padding(20)
                }.frame(maxWidth: 510).frame(height: min(geometry.size.height - 24, 640))
            }
        }.onAppear { focused = true }.onChange(of: state.outcome) { _ in focused = true }
    }
    private var symbol: String {
        switch state.outcome { case .completed: return state.game?.playable == true ? "checkmark.circle" : "info.circle"; case .failed: return "exclamationmark.triangle"; case .cancelled, .cancelling: return "xmark.circle"; case .running: return "arrow.left.arrow.right" }
    }
}

struct StoreScreen: View {
    @EnvironmentObject var library: Library
    @StateObject private var state = StoreState()
    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Button { state.back() } label: { Image(systemName: "chevron.left").frame(width: 44, height: 44) }.disabled(!state.canGoBack).accessibilityLabel("Назад в магазине")
                VStack(alignment: .leading, spacing: 3) { Text("Магазин").font(.headline); Text("dustore.ru").font(.caption).foregroundColor(Theme.dim) }
                Spacer(minLength: 4)
                Button { state.reload() } label: { Image(systemName: "arrow.clockwise").frame(width: 44, height: 44) }.accessibilityLabel("Обновить магазин").accessibilityIdentifier("store.reload")
                EditionMark()
            }.foregroundColor(Theme.text).padding(.horizontal, 12).padding(.vertical, 5).background(Theme.background)
            if state.loading { ProgressView(value: state.progress).tint(Theme.selection).accessibilityLabel("Загрузка магазина") }
            ZStack {
                StoreWebView(library: library, state: state)
                if let error = state.error {
                    ScrollView {
                        Card {
                            Image(systemName: "wifi.exclamationmark").font(.largeTitle).foregroundColor(Theme.selection).accessibilityHidden(true)
                            DisplayTitle(text: "Магазин недоступен", size: 26)
                            Text(error).font(.subheadline).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true)
                            Text("Ваша библиотека остаётся на устройстве. Для новых загрузок понадобится интернет.").font(.subheadline).foregroundColor(Theme.dim)
                            YellowButton(title: "Попробовать снова", symbol: "arrow.clockwise") { state.reload() }.accessibilityIdentifier("store.retry")
                        }.padding(22).frame(maxWidth: 600).frame(maxWidth: .infinity)
                    }.background(Theme.background).accessibilityIdentifier("store.error")
                }
            }
        }.background(Theme.background).toolbarBackground(Theme.background, for: .tabBar)
    }
}

private enum LibraryFilter: String, CaseIterable {
    case all, ready, favorites
    var title: String { switch self { case .all: return "Все"; case .ready: return "Готовые"; case .favorites: return "Избранное" } }
}
private enum LibrarySort: String, CaseIterable { case recent, title, played
    var title: String { switch self { case .recent: return "Сначала новые"; case .title: return "По названию"; case .played: return "Недавно играли" } }
}

struct LibraryScreen: View {
    @EnvironmentObject var library: Library
    let importGame: () -> Void
    let openStore: () -> Void
    @State private var query = ""
    @State private var filter: LibraryFilter = .all
    @State private var sort: LibrarySort = .recent
    @Environment(\.dynamicTypeSize) private var dynamicType
    private var games: [Game] {
        let clean = query.trimmingCharacters(in: .whitespacesAndNewlines)
        return library.games.filter { game in
            (filter == .all || (filter == .ready && game.playable) || (filter == .favorites && game.favorite == true)) && (clean.isEmpty || game.title.localizedCaseInsensitiveContains(clean))
        }.sorted { a, b in
            switch sort { case .recent: return a.added > b.added; case .title: return a.title.localizedStandardCompare(b.title) == .orderedAscending; case .played: return (a.lastPlayed ?? .distantPast) > (b.lastPlayed ?? .distantPast) }
        }
    }
    var body: some View {
        GeometryReader { geometry in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 22) {
                    BrandHeader(title: "Ваши игры", subtitle: library.games.isEmpty ? "Коллекция, которая всегда под рукой" : "\(library.games.filter(\.playable).count) готовы к запуску · \(library.games.count) в коллекции").primeArrival()
                    if library.games.isEmpty {
                        EmptyLibrary(importGame: importGame, openStore: openStore).primeArrival(delay: 0.05)
                    } else {
                        searchField
                        ViewThatFits(in: .horizontal) { HStack { filters; Spacer(minLength: 6); sortMenu }; VStack(alignment: .leading, spacing: 10) { filters; sortMenu } }
                        if games.isEmpty {
                            Card {
                                DisplayTitle(text: filter == .favorites && query.isEmpty ? "Здесь будут любимые игры" : "Ничего не найдено", size: 25)
                                Text(filter == .favorites && query.isEmpty ? "Добавьте игру в избранное через меню на её карточке." : "Попробуйте другое название или сбросьте фильтры.").font(.subheadline).foregroundColor(Theme.muted)
                                Button("Показать все игры") { query = ""; filter = .all }.buttonStyle(PrimePressStyle())
                            }
                        } else {
                            if query.isEmpty && filter == .all, let featured = games.first(where: \.playable) { FeaturedGame(game: featured) }
                            HStack { Eyebrow(text: "Ваша коллекция"); Spacer(); Text("\(games.count)").font(.caption.monospacedDigit()).foregroundColor(Theme.dim) }
                            LazyVGrid(columns: columns(geometry.size.width), alignment: .leading, spacing: 16) {
                                ForEach(games) { game in GameCard(game: game) }
                            }
                        }
                        Button(action: importGame) { Label("Добавить из Файлов", systemImage: "plus").frame(maxWidth: .infinity) }.buttonStyle(PrimePressStyle()).disabled(library.isBusy).accessibilityIdentifier("library.import")
                    }
                    Text(library.quotaLine).font(.caption).foregroundColor(Theme.dim).fixedSize(horizontal: false, vertical: true)
                }.frame(maxWidth: 1200, alignment: .leading).padding(22).frame(maxWidth: .infinity)
            }.background { PrimeBackdrop() }.toolbarBackground(Theme.background, for: .tabBar)
        }
    }
    private func columns(_ width: CGFloat) -> [GridItem] {
        let count = dynamicType.isAccessibilitySize ? 1 : width >= 1150 ? 3 : width >= 760 ? 2 : 1
        return Array(repeating: GridItem(.flexible(), spacing: 16, alignment: .top), count: count)
    }
    private var searchField: some View {
        HStack(spacing: 10) {
            Image(systemName: "magnifyingglass").foregroundColor(Theme.dim).accessibilityHidden(true)
            TextField("Поиск по названию", text: $query).textInputAutocapitalization(.never).autocorrectionDisabled(true).foregroundColor(Theme.text).accessibilityLabel("Поиск по библиотеке").accessibilityIdentifier("library.search")
            if !query.isEmpty { Button { query = "" } label: { Image(systemName: "xmark.circle.fill").frame(width: 44, height: 44) }.accessibilityLabel("Очистить поиск") }
        }.padding(.leading, 16).padding(.trailing, 8).frame(minHeight: 54).background(Theme.inset).clipShape(RoundedRectangle(cornerRadius: 15)).overlay(RoundedRectangle(cornerRadius: 15).stroke(Theme.line))
    }
    private var filters: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 6) { filterButtons }
            VStack(alignment: .leading, spacing: 6) { filterButtons }
        }
    }
    @ViewBuilder private var filterButtons: some View {
        ForEach(LibraryFilter.allCases, id: \.self) { value in
            Button { filter = value } label: {
                Text(value.title).font(.subheadline.weight(.semibold)).foregroundColor(filter == value ? Theme.selection : Theme.dim)
                    .padding(.horizontal, 12).padding(.vertical, 12).frame(minHeight: 44)
                    .background(RoundedRectangle(cornerRadius: 12).fill(filter == value ? Theme.selection.opacity(0.12) : Theme.inset))
                    .overlay(RoundedRectangle(cornerRadius: 12).stroke(filter == value ? Theme.selection.opacity(0.3) : Theme.line))
            }.buttonStyle(.plain).accessibilityValue(filter == value ? "Выбран" : "").accessibilityIdentifier("filter." + value.rawValue)
        }
    }
    private var sortMenu: some View {
        Menu { Picker("Порядок игр", selection: $sort) { ForEach(LibrarySort.allCases, id: \.self) { Text($0.title).tag($0) } } } label: {
            Label("Порядок", systemImage: "arrow.up.arrow.down").font(.subheadline).padding(12).frame(minHeight: 44)
        }.accessibilityValue(sort.title)
    }
}

private struct EmptyLibrary: View {
    let importGame: () -> Void, openStore: () -> Void
    var body: some View {
        Card {
            EmptyLibraryArtwork()
            Eyebrow(text: "Первое открытие")
            DisplayTitle(text: "Начните свою коллекцию", size: 28)
            Text("Выберите архив игры в Файлах или найдите игру в магазине. eX проверит совместимость и подготовит её к запуску.").font(.subheadline).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true)
            YellowButton(title: "Добавить игру", symbol: "plus") { importGame() }.accessibilityIdentifier("library.import")
            Button(action: openStore) { Label("Открыть магазин", systemImage: "bag").frame(maxWidth: .infinity) }.buttonStyle(PrimePressStyle()).accessibilityIdentifier("library.store")
        }
    }
}

private struct FeaturedGame: View {
    @EnvironmentObject var library: Library
    let game: Game
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack { Eyebrow(text: game.lastPlayed == nil ? "Готова к первому запуску" : "Вернитесь в игру"); Spacer(); Image(systemName: "gamecontroller").font(.title).foregroundColor(Theme.selection).accessibilityHidden(true) }
            DisplayTitle(text: game.title, size: 32)
            Text(game.engine ?? "Веб-игра").font(.subheadline).foregroundColor(Theme.muted)
            YellowButton(title: "Играть", symbol: "play.fill") { library.play(game) }.accessibilityIdentifier("featured.play")
        }.padding(24).frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 25).fill(LinearGradient(colors: [Theme.selection.opacity(0.16), Theme.raised, Theme.card], startPoint: .topTrailing, endPoint: .bottomLeading)))
            .overlay(RoundedRectangle(cornerRadius: 25).stroke(Theme.selection.opacity(0.23)))
    }
}

struct GameCard: View {
    @EnvironmentObject var library: Library
    let game: Game
    @State private var removeRequested = false
    @State private var renameRequested = false
    @State private var newTitle = ""
    @Environment(\.dynamicTypeSize) private var dynamicType
    @AppStorage(Preferences.compactCards) private var compactCards = false
    private var compact: Bool { compactCards && !dynamicType.isAccessibilitySize }
    var body: some View {
        Card {
            if !compact && !dynamicType.isAccessibilitySize {
                ZStack(alignment: .bottomLeading) {
                    RoundedRectangle(cornerRadius: 15).fill(LinearGradient(colors: [Theme.selection.opacity(0.13), Theme.inset], startPoint: .topTrailing, endPoint: .bottomLeading))
                    Image(systemName: game.playable ? "gamecontroller" : "shippingbox").font(.system(size: 56, weight: .light)).foregroundColor(Theme.selection.opacity(0.32)).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing).padding(18)
                    Text(String(game.title.prefix(2)).uppercased()).font(.system(size: 38, weight: .heavy, design: .rounded)).foregroundColor(Theme.text).padding(18)
                }.frame(height: 118).accessibilityHidden(true)
            }
            HStack(alignment: .top) {
                DisplayTitle(text: game.title, size: compact ? 20 : 23).frame(maxWidth: .infinity, alignment: .leading)
                Menu {
                    Button { library.toggleFavorite(game) } label: { Label(game.favorite == true ? "Убрать из избранного" : "В избранное", systemImage: game.favorite == true ? "star.slash" : "star") }
                    Button { newTitle = game.title; renameRequested = true } label: { Label("Переименовать", systemImage: "pencil") }
                    Button("Удалить с устройства", role: .destructive) { removeRequested = true }
                } label: { Image(systemName: "ellipsis").foregroundColor(Theme.dim).frame(width: 44, height: 44) }.accessibilityLabel("Действия с игрой «\(game.title)»")
            }
            Chip(text: game.playable ? (game.engine ?? "Игра") + " · готова" : (game.engine ?? "Движок не узнан") + " · не поддерживается", color: game.playable ? Theme.good : Theme.warn)
            if game.favorite == true { Label("Избранное", systemImage: "star.fill").font(.caption).foregroundColor(Theme.action) }
            if !compact, let note = game.note { Text(note).font(.subheadline).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true) }
            if !compact { Text(game.added, style: .date).font(.caption).foregroundColor(Theme.dim).accessibilityLabel("Добавлена \(game.added.formatted(date: .abbreviated, time: .omitted))") }
            if game.playable { YellowButton(title: "Играть", symbol: "play.fill") { library.play(game) }.accessibilityIdentifier("game.play." + game.title) }
        }
        .confirmationDialog("Удалить «\(game.title)» с устройства?", isPresented: $removeRequested, titleVisibility: .visible) {
            Button("Удалить игру", role: .destructive) { library.remove(game) }; Button("Отмена", role: .cancel) {}
        } message: { Text("Игра и её локальные файлы будут удалены.") }
        .alert("Название игры", isPresented: $renameRequested) {
            TextField("Название", text: $newTitle)
            Button("Сохранить") { library.rename(game, to: newTitle) }; Button("Отмена", role: .cancel) {}
        }
    }
}

struct ExScreen: View {
    @EnvironmentObject var library: Library
    let importGame: () -> Void, openStore: () -> Void
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                BrandHeader(title: "eX. Игра ближе", subtitle: "Поддерживаемые игры с компьютера — на iPhone и iPad").primeArrival()
                Card {
                    HStack { Eyebrow(text: "Перенос игр"); Spacer(); Image(systemName: "arrow.left.arrow.right").font(.title).foregroundColor(Theme.selection).accessibilityHidden(true) }
                    DisplayTitle(text: "Выберите игру.\neX сделает следующий шаг.", size: 28)
                    Text("Скопируйте архив сборки в Файлы. Мы распакуем его, проверим движок и, если игра поддерживается, подготовим запуск на устройстве.").font(.body).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true)
                    YellowButton(title: "Выбрать файл игры", symbol: "doc.badge.plus") { importGame() }.disabled(library.isBusy).accessibilityIdentifier("ex.import")
                    Button(action: openStore) { Label("Найти игру в магазине", systemImage: "bag").frame(maxWidth: .infinity) }.buttonStyle(PrimePressStyle()).accessibilityIdentifier("ex.store")
                    Text(library.quotaLine).font(.caption).foregroundColor(Theme.dim).fixedSize(horizontal: false, vertical: true)
                }
                Card {
                    Eyebrow(text: "Как это работает")
                    SupportRow(symbol: "1.circle", title: "Выберите архив или папку", detail: "ZIP и папки со сборками Windows, Mac, Linux либо готовой веб-игрой.", color: Theme.selection)
                    Divider().overlay(Theme.line)
                    SupportRow(symbol: "2.circle", title: "Проверьте результат eX", detail: "Импорт показывает текущий этап. Его можно отменить; при ошибке сохранённый источник можно использовать повторно.", color: Theme.selection)
                    Divider().overlay(Theme.line)
                    SupportRow(symbol: "3.circle", title: "Откройте игру из библиотеки", detail: "Готовые игры сохраняются локально. Экранные кнопки и геймпад передают клавиши выбранной раскладки.", color: Theme.good)
                }
                Card {
                    Eyebrow(text: "Совместимость")
                    SupportRow(symbol: "checkmark.circle", title: "Godot 3.3+ и 4.3+", detail: "eX использует официальный веб-движок той же версии. Для первой загрузки движка нужен интернет.", color: Theme.good)
                    SupportRow(symbol: "globe", title: "Веб-игры", detail: "Готовые веб-сборки запускаются в WebKit. Управление и требования зависят от игры.", color: Theme.good)
                    SupportRow(symbol: "exclamationmark.circle", title: "Unity и Godot C# не поддерживаются", detail: Porter.unityMessage + " Godot C# не поддерживается веб-движком.", color: Theme.warn)
                }
            }.frame(maxWidth: 850, alignment: .leading).padding(22).frame(maxWidth: .infinity)
        }.background { PrimeBackdrop() }.toolbarBackground(Theme.background, for: .tabBar)
    }
}

private struct SupportRow: View {
    let symbol: String, title: String, detail: String
    let color: Color
    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: symbol).font(.title3).foregroundColor(color).frame(width: 26).padding(.top, 2).accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 7) { Text(title).font(.headline).foregroundColor(Theme.text); Text(detail).font(.subheadline).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true) }
        }.accessibilityElement(children: .combine)
    }
}

struct SettingsScreen: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @AppStorage(Preferences.motion) private var motion = MotionPreference.full.rawValue
    @AppStorage(Preferences.haptics) private var haptics = true
    @AppStorage(Preferences.compactCards) private var compactCards = false
    @AppStorage("dustore.player.touch-controls") private var touchControls = true
    @AppStorage("dustore.player.input-preset") private var inputPreset = InputPreset.arrows.rawValue
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                BrandHeader(title: "Настройки", subtitle: "Комфортный темп и привычный размер коллекции").primeArrival()
                Card {
                    Eyebrow(text: "Движение")
                    Text("Анимации интерфейса").font(.headline).foregroundColor(Theme.text)
                    Picker("Уровень анимаций", selection: $motion) {
                        ForEach(MotionPreference.allCases) { value in Text(value.title).tag(value.rawValue) }
                    }.pickerStyle(.menu).tint(Theme.selection).frame(minHeight: 44)
                    Text("Полные — мягкие пружины и появление карточек. Короткие — быстрые растворения. Выключены — мгновенный отклик.")
                        .font(.subheadline).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true)
                    if reduceMotion {
                        Label("Системное «Уменьшение движения» включено: пространственные анимации отключены.", systemImage: "accessibility")
                            .font(.caption).foregroundColor(Theme.selection).fixedSize(horizontal: false, vertical: true)
                    }
                }.primeArrival(delay: 0.04)
                Card {
                    Eyebrow(text: "Коллекция и отклик")
                    Toggle(isOn: $haptics) {
                        VStack(alignment: .leading, spacing: 5) {
                            Text("Тактильный отклик").font(.headline).foregroundColor(Theme.text)
                            Text("Лёгкое подтверждение при нажатии кнопок").font(.caption).foregroundColor(Theme.dim)
                        }
                    }.tint(Theme.selection)
                    Divider().overlay(Theme.line)
                    Toggle(isOn: $compactCards) {
                        VStack(alignment: .leading, spacing: 5) {
                            Text("Компактные карточки").font(.headline).foregroundColor(Theme.text)
                            Text("Меньше деталей на полке; при крупном системном тексте сохраняется полный размер.")
                                .font(.caption).foregroundColor(Theme.dim).fixedSize(horizontal: false, vertical: true)
                        }
                    }.tint(Theme.selection)
                }.primeArrival(delay: 0.08)
                Card {
                    Eyebrow(text: "Игровое управление")
                    Toggle("Экранные кнопки", isOn: $touchControls).tint(Theme.selection)
                    Picker("Направления по умолчанию", selection: $inputPreset) {
                        ForEach(InputPreset.allCases) { Text($0.title).tag($0.rawValue) }
                    }.pickerStyle(.menu).frame(minHeight: 44)
                    Text("Настройки можно изменить в меню самой игры. Экранные кнопки и физический геймпад передают клавиши выбранной раскладки; совместимость зависит от игры.")
                        .font(.subheadline).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true)
                }.primeArrival(delay: 0.1)
                Card {
                    HStack(spacing: 12) {
                        Image("BrandMark").resizable().scaledToFit().frame(width: 48, height: 48).accessibilityHidden(true)
                        VStack(alignment: .leading, spacing: 6) {
                            Text("DustoreX \(Edition.name)").font(.headline).foregroundColor(Theme.text)
                            Text("Версия \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "—")")
                                .font(.caption).foregroundColor(Theme.dim)
                        }
                    }
                    Text(Edition.isPrime ? "eX без лимита, очереди и ограничения скорости." : "Лимит Free: три переноса eX в день.")
                        .font(.subheadline).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true)
                }.primeArrival(delay: 0.12)
            }
            .frame(maxWidth: 760, alignment: .leading).padding(20).padding(.bottom, 8).frame(maxWidth: .infinity)
        }.background { PrimeBackdrop() }.toolbarBackground(Theme.background, for: .tabBar)
    }
}


