using System;
using System.Collections.Generic;
using Godot;

/// <summary>
///   On-screen touch controls for the microbe stage. A finger put down on the left side of the screen (outside the
///   HUD) becomes a floating movement stick, a finger elsewhere on the world makes the cell face that point, two
///   fingers pinch to zoom, and round buttons on the right side trigger the main abilities. The rest of the HUD keeps
///   working through Godot's touch to mouse emulation (which only covers the first finger).
/// </summary>
/// <remarks>
///   <para>
///     This only collects the touch state, <see cref="PlayerMicrobeInput"/> reads it each frame and applies it to
///     the player cell.
///   </para>
///   <para>
///     The first finger (index 0) is special: Godot turns it into mouse events for the GUI. So a new first finger
///     is only taken for the world once its emulated mouse press reaches unhandled input, which means no HUD
///     element used it. Other fingers never reach the GUI buttons so they are taken directly.
///   </para>
/// </remarks>
public partial class MicrobeTouchControls : CanvasLayer
{
    /// <summary>
    ///   Fraction of the screen width (from the left edge) where a new touch starts the movement stick
    /// </summary>
    private const float STICK_ZONE_WIDTH_FRACTION = 0.45f;

    /// <summary>
    ///   Stick radius as a fraction of the screen height so it is the same physical size on different phones
    /// </summary>
    private const float STICK_RADIUS_HEIGHT_FRACTION = 0.13f;

    private const float BUTTON_RADIUS_HEIGHT_FRACTION = 0.07f;

    private const float STICK_DEADZONE = 0.12f;

    /// <summary>
    ///   How much camera height changes per pixel of pinch distance change
    /// </summary>
    private const float PINCH_ZOOM_PER_PIXEL = 0.08f;

    private const int BUTTON_FONT_SIZE = 22;

    private readonly Dictionary<int, Vector2> worldFingers = new();

    private readonly TouchButton[] buttons =
    {
        new(TouchAction.Toxin, "Toxin", new Vector2(-1.5f, 0.32f)),
        new(TouchAction.Engulf, "Engulf", new Vector2(-1.5f, 0.50f)),
        new(TouchAction.Slime, "Slime", new Vector2(-4.0f, 0.41f)),
        new(TouchAction.Sprint, "Sprint", new Vector2(-4.0f, 0.59f)),
    };

    private readonly Color stickBaseColour = new(1, 1, 1, 0.18f);
    private readonly Color stickRingColour = new(1, 1, 1, 0.45f);
    private readonly Color stickKnobColour = new(1, 1, 1, 0.6f);
    private readonly Color idleHintColour = new(1, 1, 1, 0.1f);
    private readonly Color buttonColour = new(0.1f, 0.15f, 0.2f, 0.45f);
    private readonly Color buttonPressedColour = new(0.3f, 0.7f, 0.9f, 0.6f);
    private readonly Color buttonTextColour = new(1, 1, 1, 0.9f);

#pragma warning disable CA2213 // this is our child node and freed with us
    private Control drawArea = null!;
#pragma warning restore CA2213

    private int stickFinger = -1;
    private Vector2 stickOrigin;
    private Vector2 stickCurrent;

    private int aimFinger = -1;

    private float lastPinchDistance;
    private bool pinching;

    private float pendingZoom;

    /// <summary>
    ///   Fired when an ability button is pressed (true) or released (false)
    /// </summary>
    public event Action<TouchAction, bool>? ActionChanged;

    /// <summary>
    ///   Abilities that have their own touch button
    /// </summary>
    public enum TouchAction
    {
        Engulf,
        Toxin,
        Slime,
        Sprint,
    }

    /// <summary>
    ///   Current stick deflection, x is right and y is down on the screen. Length is between 0 and 1.
    /// </summary>
    public Vector2 StickDirection
    {
        get
        {
            if (stickFinger < 0)
                return Vector2.Zero;

            var offset = (stickCurrent - stickOrigin) / StickRadius;
            var length = offset.Length();

            if (length < STICK_DEADZONE)
                return Vector2.Zero;

            // Rescale so movement starts smoothly from the edge of the deadzone
            var scaled = Math.Min((length - STICK_DEADZONE) / (1 - STICK_DEADZONE), 1);
            return offset / length * scaled;
        }
    }

