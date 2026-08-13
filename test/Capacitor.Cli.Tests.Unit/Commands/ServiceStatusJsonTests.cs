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

    [Test]
    public async Task Status_json_carries_unit_fields_snake_cased() {
        var json = JsonSerializer.Serialize(
            new ServiceStatusJson("svc", true, "installed", "/b", "/b", null, null, false, false,
                UnitProfile: "acme", UnitServerUrl: "https://s", UnitExpectedServer: "https://s",
                UnitConsentSeed: "prompt"),
            ServiceJsonContext.Default.ServiceStatusJson);

        await Assert.That(json).Contains("\"unit_profile\":\"acme\"");
        await Assert.That(json).Contains("\"unit_consent_seed\":\"prompt\"");
        await Assert.That(json).Contains("\"unit_expected_server\":\"https://s\"");
    }

    [Test]
    public async Task Render_carries_unit_evidence_when_supplied() {
        var q = new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/u/kcap-daemon", 42);
        var (json, _) = ServiceStatusRender.Render(q, "default", "/i/kcap-daemon", 42, false, false,
            unitProfile: "acme", unitServerUrl: "https://s", unitExpectedServer: "https://s", unitConsentSeed: "prompt");
        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        await Assert.That(r.GetProperty("unit_profile").GetString()).IsEqualTo("acme");
        await Assert.That(r.GetProperty("unit_server_url").GetString()).IsEqualTo("https://s");
        await Assert.That(r.GetProperty("unit_expected_server").GetString()).IsEqualTo("https://s");
        await Assert.That(r.GetProperty("unit_consent_seed").GetString()).IsEqualTo("prompt");
    }

    [Test]
    public async Task Render_writes_null_unit_evidence_when_not_supplied() {
        var q = new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/u/kcap-daemon", 42);
        var (json, _) = ServiceStatusRender.Render(q, "default", "/i/kcap-daemon", 42, false, false);
        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        await Assert.That(r.GetProperty("unit_profile").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(r.GetProperty("unit_server_url").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(r.GetProperty("unit_expected_server").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(r.GetProperty("unit_consent_seed").ValueKind).IsEqualTo(JsonValueKind.Null);
    }
}
