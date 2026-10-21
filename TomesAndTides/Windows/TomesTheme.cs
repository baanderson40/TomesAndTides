using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace TomesAndTides.Windows;

/// <summary>TT-owned slate/cyan ImGui styling; pushes are paired around each window draw.</summary>
internal static class TomesTheme
{
    public static readonly Vector4 Window = Rgb(0x0E2029);
    public static readonly Vector4 Panel = Rgb(0x152D38);
    public static readonly Vector4 Raised = Rgb(0x203944);
    public static readonly Vector4 Hover = Rgb(0x27444F);
    public static readonly Vector4 Border = Rgb(0x2C4956);
    public static readonly Vector4 Text = Rgb(0xF3F5F7);
    public static readonly Vector4 Muted = Rgb(0xB8C8D0);
    public static readonly Vector4 Cyan = Rgb(0x00C6DF);
    public static readonly Vector4 Selected = Rgb(0x075563);
    public static readonly Vector4 Mint = Rgb(0x87E6C7);
    public static readonly Vector4 Amber = Rgb(0xFFD38A);
    public static readonly Vector4 Rose = Rgb(0xEE7788);

    private const int StyleVarCount = 9;
    private static readonly (ImGuiCol Slot, Vector4 Color)[] Colors =
    [
        (ImGuiCol.WindowBg, Window), (ImGuiCol.ChildBg, Panel), (ImGuiCol.PopupBg, Panel),
        (ImGuiCol.Text, Text), (ImGuiCol.TextDisabled, Muted), (ImGuiCol.Border, Border),
        (ImGuiCol.Separator, Border), (ImGuiCol.SeparatorHovered, Hover), (ImGuiCol.SeparatorActive, Cyan),
        (ImGuiCol.FrameBg, Raised), (ImGuiCol.FrameBgHovered, Hover), (ImGuiCol.FrameBgActive, Selected),
        (ImGuiCol.Button, Raised), (ImGuiCol.ButtonHovered, Hover), (ImGuiCol.ButtonActive, Selected),
        (ImGuiCol.Header, Selected), (ImGuiCol.HeaderHovered, Hover), (ImGuiCol.HeaderActive, Cyan),
        (ImGuiCol.Tab, Raised), (ImGuiCol.TabHovered, Hover), (ImGuiCol.TabActive, Selected),
        (ImGuiCol.TabUnfocused, Raised), (ImGuiCol.TabUnfocusedActive, Selected),
        (ImGuiCol.CheckMark, Cyan), (ImGuiCol.SliderGrab, Selected), (ImGuiCol.SliderGrabActive, Cyan),
        (ImGuiCol.TableHeaderBg, Raised), (ImGuiCol.TableRowBg, Vector4.Zero),
        (ImGuiCol.TableRowBgAlt, new Vector4(0.15f, 0.27f, 0.31f, 0.35f)),
        (ImGuiCol.TitleBg, Window), (ImGuiCol.TitleBgActive, Panel), (ImGuiCol.TitleBgCollapsed, Window),
        (ImGuiCol.ScrollbarBg, Window), (ImGuiCol.ScrollbarGrab, Border),
        (ImGuiCol.ScrollbarGrabHovered, Hover), (ImGuiCol.ScrollbarGrabActive, Cyan),
        (ImGuiCol.ResizeGrip, Border), (ImGuiCol.ResizeGripHovered, Hover), (ImGuiCol.ResizeGripActive, Cyan),
        (ImGuiCol.TextSelectedBg, Selected), (ImGuiCol.NavHighlight, Cyan),
        (ImGuiCol.ModalWindowDimBg, new Vector4(Window.X, Window.Y, Window.Z, 0.65f)),
    ];

    public static float Scale => ImGuiHelpers.GlobalScale;

    public static void Push()
    {
        var scale = Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, 7) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(10, 8) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(8, 6) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(10, 7) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, scale);
        foreach (var (slot, color) in Colors)
            ImGui.PushStyleColor(slot, color);
    }

    public static void Pop()
    {
        ImGui.PopStyleColor(Colors.Length);
        ImGui.PopStyleVar(StyleVarCount);
    }

    public static Vector4 ForStatus(string? status) => status switch
    {
        "Eligible" or "Completed" or "Ready" or "Active" => Mint,
        "Blocked" or "Unknown" or "Unavailable" => Amber,
        "Failed" or "RecoveryRequired" => Rose,
        _ => Muted,
    };

    public static Vector4 Rgb(uint rgb) => new(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f,
        1f);
}
