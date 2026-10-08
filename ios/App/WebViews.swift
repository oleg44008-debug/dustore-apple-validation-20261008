import SwiftUI
import WebKit

@MainActor
final class StoreState: ObservableObject {
    @Published var loading = false
    @Published var progress = 0.0
    @Published var error: String?
    @Published var canGoBack = false
    weak var webView: WKWebView?
    func reload() {
        error = nil
        if let view = webView {
            if view.url == nil, let url = URL(string: "https://dustore.ru/") { view.load(URLRequest(url: url)) }
            else { view.reload() }
        }
    }
    func back() { webView?.goBack() }
}

/// Native navigation and WKDownload recovery remain available when the website is offline.
struct StoreWebView: UIViewRepresentable {
    let library: Library
    @ObservedObject var state: StoreState
    func makeCoordinator() -> Coordinator { Coordinator(library: library, state: state) }
    func makeUIView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .default()
        let view = WKWebView(frame: .zero, configuration: configuration)
        view.navigationDelegate = context.coordinator
        view.allowsBackForwardNavigationGestures = true
        view.isOpaque = false
        view.backgroundColor = UIColor(Theme.background)
        state.webView = view
        context.coordinator.attach(view)
        if ProcessInfo.processInfo.arguments.contains("-dustoreOfflineStore") {
            DispatchQueue.main.async { state.error = "Тестовый автономный режим. Проверьте подключение к интернету." }
        } else if let url = URL(string: "https://dustore.ru/") { view.load(URLRequest(url: url)) }
        return view
    }
    func updateUIView(_ view: WKWebView, context: Context) {}
    static func dismantleUIView(_ view: WKWebView, coordinator: Coordinator) {
        coordinator.close()
        view.stopLoading(); view.navigationDelegate = nil
    }

    @MainActor
    final class Coordinator: NSObject, WKNavigationDelegate, WKDownloadDelegate {
        let library: Library
        let state: StoreState
        private var navigationObservation: NSKeyValueObservation?
        private var targets: [ObjectIdentifier: DownloadRecord] = [:]
        private weak var lastView: WKWebView?
        init(library: Library, state: StoreState) { self.library = library; self.state = state }
        func attach(_ view: WKWebView) {
            lastView = view
            navigationObservation = view.observe(\.estimatedProgress, options: [.new]) { [weak self] view, _ in
                Task { @MainActor in self?.state.progress = view.estimatedProgress }
            }
        }
        func close() {
            navigationObservation?.invalidate(); navigationObservation = nil
            for record in Array(targets.values) { cancel(record.download) }
        }
        func webView(_ webView: WKWebView, didStartProvisionalNavigation navigation: WKNavigation!) {
            state.loading = true; state.error = nil; state.canGoBack = webView.canGoBack
        }
        func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
            state.loading = false; state.canGoBack = webView.canGoBack
        }
        func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) { failed(error) }
        func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) { failed(error) }
        private func failed(_ error: Error) {
            state.loading = false
            if (error as NSError).code != NSURLErrorCancelled { state.error = error.localizedDescription }
        }
        func webView(_ webView: WKWebView, decidePolicyFor response: WKNavigationResponse, decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void) {
            lastView = webView
            let http = response.response as? HTTPURLResponse
            let disposition = http?.value(forHTTPHeaderField: "Content-Disposition")?.lowercased() ?? ""
            let type = response.response.mimeType?.lowercased() ?? ""
            let download = disposition.contains("attachment") || !response.canShowMIMEType
                || ["application/zip", "application/octet-stream", "application/x-zip-compressed", "application/vnd.android.package-archive"].contains(type)
            if download && library.isBusy {
                library.message = "Дождитесь текущего переноса или отмените его."
                decisionHandler(.cancel)
            } else { decisionHandler(download ? .download : .allow) }
        }
        func webView(_ webView: WKWebView, navigationResponse: WKNavigationResponse, didBecome download: WKDownload) {
            state.loading = false; download.delegate = self
        }
        func download(_ download: WKDownload, decideDestinationUsing response: URLResponse, suggestedFilename: String, completionHandler: @escaping (URL?) -> Void) {
            if let record = targets[ObjectIdentifier(download)] { completionHandler(record.file); return }
            let folder = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("Downloads", isDirectory: true)
            do { try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true) }
            catch { library.message = error.localizedDescription; completionHandler(nil); return }
            let safeName = (suggestedFilename as NSString).lastPathComponent
            let file = folder.appendingPathComponent(UUID().uuidString + "-" + safeName)
            var title = lastView?.title?.replacingOccurrences(of: "Dustore — ", with: "").replacingOccurrences(of: " — Dustore", with: "")
            if title?.isEmpty != false { title = (safeName as NSString).deletingPathExtension }
            guard let id = library.beginDownload(title: title ?? safeName, cancel: { [weak self, weak download] in
                if let download { self?.cancel(download) }
            }) else { completionHandler(nil); return }
            let record = DownloadRecord(download: download, file: file, title: title, id: id)
            targets[ObjectIdentifier(download)] = record
            observe(record)
            completionHandler(file)
        }
        private func observe(_ record: DownloadRecord) {
            record.observation = record.download.progress.observe(\.fractionCompleted, options: [.initial, .new]) { [weak self, weak record] _, _ in
                Task { @MainActor in
                    guard let self, let record else { return }
                    let progress = record.download.progress
                    self.library.downloadProgress(id: record.id, fraction: progress.totalUnitCount > 0 ? progress.fractionCompleted : nil,
                        detail: progress.totalUnitCount > 0 ? ByteCountFormatter.string(fromByteCount: progress.completedUnitCount, countStyle: .file) + " из " + ByteCountFormatter.string(fromByteCount: progress.totalUnitCount, countStyle: .file) : "Размер загрузки пока неизвестен")
                }
            }
        }
        private func cancel(_ download: WKDownload) {
            guard let record = targets[ObjectIdentifier(download)], !record.cancelling else { return }
            record.cancelling = true
            download.cancel { [weak self] data in
                Task { @MainActor in self?.completeFailure(download, error: nil, resumeData: data) }
            }
        }
        func downloadDidFinish(_ download: WKDownload) {
            guard let record = targets.removeValue(forKey: ObjectIdentifier(download)) else { return }
            record.observation?.invalidate()
            Task { @MainActor in await library.importDownloaded(from: record.file, title: record.title, id: record.id) }
        }
        func download(_ download: WKDownload, didFailWithError error: Error, resumeData: Data?) {
            guard targets[ObjectIdentifier(download)]?.cancelling != true else { return }
            completeFailure(download, error: error, resumeData: resumeData)
        }
        private func completeFailure(_ download: WKDownload, error: Error?, resumeData: Data?) {
            guard let record = targets.removeValue(forKey: ObjectIdentifier(download)) else { return }
            record.observation?.invalidate()
            let retry: (() -> Void)? = resumeData.map { data in
                { [weak self] in self?.resume(data, record: record) }
            }
            library.downloadFailed(id: record.id, error: error, retry: retry)
        }
        private func resume(_ data: Data, record: DownloadRecord) {
            guard let view = lastView else { library.message = "Откройте магазин и повторите загрузку."; return }
            view.resumeDownload(fromResumeData: data) { [weak self] download in
                guard let self else { return }
                download.delegate = self
                guard let id = self.library.beginDownload(title: record.title ?? record.file.lastPathComponent, cancel: { [weak self, weak download] in
                    if let download { self?.cancel(download) }
                }) else { download.cancel { _ in }; return }
                let resumed = DownloadRecord(download: download, file: record.file, title: record.title, id: id)
                self.targets[ObjectIdentifier(download)] = resumed
                self.observe(resumed)
            }
        }
        private final class DownloadRecord {
            let download: WKDownload, file: URL, title: String?, id: UUID
            var observation: NSKeyValueObservation?
            var cancelling = false
            init(download: WKDownload, file: URL, title: String?, id: UUID) { self.download = download; self.file = file; self.title = title; self.id = id }
        }
    }
}

