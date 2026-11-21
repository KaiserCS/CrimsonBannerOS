// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RedStarOS.Services.Greeter;

/// <summary>Where signing in has got to.</summary>
public sealed record FlowStep(FlowStep.Kinds Kind, string? Message = null, bool Secret = true, IReadOnlyList<string>? Notes = null)
{
    public enum Kinds { Question, Authenticated, Failed, Started }
}

/// <summary>
/// Signing in through greetd, without the window: start a session for someone, answer each question PAM asks
/// (password, a second factor, an expired password's new one), pass on its messages, then start the desktop.
/// After a refusal greetd's session is cancelled, so the next try starts cleanly.
/// </summary>
public sealed class GreeterFlow
{
    private readonly IGreetd _greetd;
    private bool _open;

    public GreeterFlow(IGreetd greetd) => _greetd = greetd;

    public async Task<FlowStep> BeginAsync(string user, CancellationToken ct = default)
    {
        if (_open) await CancelAsync(ct);
        _open = true;
        return await Follow(await _greetd.CreateSessionAsync(user, ct), ct);
    }

    public async Task<FlowStep> AnswerAsync(string answer, CancellationToken ct = default) =>
        await Follow(await _greetd.RespondAsync(answer, ct), ct);

    public async Task<FlowStep> StartAsync(IReadOnlyList<string> cmd, IReadOnlyList<string> env, CancellationToken ct = default)
    {
        var r = await _greetd.StartSessionAsync(cmd, env, ct);
        if (r.IsSuccess) { _open = false; return new FlowStep(FlowStep.Kinds.Started); }
        await CancelAsync(ct);
        return new FlowStep(FlowStep.Kinds.Failed, "The desktop couldn't be started: " + (r.Description ?? "greetd said no"));
    }

    public async Task CancelAsync(CancellationToken ct = default)
    {
        if (!_open) return;
        _open = false;
        try { await _greetd.CancelSessionAsync(ct); } catch (Exception) { /* nothing left to cancel */ }
    }

    private async Task<FlowStep> Follow(GreetdReply r, CancellationToken ct)
    {
        var notes = new List<string>();
        // informational messages ("Your password expires in 3 days") only need acknowledging
        for (int guard = 0; r.IsQuestion && r.AuthMessageType is "info" or "error" && guard < 20; guard++)
        {
            if (!string.IsNullOrWhiteSpace(r.AuthMessage)) notes.Add(r.AuthMessage!.Trim());
            r = await _greetd.RespondAsync(null, ct);
        }
        if (r.IsQuestion)
            return new FlowStep(FlowStep.Kinds.Question, Prompt(r.AuthMessage, r.AuthMessageType == "secret"), r.AuthMessageType != "visible", notes);
        if (r.IsSuccess) return new FlowStep(FlowStep.Kinds.Authenticated, null, true, notes);
        await CancelAsync(ct);
        var why = r.IsAuthError ? "That password didn't work. Try again." : "Signing in didn't work: " + (r.Description ?? "unknown error");
        return new FlowStep(FlowStep.Kinds.Failed, why, true, notes);
    }

    /// <summary>PAM's prompt as a placeholder: "Password: " → "Password"; an empty one means the password.</summary>
    public static string Prompt(string? text, bool secret)
    {
        var t = (text ?? "").Trim().TrimEnd(':').Trim();
        return t.Length > 0 ? t : secret ? "Password" : "Answer";
    }
}
