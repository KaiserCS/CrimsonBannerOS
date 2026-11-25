// SPDX-License-Identifier: GPL-3.0-or-later
using System.Threading.Tasks;

namespace RedStarOS.Services;

/// <summary>
/// Crimson Banner OS's update channel: releases, or testing (builds of main before they're released). One pacman
/// repository, [crimson-banner], on two servers; crimson-repo switches between them (packaging/crimson-repo).
/// </summary>
public static class UpdateChannel
{
    public enum State { Unknown, Releases, Testing, Off, OwnBuilds }

    /// <summary>What "crimson-repo channel" printed.</summary>
    public static State Parse(string? output) => (output ?? "").Trim() switch
    {
        "releases" => State.Releases,
        "testing" => State.Testing,
        "disabled" => State.Off,
        "local" => State.OwnBuilds,
        _ => State.Unknown,
    };

    public static async Task<State> CurrentAsync()
    {
        var helper = ProcessUtil.Which("crimson-repo");
        if (helper == null) return State.Unknown;
        var (code, output) = await ProcessUtil.RunAsync(helper, new[] { "channel" });
        return code == 0 ? Parse(output) : State.Unknown;
    }

    /// <summary>Switches through pkexec (the administrator password prompt); null when it worked, else why not.</summary>
    public static async Task<string?> SetAsync(bool testing)
    {
        var helper = ProcessUtil.Which("crimson-repo");
        var pkexec = ProcessUtil.Which("pkexec");
        if (helper == null || pkexec == null) return "crimson-repo or pkexec is missing";
        var (code, output) = await ProcessUtil.RunAsync(pkexec, new[] { helper, "channel", testing ? "testing" : "releases" }, 300000);
        if (code == 0) return null;
        return code is 126 or 127 ? "the administrator password wasn't given" : output.Trim();
    }
}
