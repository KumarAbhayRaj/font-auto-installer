# Mac version plan

Brief for building the macOS version of Font Auto Installer. The Windows version in `windows/` is the reference: same behaviour, same rules, native Mac code.

## Goal

A menu bar app that watches `~/Downloads` and installs new fonts the moment a download finishes. Same user experience as Windows: zero setup, tiny, offline, light on battery.

## Where the code goes

`mac/` in this repo. Keep it as small and flat as `windows/src`.

```
mac/
  build.sh                 builds FontAutoInstaller.app with swiftc (no Xcode project needed)
  Info.plist
  Sources/*.swift
```

Build with the free Xcode Command Line Tools only (`swiftc`). No Xcode IDE, no third-party packages. Ad-hoc sign the bundle: `codesign --force --deep -s - FontAutoInstaller.app`.

## Behaviour (must match Windows)

1. **Watch** `~/Downloads` top level with FSEvents. No polling. Ignore `.crdownload`, `.part`, `.download` (Safari bundles end in `.download`), `.tmp`.
2. **Wait until finished**: size + modified time unchanged for 3 s, then process. Startup catch-up: items created in the last 15 min.
3. **Find fonts** in: single `.ttf`/`.otf`, folders, `.zip`, `.rar`, `.7z`, nested up to 3 levels. Extract with `/usr/bin/bsdtar -xf` (handles zip, rar, 7z) into a temp work folder. Copy the archive to an ASCII name first. Skip archives over 500 MB. Never follow symlinks. Ignore `__MACOSX` and `._*`.
4. **Pick rules** (port `windows/src/FontInfo.cs` `FontPicker` exactly):
   - Read family (name ID 16, else 1), style (17, else 2), full name (4) from the font's `name` table. Variable = has `fvar` table, or `Variable` / `[` in file name.
   - Group per family (style words stripped when name ID 16 is missing), separately for upright and italic.
   - Variable present: install only variable files. Else per font prefer OTF over TTF.
   - Dedupe by normalized full name (lowercase letters and digits only): `Bloka-BlackItalic` == `Bloka Black Italic`.
5. **Install** by copying to `~/Library/Fonts`. Skip if a font with the same normalized full name is already available (check `CTFontManagerCopyAvailableFontFamilyNames` / font descriptors, plus files in `~/Library/Fonts`). Never overwrite.
6. **Clean up**: if the download held only fonts + harmless extras (txt, md, pdf, rtf, html, url, doc, docx, png, jpg, gif, webp, css, json, woff, woff2, eot, svg, ttc, files without extension), move it to the Trash with `FileManager.trashItem`. Otherwise leave it.
7. **Popup** (port `windows/src/FontToast.cs`):
   - `NSPanel` with `.nonactivatingPanel`, level `.floating`, `canBecomeKey = false`. It must NEVER take focus from the app the user is typing in.
   - Bottom-right of the main screen's visible frame. Rounded, 1 px border, follows light/dark mode. No drop shadow gimmicks.
   - Title ("Installed 4 fonts"), subtitle (download name + "moved to Trash"), one row per family with the name drawn in its own font (`CTFontManagerCreateFontDescriptorsFromURL`), detail line ("Variable, all weights + 9 italics").
   - Click a name to copy it. Close button. Fades after 5 s, hover pauses.
   - Skip the popup while a fullscreen app is frontmost.
8. **History**: `~/Library/Application Support/FontAutoInstaller/history.txt` (same tab-separated format as Windows). Menu bar item "Recently installed fonts" shows the last 50, click to copy.
9. **Menu bar**: "Aa" icon (template image). Items: Recently installed fonts, Pause watching, Open log, Open fonts folder, Start at login (`SMAppService.mainApp`, macOS 13+), Quit. `LSUIElement = true` (no Dock icon).
10. **Battery**: FSEvents only, background QoS for work, nothing running while idle.
11. **Privacy**: no networking code at all. macOS will ask once for Downloads access; that prompt is the only permission needed.

## Done when

- `mac/build.sh` produces `FontAutoInstaller.app`, opens and shows the menu bar icon.
- Real test: drop a font ZIP into Downloads, it installs, the popup shows without stealing focus, ZIP goes to Trash, font appears in Font Book.
- README gets a Mac section (install: right-click > Open the first time, since the app isn't notarized).
- Release asset: zipped `FontAutoInstaller-mac.zip` next to the Windows exe.
