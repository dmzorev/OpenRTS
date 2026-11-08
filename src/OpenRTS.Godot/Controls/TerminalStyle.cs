using Godot;

namespace OpenRTS.Godot.Controls;

public static class TerminalStyle
{
    private static readonly Color TerminalInk = new("#dce6c8");
    private static readonly Color TerminalAmber = new("#efbb60");
    private static readonly Color TerminalOlive = new("#59654b");

    public static StyleBoxFlat Box(Color background, Color border, int padding = 6) => new()
    {
        BgColor = background, BorderColor = border,
        BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
        CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        ContentMarginLeft = padding, ContentMarginRight = padding, ContentMarginTop = padding, ContentMarginBottom = padding
    };

    public static Theme CreateTheme()
    {
        var theme = new Theme { DefaultFontSize = 13 };
        theme.SetStylebox("panel", "PanelContainer", Box(new("#131911"), TerminalOlive, 8));
        theme.SetStylebox("normal", "Button", Box(new("#21291c"), TerminalOlive));
        theme.SetStylebox("hover", "Button", Box(new("#353d26"), TerminalInk));
        theme.SetStylebox("pressed", "Button", Box(new("#393220"), TerminalAmber));
        theme.SetStylebox("disabled", "Button", Box(new("#181d16"), new("#343e2f")));
        theme.SetColor("font_color", "Button", TerminalInk);
        theme.SetColor("font_disabled_color", "Button", new("#85907a"));
        theme.SetColor("font_color", "Label", TerminalInk);
        theme.SetStylebox("background", "ProgressBar", Box(new("#0d130d"), new("#394733"), 0));
        theme.SetStylebox("fill", "ProgressBar", Box(new("#80a653"), new("#80a653"), 0));
        return theme;
    }

    public static Label Heading(string text)
    {
        var heading = new Label { Text = text };
        heading.AddThemeColorOverride("font_color", TerminalAmber);
        heading.AddThemeFontSizeOverride("font_size", 13);
        return heading;
    }
}
