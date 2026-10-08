import XCTest
import WebKit
@testable import DustoreX

final class CoreTests: XCTestCase {
    private func workspace() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("dustore-tests-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: folder) }
        return folder
    }
    func testLegacyLibraryDecodesWithoutNewMetadata() throws {
        let json = """
        [{"id":"00850164-0568-4005-9DEA-0697F3C75CF2","title":"Старая игра","kind":"web","added":0}]
        """
        let games = try JSONDecoder().decode([Game].self, from: Data(json.utf8))
        XCTAssertEqual(games.count, 1); XCTAssertNil(games[0].favorite); XCTAssertNil(games[0].lastPlayed)
        XCTAssertTrue(games[0].playable)
    }
    func testZipRejectsShortAndMalformedDirectoryWithoutCrash() throws {
        for bytes in [Data(), Data([0x50, 0x4b, 0x05, 0x06]), Data(repeating: 0, count: 40)] {
            XCTAssertThrowsError(try ZipArchive(source: DataZipSource(bytes)))
        }
        var broken = Data(repeating: 0, count: 22)
        broken.replaceSubrange(0..<4, with: [0x50, 0x4b, 0x05, 0x06])
        broken[10] = 1; broken[12] = 90
        XCTAssertThrowsError(try ZipArchive(source: DataZipSource(broken)))
    }
    func testDataSourceRejectsOutOfRangeOffsets() {
        let source = DataZipSource(Data([1, 2, 3]))
        XCTAssertThrowsError(try source.read(at: UInt64.max, count: 10))
        XCTAssertThrowsError(try source.read(at: 1, count: -1))
    }
    func testCopyCancellationLeavesOriginalIntact() throws {
        let folder = try workspace(), source = folder.appendingPathComponent("original.bin"), target = folder.appendingPathComponent("copy.bin")
        let content = Data(repeating: 0x42, count: 2_500_000)
        try content.write(to: source)
        let cancellation = ImportCancellation()
        XCTAssertThrowsError(try ImportFiles.copy(source, to: target, cancellation: cancellation) { _ in cancellation.cancel() })
        XCTAssertEqual(try Data(contentsOf: source), content)
        XCTAssertLessThan((try Data(contentsOf: target)).count, content.count)
    }
    func testPrecancelledArchiveAndImporterStop() async throws {
        let folder = try workspace(), source = folder.appendingPathComponent("index.html")
        try "<html></html>".write(to: source, atomically: true, encoding: .utf8)
        let cancellation = ImportCancellation(); cancellation.cancel()
        XCTAssertThrowsError(try ZipArchive(source: DataZipSource(Data()), cancellation: cancellation))
        do { _ = try await Porter.importGame(from: source, title: "Cancelled", cancellation: cancellation) { _ in }; XCTFail("Import must be cancelled") }
        catch { XCTAssertTrue(error is CancellationError) }
    }
    func testActualWebFolderImportProducesPlayableFiles() async throws {
        let folder = try workspace()
        try "<html><script src='game.js'></script></html>".write(to: folder.appendingPathComponent("index.html"), atomically: true, encoding: .utf8)
        try "document.body.dataset.ready='true';".write(to: folder.appendingPathComponent("game.js"), atomically: true, encoding: .utf8)
        let game = try await Porter.importGame(from: folder, title: "Owned web test") { _ in }
        defer { try? FileManager.default.removeItem(at: game.folder) }
        XCTAssertEqual(game.kind, .web)
        XCTAssertTrue(FileManager.default.fileExists(atPath: game.webRoot.appendingPathComponent("index.html").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: game.folder.appendingPathComponent("src").path))
    }
    func testMotionPolicyRespectsOffAndSystemReduceMotion() {
        XCTAssertNil(MotionPreference.off.animation(reduceMotion: false))
        XCTAssertFalse(MotionPreference.full.moves(reduceMotion: true))
        XCTAssertFalse(MotionPreference.reduced.moves(reduceMotion: false))
    }
    @MainActor
    func testNativeWebKitInputBridgePressAndRelease() async throws {
        let view = WKWebView(frame: .zero)
        let loaded = expectation(description: "owned page loaded")
        let delegate = PageDelegate(loaded: loaded)
        view.navigationDelegate = delegate
        view.loadHTMLString("<canvas tabindex='0'></canvas><script>window.events=[];document.addEventListener('keydown',e=>events.push(e.type+':'+e.code+':'+e.keyCode));document.addEventListener('keyup',e=>events.push(e.type+':'+e.code+':'+e.keyCode));</script>", baseURL: nil)
        await fulfillment(of: [loaded], timeout: 15)
        _ = try await view.evaluateJavaScript(GameInput.bridgeScript)
        _ = try await bridge(view, keys: ["ArrowUp", "Space"])
        _ = try await bridge(view, keys: ["ArrowUp", "Space"])
        _ = try await bridge(view, keys: [])
        let events = try await view.evaluateJavaScript("window.events") as? [String]
        XCTAssertEqual(events, ["keydown:ArrowUp:38", "keydown:Space:32", "keyup:ArrowUp:38", "keyup:Space:32"])
        view.navigationDelegate = nil
    }
    @MainActor
    private func bridge(_ view: WKWebView, keys: [String]) async throws -> Any {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Any, Error>) in
            view.callAsyncJavaScript("return window.__dustoreInput(keys)", arguments: ["keys": keys], in: nil, in: .page) { result in
                continuation.resume(with: result)
            }
        }
    }
}

private final class PageDelegate: NSObject, WKNavigationDelegate {
    let loaded: XCTestExpectation
    init(loaded: XCTestExpectation) { self.loaded = loaded }
    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) { loaded.fulfill() }
}
