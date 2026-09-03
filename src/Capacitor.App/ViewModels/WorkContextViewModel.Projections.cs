using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.ViewModels;

public sealed partial class WorkContextViewModel {
    static void InitializeProjections() { }

    static void UpdateRequester(AgentStatusDto dto, string vendorLabel) { }

    static void ClearServerProjections() { }

    void ApplyReady(WorkContextRead read) {
        Phase = read.Primary is null ? WorkContextPhase.NoWorkItem : WorkContextPhase.Ready;
        IsStale = read.TopologyFailed || read.SummaryFailed;
    }
}
