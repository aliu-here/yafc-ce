﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using SDL2;

namespace Yafc.UI;

public enum SetKeyboardFocus {
    /// <summary>
    /// Do not explicitly focus this edit control.
    /// </summary>
    No,
    /// <summary>
    /// Explicitly focus this edit control the first time the panel/pseudoscreen is drawn, but not after that.<br/>
    /// If you are adding a new control to an existing panel (e.g. the production table), you will need to use <see cref="Always"/> instead.
    /// </summary>
    OnFirstPanelDraw,
    /// <summary>
    /// Always explicitly focus this edit control.<br/>
    /// The caller is responsible for properly implementing "only the first time the control is drawn".
    /// </summary>
    Always,
};

public partial class ImGui {
    private readonly struct DrawCommand<T>(Rect rect, T data, SchemeColor color) {
        public readonly Rect rect = rect;
        public readonly T data = data;
        public readonly SchemeColor color = color;

        public void Deconstruct(out Rect rect, out T data, out SchemeColor color) {
            rect = this.rect;
            data = this.data;
            color = this.color;
        }
    }

    private readonly List<DrawCommand<(RectangleBorder, bool)>> rects = [];
    private readonly List<DrawCommand<Icon>> icons = [];
    private readonly List<DrawCommand<IRenderable>> renderables = [];
    private readonly List<DrawCommand<IPanel>> panels = [];
    internal bool enableDrawing { get; private set; } = true;
    public SchemeColor initialTextColor { get; set; } = SchemeColor.BackgroundText;
    public SchemeColor boxColor { get; set; } = SchemeColor.None;
    public RectangleBorder boxShadow { get; set; } = RectangleBorder.None;
    public Padding initialPadding { get; set; }

    public void DrawRectangle(Rect rect, SchemeColor color, RectangleBorder border = RectangleBorder.None, bool drawTransparent = false) {
        if (action != ImGuiAction.Build || !enableDrawing) {
            return;
        }

        rects.Add(new DrawCommand<(RectangleBorder, bool)>(rect, (border, drawTransparent), color));
    }

    public void DrawIcon(Rect rect, Icon icon, SchemeColor color) {
        if (action != ImGuiAction.Build || icon == Icon.None || !enableDrawing) {
            return;
        }

        icons.Add(new DrawCommand<Icon>(rect, icon, color));
    }

    public void DrawRenderable(Rect rect, IRenderable? renderable, SchemeColor color) {
        if (action != ImGuiAction.Build || renderable == null || !enableDrawing) {
            return;
        }

        renderables.Add(new DrawCommand<IRenderable>(rect, renderable, color));
    }

    public void DrawPanel(Rect rect, IPanel panel) {
        if (action != ImGuiAction.Build || panel == null) {
            return;
        }

        panels.Add(new DrawCommand<IPanel>(rect, panel, 0));
        _ = panel.CalculateState(rect.Width, pixelsPerUnit);
    }

    private void ClearDrawCommandList() {
        rects.Clear();
        icons.Clear();
        renderables.Clear();
        panels.Clear();
    }

    public void ManualDrawingClear() {
        if (guiBuilder == null) {
            ClearDrawCommandList();
        }
    }

    public readonly ImGuiCache<TextCache, (FontFile.FontSize size, string text, uint wrapWidth)>.Cache textCache = new ImGuiCache<TextCache, (FontFile.FontSize size, string text, uint wrapWidth)>.Cache();

    public FontFile.FontSize GetFontSize(Font? font = null) => (font ?? Font.text).GetFontSize(pixelsPerUnit);

    public SchemeColor textColor {
        get => state.textColor;
        set => state.textColor = value;
    }

    public void BuildText(string? text, TextBlockDisplayStyle? displayStyle = null, float topOffset = 0f, float maxWidth = float.MaxValue) {
        displayStyle ??= TextBlockDisplayStyle.Default();
        SchemeColor color = displayStyle.Color;

        if (color == SchemeColor.None) {
            color = state.textColor;
        }

        Rect rect = AllocateTextRect(out TextCache? cache, text, displayStyle, topOffset, maxWidth);

        if (action == ImGuiAction.Build && cache != null) {
            DrawRenderable(rect, cache, color);
        }
    }

    public void BuildWrappedText(string text) => BuildText(text, TextBlockDisplayStyle.WrappedText);

    public Vector2 GetTextDimensions(out TextCache? cache, string? text, Font? font = null, bool wrap = false, float maxWidth = float.MaxValue) {
        if (string.IsNullOrEmpty(text)) {
            cache = null;
            return Vector2.Zero;
        }

        var fontSize = GetFontSize(font);
        cache = textCache.GetCached((fontSize, text, wrap ? (uint)UnitsToPixels(MathF.Max(width, 5f)) : uint.MaxValue));
        float textWidth = Math.Min(cache.texRect.w / pixelsPerUnit, maxWidth);

        return new Vector2(textWidth, cache.texRect.h / pixelsPerUnit);
    }