    /// <summary>
    ///   True while the player is holding a finger on the world to aim (and not pinching)
    /// </summary>
    public bool IsAiming => aimFinger >= 0 && !pinching;

    /// <summary>
    ///   The screen position the player is aiming at, only valid when <see cref="IsAiming"/> is true
    /// </summary>
    public Vector2 AimScreenPosition => worldFingers.TryGetValue(aimFinger, out var position) ? position : default;

    private Vector2 ScreenSize => GetViewport().GetVisibleRect().Size;

    private float StickRadius => ScreenSize.Y * STICK_RADIUS_HEIGHT_FRACTION;

    private float ButtonRadius => ScreenSize.Y * BUTTON_RADIUS_HEIGHT_FRACTION;

    public override void _Ready()
    {
        // Draw under the HUD so the controls never hide HUD information
        Layer = -1;

        drawArea = new Control
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        drawArea.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        drawArea.Draw += OnDrawArea;
        AddChild(drawArea);

        // Positions are relative to the screen size so redraw when it changes (rotation, window resize)
        GetViewport().SizeChanged += drawArea.QueueRedraw;
    }

    public override void _ExitTree()
    {
        base._ExitTree();

        GetViewport().SizeChanged -= drawArea.QueueRedraw;
    }

    /// <summary>
    ///   Returns the pinch zoom amount (camera height change) accumulated since the last call
    /// </summary>
    /// <returns>Height change, positive zooms out</returns>
    public float TakeZoomChange()
    {
        var result = pendingZoom;
        pendingZoom = 0;
        return result;
    }

    /// <summary>
    ///   Forgets all fingers, for example when the stage is paused or the editor is entered
    /// </summary>
    public void ResetTouches()
    {
        stickFinger = -1;
        aimFinger = -1;
        pinching = false;
        pendingZoom = 0;
        worldFingers.Clear();

        foreach (var button in buttons)
        {
            if (button.Finger >= 0)
            {
                button.Finger = -1;
                ActionChanged?.Invoke(button.Action, false);
            }
        }

        drawArea.QueueRedraw();
    }

    public override void _Input(InputEvent @event)
    {
        // Ability buttons and fingers we already own are handled here before the GUI, so that sliding a finger over
        // a HUD element doesn't lose track of it
        if (@event is InputEventScreenTouch touch)
        {
            if (touch.Pressed)
            {
                var button = FindButton(touch.Position);

                if (button != null && button.Finger < 0)
                {
                    button.Finger = touch.Index;
                    ActionChanged?.Invoke(button.Action, true);
                    drawArea.QueueRedraw();
                    GetViewport().SetInputAsHandled();
                }
            }
            else
            {
                HandleRelease(touch.Index);
            }
        }
        else if (@event is InputEventScreenDrag drag)
        {
            if (drag.Index == stickFinger)
            {
                stickCurrent = ClampToStick(drag.Position);
                drawArea.QueueRedraw();
                GetViewport().SetInputAsHandled();
            }
            else if (worldFingers.ContainsKey(drag.Index))
            {
                worldFingers[drag.Index] = drag.Position;
                UpdatePinch();
                GetViewport().SetInputAsHandled();
            }
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // New world fingers are only taken here, after the GUI had a chance to use the touch for a HUD button
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mouse)
        {
            // This is the emulated mouse press of the first finger (or a real click when testing on desktop)
            if (FindButton(mouse.Position) == null && !IsFingerUsed(0))
                StartWorldFinger(0, mouse.Position);

            return;
        }

        if (@event is InputEventScreenTouch { Pressed: true, Index: > 0 } touch && !IsFingerUsed(touch.Index))
        {
            StartWorldFinger(touch.Index, touch.Position);
            GetViewport().SetInputAsHandled();
        }
    }

    private void StartWorldFinger(int index, Vector2 position)
    {
        if (stickFinger < 0 && position.X < ScreenSize.X * STICK_ZONE_WIDTH_FRACTION)
        {
            stickFinger = index;
            stickOrigin = position;
            stickCurrent = position;
            drawArea.QueueRedraw();
            return;
        }

        worldFingers[index] = position;

        if (worldFingers.Count >= 2)
        {
            pinching = true;
            lastPinchDistance = CurrentPinchDistance();
        }
        else
        {
            aimFinger = index;
        }
    }

