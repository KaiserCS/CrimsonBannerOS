// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace RedStarOS.Services.Matrix;

public enum MessageKind { Text, Notice, Emote, Image, File, Video, Audio, Encrypted, Deleted, Membership }

/// <summary>One line of a room's conversation.</summary>
public sealed class MatrixMessage
{
    public string EventId { get; set; } = "";
    public string Sender { get; init; } = "";
    public DateTimeOffset Time { get; init; }
    public MessageKind Kind { get; set; }
    public string Body { get; set; } = "";
    public string? Mxc { get; set; }
    public string? FileName { get; set; }
    public long Size { get; set; }
    public bool Edited { get; set; }
    /// <summary>Sent from here, not yet confirmed by the server (its transaction id), or failed.</summary>
    public string? PendingTxn { get; set; }
    public bool Failed { get; set; }
}

/// <summary>A room (a group chat, or a direct chat with one person) as far as sync has told us.</summary>
public sealed class MatrixRoom
{
    public string Id { get; init; } = "";
    public string? Name { get; set; }
    public string? Topic { get; set; }
    public string? Alias { get; set; }
    public string? AvatarMxc { get; set; }
    public bool Encrypted { get; set; }
    public bool Invite { get; set; }
    public string? Inviter { get; set; }
    public bool IsDirect { get; set; }
    public int Unread { get; set; }
    public int Highlights { get; set; }
    public int JoinedCount { get; set; }
    public List<string> Heroes { get; set; } = new();
    public Dictionary<string, (string Name, string Membership)> Members { get; } = new();
    public List<MatrixMessage> Timeline { get; } = new();
    public string? PrevBatch { get; set; }
    public bool HasOlder { get; set; } = true;
    public List<string> Typing { get; set; } = new();

    public DateTimeOffset LastActivity => Timeline.Count > 0 ? Timeline[^1].Time : DateTimeOffset.MinValue;

    /// <summary>A person's display name in this room, else the local part of their id.</summary>
    public string NameOf(string userId) =>
        Members.TryGetValue(userId, out var m) && m.Name.Length > 0 ? m.Name : userId.TrimStart('@').Split(':')[0];

    /// <summary>The room's name as the spec says to work it out: its name, its alias, else the people in it.</summary>
    public string DisplayName(string me)
    {
        if (!string.IsNullOrWhiteSpace(Name)) return Name!;
        if (!string.IsNullOrWhiteSpace(Alias)) return Alias!;
        var others = (Heroes.Count > 0 ? Heroes : Members.Where(m => m.Value.Membership is "join" or "invite").Select(m => m.Key).ToList())
            .Where(u => u != me).Distinct().ToList();
        if (others.Count == 0) return Invite && Inviter != null ? NameOf(Inviter) : "Empty room";
        var names = others.Take(3).Select(NameOf).ToList();
        int more = Math.Max(0, (JoinedCount > 0 ? JoinedCount - 1 : others.Count) - names.Count);
        return more > 0 ? string.Join(", ", names) + $" and {more} other{(more == 1 ? "" : "s")}"
            : names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }

    /// <summary>The last message, for the room list.</summary>
    public string Preview(string me)
    {
        var m = Timeline.LastOrDefault(x => x.Kind != MessageKind.Membership);
        if (m == null) return Invite ? "Invited you" : "";
        var who = m.Sender == me ? "You" : NameOf(m.Sender);
        return who + ": " + MatrixState.Summary(m);
    }
}

/// <summary>
/// Everything the client knows, built from /sync responses and back-pagination (spec.matrix.org client-server API).
/// Kept separate from the network code so it can be tested with plain JSON.
/// </summary>
public sealed class MatrixState
{
    public Dictionary<string, MatrixRoom> Rooms { get; } = new();
    public string? Since { get; set; }
    public string Me { get; }
    public MatrixState(string me) { Me = me; }

    /// <summary>A message arrived that may deserve a notification (the room's unread count went up).</summary>
    public sealed record Arrival(MatrixRoom Room, MatrixMessage Message);

