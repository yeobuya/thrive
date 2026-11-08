iPhone port (work in progress)
==============================

This fork is working toward an iPhone-only TestFlight build of Thrive, starting with the microbe stage.
This page says what is in place, how to try it, and what is still unknown.

What is in place
----------------

- **Touch controls for the microbe stage** (`src/microbe_stage/MicrobeTouchControls.cs`, wired up in
  `PlayerMicrobeInput.cs`):
  - Put a finger down anywhere on the left 45% of the screen (outside the HUD) and drag: a floating stick
    appears and the cell swims that way, turning to face where it goes.
  - Hold a finger on the world on the right side: the cell turns to face that point (for aiming toxins or
    lining up an engulf). Dragging the finger keeps aiming.
  - Two fingers on the world: pinch to zoom.
  - Round buttons on the right edge: **Toxin** (tap), **Engulf** (tap to toggle), **Slime** (hold) and
    **Sprint** (hold). These take any finger, so they work while the other thumb steers.
  - Every other HUD element (editor button, pause, panels, the ability bar) works as before because Godot turns
    the first finger into mouse clicks. Those only respond to the first finger down.
- **Desktop testing of touch controls:** start the game with the `--touch-controls` user argument. The mouse
  then acts as one finger, so you can try the stick, aiming and buttons (but not pinch) on a Mac.
- **iOS export preset** "iOS" in `export_presets.cfg`: iPhone only, team M5W572B65D, bundle id
  `com.yeobuya.thrive`, exports an Xcode project.
- **Landscape orientation** on phones (`display/window/handheld/orientation`).
- **iOS builds use .NET 10** like desktop (the Godot default of .NET 8 for iOS doesn't build: the shared
  projects and packages need .NET 10). Reflection-heavy assemblies (game data loading, saves) are kept whole
  instead of trimmed (see `Thrive.csproj`).
- **Mods are off on mobile**: iOS doesn't allow loading code at runtime.
- **Native libraries as static libraries on iOS**: CMake builds `thrive_native` and `thrive_extension` as
  static libraries when targeting iOS. `ios/build-ios.sh` merges them (with Jolt and godot-cpp) into
  `lib/ios/thrive_ios.xcframework`, which the GDExtension file points to. On iOS the C# side finds the native
  functions in the app executable itself (`NativeInterop.cs`).

How to try it
-------------

### Touch controls on the Mac (quick check, no iPhone needed)

1. Set up the normal Thrive development environment on the Mac (see `setup_instructions.md`), with Godot 4.7.2
   .NET and the native libraries.
2. Run from a terminal: `/Applications/Godot_mono.app/Contents/MacOS/Godot --path . -- --touch-controls`
   (or in the editor: Project Settings > Editor > Run > Main Run Args: `-- --touch-controls`).
3. Start a new microbe game. Click and drag on the left side to swim, hold the mouse on the right side to aim,
   click the round buttons.

### iPhone build (MacBook Pro)

Needs Xcode signed in to the Apple account, Godot 4.7.2 .NET with export templates
(Editor > Manage Export Templates), the .NET 10 SDK, CMake and git-lfs (`brew install cmake git-lfs`).

1. `bash ios/build-ios.sh --no-upload` builds the native libraries for iPhone, exports the Xcode project from
   Godot and archives it. The first run imports every asset and takes a long time.
2. In App Store Connect, create the app with bundle id `com.yeobuya.thrive` if it isn't there yet.
3. `bash ios/build-ios.sh --upload-only` uploads the archive to TestFlight. Later runs can do everything in one
   go with `bash ios/build-ios.sh`.

Logs go to `builds/ios/` (`native.log`, `export.log`, `archive.log`, `upload.log`).

Unknowns and likely first problems
----------------------------------

None of the iPhone build steps have been run yet; they were written without a Mac. In the order they will
probably come up:

1. **Native library cross-compile.** Jolt, godot-cpp and Thrive's C++ have only been built for desktop. Expect
   compile flags to need small fixes for iOS. `native.log` has the details.
2. **Godot C# iOS export.** If Godot asks for an iOS workload: `sudo dotnet workload install ios`.
3. **Missing native symbols at runtime.** If the game starts but crashes when the microbe stage loads (physics
   or the native library), the linker probably dropped functions that only C# calls. The script already uses
   `-force_load` and turns off dead code stripping for this.
4. **Ahead-of-time compile problems.** The build lists about 30 AOT warnings, mostly in JSON loading and save
   converters (`src/saving/serializers`). If loading game data or saves fails on the phone, start there.
5. **Performance and memory.** The desktop renderer settings, compound clouds and auto-evo were never tuned for
   phones. Godot uses its Mobile renderer on iOS automatically.
6. **UI size.** The HUD and editor are laid out for a 1280x720 desktop screen and will be small on a phone.
   The cell editor has no touch-specific controls yet.

Next steps
----------

1. Get a first build onto an iPhone and fix what breaks in the list above.
2. Playtest the touch controls and tune stick size, button positions and pinch speed (constants at the top of
   `MicrobeTouchControls.cs`).
3. Make the cell editor usable by touch (bigger hex grid, tap to place, drag to rotate).
4. Bigger UI on phones (a larger theme scale or a phone layout for the HUD).
