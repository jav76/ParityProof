using System;
using ParityProof.App.ViewModels;
using ParityProof.Core.Models;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class BuildInfoUiTests
{
    [Fact]
    public void MainViewModel_ExposesBuildInfoAndWindowTitle()
    {
        using MainViewModel vm = new();

        Assert.NotNull(vm.AppVersionDisplay);
        Assert.NotEmpty(vm.AppVersionDisplay);
        Assert.StartsWith("ParityProof v", vm.AppVersionDisplay);

        Assert.NotNull(vm.WindowTitle);
        Assert.StartsWith("ParityProof - ParityProof v", vm.WindowTitle);

        Assert.NotNull(vm.BuildCommitSha);
        Assert.NotEmpty(vm.BuildCommitSha);

        Assert.NotNull(vm.BuildSemVer);
        Assert.NotEmpty(vm.BuildSemVer);

        Assert.NotNull(vm.BuildConfiguration);
        Assert.True(vm.BuildConfiguration == "Debug" || vm.BuildConfiguration == "Release");
    }
}