    public static string Summary(MatrixMessage m) => m.Kind switch
    {
        MessageKind.Image => "📷 " + (m.Body.Length > 0 ? m.Body : "Picture"),
        MessageKind.File => "📎 " + (m.FileName ?? m.Body),
        MessageKind.Video => "🎞 " + (m.FileName ?? m.Body),
        MessageKind.Audio => "🔊 " + (m.FileName ?? m.Body),
        MessageKind.Encrypted => "🔒 Encrypted message",
        MessageKind.Deleted => "Message deleted",
        _ => m.Body.Split('\n')[0],
    };

    private static string? S(JsonNode? n) { try { return n?.GetValue<string>(); } catch { return null; } }
    private static long L(JsonNode? n) { try { return n?.GetValue<long>() ?? 0; } catch { return 0; } }
    private static int I(JsonNode? n) { try { return n?.GetValue<int>() ?? 0; } catch { return 0; } }

    /// <summary>A reply's body without the quoted message the spec puts before it ("> &lt;@x&gt; …" lines).</summary>
    public static string StripReplyFallback(string body)
    {
        if (!body.StartsWith("> ")) return body;
        var lines = body.Split('\n');
        int i = 0;
        while (i < lines.Length && lines[i].StartsWith(">")) i++;
        if (i < lines.Length && lines[i].Length == 0) i++;
        return i < lines.Length ? string.Join('\n', lines[i..]) : body;
    }

    private MatrixRoom Room(string id) => Rooms.TryGetValue(id, out var r) ? r : Rooms[id] = new MatrixRoom { Id = id };

    private static void ApplyState(MatrixRoom room, JsonNode ev)
    {
        var type = S(ev["type"]);
        var c = ev["content"];
        switch (type)
        {
            case "m.room.name": room.Name = S(c?["name"]); break;
            case "m.room.topic": room.Topic = S(c?["topic"]); break;
            case "m.room.canonical_alias": room.Alias = S(c?["alias"]); break;
            case "m.room.avatar": room.AvatarMxc = S(c?["url"]); break;
            case "m.room.encryption": room.Encrypted = true; break;
            case "m.room.member":
                if (S(ev["state_key"]) is { } who)
                    room.Members[who] = (S(c?["displayname"]) ?? "", S(c?["membership"]) ?? "");
                break;
        }
    }

    /// <summary>A timeline event as a message; null for events that aren't shown (and edits, applied in place).</summary>
    private MatrixMessage? ToMessage(MatrixRoom room, JsonNode ev)
    {
        var type = S(ev["type"]);
        var c = ev["content"] as JsonObject;
        var time = DateTimeOffset.FromUnixTimeMilliseconds(L(ev["origin_server_ts"]));
        var sender = S(ev["sender"]) ?? "";
        var id = S(ev["event_id"]) ?? "";
        if (ev["unsigned"]?["redacted_because"] != null && type is "m.room.message" or "m.room.encrypted" or "m.sticker")
            return new MatrixMessage { EventId = id, Sender = sender, Time = time, Kind = MessageKind.Deleted };
        switch (type)
        {
            case "m.room.redaction":
            {
                var target = S(ev["redacts"]) ?? S(c?["redacts"]);
                if (room.Timeline.FirstOrDefault(m => m.EventId == target) is { } t) { t.Kind = MessageKind.Deleted; t.Body = ""; t.Mxc = null; }
                return null;
            }
            case "m.room.encrypted":
                return new MatrixMessage { EventId = id, Sender = sender, Time = time, Kind = MessageKind.Encrypted };
            case "m.sticker":
                return new MatrixMessage { EventId = id, Sender = sender, Time = time, Kind = MessageKind.Image, Body = S(c?["body"]) ?? "Sticker", Mxc = S(c?["url"]) };
            case "m.room.message" when c != null:
            {
                // an edit: the new text goes into the message it edits
                if (S(c["m.relates_to"]?["rel_type"]) == "m.replace")
                {
                    var target = S(c["m.relates_to"]?["event_id"]);
                    var nc = c["m.new_content"];
                    if (room.Timeline.FirstOrDefault(m => m.EventId == target) is { } t && nc != null && t.Sender == sender)
                    { t.Body = S(nc["body"]) ?? t.Body; t.Edited = true; }
                    return null;
                }
                var msgtype = S(c["msgtype"]);
                var body = S(c["body"]) ?? "";
                var m = new MatrixMessage { EventId = id, Sender = sender, Time = time, Body = StripReplyFallback(body) };
                switch (msgtype)
                {
                    case "m.notice": m.Kind = MessageKind.Notice; break;
                    case "m.emote": m.Kind = MessageKind.Emote; break;
                    case "m.image": m.Kind = MessageKind.Image; break;
                    case "m.file": m.Kind = MessageKind.File; break;
                    case "m.video": m.Kind = MessageKind.Video; break;
                    case "m.audio": m.Kind = MessageKind.Audio; break;
                    default: m.Kind = MessageKind.Text; break;
                }
                if (m.Kind is MessageKind.Image or MessageKind.File or MessageKind.Video or MessageKind.Audio)
                {
                    m.Mxc = S(c["url"]);
                    m.FileName = S(c["filename"]) ?? body;
                    m.Size = L(c["info"]?["size"]);
                    if (m.Mxc == null && c["file"] != null) m.Kind = MessageKind.Encrypted;     // an encrypted attachment
                }
                return m;
            }
            case "m.room.member":
            {
                var who = S(ev["state_key"]) ?? sender;
                var membership = S(c?["membership"]);
                var prev = S(ev["unsigned"]?["prev_content"]?["membership"]);
                var name = S(c?["displayname"]) ?? room.NameOf(who);
                string? text = membership switch
                {
                    "join" when prev != "join" => $"{name} joined",
                    "leave" when who == sender => $"{name} left",
                    "leave" => $"{room.NameOf(sender)} removed {name}",
                    "invite" => $"{room.NameOf(sender)} invited {name}",
                    "ban" => $"{room.NameOf(sender)} banned {name}",
                    _ => null,
                };
                return text == null ? null : new MatrixMessage { EventId = id, Sender = sender, Time = time, Kind = MessageKind.Membership, Body = text };
            }
            case "m.room.name" or "m.room.topic":
            {
                var what = type == "m.room.name" ? $"renamed the room to “{S(c?["name"])}”" : "changed the topic";
                return new MatrixMessage { EventId = id, Sender = sender, Time = time, Kind = MessageKind.Membership, Body = $"{room.NameOf(sender)} {what}" };
            }
        }
        return null;
    }

