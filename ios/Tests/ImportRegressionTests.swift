import XCTest
@testable import DustoreX

final class ImportRegressionTests: XCTestCase {
    private func workspace() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("dustore-import-regression-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: folder) }
        return folder
    }

    private func put(_ value: UInt64, in bytes: inout Data, at offset: Int, count: Int) {
        for index in 0..<count { bytes[offset + index] = UInt8(truncatingIfNeeded: value >> (index * 8)) }
    }

    func testZIP64RejectsExtremeEntryCountsWithoutIntegerTrap() {
        for count in [UInt64(100_001), UInt64(Int.max) + 1, UInt64.max] {
            var archive = Data(repeating: 0, count: 78)
            put(0x0606_4b50, in: &archive, at: 0, count: 4)
            put(44, in: &archive, at: 4, count: 8)
            put(count, in: &archive, at: 24, count: 8)
            put(count, in: &archive, at: 32, count: 8)
            put(0x0605_4b50, in: &archive, at: 56, count: 4)
            put(0xffff, in: &archive, at: 64, count: 2)
            put(0xffff, in: &archive, at: 66, count: 2)
            XCTAssertThrowsError(try ZipArchive(source: DataZipSource(archive))) { error in
                guard case ZipError.truncated = error else { return XCTFail("Expected an invalid-directory error: \(error)") }
            }
        }
    }

    func testEmbeddedGodotFooterCannotSubtractBeyondFileStart() async throws {
        let source = try workspace().appendingPathComponent("broken-game.exe")
        let length = 1_000_128
        for claimedSize in [UInt64(length - 11), UInt64(length - 1), UInt64.max] {
            var executable = Data(repeating: 0, count: length)
            put(claimedSize, in: &executable, at: length - 12, count: 8)
            put(0x4350_4447, in: &executable, at: length - 4, count: 4)
            try executable.write(to: source)
            let game = try await Porter.importGame(from: source, title: "Invalid footer") { _ in }
            defer { try? FileManager.default.removeItem(at: game.folder) }
            XCTAssertEqual(game.kind, .unsupported)
            XCTAssertTrue(FileManager.default.fileExists(atPath: source.path), "A rejected build must preserve its source.")
        }
    }

    func testInlineHTMLGameImportsWithoutSiblingScriptFiles() async throws {
        let source = try workspace().appendingPathComponent("index.html")
        let html = "<html><body><script>document.body.dataset.game='inline';</script></body></html>"
        try html.write(to: source, atomically: true, encoding: .utf8)
        let game = try await Porter.importGame(from: source, title: "Inline HTML") { _ in }
        defer { try? FileManager.default.removeItem(at: game.folder) }
        XCTAssertEqual(game.kind, .web)
        XCTAssertEqual(try String(contentsOf: game.webRoot.appendingPathComponent("index.html"), encoding: .utf8), html)
    }

    func testHTMLGameImportsScriptsFromSubfolders() async throws {
        let source = try workspace()
        let scripts = source.appendingPathComponent("scripts", isDirectory: true)
        try FileManager.default.createDirectory(at: scripts, withIntermediateDirectories: true)
        try "<html><script src='scripts/game.js'></script></html>".write(to: source.appendingPathComponent("index.html"), atomically: true, encoding: .utf8)
        let script = "document.body.dataset.game='nested';"
        try script.write(to: scripts.appendingPathComponent("game.js"), atomically: true, encoding: .utf8)
        let game = try await Porter.importGame(from: source, title: "Nested scripts") { _ in }
        defer { try? FileManager.default.removeItem(at: game.folder) }
        XCTAssertEqual(game.kind, .web)
        XCTAssertEqual(try String(contentsOf: game.webRoot.appendingPathComponent("scripts/game.js"), encoding: .utf8), script)
    }

    func testMixedCaseHTMLGameUsesExactPlayerEntryFilename() async throws {
        for name in ["INDEX.HTML", "Index.Html"] {
            let source = try workspace().appendingPathComponent(name)
            let html = "<html><script>window.owned='case-sensitive-entry';</script></html>"
            try html.write(to: source, atomically: true, encoding: .utf8)
            let game = try await Porter.importGame(from: source, title: "Mixed case entry") { _ in }
            defer { try? FileManager.default.removeItem(at: game.folder) }
            XCTAssertEqual(game.kind, .web)
            let names = try FileManager.default.contentsOfDirectory(atPath: game.webRoot.path)
            XCTAssertTrue(names.contains("index.html"), "The player always requests /index.html, including on a case-sensitive device.")
            XCTAssertFalse(names.contains(name), "Check exact stored case; fileExists alone can pass on a case-insensitive simulator.")
            XCTAssertEqual(try String(contentsOf: game.webRoot.appendingPathComponent("index.html"), encoding: .utf8), html)
            XCTAssertTrue(FileManager.default.fileExists(atPath: source.path))
        }
    }

    @MainActor
    func testCancellationBetweenDownloadCompletionAndImportHandoffStopsImport() async throws {
        let library = Library.shared
        XCTAssertFalse(library.isBusy)
        guard !library.isBusy else { return }
        library.dismissTransfer()
        defer { library.dismissTransfer() }
        let source = try workspace().appendingPathComponent("index.html")
        try "<html><script>window.owned=true;</script></html>".write(to: source, atomically: true, encoding: .utf8)
        let previousGames = library.games
        let id = try XCTUnwrap(library.beginDownload(title: "Owned finished download", cancel: {}))
        library.cancelTransfer()
        XCTAssertEqual(library.transfer?.outcome, .cancelling)
        // Matches the queued handoff in StoreDownloadSession.downloadDidFinish.
        await library.importDownloaded(from: source, title: "Must remain cancelled", id: id)
        XCTAssertEqual(library.transfer?.outcome, .cancelled)
        XCTAssertFalse(library.isBusy)
        XCTAssertEqual(library.games, previousGames, "A cancellation must not add an imported game.")
        XCTAssertTrue(FileManager.default.fileExists(atPath: source.path), "Cancellation must preserve the downloaded source.")
    }
}
