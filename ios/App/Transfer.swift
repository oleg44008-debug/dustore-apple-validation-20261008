import Foundation

enum TransferPhase: String {
    case copying, clock, queue, unpacking, identifying, engine, assembling, speedLimit, downloading, saving
    var title: String {
        switch self {
        case .copying: return "Копирование из Файлов"
        case .clock: return "Проверка дневного лимита"
        case .queue: return "Очередь Free"
        case .unpacking: return "Распаковка архива"
        case .identifying: return "Проверка совместимости"
        case .engine: return "Загрузка движка Godot"
        case .assembling: return "Подготовка игры"
        case .speedLimit: return "Перенос Free"
        case .downloading: return "Загрузка из магазина"
        case .saving: return "Сохранение в библиотеку"
        }
    }
}

struct ImportProgress {
    var phase: TransferPhase
    var fraction: Double? = nil
    var detail: String? = nil
}

enum TransferOutcome: Equatable { case running, cancelling, completed, cancelled, failed }

struct TransferState: Identifiable {
    let id: UUID
    var source: String
    var phase: TransferPhase
    var fraction: Double?
    var detail: String?
    var outcome: TransferOutcome = .running
    var game: Game?
    var canRetry = false
    var isActive: Bool { outcome == .running || outcome == .cancelling }
    var title: String {
        switch outcome {
        case .running: return phase.title
        case .cancelling: return "Отмена переноса"
        case .completed: return game?.playable == true ? "Игра готова" : "Проверка завершена"
        case .cancelled: return "Перенос отменён"
        case .failed: return "Не удалось завершить перенос"
        }
    }
}

/// A cancellation flag shared by synchronous archive work and URLSession operations.
/// Task cancellation alone cannot interrupt a synchronous ZIP reader on its worker thread.
final class ImportCancellation: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false
    private var tasks: [ObjectIdentifier: URLSessionTask] = [:]

    func check() throws {
        lock.lock(); let value = cancelled; lock.unlock()
        if value || Task.isCancelled { throw CancellationError() }
    }
    func register(_ task: URLSessionTask) {
        lock.lock()
        let shouldCancel = cancelled
        if !shouldCancel { tasks[ObjectIdentifier(task)] = task }
        lock.unlock()
        if shouldCancel { task.cancel() }
    }
    func unregister(_ task: URLSessionTask) {
        lock.lock(); tasks.removeValue(forKey: ObjectIdentifier(task)); lock.unlock()
    }
    func cancel() {
        lock.lock(); cancelled = true; let pending = Array(tasks.values); tasks.removeAll(); lock.unlock()
        pending.forEach { $0.cancel() }
    }
}

struct StagedImport { let workspace: URL; let source: URL }

enum ImportFiles {
    /// Copies security-scoped content on a worker. Cancellation is checked between 1 MB chunks.
    static func stage(_ source: URL, cancellation: ImportCancellation,
                      progress: @escaping (ImportProgress) -> Void) async throws -> StagedImport {
        try await Task.detached(priority: .userInitiated) {
            try stageSynchronously(source, cancellation: cancellation, progress: progress)
        }.value
    }

    // DirectoryEnumerator's NSFastEnumeration iterator is synchronous by contract.
    // Keeping the loop in this helper also makes its worker-thread ownership explicit.
    private static func stageSynchronously(_ source: URL, cancellation: ImportCancellation,
                                           progress: @escaping (ImportProgress) -> Void) throws -> StagedImport {
            let fm = FileManager.default
            let workspace = fm.temporaryDirectory.appendingPathComponent("dustore-import-" + UUID().uuidString, isDirectory: true)
            let target = workspace.appendingPathComponent(source.lastPathComponent)
            try cancellation.check()
            try fm.createDirectory(at: workspace, withIntermediateDirectories: true)
            do {
                var directory: ObjCBool = false
                guard fm.fileExists(atPath: source.path, isDirectory: &directory) else { throw PortError(message: "Исходный файл больше не доступен.") }
                if directory.boolValue {
                    try fm.createDirectory(at: target, withIntermediateDirectories: true)
                    guard let walker = fm.enumerator(at: source, includingPropertiesForKeys: [.isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey]) else {
                        throw PortError(message: "Не удалось прочитать папку игры.")
                    }
                    for case let item as URL in walker {
                        try cancellation.check()
                        let values = try item.resourceValues(forKeys: [.isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey])
                        if values.isSymbolicLink == true { walker.skipDescendants(); continue }
                        let relative = String(item.path.dropFirst(source.path.count)).trimmingCharacters(in: CharacterSet(charactersIn: "/"))
                        let destination = target.appendingPathComponent(relative)
                        if values.isDirectory == true { try fm.createDirectory(at: destination, withIntermediateDirectories: true) }
                        else if values.isRegularFile == true {
                            progress(ImportProgress(phase: .copying, detail: item.lastPathComponent))
                            try copy(item, to: destination, cancellation: cancellation)
                        }
                    }
                } else {
                    let length = (try source.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
                    var last = Date.distantPast
                    try copy(source, to: target, cancellation: cancellation) { bytes in
                        if Date().timeIntervalSince(last) >= 0.12 || bytes == length {
                            last = Date()
                            progress(ImportProgress(phase: .copying, fraction: length > 0 ? Double(bytes) / Double(length) : nil,
                                                    detail: source.lastPathComponent))
                        }
                    }
                }
                try cancellation.check()
                return StagedImport(workspace: workspace, source: target)
            } catch {
                try? fm.removeItem(at: workspace)
                throw error
            }
    }

    static func copy(_ source: URL, to target: URL, cancellation: ImportCancellation,
                     progress: ((Int) -> Void)? = nil) throws {
        try cancellation.check()
        try FileManager.default.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard FileManager.default.createFile(atPath: target.path, contents: nil) else { throw PortError(message: "Не удалось создать файл игры.") }
        let input = try FileHandle(forReadingFrom: source)
        defer { try? input.close() }
        let output = try FileHandle(forWritingTo: target)
        defer { try? output.close() }
        var bytes = 0
        while true {
            try cancellation.check()
            guard let chunk = try input.read(upToCount: 1_048_576), !chunk.isEmpty else { break }
            try output.write(contentsOf: chunk)
            bytes += chunk.count
            progress?(bytes)
        }
        try cancellation.check()
    }
}
