using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

namespace LocalInventoryExport;

internal sealed class ScrollBar
{
    internal const float Width = 18f;
    private const float MinimumThumb = 0.06f;
    private const float Epsilon = 0.001f;

    private readonly ScrollRect _scroll;
    private readonly Slot _thumbSlot;
    private readonly RectTransform _thumb;
    private readonly Button _button;
    private TouchSource? _dragSource;
    private Slot? _shield;
    private float _size = -1f;
    private float _top = -1f;

    private ScrollBar(ScrollRect scroll, Slot thumbSlot, Button button)
    {
        _scroll = scroll;
        _thumbSlot = thumbSlot;
        _thumb = thumbSlot.GetComponent<RectTransform>();
        _button = button;
    }

    internal bool IsValid => !_scroll.IsDestroyed && !_thumbSlot.IsDestroyed;

    internal static ScrollBar Create(UIBuilder ui, ScrollRect scroll)
    {
        ui.PushStyle();
        ui.Style.MinWidth = Width;
        ui.Style.PreferredWidth = Width;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = 1f;
        Slot track = ui.Next("Scroll Bar");
        if (track.GetComponent<LayoutElement>() is LayoutElement element)
        {
            element.UseZeroMetrics.Value = true;
            element.FlexibleWidth.Value = 0f;
        }
        ScrollBar bar = Finish(track, scroll);
        ui.PopStyle();
        return bar;
    }

    internal static ScrollBar? Attach(ScrollRect scroll)
    {
        RectTransform? viewport = scroll.RectTransform?.RectParent;
        Slot? panel = viewport?.Slot.Parent;
        if (viewport is null || panel is null)
            return null;

        viewport.OffsetMax.Value = new float2(-Width - 4f, viewport.OffsetMax.Value.y);
        Slot track = panel.AddSlot("Scroll Bar");
        RectTransform rect = track.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(1f, 0f);
        rect.AnchorMax.Value = new float2(1f, 1f);
        rect.OffsetMin.Value = new float2(-Width - 2f, 2f);
        rect.OffsetMax.Value = new float2(-2f, -2f);
        return Finish(track, scroll);
    }

    private static ScrollBar Finish(Slot track, ScrollRect scroll)
    {
        Image background = track.AttachComponent<Image>();
        background.Tint.Value = new colorX(1f, 1f, 1f, 0.07f);
        Button button = track.AttachComponent<Button>();
        button.PassThroughVerticalMovement.Value = false;
        button.PassThroughHorizontalMovement.Value = false;
        InteractionElement.ColorDriver hover = button.ColorDrivers.Count > 0 ? button.ColorDrivers[0] : button.ColorDrivers.Add();
        if (!hover.ColorDrive.IsLinkValid)
            hover.ColorDrive.Target = background.Tint;

        hover.TintColorMode.Value = InteractionElement.ColorMode.Direct;
        hover.NormalColor.Value = new colorX(1f, 1f, 1f, 0.07f);
        hover.HighlightColor.Value = new colorX(1f, 1f, 1f, 0.14f);
        hover.PressColor.Value = new colorX(1f, 1f, 1f, 0.2f);
        hover.DisabledColor.Value = colorX.Clear;

        Slot thumb = track.AddSlot("Thumb");
        RectTransform rect = thumb.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0.2f, 0f);
        rect.AnchorMax.Value = new float2(0.8f, 1f);
        thumb.AttachComponent<Image>().Tint.Value = RadiantUI_Constants.Hero.CYAN.SetA(0.55f);

