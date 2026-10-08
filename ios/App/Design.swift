import SwiftUI
import UIKit

/// Shared Prime roles: obsidian surfaces, lavender selection and pale citrus actions.
enum Theme {
    static let background = Color(hex: 0x111116)
    static let card = Color(hex: 0x1C1C25)
    static let raised = Color(hex: 0x242430)
    static let inset = Color(hex: 0x15151D)
    static let line = Color(hex: 0x353541)
    static let selection = Color(hex: 0xBEB0FF)
    static let action = Color(hex: 0xECF39A)
    static let ink = Color(hex: 0x1A1C16)
    static let text = Color(hex: 0xF5F4FA)
    static let muted = Color(hex: 0xC8C5D3)
    static let dim = Color(hex: 0xA29EAF)
    static let good = Color(hex: 0x8DDBB3)
    static let warn = Color(hex: 0xEDC68C)
    // Names kept for the existing web view and other native components.
    static let pink = selection
    static let yellow = action
}

private extension Color {
    init(hex: UInt32) {
        self.init(red: Double((hex >> 16) & 255) / 255,
                  green: Double((hex >> 8) & 255) / 255,
                  blue: Double(hex & 255) / 255)
    }
}

enum Preferences {
    static let motion = "dustore.prime.motion"
    static let haptics = "dustore.prime.haptics"
    static let compactCards = "dustore.prime.compact-cards"
}

enum MotionPreference: String, CaseIterable, Identifiable {
    case full, reduced, off
    var id: String { rawValue }
    var title: String {
        switch self { case .full: return "Полные"; case .reduced: return "Короткие"; case .off: return "Выключены" }
    }
    func animation(reduceMotion: Bool) -> Animation? {
        if self == .off { return nil }
        if reduceMotion || self == .reduced { return .easeOut(duration: 0.14) }
        return .spring(response: 0.42, dampingFraction: 0.86, blendDuration: 0)
    }
    func moves(reduceMotion: Bool) -> Bool { self == .full && !reduceMotion }
}

struct PrimeBackdrop: View {
    var body: some View {
        ZStack(alignment: .topTrailing) {
            Theme.background
            RadialGradient(colors: [Theme.selection.opacity(0.1), .clear], center: .topTrailing,
                           startRadius: 10, endRadius: 480)
        }.ignoresSafeArea().accessibilityHidden(true)
    }
}

struct DisplayTitle: View {
    let text: String
    var size: CGFloat = 30
    @ScaledMetric(relativeTo: .title) private var factor: CGFloat = 1
    var body: some View {
        Text(text)
            .font(.system(size: size * factor, weight: .bold, design: .rounded))
            .foregroundColor(Theme.text)
            .fixedSize(horizontal: false, vertical: true)
    }
}

struct Eyebrow: View {
    let text: String
    var body: some View {
        Text(text.uppercased()).font(.caption2.weight(.bold)).tracking(1.8)
            .foregroundColor(Theme.selection).fixedSize(horizontal: false, vertical: true)
    }
}

struct Chip: View {
    let text: String
    var color: Color = Theme.selection
    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Image(systemName: "circle.fill").font(.system(size: 5)).accessibilityHidden(true)
            Text(text).font(.caption.weight(.semibold)).fixedSize(horizontal: false, vertical: true)
        }
        .foregroundColor(color).padding(.horizontal, 10).padding(.vertical, 6)
        .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(color.opacity(0.09)))
        .overlay(RoundedRectangle(cornerRadius: 9, style: .continuous).stroke(color.opacity(0.18), lineWidth: 1))
        .accessibilityElement(children: .combine)
    }
}

struct Card<Content: View>: View {
    let content: Content
    @Environment(\.accessibilityContrast) private var contrast
    init(@ViewBuilder content: () -> Content) { self.content = content() }
    var body: some View {
        VStack(alignment: .leading, spacing: 14) { content }
            .padding(20).frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 22, style: .continuous)
                .fill(LinearGradient(colors: [Theme.raised, Theme.card], startPoint: .topLeading, endPoint: .bottomTrailing)))
            .overlay(RoundedRectangle(cornerRadius: 22, style: .continuous).stroke(.white.opacity(contrast == .increased ? 0.3 : 0.075), lineWidth: 1))
    }
}

enum ButtonTone: Equatable { case primary, secondary, quiet }