struct GameWebView: UIViewRepresentable {
    let game: Game
    @ObservedObject var session: PlayerSession
    func makeCoordinator() -> Coordinator { Coordinator(session: session) }
    func makeUIView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.setURLSchemeHandler(GameServer(root: game.webRoot), forURLScheme: GameServer.scheme)
        configuration.allowsInlineMediaPlayback = true
        configuration.mediaTypesRequiringUserActionForPlayback = []
        configuration.userContentController.add(context.coordinator, name: "ex")
        configuration.userContentController.addUserScript(WKUserScript(source: GameInput.bridgeScript, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        let view = WKWebView(frame: .zero, configuration: configuration)
        view.navigationDelegate = context.coordinator
        view.scrollView.isScrollEnabled = false
        view.scrollView.contentInsetAdjustmentBehavior = .never
        view.isOpaque = true; view.backgroundColor = .black
        if #available(iOS 16.4, *) { view.isInspectable = true }
        session.attach(view)
        return view
    }
    func updateUIView(_ view: WKWebView, context: Context) {}
    static func dismantleUIView(_ view: WKWebView, coordinator: Coordinator) {
        coordinator.session.close()
        view.stopLoading(); view.navigationDelegate = nil
        view.configuration.userContentController.removeScriptMessageHandler(forName: "ex")
        view.loadHTMLString("", baseURL: nil)
    }
    @MainActor
    final class Coordinator: NSObject, WKScriptMessageHandler, WKNavigationDelegate {
        let session: PlayerSession
        init(session: PlayerSession) { self.session = session }
        func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
            guard message.frameInfo.isMainFrame else { return }
            session.report(message.body)
        }
        func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) { session.pageLoaded() }
        func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) { session.fail(error.localizedDescription) }
        func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) { session.fail(error.localizedDescription) }
        func webViewWebContentProcessDidTerminate(_ webView: WKWebView) { session.fail("Процесс игры остановлен системой. Можно перезапустить игру.") }
    }
}
