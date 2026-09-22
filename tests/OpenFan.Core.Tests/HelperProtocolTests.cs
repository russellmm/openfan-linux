using OpenFan.Core.ControlLoop;
using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

public class HelperProtocolTests
{

    // ---- power command (GPU page) ----

    private sealed class RecordingPowerWriter : IGpuPowerWriter
    {
        public List<(string Uuid, int Watts)> Calls { get; } = [];
        public bool Result { get; set; } = true;
        public bool SetPowerLimit(string uuid, int watts)
        {
            Calls.Add((uuid, watts));
            return Result;
        }
    }

    [Fact]
    public void Power_routes_uuid_and_watts_to_writer()
    {
        var writer = new RecordingPowerWriter();
        var session = new HelperSession(new RecordingActuator(), writer);

        session.HandleLine("power GPU-6d3eab54-984e-f1bc-68eb-ee4948e6c3b0 250").Should().Be("ok");
        writer.Calls.Should().Equal(("GPU-6d3eab54-984e-f1bc-68eb-ee4948e6c3b0", 250));
    }

    [Fact]
    public void Power_without_writer_is_rejected()
        => new HelperSession(new RecordingActuator()).HandleLine("power GPU-abcdef12 250").Should().StartWith("err");

    [Theory]
    [InlineData("power notauuid 250")]
    [InlineData("power GPU-ab 250")]                  // uuid too short
    [InlineData("power GPU-abcdef12 0")]
    [InlineData("power GPU-abcdef12 5000")]
    [InlineData("power GPU-abcdef12 lotsa")]
    [InlineData("power GPU-abcdef12")]
    public void Power_rejects_malformed_input(string line)
    {
        var writer = new RecordingPowerWriter();
        var session = new HelperSession(new RecordingActuator(), writer);
        session.HandleLine(line).Should().StartWith("err");
        writer.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Power_writer_failure_surfaces_as_error()
    {
        var writer = new RecordingPowerWriter { Result = false };
        var session = new HelperSession(new RecordingActuator(), writer);
        session.HandleLine("power GPU-abcdef12 250").Should().StartWith("err");
    }

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