    public Rect AllocateTextRect(out TextCache? cache, string? text, TextBlockDisplayStyle displayStyle, float topOffset = 0f, float maxWidth = float.MaxValue) {
        FontFile.FontSize fontSize = GetFontSize(displayStyle.Font);
        Rect rect;

        if (string.IsNullOrEmpty(text)) {
            cache = null;
            rect = AllocateRect(0f, topOffset + (fontSize.lineSize / pixelsPerUnit));
        }
        else {
            if (Debugger.IsAttached && text.Contains("Key not found: ")) {
                Debugger.Break(); // drawing improperly internationalized text
            }
            Vector2 textSize = GetTextDimensions(out cache, text, displayStyle.Font, displayStyle.WrapText, maxWidth);
            rect = AllocateRect(textSize.X, topOffset + (textSize.Y), displayStyle.Alignment);
        }

        if (topOffset != 0f) {
            rect.Top += topOffset;
        }

        return rect;
    }

    public void DrawText(Rect rect, string text, RectAlignment alignment = RectAlignment.MiddleLeft, Font? font = null, SchemeColor color = SchemeColor.None) {
        if (color == SchemeColor.None) {
            color = state.textColor;
        }

        var fontSize = GetFontSize(font);
        var cache = textCache.GetCached((fontSize, text, uint.MaxValue));
        var realRect = AlignRect(rect, alignment, cache.texRect.w / pixelsPerUnit, cache.texRect.h / pixelsPerUnit);

        if (action == ImGuiAction.Build) {
            DrawRenderable(realRect, cache, color);
        }
    }

    private ImGuiTextInputHelper? textInputHelper;
    public bool BuildTextInput(string? text, out string newText, string? placeholder, Icon icon = Icon.None, bool delayed = false, SetKeyboardFocus setKeyboardFocus = SetKeyboardFocus.No) {
        TextBoxDisplayStyle displayStyle = TextBoxDisplayStyle.DefaultTextInput;

        if (icon != Icon.None) {
            displayStyle = displayStyle with { Icon = icon };
        }

        return BuildTextInput(text, out newText, placeholder, displayStyle, delayed, setKeyboardFocus);
    }

    public bool BuildTextInput(string? text, out string newText, string? placeholder, TextBoxDisplayStyle displayStyle, bool delayed, SetKeyboardFocus setKeyboardFocus = SetKeyboardFocus.No) {
        if (setKeyboardFocus != SetKeyboardFocus.Always && textInputHelper != null) {
            setKeyboardFocus = SetKeyboardFocus.No;
        }
        textInputHelper ??= new ImGuiTextInputHelper(this);
        bool result = textInputHelper.BuildTextInput(text, out newText, placeholder, GetFontSize(), delayed, displayStyle);

        if (setKeyboardFocus != SetKeyboardFocus.No) {
            SetTextInputFocus(lastRect, newText);
        }

        return result;
    }

    public void BuildIcon(Icon icon, float size = 1.5f, SchemeColor color = SchemeColor.None) {
        if (color == SchemeColor.None) {
            color = icon >= Icon.FirstCustom ? SchemeColor.Source : state.textColor;
        }

        var rect = AllocateRect(size, size, RectAlignment.Middle);

        if (action == ImGuiAction.Build) {
            DrawIcon(rect, icon, color);
        }
    }

    public Vector2 mousePosition => InputSystem.Instance.mousePosition - screenRect.Position;
    public bool mousePresent { get; private set; }
    public Rect mouseDownRect { get; private set; }
    public Rect mouseOverRect { get; private set; } = Rect.VeryBig;
    private readonly RectAllocator defaultAllocator;
    private int mouseDownButton = -1;
    private float buildingWidth;
    public event Action? CollectCustomCache;

    private bool DoGui(ImGuiAction action) {
        if (guiBuilder == null) {
            return false;
        }

        this.action = action;
        ResetLayout();
        buildingWidth = buildWidth;
        buildGroupsIndex = -1;
        using (EnterGroup(initialPadding, defaultAllocator, initialTextColor)) {
            guiBuilder(this);
        }
        actionParameter = 0;

        if (action == ImGuiAction.Build) {
            return false;
        }

        bool consumed = this.action == ImGuiAction.Consumed;

        if (IsRebuildRequired()) {
            BuildGui(buildWidth);
        }

        this.action = ImGuiAction.Consumed;

        return consumed;
    }

