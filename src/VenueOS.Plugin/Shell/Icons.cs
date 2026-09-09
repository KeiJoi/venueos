using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace VenueOS.Plugin.Shell;

/// <summary>Procedural vector icons for app tiles. No texture/asset pipeline exists in this plugin yet and Unicode
/// emoji are disallowed as primary icons, so each glyph is a handful of ImGui draw-list primitives keyed off the
/// existing <c>ModuleDescriptor.Icon</c> string. Colors always come from the caller (theme tokens), never fixed.</summary>
internal static class AppIcons
{
    public static void Draw(string iconKey, Vector2 center, float radius, uint color, float thickness = 2f)
    {
        var drawList = ImGui.GetWindowDrawList();
        switch (iconKey)
        {
            case "users": DrawUsers(drawList, center, radius, color, thickness); break;
            case "message": DrawMessage(drawList, center, radius, color, thickness); break;
            case "star": DrawStar(drawList, center, radius, color); break;
            case "megaphone": DrawMegaphone(drawList, center, radius, color, thickness); break;
            case "search": DrawSearch(drawList, center, radius, color, thickness); break;
            case "ticket": DrawTicket(drawList, center, radius, color, thickness); break;
            case "circle-question": DrawQuestion(drawList, center, radius, color, thickness); break;
            case "trophy": DrawTrophy(drawList, center, radius, color, thickness); break;
            case "grid": DrawGrid(drawList, center, radius, color, thickness); break;
            case "gear": DrawGear(drawList, center, radius, color, thickness); break;
            case "home": DrawHome(drawList, center, radius, color, thickness); break;
            case "close": DrawClose(drawList, center, radius, color, thickness); break;
            case "popout": DrawPopout(drawList, center, radius, color, thickness); break;
            case "palette": DrawPalette(drawList, center, radius, color, thickness); break;
            case "terminal": DrawTerminal(drawList, center, radius, color, thickness); break;
            case "file-pen": DrawFilePen(drawList, center, radius, color, thickness); break;
            case "book": DrawBook(drawList, center, radius, color, thickness); break;
            case "block-letters": DrawBlockLetters(drawList, center, radius, color); break;
            case "gift": DrawGift(drawList, center, radius, color, thickness); break;
            case "macro": DrawMacro(drawList, center, radius, color, thickness); break;
            default: drawList.AddCircle(center, radius * 0.6f, color, 0, thickness); break;
        }
    }

    private static void DrawUsers(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        var left = c + new Vector2(-r * 0.35f, 0.05f * r);
        var right = c + new Vector2(r * 0.35f, -0.05f * r);
        d.AddCircle(left, r * 0.28f, color, 0, t);
        d.AddCircle(right, r * 0.24f, color, 0, t);
        d.PathArcTo(left, r * 0.55f, 3.66f, 5.76f, 8);
        d.PathStroke(color, ImDrawFlags.None, t);
        d.PathArcTo(right, r * 0.48f, 3.5f, 5.9f, 8);
        d.PathStroke(color, ImDrawFlags.None, t);
    }

    private static void DrawMessage(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        var min = c + new Vector2(-r * 0.6f, -r * 0.45f);
        var max = c + new Vector2(r * 0.6f, r * 0.2f);
        d.AddRect(min, max, color, r * 0.18f, ImDrawFlags.None, t);
        var tailA = new Vector2(min.X + (max.X - min.X) * 0.25f, max.Y);
        var tailB = tailA + new Vector2(-r * 0.05f, r * 0.3f);
        var tailC = new Vector2(tailA.X + r * 0.28f, max.Y);
        d.AddTriangleFilled(tailA, tailB, tailC, color);
    }

    private static void DrawStar(ImDrawListPtr d, Vector2 c, float r, uint color)
    {
        const int points = 5; var outer = r * 0.62f; var inner = outer * 0.42f; Span<Vector2> vertices = stackalloc Vector2[points * 2];
        for (var i = 0; i < points * 2; i++)
        {
            var radius = i % 2 == 0 ? outer : inner;
            var angle = -MathF.PI / 2 + i * MathF.PI / points;
            vertices[i] = c + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
        }
        for (var i = 0; i < points * 2; i++) d.AddTriangleFilled(c, vertices[i], vertices[(i + 1) % (points * 2)], color);
    }

