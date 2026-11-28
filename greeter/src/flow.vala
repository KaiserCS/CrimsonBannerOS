// SPDX-License-Identifier: GPL-3.0-or-later
namespace CrimsonLogin {

public enum StepKind { QUESTION, AUTHENTICATED, FAILED, STARTED }

/// Where signing in has got to.
public class FlowStep : Object {
    public StepKind kind;
    public string? message;
    public bool secret;
    public string[] notes;

    public FlowStep (StepKind kind, string? message = null, bool secret = true, string[] notes = {}) {
        this.kind = kind;
        this.message = message;
        this.secret = secret;
        this.notes = notes;
    }
}

/// Signing in through greetd, without the window: start a session for someone, answer each question PAM asks
/// (password, a second factor, an expired password's new one), pass on its messages, then start the desktop.
/// After a refusal greetd's session is cancelled, so the next try starts cleanly.
public class Flow : Object {
    private Greetd greetd;
    private bool open;

    public Flow (Greetd greetd) {
        this.greetd = greetd;
    }

    public async FlowStep sign_in (string user) throws Error {
        if (open) yield cancel ();
        open = true;
        return yield follow (yield greetd.create_session (user));
    }

    public async FlowStep answer (string text) throws Error {
        return yield follow (yield greetd.respond (text));
    }

    public async FlowStep start (string[] cmd, string[] env) throws Error {
        var r = yield greetd.start_session (cmd, env);
        if (r.is_success) { open = false; return new FlowStep (StepKind.STARTED); }
        yield cancel ();
        return new FlowStep (StepKind.FAILED, "The desktop couldn't be started: " + (r.description ?? "greetd said no"));
    }

    public async void cancel () {
        if (!open) return;
        open = false;
        try { yield greetd.cancel_session (); } catch (Error e) { /* nothing left to cancel */ }
    }

    private async FlowStep follow (GreetdReply first) throws Error {
        var r = first;
        string[] notes = {};
        // informational messages ("Your password expires in 3 days") only need acknowledging
        for (int guard = 0; r.is_question && (r.auth_message_type == "info" || r.auth_message_type == "error") && guard < 20; guard++) {
            if (r.auth_message != null && r.auth_message.strip () != "") notes += r.auth_message.strip ();
            r = yield greetd.respond (null);
        }
        if (r.is_question)
            return new FlowStep (StepKind.QUESTION, prompt (r.auth_message, r.auth_message_type == "secret"),
                                 r.auth_message_type != "visible", notes);
        if (r.is_success) return new FlowStep (StepKind.AUTHENTICATED, null, true, notes);
        yield cancel ();
        var why = r.is_auth_error ? "That password didn't work. Try again."
                                  : "Signing in didn't work: " + (r.description ?? "unknown error");
        return new FlowStep (StepKind.FAILED, why, true, notes);
    }

    /// PAM's prompt as a placeholder: "Password: " becomes "Password"; an empty one means the password.
    public static string prompt (string? text, bool secret) {
        var t = (text ?? "").strip ();
        while (t.has_suffix (":")) t = t.substring (0, t.length - 1).strip ();
        return t.length > 0 ? t : (secret ? "Password" : "Answer");
    }
}

}
