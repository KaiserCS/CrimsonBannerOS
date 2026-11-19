using System;
using System.Collections.Generic;
using Avalonia.Threading;

namespace RedStarOS.Services;

/// <summary>A button on a notification ("Open", "Show in folder"...).</summary>
public sealed record NotificationAction(string Label, Action Run);

public sealed record NotificationRecord(DateTime When, string Title, string Message, string? AppId, IReadOnlyList<NotificationAction>? Actions = null);

/// <summary>
/// Notifications: every message is kept in a history (shown in the tray's notification centre);
/// it also pops up as a toast unless Do Not Disturb is on.
/// </summary>
public static class Notifier
{
    private const int MaxHistory = 50;
    private static readonly List<NotificationRecord> _history = new();
    private static DateTime _lastSound;

    public static IReadOnlyList<NotificationRecord> History => _history;
    public static int Unread { get; private set; }
    public static bool DoNotDisturb { get; private set; }

    /// <summary>A toast should be shown: title, message, app id (may be null).</summary>
    public static event Action<string, string, string?>? Posted;
    /// <summary>Same as <see cref="Posted"/>, with the whole record (including its action buttons).</summary>
    public static event Action<NotificationRecord>? PostedRecord;
    /// <summary>History, unread count or Do Not Disturb changed.</summary>
    public static event Action? Changed;

    public static void Show(string title, string message, string? appId = null, params NotificationAction[] actions) =>
        Dispatcher.UIThread.Post(() =>
        {
            var rec = new NotificationRecord(DateTime.Now, title, message, appId, actions.Length > 0 ? actions : null);
            _history.Insert(0, rec);
            if (_history.Count > MaxHistory) _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
            Unread++;
            Changed?.Invoke();
            if (!DoNotDisturb)
            {
                if ((DateTime.Now - _lastSound).TotalSeconds > 2) { _lastSound = DateTime.Now; SystemSounds.Play("notify"); }
                if (PostedRecord != null) PostedRecord.Invoke(rec);
                else Posted?.Invoke(title, message, appId);
            }
        });

    public static void MarkAllRead()
    {
        if (Unread == 0) return;
        Unread = 0;
        Changed?.Invoke();
    }

    public static void Clear()
    {
        _history.Clear();
        Unread = 0;
        Changed?.Invoke();
    }

    public static void SetDoNotDisturb(bool on)
    {
        if (DoNotDisturb == on) return;
        DoNotDisturb = on;
        Changed?.Invoke();
    }
}