    private void BuildGui(float width) {
        if (guiBuilder == null) {
            return;
        }

        buildWidth = width;
        nextRebuildTimer = long.MaxValue;
        rebuildRequested = false;
        ClearDrawCommandList();
        _ = DoGui(ImGuiAction.Build);
        contentSize = new Vector2(lastContentRect.Right, lastContentRect.Height);

        if (boxColor != SchemeColor.None) {
            Rect rect = new Rect(default, contentSize);
            rects.Add(new(rect, (boxShadow, false), boxColor));
        }

        textCache.PurgeUnused();
        CollectCustomCache?.Invoke();
        Repaint();
    }

    public void MouseMove(int mouseDownButton) {
        actionParameter = mouseDownButton;
        mousePresent = true;

        if (currentDraggingObject != null) {
            _ = DoGui(ImGuiAction.MouseDrag);
            return;
        }

        if (!mouseOverRect.Contains(mousePosition)) {
            mouseOverRect = Rect.VeryBig;
            rebuildRequested = true;
            if (!cursorSetByMouseDown) {
                SDL.SDL_SetCursor(RenderingUtils.cursorArrow);
            }
        }

        _ = DoGui(ImGuiAction.MouseMove);
    }

    public void MouseDown(int button) {
        mouseDownButton = button;
        actionParameter = button;
        mouseDownRect = default;
        _ = DoGui(ImGuiAction.MouseDown);
    }

    public void MouseLost() {
        mouseDownButton = -1;
        mouseDownRect = default;
        mouseOverRect = Rect.VeryBig;
    }

    public void MouseUp(int button) {
        mouseDownButton = -1;

        if (currentDraggingObject != null) {
            currentDraggingObject = null;
            rebuildRequested = true;
            return;
        }

        if (cursorSetByMouseDown) {
            SDL.SDL_SetCursor(RenderingUtils.cursorHand);
            cursorSetByMouseDown = false;
        }

        actionParameter = button;
        _ = DoGui(ImGuiAction.MouseUp);
    }

    public void MouseScroll(int delta) {
        actionParameter = delta;

        if (!DoGui(ImGuiAction.MouseScroll)) {
            parent?.MouseScroll(delta);
        }
    }

    public void MouseExit() {
        mousePresent = false;

        if (mouseOverRect != Rect.VeryBig) {
            mouseOverRect = Rect.VeryBig;
            SDL.SDL_SetCursor(RenderingUtils.cursorArrow);
            BuildGui(buildWidth);
        }
    }

    private bool cursorSetByMouseDown;
    public bool ConsumeMouseDown(Rect rect, uint button = SDL.SDL_BUTTON_LEFT, IntPtr cursor = default) {
        if (action == ImGuiAction.MouseDown && mousePresent && rect.Contains(mousePosition) && actionParameter == button) {
            action = ImGuiAction.Consumed;
            rebuildRequested = true;
            mouseDownRect = rect;

            if (cursor != default) {
                SDL.SDL_SetCursor(cursor);
                cursorSetByMouseDown = true;
            }
            return true;
        }

        return false;
    }

    public bool ConsumeMouseOver(Rect rect, IntPtr cursor = default, bool rebuild = true) {
        if (action == ImGuiAction.MouseMove && mousePresent && rect.Contains(mousePosition)) {
            action = ImGuiAction.Consumed;

            if (mouseOverRect != rect) {
                if (rebuild) {
                    rebuildRequested = true;
                }

                mouseOverRect = rect;

                if (!cursorSetByMouseDown) {
                    SDL.SDL_SetCursor(cursor == default ? RenderingUtils.cursorArrow : cursor);
                }

                return true;
            }
        }
        return false;
    }

    public bool ConsumeMouseUp(Rect rect, bool inside = true) {
        if (action == ImGuiAction.MouseUp && rect == mouseDownRect && (!inside || rect.Contains(mousePosition))) {
            action = ImGuiAction.Consumed;
            Rebuild();
            return true;
        }

        return false;
    }

    public bool ConsumeEvent(Rect rect) {
        if (action == ImGuiAction.MouseScroll && rect.Contains(mousePosition)) {
            action = ImGuiAction.Consumed;
            return true;
        }

        return false;
    }

    public bool IsMouseOver(Rect rect) => rect == mouseOverRect;

    public bool IsMouseDown(Rect rect, uint button = SDL.SDL_BUTTON_LEFT) => rect == mouseDownRect && mouseDownButton == button;

    public bool IsMouseOverOrDown(Rect rect, uint button = SDL.SDL_BUTTON_LEFT) => mouseOverRect == rect || (mouseDownRect == rect && mouseDownButton == button);

    public bool IsLastMouseDown(Rect rect) => rect == mouseDownRect;

    public void SetTextInputFocus(Rect rect, string text) {
        if (textInputHelper != null && InputSystem.Instance.currentKeyboardFocus != textInputHelper) {
            Rebuild();
            textInputHelper.SetFocus(rect, text);
        }
    }
}
