using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// Assistant prose: a ContentControl whose content is rebuilt from the markdown on every change.
public sealed class MarkdownView : ContentControl {
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Text));

    public static readonly StyledProperty<ICommand?> OpenLinkProperty =
        AvaloniaProperty.Register<MarkdownView, ICommand?>(nameof(OpenLink));

    static MarkdownView() {
        TextProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
        OpenLinkProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
    }

    public string? Text {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ICommand? OpenLink {
        get => GetValue(OpenLinkProperty);
        set => SetValue(OpenLinkProperty, value);
    }

    void Rebuild() => Content = MarkdownBlocks.Build(Text ?? "", OpenLink);
}
