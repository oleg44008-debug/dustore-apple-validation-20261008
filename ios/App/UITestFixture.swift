import Foundation

/// Owned, deterministic diagnostic fixtures. Never enabled in a release build or normal launch.
enum UITestFixture {
    static var enabled: Bool {
        #if DEBUG
        return ProcessInfo.processInfo.arguments.contains("-dustoreUITest")
        #else
        return false
        #endif
    }
    static var name: String {
        let args = ProcessInfo.processInfo.arguments
        guard let index = args.firstIndex(of: "-dustoreFixture"), index + 1 < args.count else { return "empty" }
        return args[index + 1]
    }
    static func prepareIfRequested() {
        guard enabled else { return }
        UserDefaults.standard.set(MotionPreference.off.rawValue, forKey: Preferences.motion)
        UserDefaults.standard.set(false, forKey: Preferences.haptics)
        let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        var games: [Game] = []
        if name == "library" || name == "player" {
            for (index, title) in ["Celestial Lab", "Лунная тропа", "Crystal Garden"].enumerated() {
                let game = Game(id: UUID(), title: title, kind: .web, engine: "Веб-игра", note: nil,
                                added: Date(timeIntervalSince1970: 1_790_000_000 + Double(index)), favorite: index == 0, lastPlayed: nil)
                try? FileManager.default.createDirectory(at: game.webRoot, withIntermediateDirectories: true)
                try? page.write(to: game.webRoot.appendingPathComponent("index.html"), atomically: true, encoding: .utf8)
                try? "/* owned UI fixture */".write(to: game.webRoot.appendingPathComponent("game.js"), atomically: true, encoding: .utf8)
                games.append(game)
            }
        }
        if let data = try? JSONEncoder().encode(games) { try? data.write(to: docs.appendingPathComponent("library.json"), options: .atomic) }
    }
    static let page = #"""
    <!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover"><style>html,body{margin:0;height:100%;background:#12111b;color:#f5f4fa;font:18px -apple-system}canvas{width:100%;height:100%;display:block}#keys{position:absolute;top:24px;left:24px;background:#242430;padding:12px;border-radius:12px}</style></head><body><canvas tabindex="0"></canvas><div id="keys">Готова к управлению</div><script>
    const canvas=document.querySelector('canvas'),g=canvas.getContext('2d');canvas.width=960;canvas.height=600;g.fillStyle='#171523';g.fillRect(0,0,960,600);for(let i=0;i<45;i++){g.fillStyle=i%3?'#beb0ff':'#ecf39a';g.beginPath();g.arc((i*157)%960,(i*83)%600,2+i%4,0,7);g.fill()}g.fillStyle='#f5f4fa';g.font='bold 48px sans-serif';g.fillText('CELESTIAL LAB',60,260);g.font='24px sans-serif';g.fillStyle='#beb0ff';g.fillText('Owned diagnostic web fixture',60,310);const keys=new Set();document.addEventListener('keydown',e=>{keys.add(e.code);document.querySelector('#keys').textContent='Ввод: '+[...keys].join(', ')});document.addEventListener('keyup',e=>{keys.delete(e.code);document.querySelector('#keys').textContent=keys.size?'Ввод: '+[...keys].join(', '):'Все клавиши отпущены'});
    </script></body></html>
    """#
}