    private static void DrawMegaphone(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        var bodyLeft = c + new Vector2(-r * 0.55f, -r * 0.15f);
        var bodyRight = c + new Vector2(0, -r * 0.4f);
        var bodyRightBottom = c + new Vector2(0, r * 0.4f);
        var bodyLeftBottom = c + new Vector2(-r * 0.55f, r * 0.15f);
        d.AddQuadFilled(bodyLeft, bodyRight, bodyRightBottom, bodyLeftBottom, color);
        d.AddTriangleFilled(bodyRight, c + new Vector2(r * 0.5f, -r * 0.55f), c + new Vector2(r * 0.5f, r * 0.55f), color);
        d.AddRectFilled(c + new Vector2(-r * 0.75f, -r * 0.12f), c + new Vector2(-r * 0.55f, r * 0.12f), color);
        d.PathArcTo(c + new Vector2(-r * 0.35f, r * 0.3f), r * 0.35f, -0.8f, 0.9f, 8);
        d.PathStroke(color, ImDrawFlags.None, t);
    }

    private static void DrawSearch(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        var lensCenter = c + new Vector2(-r * 0.12f, -r * 0.12f);
        d.AddCircle(lensCenter, r * 0.4f, color, 0, t);
        var handleStart = lensCenter + new Vector2(r * 0.28f, r * 0.28f);
        d.AddLine(handleStart, c + new Vector2(r * 0.55f, r * 0.55f), color, t * 1.4f);
    }

    private static void DrawTicket(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        var min = c + new Vector2(-r * 0.6f, -r * 0.35f);
        var max = c + new Vector2(r * 0.6f, r * 0.35f);
        d.AddRect(min, max, color, r * 0.12f, ImDrawFlags.None, t);
        var notchX = c.X;
        d.AddCircleFilled(new Vector2(notchX, min.Y), r * 0.09f, ImGui.GetColorU32(ImGuiCol.WindowBg));
        d.AddCircleFilled(new Vector2(notchX, max.Y), r * 0.09f, ImGui.GetColorU32(ImGuiCol.WindowBg));
        for (var y = min.Y + r * 0.15f; y < max.Y; y += r * 0.15f) d.AddLine(new Vector2(notchX, y), new Vector2(notchX, y + r * 0.06f), color, 1f);
    }

    /// <summary>Mair's Editor's glyph — a document with a pencil crossing its corner. Deliberately distinct from
    /// Mair's Trivia's "circle-question" so the two related modules never share an icon.</summary>
    private static void DrawFilePen(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        var min = c + new Vector2(-r * 0.55f, -r * 0.62f);
        var max = c + new Vector2(r * 0.2f, r * 0.62f);
        d.AddRect(min, max, color, r * 0.08f, ImDrawFlags.None, t);
        for (var y = min.Y + r * 0.28f; y < max.Y - r * 0.15f; y += r * 0.24f) d.AddLine(new Vector2(min.X + r * 0.12f, y), new Vector2(max.X - r * 0.12f, y), color, t * 0.8f);
        var penStart = c + new Vector2(r * 0.05f, r * 0.35f);
        var penEnd = c + new Vector2(r * 0.65f, -r * 0.55f);
        d.AddLine(penStart, penEnd, color, t * 1.4f);
        d.AddTriangleFilled(penStart, penStart + new Vector2(-r * 0.05f, r * 0.18f), penStart + new Vector2(r * 0.18f, r * 0.05f), color);
    }

    private static void DrawQuestion(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        d.AddCircle(c, r * 0.62f, color, 0, t);
        var textSize = ImGui.CalcTextSize("?");
        ImGui.GetWindowDrawList().AddText(c - textSize / 2, color, "?");
    }

    private static void DrawTrophy(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        var cupMin = c + new Vector2(-r * 0.32f, -r * 0.5f);
        var cupMax = c + new Vector2(r * 0.32f, r * 0.05f);
        d.AddRect(cupMin, cupMax, color, r * 0.1f, ImDrawFlags.None, t);
        d.PathArcTo(new Vector2(cupMin.X, c.Y - r * 0.3f), r * 0.22f, 1.6f, 4.7f, 8);
        d.PathStroke(color, ImDrawFlags.None, t);
        d.PathArcTo(new Vector2(cupMax.X, c.Y - r * 0.3f), r * 0.22f, -1.6f, 1.6f, 8);
        d.PathStroke(color, ImDrawFlags.None, t);
        d.AddLine(c + new Vector2(0, r * 0.05f), c + new Vector2(0, r * 0.35f), color, t);
        d.AddLine(c + new Vector2(-r * 0.25f, r * 0.45f), c + new Vector2(r * 0.25f, r * 0.45f), color, t);
    }

    private static void DrawGrid(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        var min = c + new Vector2(-r * 0.55f, -r * 0.55f);
        var max = c + new Vector2(r * 0.55f, r * 0.55f);
        d.AddRect(min, max, color, r * 0.08f, ImDrawFlags.None, t);
        var third = (max.X - min.X) / 3f;
        d.AddLine(new Vector2(min.X + third, min.Y), new Vector2(min.X + third, max.Y), color, t);
        d.AddLine(new Vector2(min.X + third * 2, min.Y), new Vector2(min.X + third * 2, max.Y), color, t);
        d.AddLine(new Vector2(min.X, min.Y + third), new Vector2(max.X, min.Y + third), color, t);
        d.AddLine(new Vector2(min.X, min.Y + third * 2), new Vector2(max.X, min.Y + third * 2), color, t);
    }

