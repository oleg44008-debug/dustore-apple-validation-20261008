#!/usr/bin/env swift
// A deterministic native asset wrapper for the supplied logo. The original PNG is never changed.
import Foundation
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers

enum IconError: Error { case missingLogo, invalidLogoSize, context, image, destination, write, alpha }

let sourceRoot = CommandLine.arguments.count > 1
    ? URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
    : URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
let logoURL = sourceRoot.appendingPathComponent("App/Assets.xcassets/BrandMark.imageset/dustore-logo-original.png")
let iconFolder = sourceRoot.appendingPathComponent("App/Assets.xcassets/AppIcon.appiconset", isDirectory: true)
let iconURL = iconFolder.appendingPathComponent("dustore-app-icon.png")

guard let source = CGImageSourceCreateWithURL(logoURL as CFURL, nil),
      let logo = CGImageSourceCreateImageAtIndex(source, 0, nil) else { throw IconError.missingLogo }
// Preserve the actual supplied pixel-art logo at its native resolution, without resampling.
guard logo.width == 856, logo.height == 636 else { throw IconError.invalidLogoSize }
let size = 1024
let space = CGColorSpace(name: CGColorSpace.sRGB)!
guard let context = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8,
                              bytesPerRow: 0, space: space,
                              bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { throw IconError.context }
context.setFillColor(CGColor(colorSpace: space, components: [23.0 / 255, 23.0 / 255, 31.0 / 255, 1])!)
context.fill(CGRect(x: 0, y: 0, width: CGFloat(size), height: CGFloat(size)))
context.interpolationQuality = .none
context.draw(logo, in: CGRect(x: CGFloat((size - logo.width) / 2), y: CGFloat((size - logo.height) / 2),
                             width: CGFloat(logo.width), height: CGFloat(logo.height)))
guard let image = context.makeImage() else { throw IconError.image }
try FileManager.default.createDirectory(at: iconFolder, withIntermediateDirectories: true)
guard let output = CGImageDestinationCreateWithURL(iconURL as CFURL, UTType.png.identifier as CFString, 1, nil) else { throw IconError.destination }
CGImageDestinationAddImage(output, image, nil)
guard CGImageDestinationFinalize(output) else { throw IconError.write }
guard let check = CGImageSourceCreateWithURL(iconURL as CFURL, nil),
      let properties = CGImageSourceCopyPropertiesAtIndex(check, 0, nil) as? [CFString: Any],
      properties[kCGImagePropertyHasAlpha] as? Bool != true else { throw IconError.alpha }
print("Generated opaque sRGB 1024×1024 AppIcon from unchanged 856×636 BrandMark:", iconURL.path)
