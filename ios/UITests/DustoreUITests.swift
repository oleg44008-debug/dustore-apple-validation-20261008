import XCTest
import UIKit

final class DustoreUITests: XCTestCase {
    override func setUpWithError() throws { continueAfterFailure = false }
    private func launch(_ fixture: String = "library", largeText: Bool = false) -> XCUIApplication {
        let app = XCUIApplication()
        app.launchArguments = ["-dustoreUITest", "-dustoreFixture", fixture, "-dustoreOfflineStore"]
        if let fallback = ProcessInfo.processInfo.environment["DUSTORE_CI_SIMULATOR_DYLD_FALLBACK"] {
            app.launchEnvironment["DYLD_FALLBACK_LIBRARY_PATH"] = fallback
        }
        if largeText { app.launchArguments += ["-UIPreferredContentSizeCategoryName", "UICTContentSizeCategoryAccessibilityXXXL"] }
        app.launch(); return app
    }
    private func capture(_ name: String) {
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name; attachment.lifetime = .keepAlways; add(attachment)
    }
    // Source-only Phase B candidate. Native execution is intentionally pending.
    private enum Destination: String, CaseIterable, Hashable {
        case store = "Магазин", library = "Библиотека", ex = "eX", settings = "Настройки"
        var symbol: String {
            switch self {
            case .store: return "bag"
            case .library: return "square.grid.2x2"
            case .ex: return "arrow.left.arrow.right"
            case .settings: return "slider.horizontal.3"
            }
        }
        var sidebarID: String {
            switch self {
            case .store: return "tab.0"
            case .library: return "tab.1"
            case .ex: return "tab.2"
            case .settings: return "tab.3"
            }
        }
    }
    private enum NavigationSurface: String, Equatable {
        case systemTabBar, iPadSystemTopTabs, adaptiveSidebar
    }
    private struct Navigation {
        let surface: NavigationSurface
        let parentFrame: CGRect
        let buttons: [Destination: XCUIElement]
    }
    private struct NavigationIssue: Error, CustomStringConvertible {
        let description: String
        init(_ description: String) { self.description = description }
    }
    private func positive(_ frame: CGRect) -> Bool {
        !frame.isNull && !frame.isInfinite && !frame.isEmpty
            && frame.minX.isFinite && frame.minY.isFinite
            && frame.width.isFinite && frame.height.isFinite
    }
    private func contains(_ parent: CGRect, _ child: CGRect) -> Bool {
        positive(parent) && positive(child) && parent.insetBy(dx: -1, dy: -1).contains(child)
    }
    private func coincides(_ left: CGRect, _ right: CGRect) -> Bool {
        abs(left.minX - right.minX) <= 1 && abs(left.minY - right.minY) <= 1
            && abs(left.width - right.width) <= 1 && abs(left.height - right.height) <= 1
    }
    private func validate(_ button: XCUIElement, destination: Destination,
                          identifier: String?, parent: CGRect, window: CGRect) throws {
        guard button.exists, button.elementType == .button,
              button.label == destination.rawValue else {
            throw NavigationIssue("Missing exact navigation Button \(destination.rawValue)")
        }
        if let identifier = identifier, button.identifier != identifier {
            throw NavigationIssue("Wrong navigation identity for \(destination.rawValue): \(button.identifier)")
        }
        let frame = button.frame
        guard contains(parent, frame), contains(window, frame), button.isHittable else {
            throw NavigationIssue("Navigation destination is not immediately hittable: \(destination.rawValue), \(frame)")
        }
    }
    private func exactWindow(in app: XCUIApplication) throws -> CGRect {
        let frames = app.windows.allElementsBoundByIndex.compactMap { element -> CGRect? in
            guard element.exists else { return nil }
            let frame = element.frame
            return positive(frame) ? frame : nil
        }
        guard frames.count == 1 else {
            throw NavigationIssue("Expected one app window, found \(frames.count)")
        }
        return frames[0]
    }
    private func systemBar(_ bar: XCUIElement, window: CGRect) throws -> Navigation {
        // A single native snapshot keeps all four frames in the same accessibility generation.
        // Interaction still uses a fresh, unique exact-label Button inside this one native bar.
        let snapshot = try bar.snapshot()
        let captured = snapshot.children.filter { $0.elementType == .button }
        let children = bar.children(matching: .button)
        let parent = snapshot.frame
        guard bar.exists, snapshot.elementType == .tabBar,
              captured.count == Destination.allCases.count,
              children.count == Destination.allCases.count, contains(window, parent) else {
            throw NavigationIssue("The actual system TabBar must contain exactly four destinations")
        }
        var buttons: [Destination: XCUIElement] = [:]
        for destination in Destination.allCases {
            let native = captured.filter { $0.label == destination.rawValue }
            let matches = children.matching(NSPredicate(format: "label == %@", destination.rawValue))
            guard native.count == 1, matches.count == 1 else {
                throw NavigationIssue("System TabBar destination is missing or ambiguous: \(destination.rawValue)")
            }
            let frame = native[0].frame
            let button = matches.element
            guard contains(parent, frame), contains(window, frame), button.exists, button.isHittable else {
                throw NavigationIssue("Navigation destination is not immediately hittable: \(destination.rawValue), \(frame)")
            }
            buttons[destination] = button
        }
        return Navigation(surface: .systemTabBar, parentFrame: parent, buttons: buttons)
    }
    private func iPadTopGroup(_ parent: XCUIElement, window: CGRect) throws -> Navigation {
        // The recorded native iPadOS18 hierarchy uses Other -> four direct Button
        // cells. A cell may expose one same-frame Button child. Never choose an
        // arbitrary app button by title or a private UIKit class.
        let cells = parent.children(matching: .button).allElementsBoundByIndex
        guard parent.exists, parent.elementType == .other,
              cells.count == Destination.allCases.count, contains(window, parent.frame),
              parent.frame.maxY <= window.minY + window.height * 0.25 else {
            throw NavigationIssue("Not the four-cell native iPad header")
        }
        var buttons: [Destination: XCUIElement] = [:]
        var cellFrames: [CGRect] = []
        for destination in Destination.allCases {
            let matches = cells.filter { $0.identifier == destination.symbol && $0.label == destination.rawValue }
            guard matches.count == 1 else {
                throw NavigationIssue("Native iPad top-tab identity is missing or ambiguous: \(destination.rawValue)")
            }
            let cell = matches[0]
            let nested = cell.children(matching: .button).allElementsBoundByIndex
            guard nested.count <= 1 else {
                throw NavigationIssue("Unsupported nested native top-tab shape: \(destination.rawValue)")
            }
            let button = nested.isEmpty ? cell : nested[0]
            guard button.children(matching: .button).count == 0,
                  button.identifier == destination.symbol, button.label == destination.rawValue,
                  coincides(cell.frame, button.frame) else {
                throw NavigationIssue("Top-tab nested Button must retain the cell identity and bounds")
            }
            try validate(button, destination: destination, identifier: destination.symbol,
                         parent: parent.frame, window: window)
            buttons[destination] = button
            cellFrames.append(cell.frame)
        }
        guard let first = cellFrames.first, let last = cellFrames.last,
              parent.frame.height <= cellFrames.map(\.height).max()! * 2.4 else {
            throw NavigationIssue("Native top-tab group is not a compact horizontal header")
        }
        for (index, frame) in cellFrames.enumerated() {
            guard abs(frame.midY - first.midY) <= 1 else {
                throw NavigationIssue("Native top-tabs must share one visible horizontal row")
            }
            if index > 0, cellFrames[index - 1].maxX > frame.minX + 1 {
                throw NavigationIssue("Native top-tab bounds overlap or have an unexpected order")
            }
        }
        guard last.maxX > first.minX else { throw NavigationIssue("Invalid native top-tab row") }
        return Navigation(surface: .iPadSystemTopTabs, parentFrame: parent.frame, buttons: buttons)
    }
    private func resolveNavigation(in app: XCUIApplication, requireNative: Bool) throws -> Navigation {
        let window = try exactWindow(in: app)
        let sidebarQueries = Destination.allCases.map {
            app.buttons.matching(NSPredicate(format: "identifier == %@", $0.sidebarID))
        }
        let sidebarCounts = sidebarQueries.map(\.count)
        if sidebarCounts.contains(where: { $0 > 0 }) {
            guard !requireNative, sidebarCounts.allSatisfy({ $0 == 1 }) else {
                throw NavigationIssue("XXXL requires native tabs; adaptive sidebar must otherwise be complete and unique")
            }
            var buttons: [Destination: XCUIElement] = [:]
            for (index, destination) in Destination.allCases.enumerated() {
                let button = sidebarQueries[index].element(boundBy: 0)
                try validate(button, destination: destination, identifier: destination.sidebarID,
                             parent: window, window: window)
                buttons[destination] = button
            }
            return Navigation(surface: .adaptiveSidebar, parentFrame: window, buttons: buttons)
        }
        let bars = app.tabBars.allElementsBoundByIndex
        if !bars.isEmpty {
            guard bars.count == 1 else { throw NavigationIssue("Ambiguous system TabBar container") }
            return try systemBar(bars[0], window: window)
        }
        guard UIDevice.current.userInterfaceIdiom == .pad else {
            throw NavigationIssue("A phone must expose an actual system TabBar")
        }
        if #available(iOS 18.0, *) {
            let possible = app.otherElements.containing(.button, identifier: Destination.library.symbol)
                .allElementsBoundByIndex
            let valid = possible.compactMap { try? iPadTopGroup($0, window: window) }
            guard valid.count == 1 else {
                throw NavigationIssue("Expected one complete native iPad top-tab header; found \(valid.count)")
            }
            return valid[0]
        }
        throw NavigationIssue("Legacy iPad without a sidebar must expose an actual system TabBar")
    }
    private func navigation(in app: XCUIApplication, requireNative: Bool = false, timeout: TimeInterval = 5) -> Navigation? {
        let deadline = ProcessInfo.processInfo.systemUptime + timeout
        func remaining() -> TimeInterval { max(0, deadline - ProcessInfo.processInfo.systemUptime) }
        var resolved: Navigation?
        var lastIssue = "Navigation not resolved"
        func attempt() -> Bool {
            do {
                let current = try resolveNavigation(in: app, requireNative: requireNative)
                guard remaining() > 0 else {
                    lastIssue = "Complete navigation read exceeded the original \(timeout)s readiness budget"
                    return false
                }
                resolved = current
                return true
            } catch {
                lastIssue = String(describing: error)
                return false
            }
        }
        // Do not spend the first poll interval before reading an already-ready native bar.
        // A retry receives only the remainder of the same original deadline.
        var ready = attempt()
        if !ready, remaining() > 0 {
            let predicate = NSPredicate { _, _ in attempt() }
            let expectation = XCTNSPredicateExpectation(predicate: predicate, object: app)
            ready = XCTWaiter.wait(for: [expectation], timeout: remaining()) == .completed
        }
        guard ready, remaining() > 0, let resolved = resolved else {
            let failure = XCTAttachment(string: lastIssue + "\n" + app.debugDescription)
            failure.name = "navigation-resolution-failure"
            failure.lifetime = .keepAlways; add(failure)
            XCTFail("All four destinations must exist and be immediately hittable: \(lastIssue)")
            return nil
        }
        return resolved
    }
    private func navigationEvidence(_ navigation: Navigation, name: String) {
        let rows = Destination.allCases.map {
            let button = navigation.buttons[$0]!
            return "\($0.rawValue) id=\(button.identifier) exists=\(button.exists) hittable=\(button.isHittable) frame=\(button.frame)"
        }
        let evidence = XCTAttachment(string: "surface=\(navigation.surface.rawValue)\nparent=\(navigation.parentFrame)\n" + rows.joined(separator: "\n"))
        evidence.name = name; evidence.lifetime = .keepAlways; add(evidence)
    }
    private func assertAllNavigation(in app: XCUIApplication, requireNative: Bool) {
        guard let navigation = navigation(in: app, requireNative: requireNative, timeout: 10) else { return }
        if requireNative {
            XCTAssertNotEqual(navigation.surface, .adaptiveSidebar, "Accessibility XXXL must preserve native navigation.")
        }
        navigationEvidence(navigation, name: "all-four-navigation-destinations")
    }
    private func reachableStoreRecovery(in app: XCUIApplication, retry: XCUIElement, timeout: TimeInterval) -> Bool {
        let deadline = ProcessInfo.processInfo.systemUptime + timeout
        func remaining() -> TimeInterval { max(0, deadline - ProcessInfo.processInfo.systemUptime) }
        let errors = app.scrollViews.matching(NSPredicate(format: "identifier == %@", "store.error"))
        guard let window = try? exactWindow(in: app), remaining() > 0 else { return false }
        func errorVisible() -> Bool {
            guard errors.count == 1 else { return false }
            let error = errors.element
            let titles = error.staticTexts.matching(NSPredicate(format: "label == %@", "Магазин недоступен"))
            guard titles.count == 1 else { return false }
            let title = titles.element
            let errorFrame = error.frame
            let titleFrame = title.frame
            return contains(window, errorFrame) && contains(window, titleFrame)
                && error.isHittable && title.isHittable && remaining() > 0
        }
        var visible = errorVisible()
        if !visible, remaining() > 0 {
            let ready = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in errorVisible() }, object: app)
            visible = XCTWaiter.wait(for: [ready], timeout: remaining()) == .completed
        }
        guard visible, remaining() > 0 else { return false }
        let error = errors.element
        var gestures = 0
        while remaining() > 0 {
            if retry.exists && retry.isHittable && remaining() > 0 { return true }
            guard gestures < 4, errors.count == 1, error.isHittable,
                  contains(window, error.frame), remaining() > 0 else { return false }
            // Only this app's exact error ScrollView; no window-wide swipe or deadline reset.
            error.swipeUp()
            gestures += 1
        }
        return false
    }
    private func destination(_ title: String, in app: XCUIApplication, requireNative: Bool = false) {
        guard let target = Destination(rawValue: title), let navigation = navigation(in: app, requireNative: requireNative) else {
            XCTFail("Unknown or unavailable destination \(title)"); return
        }
        navigationEvidence(navigation, name: "navigation-before-\(title)")
        let button = navigation.buttons[target]!
        XCTAssertTrue(button.isHittable, "The exact navigation destination must be immediately reachable.")
        button.tap()
        let marker: XCUIElement
        let timeout: TimeInterval
        switch target {
        case .store: marker = app.buttons["store.retry"]; timeout = 10
        case .library: marker = app.buttons["library.import"]; timeout = 5
        case .ex: marker = app.buttons["ex.import"]; timeout = 5
        case .settings: marker = app.staticTexts["Анимации интерфейса"]; timeout = 5
        }
        if target == .store {
            XCTAssertTrue(reachableStoreRecovery(in: app, retry: marker, timeout: timeout),
                          "The actual Store error must be visible and Retry reachable within the original navigation budget.")
        } else {
            XCTAssertTrue(marker.waitForExistence(timeout: timeout), "Navigation must display the actual \(title) content.")
        }
        XCTAssertTrue(marker.isHittable, "The actual destination content must be onscreen after navigation.")
        XCTAssertEqual(app.state, .runningForeground, "The navigation round trip must preserve the live app.")
        let evidence = XCTAttachment(string: "destination=\(title)\nmarker=\(marker.identifier)\nlabel=\(marker.label)\nexists=\(marker.exists)\nhittable=\(marker.isHittable)\nframe=\(marker.frame)")
        evidence.name = "navigation-endstate-\(title)"; evidence.lifetime = .keepAlways; add(evidence)
    }
    func testEmptyLibraryOffersActualImportAndStore() {
        let app = launch("empty")
        XCTAssertTrue(app.buttons["library.import"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.buttons["library.store"].exists)
        capture("01-library-empty")
    }
    func testSearchClearAndFavoriteFilter() {
        let app = launch()
        let search = app.textFields["library.search"]
        XCTAssertTrue(search.waitForExistence(timeout: 10)); search.tap(); search.typeText("Celestial")
        XCTAssertTrue(app.staticTexts["Celestial Lab"].waitForExistence(timeout: 5))
        XCTAssertFalse(app.staticTexts["Crystal Garden"].exists)
        capture("02-library-search")
        app.buttons["Очистить поиск"].tap()
        app.buttons["filter.favorites"].tap()
        XCTAssertTrue(app.staticTexts["Celestial Lab"].exists); XCTAssertFalse(app.staticTexts["Crystal Garden"].exists)
        capture("03-library-favorites")
    }
    func testTransferCancelReleasesBlockingPresentation() {
        let app = launch("transfer")
        XCTAssertTrue(app.buttons["transfer.cancel"].waitForExistence(timeout: 10))
        capture("04-transfer-active")
        app.buttons["transfer.cancel"].tap()
        XCTAssertTrue(app.staticTexts["Перенос отменён"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.buttons["transfer.dismiss"].exists)
        capture("05-transfer-cancelled")
        app.buttons["transfer.dismiss"].tap()
        XCTAssertTrue(app.buttons["library.import"].waitForExistence(timeout: 5))
    }
    func testOfflineStoreAndExHaveRecoveryActions() {
        let app = launch("empty")
        destination("Магазин", in: app)
        XCTAssertTrue(app.buttons["store.retry"].waitForExistence(timeout: 10)); capture("06-store-offline")
        destination("eX", in: app)
        XCTAssertTrue(app.buttons["ex.import"].waitForExistence(timeout: 5)); XCTAssertTrue(app.buttons["ex.store"].exists)
        capture("07-ex")
        destination("Настройки", in: app)
        XCTAssertTrue(app.staticTexts["Анимации интерфейса"].waitForExistence(timeout: 5))
        capture("12-adaptive-settings-navigation")
        destination("Библиотека", in: app)
        XCTAssertTrue(app.buttons["library.import"].waitForExistence(timeout: 5))
    }
    func testPlayerRealPageAndSafeClose() {
        let app = launch("player")
        for cycle in 0..<3 {
            XCTAssertTrue(app.buttons["featured.play"].waitForExistence(timeout: 10)); app.buttons["featured.play"].tap()
            XCTAssertTrue(app.buttons["player.close"].waitForExistence(timeout: 10))
            XCTAssertTrue(app.webViews.firstMatch.waitForExistence(timeout: 10))
            XCTAssertTrue(app.buttons["pad.Space"].waitForExistence(timeout: 5))
            app.buttons["pad.Space"].press(forDuration: 0.2)
            if cycle == 0 { capture("08-player") }
            app.buttons["player.menu"].tap()
            XCTAssertTrue(app.staticTexts["Управление"].waitForExistence(timeout: 5))
            if cycle == 0 { capture("09-player-controls") }
            app.buttons["Готово"].tap(); app.buttons["player.close"].tap()
            XCTAssertTrue(app.textFields["library.search"].waitForExistence(timeout: 5), "Closing and reopening the real player must preserve the live library process.")
        }
    }
    func testAccessibilityTextStillHasImportAndSettingsActions() {
        let app = launch("empty", largeText: true)
        assertAllNavigation(in: app, requireNative: true)
        XCTAssertTrue(app.buttons["library.import"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.buttons["library.import"].isHittable, "Large text must keep the primary import action immediately reachable.")
        app.scrollViews.firstMatch.swipeUp()
        XCTAssertTrue(app.buttons["library.import"].isHittable, "Scrolling the large-text instructions must preserve the primary import action.")
        capture("10-library-accessibility-text")
        destination("Настройки", in: app, requireNative: true)
        XCTAssertTrue(app.staticTexts["Анимации интерфейса"].waitForExistence(timeout: 5)); capture("11-settings-accessibility-text")
        destination("Магазин", in: app, requireNative: true)
        XCTAssertTrue(app.buttons["store.retry"].waitForExistence(timeout: 10))
        destination("eX", in: app, requireNative: true)
        XCTAssertTrue(app.buttons["ex.import"].waitForExistence(timeout: 5)); XCTAssertTrue(app.buttons["ex.store"].exists)
        capture("13-ex-accessibility-navigation")
        destination("Библиотека", in: app, requireNative: true)
        XCTAssertTrue(app.buttons["library.import"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.buttons["library.import"].isHittable, "The full-size import action must remain reachable after the four-destination round trip.")
        capture("14-library-accessibility-return")
    }
}
