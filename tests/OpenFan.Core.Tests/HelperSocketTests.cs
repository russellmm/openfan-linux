using System.Net.Sockets;
using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

/// <summary>End-to-end: real Unix socket, real client, fake actuator standing in for NVML.</summary>
public class HelperSocketTests
{
    private const string FanId = "nvml:GPU-abc-1234:fan:1";

    private static string TempSocketPath() =>
        Path.Combine(Path.GetTempPath(), $"openfan-test-{Guid.NewGuid():N}.sock");

    [Fact]
    public async Task Client_roundtrips_ping_set_default_over_socket()
    {
        var actuator = new RecordingActuator();
        var server = new HelperServer(actuator, TempSocketPath());
        server.Start();
        try
        {
            using var client = new NvmlHelperClient(server.SocketPath);

            client.Ping().Should().BeTrue();
            client.SetPercent(FanId, 55).Should().BeTrue();
            actuator.Sets.Should().Equal((FanId, 55));

            client.SetDefault(FanId).Should().BeTrue();
            actuator.Restores.Should().Equal(FanId);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Client_reports_missing_helper_without_throwing()
    {
        var client = new NvmlHelperClient(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.sock"));

        client.SetPercent(FanId, 40).Should().BeFalse();
        client.LastError.Should().Contain("helper");
    }

    [Fact]
    public async Task Client_disconnection_restores_owned_fans_server_side()
    {
        var actuator = new RecordingActuator();
        var server = new HelperServer(actuator, TempSocketPath());
        server.Start();
        try
        {
            // Raw client: set a fan, then hard-close the socket (simulates killed GUI).
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(server.SocketPath));
            using (socket)
            {
                var stream = new NetworkStream(socket, ownsSocket: false);
                using var writer = new StreamWriter(stream) { AutoFlush = true };
                using var reader = new StreamReader(stream);
                writer.WriteLine($"set {FanId} 70");
                (await reader.ReadLineAsync()).Should().Be("ok");
            } // socket disposed — EOF at the helper

            // Server restores asynchronously on EOF; poll briefly.
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && actuator.Restores.Count == 0)
                await Task.Delay(25);

            actuator.Restores.Should().Equal(FanId);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Protocol_error_surfaces_to_client_and_keeps_connection_usable()
    {
        var actuator = new RecordingActuator();
        var server = new HelperServer(actuator, TempSocketPath());
        server.Start();
        try
        {
            using var client = new NvmlHelperClient(server.SocketPath);

            client.SetPercent("hwmon:nct6799:10:pwm:4", 50).Should().BeFalse();
            client.LastError.Should().Contain("nvml");

            // Persistent connection survived the rejected command:
            client.Ping().Should().BeTrue();
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
}