    private void AddToTimeline(MatrixRoom room, JsonNode ev, List<Arrival>? arrivals)
    {
        // our own message coming back: it replaces the one shown while it was being sent
        var txn = S(ev["unsigned"]?["transaction_id"]);
        if (txn != null && room.Timeline.FirstOrDefault(m => m.PendingTxn == txn) is { } pending)
        {
            pending.EventId = S(ev["event_id"]) ?? pending.EventId;
            pending.PendingTxn = null;
            return;
        }
        if (S(ev["event_id"]) is { } id && room.Timeline.Any(m => m.EventId == id)) return;
        if (ev["state_key"] != null) ApplyState(room, ev);
        if (ToMessage(room, ev) is { } msg)
        {
            room.Timeline.Add(msg);
            if (arrivals != null && msg.Sender != Me && msg.Kind != MessageKind.Membership) arrivals.Add(new Arrival(room, msg));
        }
    }

    /// <summary>Applies one /sync response. Returns the messages from others that came with an unread count going up
    /// (the server's push rules decide what counts, so muted rooms stay quiet). Nothing for the very first sync.</summary>
    public List<Arrival> Apply(JsonNode sync)
    {
        bool initial = Since == null;
        var notify = new List<Arrival>();
        if (sync["account_data"]?["events"] is JsonArray acct)
            foreach (var ev in acct.OfType<JsonNode>())
                if (S(ev["type"]) == "m.direct" && ev["content"] is JsonObject direct)
                {
                    var ids = direct.SelectMany(kv => (kv.Value as JsonArray)?.Select(x => S(x)) ?? Enumerable.Empty<string?>()).OfType<string>().ToHashSet();
                    foreach (var r in Rooms.Values) r.IsDirect = ids.Contains(r.Id);
                    foreach (var id in ids) if (Rooms.TryGetValue(id, out var r)) r.IsDirect = true;
                    _direct = ids;
                }
        if (sync["rooms"]?["join"] is JsonObject join)
            foreach (var (id, node) in join)
            {
                if (node == null) continue;
                var room = Room(id);
                room.Invite = false;
                room.IsDirect |= _direct.Contains(id);
                if (node["summary"] is JsonObject sum)
                {
                    if (sum["m.heroes"] is JsonArray h) room.Heroes = h.Select(x => S(x)).OfType<string>().ToList();
                    if (sum["m.joined_member_count"] != null) room.JoinedCount = I(sum["m.joined_member_count"]);
                }
                if (node["state"]?["events"] is JsonArray st) foreach (var ev in st.OfType<JsonNode>()) ApplyState(room, ev);
                if (node["timeline"] is JsonNode tl)
                {
                    if (tl["limited"]?.GetValue<bool>() == true && !initial) room.Timeline.RemoveAll(m => m.PendingTxn == null);   // a gap: start again
                    if (initial || tl["limited"]?.GetValue<bool>() == true) room.PrevBatch = S(tl["prev_batch"]) ?? room.PrevBatch;
                    var before = room.Unread;
                    var arrived = new List<Arrival>();
                    if (tl["events"] is JsonArray evs) foreach (var ev in evs.OfType<JsonNode>()) AddToTimeline(room, ev, arrived);
                    if (node["unread_notifications"] is JsonNode un)
                    {
                        room.Unread = I(un["notification_count"]);
                        room.Highlights = I(un["highlight_count"]);
                    }
                    if (!initial && room.Unread > before) notify.AddRange(arrived);
                }
                else if (node["unread_notifications"] is JsonNode un2) { room.Unread = I(un2["notification_count"]); room.Highlights = I(un2["highlight_count"]); }
                if (node["ephemeral"]?["events"] is JsonArray eph)
                    foreach (var ev in eph.OfType<JsonNode>())
                        if (S(ev["type"]) == "m.typing")
                            room.Typing = (ev["content"]?["user_ids"] as JsonArray)?.Select(x => S(x)).OfType<string>().Where(u => u != Me).ToList() ?? new();
            }
        if (sync["rooms"]?["invite"] is JsonObject invites)
            foreach (var (id, node) in invites)
            {
                var room = Room(id);
                room.Invite = true;
                if (node?["invite_state"]?["events"] is JsonArray st)
                    foreach (var ev in st.OfType<JsonNode>())
                    {
                        ApplyState(room, ev);
                        if (S(ev["type"]) == "m.room.member" && S(ev["state_key"]) == Me)
                        {
                            room.Inviter = S(ev["sender"]);
                            room.IsDirect = ev["content"]?["is_direct"]?.GetValue<bool>() == true;
                        }
                    }
                if (!initial && room.Inviter != null)
                    notify.Add(new Arrival(room, new MatrixMessage { Sender = room.Inviter, Time = DateTimeOffset.Now, Kind = MessageKind.Notice, Body = "invited you" }));
            }
        if (sync["rooms"]?["leave"] is JsonObject left)
            foreach (var (id, _) in left) Rooms.Remove(id);
        Since = S(sync["next_batch"]) ?? Since;
        return notify;
    }

