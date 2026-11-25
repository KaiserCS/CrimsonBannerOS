// SPDX-License-Identifier: GPL-3.0-or-later
using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

public class UpdateChannelTests
{
    [Theory]
    [InlineData("releases\n", UpdateChannel.State.Releases)]
    [InlineData("testing\n", UpdateChannel.State.Testing)]
    [InlineData("disabled\n", UpdateChannel.State.Off)]
    [InlineData("local\n", UpdateChannel.State.OwnBuilds)]
    [InlineData("", UpdateChannel.State.Unknown)]
    [InlineData(null, UpdateChannel.State.Unknown)]
    [InlineData("Run it with sudo.", UpdateChannel.State.Unknown)]
    public void Reads_what_crimson_repo_says(string? output, UpdateChannel.State expected)
        => Assert.Equal(expected, UpdateChannel.Parse(output));
}
