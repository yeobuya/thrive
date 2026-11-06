using System;
using System.Linq;
using Godot;

/// <summary>
///   Decides whether the on-screen touch controls should be used
/// </summary>
/// <remarks>
///   <para>
///     Touch controls are always on for mobile (iOS and Android) builds. On desktop they can be tested by starting
///     the game with the <c>--touch-controls</c> user argument (for example <c>godot -- --touch-controls</c>),
///     which also turns on touch emulation from the mouse so that clicking and dragging acts like one finger.
///   </para>
/// </remarks>
public static class TouchControlsHelper
{
    public const string TOUCH_CONTROLS_ARGUMENT = "--touch-controls";

    private static readonly Lazy<bool> UseTouchControlsValue = new(Detect);

    /// <summary>
    ///   True when touch controls should be shown and handled
    /// </summary>
    public static bool UseTouchControls => UseTouchControlsValue.Value;

    private static bool Detect()
    {
        if (OS.GetCmdlineUserArgs().Contains(TOUCH_CONTROLS_ARGUMENT))
        {
            GD.Print("Touch controls forced on by command line, emulating touch from the mouse");
            Input.EmulateTouchFromMouse = true;
            return true;
        }

        return FeatureInformation.IsMobile;
    }
}
