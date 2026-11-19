using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;

namespace Crm.Web.Pages;

/// <summary>
/// Small markup helpers for the redesigned views (icons, avatars, chips). Icon names are literals chosen in the views, never user input;
/// every dynamic text goes through the HTML encoder.
/// </summary>
public static class Ui
{
    private const string Sprite = "/img/ui/icons.svg";

    /// <summary>Icon ids are written into markup unencoded, so anything but lowercase letters, digits and dashes falls back to a neutral icon.</summary>
    private static string SafeId(string name) => name.Length is > 0 and <= 32 && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') ? name : "info";

    /// <summary>Bare line icon, decorative (the text next to it carries the meaning).</summary>
    public static IHtmlContent Icon(string name, string? cls = null) =>
        new HtmlString($"<svg class=\"icon{(cls is null ? "" : " " + cls)}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"{Sprite}#i-{SafeId(name)}\"/></svg>");

    /// <summary>Icon in a circle. size is "", "r40", "r48" or "r56".</summary>
    public static IHtmlContent Ring(string name, string size = "") =>
        new HtmlString($"<span class=\"icon-ring{(size.Length == 0 ? "" : " " + size)}\" aria-hidden=\"true\"><svg class=\"icon\" focusable=\"false\"><use href=\"{Sprite}#i-{SafeId(name)}\"/></svg></span>");

    /// <summary>The data-theme attribute for the html element: only "light" or "dark" (the stored preference), nothing for "system".</summary>
    public static IHtmlContent ThemeAttr(string? theme) =>
        theme is "light" or "dark" ? new HtmlString($" data-theme=\"{theme}\"") : HtmlString.Empty;

    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2);
        return string.Concat(parts.Select(p => char.ConvertFromUtf32(char.ConvertToUtf32(p, 0)))).ToUpperInvariant();
    }

    /// <summary>Initials in a line circle. Decorative: the owner's name is always written next to it.</summary>
    public static IHtmlContent Avatar(string name, string size = "", bool inactive = false) =>
        new HtmlString($"<span class=\"avatar{(size.Length == 0 ? "" : " " + size)}{(inactive ? " inactive" : "")}\" aria-hidden=\"true\">{HtmlEncoder.Default.Encode(Initials(name))}</span>");

    /// <summary>Avatar + name, for table cells and meta lines.</summary>
    public static IHtmlContent Owner(string name, string size = "s24") =>
        name.Length == 0 ? HtmlString.Empty : new HtmlString($"<span class=\"owner\">{Avatar(name, size).ToHtml()}<span>{HtmlEncoder.Default.Encode(name)}</span></span>");

    /// <summary>Progress pips: n filled of total (decorative).</summary>
    public static IHtmlContent Pips(int on, int total)
    {
        if (total <= 0)
        {
            return HtmlString.Empty;
        }
        var sb = new System.Text.StringBuilder("<span class=\"pips\" aria-hidden=\"true\">");
        for (var i = 1; i <= total; i++)
        {
            sb.Append(i <= on ? "<span class=\"pip on\"></span>" : "<span class=\"pip\"></span>");
        }
        return new HtmlString(sb.Append("</span>").ToString());
    }

    /// <summary>Stage chip: open stages show progress pips, won and lost show their marker. Text always says the stage.</summary>
    public static IHtmlContent StageChip(string stageName, string stageType, int openIndex, int openCount)
    {
        var enc = HtmlEncoder.Default.Encode(stageName);
        return stageType switch
        {
            "won" => new HtmlString($"<span class=\"chip won\">{enc}</span>"),
            "lost" => new HtmlString($"<span class=\"chip lost\">{enc}</span>"),
            _ => new HtmlString($"<span class=\"chip stage\">{Pips(openIndex, openCount).ToHtml()}{enc}</span>"),
        };
    }

    public sealed record Segment(string Code, string Label, string IconBase);

    /// <summary>The DAFA segment for an Industry picklist value code (D8), or null.</summary>
    public static Segment? SegmentOf(string? code) => code switch
    {
        "wind" => new("wind", "Wind energy", "icon-wind"),
        "building" => new("building", "Building materials", "icon-building-materials"),
        "industry" => new("industry", "Industry", "icon-industry"),
        _ => null,
    };

    public static IHtmlContent SegmentChip(string? code) =>
        SegmentOf(code) is { } s ? new HtmlString($"<span class=\"chip segment {s.Code}\">{HtmlEncoder.Default.Encode(s.Label)}</span>") : HtmlString.Empty;

    /// <summary>The two segment icon files (Light gray, Dark white), shown by theme.</summary>
    public static IHtmlContent SegmentIcon(string? code) =>
        SegmentOf(code) is { } s
            ? new HtmlString($"<img class=\"seg-icon logo-light\" src=\"/img/brand/{s.IconBase}-gray.svg\" alt=\"\" width=\"48\" height=\"48\"><img class=\"seg-icon logo-dark\" src=\"/img/brand/{s.IconBase}-white.svg\" alt=\"\" width=\"48\" height=\"48\">")
            : HtmlString.Empty;

    /// <summary>Rings ornament (D6): gray in Light, white in Dark; decorative.</summary>
    public static IHtmlContent Ornament() =>
        new HtmlString("<span class=\"ornament\" aria-hidden=\"true\"><img class=\"logo-light\" src=\"/img/brand/dafa-rings-gray.svg\" alt=\"\" width=\"200\" height=\"200\"><img class=\"logo-dark\" src=\"/img/brand/dafa-rings-white.svg\" alt=\"\" width=\"200\" height=\"200\"></span>");

    public static string ToHtml(this IHtmlContent c)
    {
        using var w = new StringWriter(CultureInfo.InvariantCulture);
        c.WriteTo(w, HtmlEncoder.Default);
        return w.ToString();
    }

    /// <summary>Server-rendered bar for a share (0-100): geometry attributes only, so the CSP is fine.</summary>
    public static IHtmlContent Bar(decimal percent)
    {
        var p = Math.Clamp(percent, 0m, 100m).ToString("0.##", CultureInfo.InvariantCulture);
        return new HtmlString($"<svg class=\"bar\" viewBox=\"0 0 100 8\" preserveAspectRatio=\"none\" aria-hidden=\"true\"><rect width=\"{p}\" height=\"8\"/></svg>");
    }
}