    private void HandleRelease(int index)
    {
        foreach (var button in buttons)
        {
            if (button.Finger == index)
            {
                button.Finger = -1;
                ActionChanged?.Invoke(button.Action, false);
                drawArea.QueueRedraw();
            }
        }

        if (index == stickFinger)
        {
            stickFinger = -1;
            drawArea.QueueRedraw();
            return;
        }

        if (!worldFingers.Remove(index))
            return;

        if (index == aimFinger)
            aimFinger = -1;

        if (worldFingers.Count < 2)
            pinching = false;

        // If one finger is left after a pinch, let it aim again
        if (aimFinger < 0 && worldFingers.Count == 1)
        {
            foreach (var finger in worldFingers.Keys)
                aimFinger = finger;
        }
    }

    private bool IsFingerUsed(int index)
    {
        if (index == stickFinger || worldFingers.ContainsKey(index))
            return true;

        foreach (var button in buttons)
        {
            if (button.Finger == index)
                return true;
        }

        return false;
    }

    private TouchButton? FindButton(Vector2 position)
    {
        var radius = ButtonRadius;

        foreach (var button in buttons)
        {
            if (ButtonCentre(button).DistanceTo(position) <= radius)
                return button;
        }

        return null;
    }

    /// <summary>
    ///   Button layout is relative to the right screen edge: x is in button radii from the edge and y is a fraction
    ///   of screen height
    /// </summary>
    private Vector2 ButtonCentre(TouchButton button)
    {
        var size = ScreenSize;
        return new Vector2(size.X + button.Layout.X * ButtonRadius, size.Y * button.Layout.Y);
    }

    private Vector2 ClampToStick(Vector2 position)
    {
        var offset = position - stickOrigin;
        var radius = StickRadius;

        if (offset.Length() > radius)
            offset = offset.Normalized() * radius;

        return stickOrigin + offset;
    }

    private void UpdatePinch()
    {
        if (!pinching || worldFingers.Count < 2)
            return;

        var distance = CurrentPinchDistance();

        // Fingers moving apart zoom in (lower camera)
        pendingZoom += (lastPinchDistance - distance) * PINCH_ZOOM_PER_PIXEL;
        lastPinchDistance = distance;
    }

    private float CurrentPinchDistance()
    {
        Vector2? first = null;

        foreach (var position in worldFingers.Values)
        {
            if (first == null)
            {
                first = position;
                continue;
            }

            return first.Value.DistanceTo(position);
        }

        return 0;
    }

    private void OnDrawArea()
    {
        var radius = StickRadius;

        if (stickFinger < 0)
        {
            // Faint hint showing where the stick lives
            var hintCentre = new Vector2(ScreenSize.X * 0.16f, ScreenSize.Y * 0.62f);
            drawArea.DrawArc(hintCentre, radius, 0, MathF.Tau, 48, idleHintColour, 3, true);
        }
        else
        {
            drawArea.DrawCircle(stickOrigin, radius, stickBaseColour);
            drawArea.DrawArc(stickOrigin, radius, 0, MathF.Tau, 48, stickRingColour, 3, true);
            drawArea.DrawCircle(stickCurrent, radius * 0.4f, stickKnobColour);
        }

        var buttonRadius = ButtonRadius;
        var font = ThemeDB.FallbackFont;

        foreach (var button in buttons)
        {
            var centre = ButtonCentre(button);
            drawArea.DrawCircle(centre, buttonRadius, button.Finger >= 0 ? buttonPressedColour : buttonColour);
            drawArea.DrawArc(centre, buttonRadius, 0, MathF.Tau, 40, stickRingColour, 2, true);

            var textWidth = buttonRadius * 2;
            drawArea.DrawString(font, centre + new Vector2(-buttonRadius, BUTTON_FONT_SIZE * 0.35f), button.Label,
                HorizontalAlignment.Center, textWidth, BUTTON_FONT_SIZE, buttonTextColour);
        }
    }

    private sealed class TouchButton
    {
        public readonly TouchAction Action;
        public readonly string Label;
        public readonly Vector2 Layout;

        public int Finger = -1;

        public TouchButton(TouchAction action, string label, Vector2 layout)
        {
            Action = action;
            Label = label;
            Layout = layout;
        }
    }
}