        var bar = new ScrollBar(scroll, thumb, button);
        button.LocalPressed += (_, data) =>
        {
            bar.Jump(data.normalizedPressPoint.y);
            bar.BeginDrag(data.source);
        };
        button.LocalPressing += (_, data) => bar.Jump(data.normalizedPressPoint.y);
        return bar;
    }

    private float VisibleFraction()
    {
        RectTransform content = _scroll.RectTransform;
        RectTransform? viewport = content?.RectParent;
        if (content is null || viewport is null)
            return 1f;

        float contentHeight = content.LocalComputeRect.size.y;
        float viewportHeight = viewport.LocalComputeRect.size.y;
        if (contentHeight <= 0f || viewportHeight <= 0f)
            return 1f;

        return MathX.Clamp(viewportHeight / contentHeight, 0f, 1f);
    }

    private static bool IsHeld(TouchSource source)
    {
        InputInterface? input = source.InputInterface;
        if (input is { ScreenActive: true })
            return input.Mouse?.LeftButton.PressedOrHeld == true;

        return source.LocalForceTouch;
    }

    private void BeginDrag(Component source)
    {
        _dragSource = source is TouchSource { TouchType: TouchType.Remote } touch && IsHeld(touch) ? touch : null;
        SetShield(_dragSource is not null);
    }

    private void EndDrag()
    {
        _dragSource = null;
        SetShield(false);
    }

    private void SetShield(bool active)
    {
        if (!active)
        {
            if (_shield is { IsDestroyed: false, ActiveSelf: true })
                _shield.ActiveSelf = false;

            return;
        }
        if (_shield is not { IsDestroyed: false })
        {
            RectTransform? root = _button.RectTransform;
            while (root?.RectParent is RectTransform parent)
                root = parent;

            if (root is null)
                return;

            Slot shield = root.Slot.AddSlot("Scroll Bar Drag Shield");
            shield.PersistentSelf = false;
            shield.AttachComponent<RectTransform>();
            shield.AttachComponent<IgnoreLayout>();
            Button block = shield.AttachComponent<Button>();
            block.PassThroughHorizontalMovement.Value = false;
            block.PassThroughVerticalMovement.Value = false;
            _shield = shield;
        }
        if (!_shield.ActiveSelf)
            _shield.ActiveSelf = true;
    }

    private void ContinueDrag()
    {
        TouchSource? source = _dragSource;
        if (source is null)
            return;

        if (source.IsDestroyed || !IsHeld(source))
        {
            EndDrag();
            return;
        }
        if (_button.IsPressed.Value)
            return;

        Canvas? canvas = _button.RectTransform?.Canvas;
        if (canvas is null)
        {
            EndDrag();
            return;
        }
        Slot canvasSlot = canvas.Slot;
        float3 origin = canvasSlot.GlobalPointToLocal(source.TipPosition);
        float3 direction = canvasSlot.GlobalDirectionToLocal(source.TipDirection);
        if (MathX.Abs(direction.z) < 0.00001f)
            return;

        float distance = -origin.z / direction.z;
        if (distance < 0f)
            return;

        float2 point = (origin + direction * distance).xy * canvas.ComputeUnitScale;
        Jump(_button.CurrentGlobalRect.GetNormalizedPoint(point).y);
    }

    internal void Update()
    {
        if (!IsValid)
        {
            EndDrag();
            return;
        }
        ContinueDrag();
        float visible = VisibleFraction();
        if (visible >= 0.999f)
        {
            if (_thumbSlot.ActiveSelf)
                _thumbSlot.ActiveSelf = false;

            _size = 1f;
            return;
        }
        if (!_thumbSlot.ActiveSelf)
            _thumbSlot.ActiveSelf = true;

        float size = MathX.Max(MinimumThumb, visible);
        float position = MathX.Clamp01(_scroll.NormalizedPosition.Value.y);
        float top = position * (1f - size);
        if (MathX.Abs(size - _size) < Epsilon && MathX.Abs(top - _top) < Epsilon)
            return;

        _size = size;
        _top = top;
        _thumb.AnchorMin.Value = new float2(0.2f, 1f - top - size);
        _thumb.AnchorMax.Value = new float2(0.8f, 1f - top);
    }

    private void Jump(float pressY)
    {
        if (!IsValid)
            return;

        float visible = VisibleFraction();
        if (visible >= 0.999f)
            return;

        float size = MathX.Max(MinimumThumb, visible);
        float fromTop = 1f - MathX.Clamp01(pressY);
        float position = MathX.Clamp01((fromTop - size * 0.5f) / (1f - size));
        float2 current = _scroll.NormalizedPosition.Value;
        _scroll.NormalizedPosition.Value = new float2(current.x, position);
    }
}
