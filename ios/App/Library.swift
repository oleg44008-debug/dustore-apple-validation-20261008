import Foundation
import SwiftUI

/// The importer owns its task and source until success, cancellation or an explicit dismissal.
/// Free quota and speed rules are retained; a failed/cancelled transfer never consumes quota.
@MainActor
final class Library: ObservableObject {
    static let shared = Library()
    @Published private(set) var games: [Game] = []
    @Published private(set) var transfer: TransferState?
    @Published var message: String?
    @Published var playing: Game?
    @Published var quotaLine = ""
    private var importTask: Task<Game?, Never>?
    private var cancellation: ImportCancellation?
    private var retryImport: (source: URL, title: String?, workspace: URL?, deleteOnSuccess: Bool)?
    private var downloadCancel: (() -> Void)?
    private var downloadRetry: (() -> Void)?
    private var lastProgress = Date.distantPast
    var isBusy: Bool { transfer?.isActive == true }
    var busy: String? { isBusy ? transfer?.title : nil }
    // Download ownership follows this application-wide model, including adaptive view changes.
    lazy var storeDownloads = StoreDownloadSession(library: self)
    private var file: URL { FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("library.json") }

    private init() {
        if let data = try? Data(contentsOf: file), let saved = try? JSONDecoder().decode([Game].self, from: data) {
            games = saved.filter { !$0.playable || FileManager.default.fileExists(atPath: $0.webRoot.path) }
        }
        refreshQuota()
        if UITestFixture.enabled {
            TrustedClock.override = Date(timeIntervalSince1970: 1_791_108_000)
            if UITestFixture.name == "transfer" {
                Task { [weak self] in
                    guard let self else { return }
                    var transferID: UUID?
                    transferID = self.beginDownload(title: "Owned transfer diagnostic", cancel: { [weak self] in
                        if let transferID { self?.downloadFailed(id: transferID, error: nil, retry: nil) }
                    })
                    if let transferID { self.downloadProgress(id: transferID, fraction: 0.42, detail: "Проверка отмены в собственном тестовом состоянии") }
                }
            }
        } else { Task { await TrustedClock.sync(); refreshQuota() } }
    }
    func refreshQuota() {
        quotaLine = Edition.isPrime ? "Prime: eX без лимита и очереди" : "Free: сегодня осталось \(ExQuota.leftToday) из \(Edition.freePerDay) переносов eX · 2 МБ/с · очередь \(Edition.freeQueueSeconds) с"
    }
    private func save() throws { try JSONEncoder().encode(games).write(to: file, options: .atomic) }
    func remove(_ game: Game) {
        do {
            if FileManager.default.fileExists(atPath: game.folder.path) { try FileManager.default.removeItem(at: game.folder) }
            games.removeAll { $0.id == game.id }
            try save()
        } catch { message = "Не удалось удалить игру: " + error.localizedDescription }
    }
    func rename(_ game: Game, to title: String) {
        let clean = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty, let index = games.firstIndex(where: { $0.id == game.id }) else { return }
        let old = games[index].title
        games[index].title = String(clean.prefix(120))
        do { try save() } catch { games[index].title = old; message = "Не удалось сохранить название: " + error.localizedDescription }
    }
    func toggleFavorite(_ game: Game) {
        guard let index = games.firstIndex(where: { $0.id == game.id }) else { return }
        games[index].favorite = !(games[index].favorite ?? false)
        do { try save() } catch { games[index].favorite = !(games[index].favorite ?? false); message = error.localizedDescription }
    }
    func play(_ game: Game) {
        guard game.playable else { return }
        if let index = games.firstIndex(where: { $0.id == game.id }) {
            games[index].lastPlayed = Date()
            do { try save() } catch { message = error.localizedDescription }
        }
        playing = game
    }