struct PrimePressStyle: ButtonStyle {
    var tone: ButtonTone = .secondary
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.isEnabled) private var enabled
    @AppStorage(Preferences.motion) private var motion = MotionPreference.full.rawValue
    @AppStorage(Preferences.haptics) private var haptics = true

    func makeBody(configuration: Configuration) -> some View {
        let policy = MotionPreference(rawValue: motion) ?? .full
        let pressed = configuration.isPressed && enabled
        let fill = tone == .primary ? Theme.action : tone == .secondary ? Theme.raised : Color.clear
        return configuration.label
            .font(.body.weight(.semibold))
            .foregroundColor(enabled ? (tone == .primary ? Theme.ink : Theme.text) : Theme.dim)
            .padding(.horizontal, tone == .quiet ? 10 : 18)
            .padding(.vertical, tone == .quiet ? 10 : 14)
            .frame(minHeight: 44)
            .background(RoundedRectangle(cornerRadius: 15, style: .continuous).fill(fill))
            .overlay(RoundedRectangle(cornerRadius: 15, style: .continuous)
                .stroke(tone == .secondary ? Theme.line : Color.clear, lineWidth: 1))
            .contentShape(RoundedRectangle(cornerRadius: 15, style: .continuous))
            .scaleEffect(pressed && policy.moves(reduceMotion: reduceMotion) ? 0.974 : 1)
            .opacity(enabled ? (pressed ? 0.84 : 1) : 0.5)
            .animation(policy.animation(reduceMotion: reduceMotion), value: pressed)
            .onChange(of: configuration.isPressed) { down in
                if down && enabled && haptics { UIImpactFeedbackGenerator(style: .soft).impactOccurred(intensity: 0.6) }
            }
    }
}

struct YellowButton: View {
    let title: String
    var symbol: String = "arrow.right"
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            HStack(spacing: 9) {
                Image(systemName: symbol).accessibilityHidden(true)
                Text(title).fixedSize(horizontal: false, vertical: true)
            }.frame(maxWidth: .infinity)
        }.buttonStyle(PrimePressStyle(tone: .primary))
    }
}

struct EditionMark: View {
    var body: some View { Chip(text: Edition.name, color: Edition.isPrime ? Theme.selection : Theme.dim) }
}

struct BrandHeader: View {
    let title: String
    let subtitle: String
    @Environment(\.dynamicTypeSize) private var dynamicType
    var body: some View {
        VStack(alignment: .leading, spacing: 15) {
            if dynamicType.isAccessibilitySize {
                VStack(alignment: .leading, spacing: 10) {
                    brand
                    EditionMark()
                }
            } else {
                HStack(spacing: 10) {
                    brand
                    Spacer(minLength: 8)
                    EditionMark()
                }
            }
            VStack(alignment: .leading, spacing: 6) {
                DisplayTitle(text: title, size: dynamicType.isAccessibilitySize ? 28 : 34)
                Text(subtitle).font(.subheadline).foregroundColor(Theme.dim).fixedSize(horizontal: false, vertical: true)
            }
        }.accessibilityElement(children: .contain)
    }

    private var brand: some View {
        HStack(spacing: 10) {
            Image("BrandMark").resizable().scaledToFit().frame(width: 33, height: 33).accessibilityHidden(true)
            Text("DUSTORE").font(.system(size: 17, weight: .heavy)).tracking(1.1).foregroundColor(Theme.text)
        }
    }
}

private struct PrimeArrival: ViewModifier {
    let delay: Double
    @State private var appeared = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @AppStorage(Preferences.motion) private var motion = MotionPreference.full.rawValue
    func body(content: Content) -> some View {
        let policy = MotionPreference(rawValue: motion) ?? .full
        return content
            .opacity(appeared ? 1 : 0)
            .offset(y: appeared || !policy.moves(reduceMotion: reduceMotion) ? 0 : 14)
            .onAppear {
                if !appeared {
                    withAnimation(policy.animation(reduceMotion: reduceMotion).map { $0.delay(delay) }) { appeared = true }
                }
            }
    }
}

extension View {
    func primeArrival(delay: Double = 0) -> some View { modifier(PrimeArrival(delay: delay)) }
}

struct EmptyLibraryArtwork: View {
    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 18).fill(Theme.inset).overlay(RoundedRectangle(cornerRadius: 18).stroke(Theme.line))
                .frame(width: 104, height: 138).rotationEffect(.degrees(-11)).offset(x: -44, y: 9)
            RoundedRectangle(cornerRadius: 18).fill(Theme.inset).overlay(RoundedRectangle(cornerRadius: 18).stroke(Theme.line))
                .frame(width: 104, height: 138).rotationEffect(.degrees(11)).offset(x: 44, y: 5)
            VStack(spacing: 18) {
                Image("BrandMark").resizable().scaledToFit().frame(width: 60, height: 60)
                HStack(spacing: 6) {
                    Capsule().fill(Theme.selection.opacity(0.55)).frame(width: 35, height: 3)
                    Capsule().fill(Theme.line).frame(width: 16, height: 3)
                }
            }
            .frame(width: 114, height: 148)
            .background(RoundedRectangle(cornerRadius: 20, style: .continuous).fill(Theme.raised))
            .overlay(RoundedRectangle(cornerRadius: 20, style: .continuous).stroke(Theme.selection.opacity(0.28)))
            .shadow(color: .black.opacity(0.22), radius: 16, y: 10)
        }
        .frame(maxWidth: .infinity).frame(height: 180)
        .accessibilityHidden(true)
    }
}
