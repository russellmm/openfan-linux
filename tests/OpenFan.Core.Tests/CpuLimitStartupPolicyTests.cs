using FluentAssertions;
using OpenFan.Core.Config;

namespace OpenFan.Core.Tests;

/// <summary>
/// The bug this prevents: a saved CPU limit was re-applied whenever the app started, regardless of
/// "keep after reboot", so an unchecked box still produced a limit that survived reboot.
/// </summary>
public sealed class CpuLimitStartupPolicyTests
{
    [Fact]
    public void NothingIsReappliedWhenTheUserDidNotAskForPersistence()
    {
        new AppSettings { CpuPowerLimitW = 280, CpuKeepAfterReboot = false }
            .CpuLimitShouldReassertOnStart.Should().BeFalse();
    }

    [Fact]
    public void SavedLimitIsReappliedOnlyWhenPersistenceWasRequested()
        => new AppSettings { CpuPowerLimitW = 280, CpuKeepAfterReboot = true }
            .CpuLimitShouldReassertOnStart.Should().BeTrue();

    [Fact]
    public void NoSavedLimitMeansNothingToReapply()
        => new AppSettings { CpuPowerLimitW = null, CpuKeepAfterReboot = true }
            .CpuLimitShouldReassertOnStart.Should().BeFalse();

    /// <summary>Round-trips through the real store: the policy must survive save/load, or a reboot
    /// reads stale intent.</summary>
    [Fact]
    public void PolicySurvivesSaveAndLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openfan-cpupolicy-{Guid.NewGuid():N}.json");
        try
        {
            new SettingsStore(path).Save(new AppSettings { CpuPowerLimitW = 280, CpuKeepAfterReboot = false });
            var loaded = new SettingsStore(path).Load();
            loaded.CpuPowerLimitW.Should().Be(280);
            loaded.CpuKeepAfterReboot.Should().BeFalse();
            loaded.CpuLimitShouldReassertOnStart.Should().BeFalse();
        }
        finally { File.Delete(path); }
    }
}