    @discardableResult
    func add(from source: URL, title: String?, copyPickedSource: Bool = false, deleteSourceOnSuccess: Bool = false) async -> Game? {
        guard !isBusy, importTask == nil else { message = "eX уже переносит другую игру."; return nil }
        cleanRetryWorkspace()
        let id = UUID(), token = ImportCancellation()
        cancellation = token
        downloadCancel = nil; downloadRetry = nil
        transfer = TransferState(id: id, source: title ?? source.deletingPathExtension().lastPathComponent,
                                 phase: copyPickedSource ? .copying : .identifying)
        let task = Task { [weak self] () -> Game? in
            guard let self else { return nil }
            var workspace: URL?
            var prepared = source
            let scoped = copyPickedSource && source.startAccessingSecurityScopedResource()
            defer { if scoped { source.stopAccessingSecurityScopedResource() } }
            do {
                try token.check()
                if copyPickedSource {
                    let staged = try await ImportFiles.stage(source, cancellation: token) { value in
                        Task { @MainActor in self.receive(value, id: id) }
                    }
                    workspace = staged.workspace; prepared = staged.source
                }
                self.retryImport = (prepared, title, workspace, deleteSourceOnSuccess)
                if !Edition.isPrime {
                    self.receive(ImportProgress(phase: .clock, detail: "Сверяем время с сервером"), id: id)
                    await TrustedClock.sync()
                    try token.check()
                    if let refusal = ExQuota.refusal { throw PortError(message: refusal) }
                    for left in stride(from: Edition.freeQueueSeconds, to: 0, by: -1) {
                        try token.check()
                        self.receive(ImportProgress(phase: .queue,
                            fraction: Double(Edition.freeQueueSeconds - left) / Double(max(1, Edition.freeQueueSeconds)),
                            detail: "Начнём через \(left) с · Prime переносит сразу"), id: id)
                        try await Task.sleep(nanoseconds: 1_000_000_000)
                    }
                }
                let started = Date()
                let bytes = Double((try? prepared.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
                let game = try await Porter.importGame(from: prepared, title: title, cancellation: token) { value in
                    Task { @MainActor in self.receive(value, id: id) }
                }
                do {
                    try token.check()
                    if game.kind == .godot && !Edition.isPrime {
                        let target = bytes / Edition.freeBytesPerSecond
                        while Date().timeIntervalSince(started) < target {
                            try token.check()
                            self.receive(ImportProgress(phase: .speedLimit,
                                fraction: min(1, Date().timeIntervalSince(started) / max(0.01, target)),
                                detail: "Free · 2 МБ/с"), id: id)
                            try await Task.sleep(nanoseconds: 250_000_000)
                        }
                    }
                    self.receive(ImportProgress(phase: .saving), id: id)
                    self.games.insert(game, at: 0)
                    do { try self.save() } catch { self.games.removeAll { $0.id == game.id }; throw error }
                    if game.kind == .godot && !Edition.isPrime { ExQuota.recordSuccess() }
                    self.refreshQuota()
                    self.transfer?.outcome = .completed; self.transfer?.fraction = nil; self.transfer?.game = game
                    self.transfer?.detail = game.note ?? "Сохранена на этом устройстве. Можно играть без повторного переноса."
                    self.retryImport = nil
                    if let workspace { try? FileManager.default.removeItem(at: workspace) }
                    if deleteSourceOnSuccess { try? FileManager.default.removeItem(at: source) }
                    self.finishTask(id: id)
                    return game
                } catch {
                    try? FileManager.default.removeItem(at: game.folder)
                    throw error
                }
            } catch {
                let cancelled = error is CancellationError || (error as? URLError)?.code == .cancelled
                if self.transfer?.id == id {
                    self.transfer?.outcome = cancelled ? .cancelled : .failed
                    self.transfer?.fraction = nil
                    self.transfer?.detail = cancelled ? "Незавершённая игра удалена. Исходный файл остаётся в Файлах или Загрузках." : error.localizedDescription
                    self.transfer?.canRetry = !cancelled && self.retryImport != nil
                }
                if cancelled {
                    if let workspace { try? FileManager.default.removeItem(at: workspace) }
                    self.retryImport = nil
                }
                self.refreshQuota()
                self.finishTask(id: id)
                return nil
            }
        }
        importTask = task
        return await task.value
    }
    private func finishTask(id: UUID) {
        if transfer?.id == id { importTask = nil; cancellation = nil }
    }
    private func receive(_ value: ImportProgress, id: UUID) {
        guard transfer?.id == id, transfer?.outcome == .running else { return }
        let changed = transfer?.phase != value.phase
        guard changed || value.fraction == 1 || Date().timeIntervalSince(lastProgress) >= 0.1 else { return }
        lastProgress = Date()
        transfer?.phase = value.phase
        transfer?.fraction = value.fraction.map { min(1, max(0, $0)) }
        transfer?.detail = value.detail
    }
    func cancelTransfer() {
        guard isBusy, transfer?.outcome != .cancelling else { return }
        transfer?.outcome = .cancelling
        if let downloadCancel { downloadCancel() } else { cancellation?.cancel(); importTask?.cancel() }
    }
    func retryTransfer() {
        guard !isBusy, importTask == nil else { return }
        if let action = downloadRetry {
            downloadRetry = nil; transfer = nil; action(); return
        }
        guard let retry = retryImport else { return }
        retryImport = nil
        Task {
            let result = await add(from: retry.source, title: retry.title, deleteSourceOnSuccess: retry.deleteOnSuccess)
            if result != nil || transfer?.outcome == .cancelled {
                if let workspace = retry.workspace { try? FileManager.default.removeItem(at: workspace) }
            } else if let workspace = retry.workspace { retryImport?.workspace = workspace }
        }
    }
    func dismissTransfer() {
        guard !isBusy else { return }
        cleanRetryWorkspace(); transfer = nil; downloadRetry = nil; downloadCancel = nil
    }
    private func cleanRetryWorkspace() {
        if let workspace = retryImport?.workspace { try? FileManager.default.removeItem(at: workspace) }
        retryImport = nil
    }
    func beginDownload(title: String, cancel: @escaping () -> Void) -> UUID? {
        guard !isBusy, importTask == nil else { message = "Дождитесь текущего переноса или отмените его."; return nil }
        cleanRetryWorkspace()
        let id = UUID()
        transfer = TransferState(id: id, source: title, phase: .downloading)
        downloadCancel = cancel; downloadRetry = nil
        return id
    }
    func downloadProgress(id: UUID, fraction: Double?, detail: String?) {
        receive(ImportProgress(phase: .downloading, fraction: fraction, detail: detail), id: id)
    }
    func downloadFailed(id: UUID, error: Error?, retry: (() -> Void)?) {
        guard transfer?.id == id else { return }
        let cancelled = transfer?.outcome == .cancelling
        transfer?.outcome = cancelled ? .cancelled : .failed
        transfer?.fraction = nil
        transfer?.detail = cancelled ? "Загрузка остановлена. При наличии данных её можно продолжить." : (error?.localizedDescription ?? "Загрузка остановлена.")
        transfer?.canRetry = retry != nil
        downloadRetry = retry; downloadCancel = nil
    }
    func importDownloaded(from source: URL, title: String?, id: UUID) async {
        guard transfer?.id == id else { return }
        transfer = nil; downloadCancel = nil; downloadRetry = nil
        await add(from: source, title: title, deleteSourceOnSuccess: true)
    }
}
