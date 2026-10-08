import XCTest
import Network
import WebKit
@testable import DustoreX

final class StoreDownloadTests: XCTestCase {
    @MainActor
    func testNativeDownloadSurvivesStoreViewTeardownAndCanCancel() async throws {
        let ready = expectation(description: "owned loopback listener ready")
        let server = try SlowDownloadServer()
        server.start(ready: ready)
        defer { server.stop() }
        await fulfillment(of: [ready], timeout: 15)
        guard let port = server.port else { throw DownloadTestError.listenerUnavailable }

        let library = Library.shared
        XCTAssertFalse(library.isBusy)
        library.dismissTransfer()
        let originalGames = library.games.count
        let downloadsFolder = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("Downloads")
        let originalFiles = Set((try? FileManager.default.contentsOfDirectory(atPath: downloadsFolder.path)) ?? [])
        defer {
            if library.isBusy { library.cancelTransfer() }
            library.dismissTransfer()
            for file in (try? FileManager.default.contentsOfDirectory(at: downloadsFolder, includingPropertiesForKeys: nil)) ?? [] {
                if !originalFiles.contains(file.lastPathComponent) && file.lastPathComponent.hasSuffix("-owned-store-lifecycle.bin") {
                    try? FileManager.default.removeItem(at: file)
                }
            }
        }

        var view: WKWebView? = WKWebView(frame: .zero)
        var navigation: StoreWebView.Coordinator? = StoreWebView.Coordinator(library: library, state: StoreState())
        navigation?.attach(view!)
        let url = URL(string: "http://localhost:\(port)/owned-store-lifecycle.bin")!
        let download: WKDownload = await withCheckedContinuation { continuation in
            view!.startDownload(using: URLRequest(url: url)) { continuation.resume(returning: $0) }
        }
        library.storeDownloads.receive(download, from: view!)
        try await waitUntil { library.isBusy && (library.transfer?.fraction ?? 0) > 0 }
        let originalTransferID = library.transfer?.id

        weak var discardedNavigation = navigation
        StoreWebView.dismantleUIView(view!, coordinator: navigation!)
        navigation = nil
        view = nil
        XCTAssertNil(discardedNavigation, "The view's coordinator must be disposable during adaptive layout changes.")
        XCTAssertTrue(library.isBusy, "A view teardown must preserve the library-owned native download.")

        library.cancelTransfer()
        try await waitUntil { !library.isBusy }
        XCTAssertEqual(library.transfer?.outcome, .cancelled)
        XCTAssertEqual(library.games.count, originalGames)

        // WKDownload can return no resume data. When it does, exercise real resume with no store view.
        if library.transfer?.canRetry == true {
            library.retryTransfer()
            try await waitUntil { library.isBusy && library.transfer?.id != originalTransferID && (library.transfer?.fraction ?? 0) > 0 }
            library.cancelTransfer()
            try await waitUntil { !library.isBusy }
            XCTAssertEqual(library.transfer?.outcome, .cancelled)
            XCTAssertEqual(library.games.count, originalGames)
        }
    }

    @MainActor
    private func waitUntil(_ condition: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(15)
        while !condition() {
            if Date() >= deadline { throw DownloadTestError.nativeDownloadDidNotSettle }
            try await Task.sleep(nanoseconds: 50_000_000)
        }
    }
}

private enum DownloadTestError: Error { case listenerUnavailable, nativeDownloadDidNotSettle }

/// Owns one loopback port and streams controlled bytes slowly enough to test real cancellation.
/// It never contacts the store, imports a game or consumes an edition quota.
private final class SlowDownloadServer: @unchecked Sendable {
    private let queue = DispatchQueue(label: "dustore.tests.download-loopback")
    private let listener: NWListener
    private var connections: [NWConnection] = []
    private let length = 32 * 1024 * 1024
    var port: UInt16? { listener.port?.rawValue }

    init() throws {
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        listener = try NWListener(using: parameters)
    }
    func start(ready: XCTestExpectation) {
        listener.stateUpdateHandler = { state in
            if case .ready = state { ready.fulfill() }
            if case .failed = state { ready.fulfill() }
        }
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { connection.cancel(); return }
            self.connections.append(connection)
            connection.start(queue: self.queue)
            self.readRequest(connection, accumulated: Data())
        }
        listener.start(queue: queue)
    }
    func stop() {
        queue.async {
            self.listener.stateUpdateHandler = nil
            self.listener.cancel()
            self.connections.forEach { $0.cancel() }
            self.connections.removeAll()
        }
    }
    private func readRequest(_ connection: NWConnection, accumulated: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16_384) { [weak self] data, _, complete, error in
            guard let self, error == nil, let data else { connection.cancel(); return }
            var request = accumulated; request.append(data)
            guard request.count < 65_536 else { connection.cancel(); return }
            let text = String(decoding: request, as: UTF8.self)
            guard text.contains("\r\n\r\n") else {
                if complete { connection.cancel() } else { self.readRequest(connection, accumulated: request) }
                return
            }
            let range = text.lowercased().components(separatedBy: "\r\n").first { $0.hasPrefix("range: bytes=") }
            let start = range.flatMap { Int($0.dropFirst("range: bytes=".count).split(separator: "-").first ?? "") } ?? 0
            guard start >= 0, start < self.length else { connection.cancel(); return }
            let status = start > 0 ? "206 Partial Content" : "200 OK"
            let rangeHeader = start > 0 ? "Content-Range: bytes \(start)-\(self.length - 1)/\(self.length)\r\n" : ""
            let headers = "HTTP/1.1 \(status)\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=owned-store-lifecycle.bin\r\nAccept-Ranges: bytes\r\nETag: \"owned-lifecycle-v1\"\r\nContent-Length: \(self.length - start)\r\n\(rangeHeader)Connection: close\r\n\r\n"
            connection.send(content: Data(headers.utf8), completion: .contentProcessed { [weak self] error in
                if error == nil { self?.sendBody(connection, offset: start) }
            })
        }
    }
    private func sendBody(_ connection: NWConnection, offset: Int) {
        guard offset < length else { connection.cancel(); return }
        let count = min(65_536, length - offset)
        connection.send(content: Data(repeating: 0x42, count: count), completion: .contentProcessed { [weak self] error in
            guard let self, error == nil else { return }
            self.queue.asyncAfter(deadline: .now() + 0.05) { [weak self] in self?.sendBody(connection, offset: offset + count) }
        })
    }
}
