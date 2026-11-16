using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MyCalendar;

/// <summary>紙の手帳のような配色。スマホ版と同じ色。</summary>
public class Palette
{
    public bool Dark;
    public SolidColorBrush Paper = null!, Sheet = null!, Ink = null!, Muted = null!, Line = null!,
        Accent = null!, AccentInk = null!, Sun = null!, Sat = null!, Soft = null!;

    public static Palette Light => new()
    {
        Dark = false,
        Paper = B("#f6f5f1"), Sheet = B("#ffffff"), Ink = B("#1f2633"), Muted = B("#6b7280"), Line = B("#e3e1da"),
        Accent = B("#d9452b"), AccentInk = B("#ffffff"), Sun = B("#c8372d"), Sat = B("#2f5fb3"), Soft = B("#efede6"),
    };

    public static Palette DarkP => new()
    {
        Dark = true,
        Paper = B("#16181d"), Sheet = B("#1e2128"), Ink = B("#e8e6e1"), Muted = B("#9aa0aa"), Line = B("#2e323b"),
        Accent = B("#ef6a4f"), AccentInk = B("#1a1a1a"), Sun = B("#f07a6c"), Sat = B("#7fa6ee"), Soft = B("#262a32"),
    };

    public static Palette For(string theme) =>
        theme == "dark" || (theme == "system" && SystemIsDark()) ? DarkP : Light;

    public static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static Color C(string? hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(string.IsNullOrEmpty(hex) ? "#3b6fd4" : hex); }
        catch { return Color.FromRgb(0x3b, 0x6f, 0xd4); }
    }

    public static SolidColorBrush B(string hex) { var b = new SolidColorBrush(C(hex)); b.Freeze(); return b; }
    public static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>予定の色を背景に薄く混ぜた色</summary>
    public SolidColorBrush Tint(string? hex, double t = 0.18) => B(Mix(Sheet.Color, C(hex), t));
}

public static class UI
{
    public static readonly FontFamily Body = new("Yu Gothic UI, Meiryo UI, Segoe UI");
    public static readonly FontFamily Num = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>ウィンドウ上部のタイトルバーをテーマに合わせる（Windows 10 20H1以降）</summary>
    public static void TitleBar(Window w, bool dark)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            int v = dark ? 1 : 0;
            if (DwmSetWindowAttribute(h, 20, ref v, 4) != 0) DwmSetWindowAttribute(h, 19, ref v, 4);
        }
        catch { }
    }

    static ControlTemplate? template;
    static ControlTemplate ButtonTemplate()
    {
        if (template != null) return template;
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        t.Seal();
        return template = t;
    }

    public enum Kind { Normal, Primary, Danger, Accent }

    public static Button Btn(Palette p, object content, Action onClick, Kind kind = Kind.Normal, string? tip = null)
    {
        var b = new Button
        {
            Content = content,
            Template = ButtonTemplate(),
            Padding = new Thickness(14, 7, 14, 7),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            FontWeight = FontWeights.SemiBold,
            MinHeight = 34,
            ToolTip = tip,
        };
        switch (kind)
        {
            case Kind.Primary: b.Background = p.Ink; b.Foreground = p.Paper; b.BorderBrush = p.Ink; break;
            case Kind.Accent: b.Background = p.Accent; b.Foreground = p.AccentInk; b.BorderBrush = p.Accent; break;
            case Kind.Danger: b.Background = p.Sheet; b.Foreground = p.Sun; b.BorderBrush = p.Line; break;
            default: b.Background = p.Sheet; b.Foreground = p.Ink; b.BorderBrush = p.Line; break;
        }
        b.Click += (_, _) => onClick();
        b.MouseEnter += (_, _) => b.Opacity = 0.82;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        if (tip != null) AutomationName(b, tip);
        return b;
    }

    public static Button IconBtn(Palette p, string glyph, Action onClick, string tip)
    {
        var b = Btn(p, new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14 }, onClick, Kind.Normal, tip);
        b.Padding = new Thickness(0);
        b.Width = 36; b.Height = 36;
        return b;
    }

    public static void AutomationName(DependencyObject o, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(o, name);

    public static TextBlock Label(Palette p, string text) =>
        new() { Text = text, Foreground = p.Muted, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4) };

    public static T Styled<T>(Palette p, T c) where T : Control
    {
        c.Background = p.Paper;
        c.Foreground = p.Ink;
        c.BorderBrush = p.Line;
        c.BorderThickness = new Thickness(1);
        c.Padding = new Thickness(8, 6, 8, 6);
        c.FontSize = 14;
        return c;
    }

    public static StackPanel Field(Palette p, string label, UIElement control, double bottom = 12)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, bottom) };
        sp.Children.Add(Label(p, label));
        sp.Children.Add(control);
        return sp;
    }

    /// <summary>スマホ版と同じ色見本</summary>
    public static readonly string[] Colors = { "#3b6fd4", "#d9452b", "#2e9d5b", "#d99a1e", "#8a55c9", "#1a9aa8", "#c24f8e", "#6b7280" };
}

/// <summary>ダイアログ共通の土台</summary>
public class BaseDialog : Window
{
    protected readonly Palette P;
    public BaseDialog(Window owner, Palette p, string title, double width)
    {
        P = p;
        Owner = owner;
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = p.Sheet;
        Foreground = p.Ink;
        FontFamily = UI.Body;
        FontSize = 14;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SourceInitialized += (_, _) => UI.TitleBar(this, p.Dark);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
