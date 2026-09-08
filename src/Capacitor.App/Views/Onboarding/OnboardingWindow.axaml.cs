using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using Capacitor.App.ViewModels.Onboarding;
using ReactiveUI.Reactive;
using ReactiveUI.Avalonia.Reactive;

namespace Capacitor.App.Views.Onboarding;

public partial class OnboardingWindow : ReactiveWindow<OnboardingViewModel> {
    // Guards the Close()<->Closing round trip below from re-entering itself either way.
    bool _closingProgrammatically;

    public OnboardingWindow() {
        InitializeComponent();

        this.WhenActivated(disposables => {
            if (ViewModel is null) return;

            void OnCloseRequested() {
                if (_closingProgrammatically) return;
                _closingProgrammatically = true;
                Close();
            }

            ViewModel.CloseRequested += OnCloseRequested;
            Disposable.Create(() => ViewModel.CloseRequested -= OnCloseRequested).DisposeWith(disposables);
        });

        // Never cancelled — the shell never blocks close, it only notifies the ViewModel.
        Closing += (_, _) => {
            _closingProgrammatically = true;
            ViewModel?.RequestClose();
        };
    }
}
