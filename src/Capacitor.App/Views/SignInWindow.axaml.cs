using Avalonia.Controls;
using Capacitor.App.ViewModels.Onboarding;

namespace Capacitor.App.Views;

public partial class SignInWindow : Window {
    public SignInWindow() {
        InitializeComponent();
        // The wizard shell calls OnEnterAsync when navigating onto the step; standalone, opening
        // the window IS that navigation, and without it the status line stays blank.
        Opened += (_, _) => _ = (DataContext as SignInStepViewModel)?.OnEnterAsync(CancellationToken.None);
    }
}