    private HashSet<string> _direct = new();

    /// <summary>The ids of rooms marked as direct chats.</summary>
    public IReadOnlyCollection<string> DirectRooms => _direct;

    /// <summary>Older messages (GET /messages, newest first) put in front of the timeline.</summary>
    public void ApplyOlder(string roomId, JsonNode page)
    {
        if (!Rooms.TryGetValue(roomId, out var room)) return;
        if (page["state"] is JsonArray st) foreach (var ev in st.OfType<JsonNode>()) ApplyState(room, ev);
        var older = new List<MatrixMessage>();
        var probe = new MatrixRoom { Id = roomId };
        foreach (var (k, v) in room.Members) probe.Members[k] = v;
        if (page["chunk"] is JsonArray chunk)
            foreach (var ev in chunk.OfType<JsonNode>().Reverse())
            {
                if (S(ev["event_id"]) is { } id && room.Timeline.Any(m => m.EventId == id)) continue;
                if (ToMessage(probe, ev) is { } m) { older.Add(m); probe.Timeline.Add(m); }
            }
        room.Timeline.InsertRange(0, older);
        var end = S(page["end"]);
        room.HasOlder = end != null && (page["chunk"] as JsonArray)?.Count > 0;
        room.PrevBatch = end ?? room.PrevBatch;
    }
}
