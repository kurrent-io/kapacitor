using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public partial class PullRequestReader : UserControl {
    PullRequestContextViewModel? _model;
    bool _restoring;
    public PullRequestReader() {
        InitializeComponent();
        DataContextChanged += (_, _) => {
            if (_model is not null) _model.PropertyChanged -= ModelChanged;
            _model = DataContext as PullRequestContextViewModel;
            if (_model is not null) _model.PropertyChanged += ModelChanged;
        };
        ReaderScroll.ScrollChanged += (_, _) => { if (!_restoring && _model?.ShowReaderContent == true) _model.ScrollOffset = ReaderScroll.Offset.Y; };
    }
    void ModelChanged(object? sender, PropertyChangedEventArgs change) {
        if (change.PropertyName is not (nameof(PullRequestContextViewModel.Rows) or nameof(PullRequestContextViewModel.Section))) return;
        var model = _model;
        if (model is null || !model.ShowReaderContent) return;
        var offset = model.ScrollOffset;
        var subject = model.Selected?.Subject;
        var section = model.Section;
        Dispatcher.UIThread.Post(() => {
            if (_model != model || !model.ShowReaderContent || model.Selected?.Subject != subject || model.Section != section) return;
            _restoring = true;
            ReaderScroll.Offset = new Vector(0, offset);
            _restoring = false;
        }, DispatcherPriority.Loaded);
    }
}