    private static void DrawGear(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        const int teeth = 8; var outer = r * 0.55f; var inner = outer * 0.72f;
        for (var i = 0; i < teeth; i++)
        {
            var angle = i * MathF.Tau / teeth; var next = angle + MathF.Tau / teeth / 2f;
            var p1 = c + new Vector2(MathF.Cos(angle) * inner, MathF.Sin(angle) * inner);
            var p2 = c + new Vector2(MathF.Cos(angle) * outer, MathF.Sin(angle) * outer);
            var p3 = c + new Vector2(MathF.Cos(next) * outer, MathF.Sin(next) * outer);
            var p4 = c + new Vector2(MathF.Cos(next) * inner, MathF.Sin(next) * inner);
            d.AddQuadFilled(p1, p2, p3, p4, color);
        }
        d.AddCircleFilled(c, inner, color);
        d.AddCircleFilled(c, inner * 0.4f, ImGui.GetColorU32(ImGuiCol.WindowBg));
    }

    private static void DrawHome(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        var eaveY = c.Y - r * 0.05f;
        var roofLeft = new Vector2(c.X - r * 0.55f, eaveY);
        var roofRight = new Vector2(c.X + r * 0.55f, eaveY);
        var roofTop = new Vector2(c.X, c.Y - r * 0.6f);
        d.AddTriangleFilled(roofLeft, roofTop, roofRight, color);
        var baseMin = new Vector2(c.X - r * 0.38f, eaveY);
        var baseMax = new Vector2(c.X + r * 0.38f, c.Y + r * 0.55f);
        d.AddRect(baseMin, baseMax, color, 0, ImDrawFlags.None, t);
        var doorMin = new Vector2(c.X - r * 0.12f, c.Y + r * 0.1f);
        var doorMax = new Vector2(c.X + r * 0.12f, baseMax.Y);
        d.AddRectFilled(doorMin, doorMax, color);
    }

    private static void DrawPalette(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        d.PathArcTo(c + new Vector2(0, r * 0.1f), r * 0.55f, 3.4f, 8.9f, 16);
        d.PathStroke(color, ImDrawFlags.None, t);
        var dot = r * 0.1f;
        d.AddCircleFilled(c + new Vector2(-r * 0.22f, -r * 0.18f), dot, color);
        d.AddCircleFilled(c + new Vector2(r * 0.05f, -r * 0.32f), dot, color);
        d.AddCircleFilled(c + new Vector2(r * 0.28f, -r * 0.1f), dot, color);
        d.AddCircleFilled(c + new Vector2(r * 0.12f, r * 0.28f), dot, color);
    }

    private static void DrawTerminal(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        var min = c + new Vector2(-r * 0.6f, -r * 0.5f);
        var max = c + new Vector2(r * 0.6f, r * 0.5f);
        d.AddRect(min, max, color, r * 0.12f, ImDrawFlags.None, t);
        var chevronStart = min + new Vector2(r * 0.18f, r * 0.3f);
        var chevronMid = chevronStart + new Vector2(r * 0.22f, r * 0.2f);
        var chevronEnd = chevronStart + new Vector2(0, r * 0.4f);
        d.AddLine(chevronStart, chevronMid, color, t);
        d.AddLine(chevronMid, chevronEnd, color, t);
        d.AddLine(chevronMid + new Vector2(r * 0.12f, r * 0.2f), chevronMid + new Vector2(r * 0.42f, r * 0.2f), color, t);
    }

    private static void DrawClose(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        var extent = r * 0.4f;
        d.AddLine(c + new Vector2(-extent, -extent), c + new Vector2(extent, extent), color, t);
        d.AddLine(c + new Vector2(-extent, extent), c + new Vector2(extent, -extent), color, t);
    }

    /// <summary>User Manual's glyph — an open book: an outer cover with a center spine and short "text line" marks
    /// on each page. Distinct from "circle-question" (already Mair's Trivia's icon) and "file-pen" (Mair's Editor's
    /// icon, which reads as "edit a document" rather than "read a document").</summary>
    private static void DrawBook(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        var min = c + new Vector2(-r * 0.55f, -r * 0.45f);
        var max = c + new Vector2(r * 0.55f, r * 0.5f);
        d.AddRect(min, max, color, r * 0.1f, ImDrawFlags.None, t);
        d.AddLine(new Vector2(c.X, min.Y + r * 0.08f), new Vector2(c.X, max.Y - r * 0.08f), color, t);
        foreach (var fraction in new[] { 0.25f, 0.55f })
        {
            var y = min.Y + (max.Y - min.Y) * fraction;
            d.AddLine(new Vector2(min.X + r * 0.12f, y), new Vector2(c.X - r * 0.1f, y), color, t * 0.8f);
            d.AddLine(new Vector2(c.X + r * 0.1f, y), new Vector2(max.X - r * 0.12f, y), color, t * 0.8f);
        }
    }

