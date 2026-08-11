using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ServiceStatusJsonTests {
    [Test]
    public async Task Renders_snake_case_full_payload() {
        var q = new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/u/kcap-daemon", 42);
        var (json, exit) = ServiceStatusRender.Render(q, "default", "/i/kcap-daemon", 42, false, false);
        await Assert.That(exit).IsEqualTo(0);
        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        await Assert.That(r.GetProperty("service_id").GetString()).IsEqualTo("default");
        await Assert.That(r.GetProperty("unit_present").GetBoolean()).IsTrue();
        await Assert.That(r.GetProperty("state").GetString()).IsEqualTo("running");
        await Assert.That(r.GetProperty("binary_path").GetString()).IsEqualTo("/u/kcap-daemon");
        await Assert.That(r.GetProperty("install_binary_path").GetString()).IsEqualTo("/i/kcap-daemon");
        await Assert.That(r.GetProperty("job_pid").GetInt32()).IsEqualTo(42);
        await Assert.That(r.GetProperty("daemon_pid").GetInt32()).IsEqualTo(42);
        await Assert.That(r.GetProperty("txn_active").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Unknown_probe_fails_nonzero() {
        var q = new ServiceQuery(LabelProbe.Unknown, true, ServiceState.NotInstalled, "/u/kcap-daemon", null);
        var (json, exit) = ServiceStatusRender.Render(q, "default", null, null, false, false);
        await Assert.That(json).IsNull();
        await Assert.That(exit).IsEqualTo(1);
    }

    [Test]
    public async Task Present_but_unloaded_reports_not_installed_with_unit_present() {
        var q = new ServiceQuery(LabelProbe.Absent, true, ServiceState.NotInstalled, "/u/kcap-daemon", null);
        var (json, _) = ServiceStatusRender.Render(q, "d", null, null, false, false);
        using var doc = JsonDocument.Parse(json!);
        await Assert.That(doc.RootElement.GetProperty("state").GetString()).IsEqualTo("not_installed");
        await Assert.That(doc.RootElement.GetProperty("unit_present").GetBoolean()).IsTrue();
    }
}
