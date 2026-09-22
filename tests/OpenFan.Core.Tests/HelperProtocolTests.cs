using OpenFan.Core.ControlLoop;
using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

public class HelperProtocolTests
{
    private const string FanId = "nvml:GPU-abc-1234:fan:0";

    [Fact]
    public void Ping_returns_ok()
        => new HelperSession(new RecordingActuator()).HandleLine("ping").Should().Be("ok");

    [Fact]
    public void Set_routes_nvml_fan_to_actuator()
    {
        var actuator = new RecordingActuator();
        var session = new HelperSession(actuator);

        session.HandleLine($"set {FanId} 55").Should().Be("ok");
        actuator.Sets.Should().Equal((FanId, 55));
    }

    [Fact]
    public void Set_rejects_hwmon_ids_acl_path_instead()
    {
        var actuator = new RecordingActuator();
        var session = new HelperSession(actuator);

        session.HandleLine("set hwmon:nct6799:10:pwm:4 55").Should().StartWith("err");
        actuator.Sets.Should().BeEmpty();
    }

    [Theory]
    [InlineData("set nvml:GPU-1:fan:0 abc")]
    [InlineData("set nvml:GPU-1:fan:0 -5")]
    [InlineData("set nvml:GPU-1:fan:0 101")]
    [InlineData("set nvml:GPU-1:fan:0")]
    [InlineData("frobnicate xyz")]
    [InlineData("default hwmon:nct6799:10:pwm:4")]
    public void Malformed_or_out_of_range_input_is_rejected(string line)
    {
        var actuator = new RecordingActuator();
        new HelperSession(actuator).HandleLine(line).Should().StartWith("err");
        actuator.Sets.Should().BeEmpty();
    }

    [Fact]
    public void Disconnect_restores_only_what_the_session_set()
    {
        var actuator = new RecordingActuator();
        var session = new HelperSession(actuator);

        session.HandleLine($"set {FanId} 60");
        session.RestoreOwned();

        actuator.Restores.Should().Equal(FanId);
    }

    [Fact]
    public void Explicit_default_clears_ownership_so_disconnect_is_quiet()
    {
        var actuator = new RecordingActuator();
        var session = new HelperSession(actuator);

        session.HandleLine($"set {FanId} 60");
        session.HandleLine($"default {FanId}").Should().Be("ok");
        session.RestoreOwned(); // GUI already restored — no second restore on EOF

        actuator.Restores.Should().Equal(FanId); // exactly the explicit one
    }

    [Fact]
    public void Failed_set_claims_no_ownership()
    {
        var actuator = new RecordingActuator { FailNextSet = true };
        var session = new HelperSession(actuator);

        session.HandleLine($"set {FanId} 60").Should().StartWith("err");
        session.RestoreOwned();

        actuator.Restores.Should().BeEmpty();
    }
}