    /// <summary>Block Letters' glyph — a pixel-art capital "A" built from filled squares on a 5x5 grid, matching
    /// the module's own subject (FFXIV block-letter text) rather than an abstract shape. Distinct from every other
    /// registered key.</summary>
    private static void DrawBlockLetters(ImDrawListPtr d, Vector2 c, float r, uint color)
    {
        ReadOnlySpan<string> pattern =
        [
            "01110",
            "10001",
            "11111",
            "10001",
            "10001",
        ];
        var cell = r * 0.32f;
        var origin = c + new Vector2(-cell * 2.5f, -cell * 2.5f);
        for (var row = 0; row < pattern.Length; row++)
        {
            for (var col = 0; col < pattern[row].Length; col++)
            {
                if (pattern[row][col] != '1') continue;
                var min = origin + new Vector2(col * cell, row * cell);
                d.AddRectFilled(min + new Vector2(1, 1), min + new Vector2(cell - 1, cell - 1), color);
            }
        }
    }

    /// <summary>Giveaways' glyph — a gift box: a lidded square with a vertical+horizontal ribbon and a small bow on
    /// top. Distinct from "ticket" (Raffle's icon, a different prize/drawing concept) and every other registered
    /// key.</summary>
    private static void DrawGift(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        var lidMin = c + new Vector2(-r * 0.55f, -r * 0.15f);
        var lidMax = c + new Vector2(r * 0.55f, r * 0.05f);
        d.AddRect(lidMin, lidMax, color, r * 0.06f, ImDrawFlags.None, t);
        var boxMin = c + new Vector2(-r * 0.45f, r * 0.05f);
        var boxMax = c + new Vector2(r * 0.45f, r * 0.6f);
        d.AddRect(boxMin, boxMax, color, r * 0.06f, ImDrawFlags.None, t);
        d.AddLine(new Vector2(c.X, lidMin.Y), new Vector2(c.X, boxMax.Y), color, t);
        d.AddLine(new Vector2(boxMin.X, c.Y + r * 0.3f), new Vector2(boxMax.X, c.Y + r * 0.3f), color, t);
        d.PathArcTo(c + new Vector2(-r * 0.12f, -r * 0.2f), r * 0.14f, 0f, MathF.PI * 1.6f, 8);
        d.PathStroke(color, ImDrawFlags.None, t * 0.8f);
        d.PathArcTo(c + new Vector2(r * 0.12f, -r * 0.2f), r * 0.14f, MathF.PI, MathF.PI * 2.6f, 8);
        d.PathStroke(color, ImDrawFlags.None, t * 0.8f);
    }

    /// <summary>Macro's glyph — three small square hotbar slots in a row, the first one holding a filled "play"
    /// triangle — reads as "a hotbar you press to run something", distinct from "grid" (an even 3x3 grid, Bingo's
    /// concept) and "terminal" (a command-line prompt, not a launcher).</summary>
    private static void DrawMacro(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        var slot = r * 0.52f;
        var gap = r * 0.18f;
        var totalWidth = slot * 3 + gap * 2;
        var originX = c.X - totalWidth / 2f;
        var top = c.Y - slot / 2f;

        for (var i = 0; i < 3; i++)
        {
            var min = new Vector2(originX + i * (slot + gap), top);
            var max = min + new Vector2(slot, slot);
            d.AddRect(min, max, color, r * 0.08f, ImDrawFlags.None, t);
            if (i == 0)
            {
                var pad = slot * 0.22f;
                d.AddTriangleFilled(min + new Vector2(pad, pad), min + new Vector2(pad, slot - pad), min + new Vector2(slot - pad, slot / 2f), color);
            }
        }
    }

    private static void DrawPopout(ImDrawListPtr d, Vector2 c, float r, uint color, float t)
    {
        var boxMin = c + new Vector2(-r * 0.5f, -r * 0.1f);
        var boxMax = c + new Vector2(r * 0.25f, r * 0.5f);
        d.AddRect(boxMin, boxMax, color, r * 0.08f, ImDrawFlags.None, t);
        var arrowStart = c + new Vector2(-r * 0.05f, -r * 0.05f);
        var arrowEnd = c + new Vector2(r * 0.5f, -r * 0.5f);
        d.AddLine(arrowStart, arrowEnd, color, t);
        d.AddLine(arrowEnd, arrowEnd + new Vector2(-r * 0.25f, 0), color, t);
        d.AddLine(arrowEnd, arrowEnd + new Vector2(0, r * 0.25f), color, t);
    }
}
