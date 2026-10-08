import XCTest

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
    private func destination(_ title: String, in app: XCUIApplication) {
        let item = app.tabBars.buttons[title]
        if item.exists { item.tap() } else { app.buttons[title].firstMatch.tap() }
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
        XCTAssertTrue(app.buttons["library.import"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.buttons["library.import"].isHittable, "Large text must keep the primary import action immediately reachable.")
        app.scrollViews.firstMatch.swipeUp()
        XCTAssertTrue(app.buttons["library.import"].isHittable, "Scrolling the large-text instructions must preserve the primary import action.")
        capture("10-library-accessibility-text")
        destination("Настройки", in: app)
        XCTAssertTrue(app.staticTexts["Анимации интерфейса"].waitForExistence(timeout: 5)); capture("11-settings-accessibility-text")
    }
}
