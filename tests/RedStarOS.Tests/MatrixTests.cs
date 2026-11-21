// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RedStarOS.Services.Matrix;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>A pretend Matrix homeserver: answers by path, remembers what was asked.</summary>
internal sealed class FakeMatrix : HttpMessageHandler
{
    public readonly List<(string Method, string Path, string Body, string? Auth)> Requests = new();
    public Func<HttpRequestMessage, string, (HttpStatusCode, string)?>? Answer;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var body = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : "";
        lock (Requests) Requests.Add((req.Method.Method, req.RequestUri!.PathAndQuery, body, req.Headers.Authorization?.Parameter));
        var (status, json) = Answer?.Invoke(req, body) ?? (HttpStatusCode.NotFound, "{\"errcode\":\"M_UNRECOGNIZED\",\"error\":\"no\"}");
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

public class MatrixTests
{
    [Fact]
    public async Task The_server_is_found_through_well_known_and_sign_in_keeps_the_token()
    {
        var fake = new FakeMatrix();
        fake.Answer = (req, body) => Uri.UnescapeDataString(req.RequestUri!.AbsolutePath) switch
        {
            "/.well-known/matrix/client" when req.RequestUri.Host == "example.org" => (HttpStatusCode.OK, "{\"m.homeserver\":{\"base_url\":\"https://matrix.example.org/\"}}"),
            "/_matrix/client/v3/login" when req.RequestUri.Host == "matrix.example.org" && body.Contains("\"m.login.password\"") && body.Contains("secret")
                => (HttpStatusCode.OK, "{\"user_id\":\"@ada:example.org\",\"access_token\":\"tok\",\"device_id\":\"DEV\"}"),
            "/_matrix/client/v3/rooms/!r:example.org/send/m.room.message/t1" => (HttpStatusCode.OK, "{\"event_id\":\"$e1\"}"),
            _ => null,
        };
        Assert.Equal("example.org", MatrixClient.ServerOf("@ada:example.org"));
        var hs = await MatrixClient.DiscoverAsync("@ada:example.org", fake);
        Assert.Equal("https://matrix.example.org", hs);
        using var c = new MatrixClient(hs, fake);
        var login = await c.PasswordLoginAsync("ada", "secret", "test");
        Assert.Equal(("@ada:example.org", "tok", "DEV"), (login.UserId, login.AccessToken, login.DeviceId));
        Assert.Equal("$e1", await c.SendTextAsync("!r:example.org", "hello", "t1"));
        var send = fake.Requests.Last();
        Assert.Equal(("PUT", "tok"), (send.Method, send.Auth));
        Assert.Contains("\"body\":\"hello\"", send.Body);
    }

    [Fact]
    public async Task A_wrong_password_is_the_servers_M_FORBIDDEN()
    {
        var fake = new FakeMatrix { Answer = (_, _) => (HttpStatusCode.Forbidden, "{\"errcode\":\"M_FORBIDDEN\",\"error\":\"Invalid password\"}") };
        using var c = new MatrixClient("https://hs", fake);
        var ex = await Assert.ThrowsAsync<MatrixException>(() => c.PasswordLoginAsync("ada", "nope", "test"));
        Assert.Equal("M_FORBIDDEN", ex.Code);
    }

    [Fact]
    public void Without_a_well_known_the_name_itself_is_the_server() =>
        Assert.Equal("https://matrix.org", MatrixClient.DiscoverAsync("matrix.org", new FakeMatrix()).Result);

    private static JsonNode Ev(string type, string sender, string id, long ts, object content, string? stateKey = null, object? unsignedPart = null)
    {
        var o = new JsonObject { ["type"] = type, ["sender"] = sender, ["event_id"] = id, ["origin_server_ts"] = ts,
            ["content"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(content)) };
        if (stateKey != null) o["state_key"] = stateKey;
        if (unsignedPart != null) o["unsigned"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(unsignedPart));
        return o;
    }

    private static JsonNode Sync(string next, JsonObject join, JsonObject? invite = null, JsonArray? account = null)
    {
        var rooms = new JsonObject { ["join"] = join };
        if (invite != null) rooms["invite"] = invite;
        var s = new JsonObject { ["next_batch"] = next, ["rooms"] = rooms };
        if (account != null) s["account_data"] = new JsonObject { ["events"] = account };
        return s;
    }

    [Fact]
    public void Sync_builds_rooms_names_messages_edits_and_deletions()
    {
        var st = new MatrixState("@me:hs");
        var first = Sync("s1", new JsonObject
        {
            ["!a:hs"] = new JsonObject
            {
                ["summary"] = new JsonObject { ["m.heroes"] = new JsonArray("@bob:hs"), ["m.joined_member_count"] = 2 },
                ["state"] = new JsonObject { ["events"] = new JsonArray(Ev("m.room.member", "@bob:hs", "$m", 1, new { membership = "join", displayname = "Bob" }, "@bob:hs")) },
                ["timeline"] = new JsonObject
                {
                    ["prev_batch"] = "p1",
                    ["events"] = new JsonArray(
                        Ev("m.room.message", "@bob:hs", "$1", 1000, new { msgtype = "m.text", body = "hi" }),
                        Ev("m.room.message", "@bob:hs", "$2", 2000, new { msgtype = "m.text", body = "> <@me:hs> earlier\n\nreply" }),
                        Ev("m.room.message", "@bob:hs", "$3", 3000, new Dictionary<string, object>
                        {
                            ["msgtype"] = "m.text", ["body"] = "* hello",
                            ["m.new_content"] = new { msgtype = "m.text", body = "hello" },
                            ["m.relates_to"] = new Dictionary<string, object> { ["rel_type"] = "m.replace", ["event_id"] = "$1" },
                        }),
                        Ev("m.room.encrypted", "@bob:hs", "$4", 4000, new { algorithm = "m.megolm.v1.aes-sha2" }),
                        Ev("m.room.redaction", "@bob:hs", "$5", 5000, new { redacts = "$4" })),
                },
                ["unread_notifications"] = new JsonObject { ["notification_count"] = 2 },
            },
        }, account: new JsonArray(new JsonObject { ["type"] = "m.direct", ["content"] = new JsonObject { ["@bob:hs"] = new JsonArray("!a:hs") } }));
        var arrivals = st.Apply(first);
        Assert.Empty(arrivals);                              // the first sync is history, not news
        var r = st.Rooms["!a:hs"];
        Assert.Equal("Bob", r.DisplayName("@me:hs"));
        Assert.True(r.IsDirect);
        Assert.Equal(new[] { "hello", "reply", "" }, r.Timeline.Select(m => m.Body));
        Assert.True(r.Timeline[0].Edited);
        Assert.Equal(MessageKind.Deleted, r.Timeline[2].Kind);
        Assert.Equal(2, r.Unread);
        Assert.Equal("p1", r.PrevBatch);
        Assert.Equal("s1", st.Since);
    }

    [Fact]
    public void New_messages_notify_only_when_the_unread_count_goes_up_and_our_own_echo_replaces_the_pending_one()
    {
        var st = new MatrixState("@me:hs");
        st.Apply(Sync("s1", new JsonObject { ["!g:hs"] = new JsonObject { ["state"] = new JsonObject { ["events"] = new JsonArray(Ev("m.room.name", "@x:hs", "$n", 1, new { name = "Team" }, "")) } } }));
        var room = st.Rooms["!g:hs"];
        room.Timeline.Add(new MatrixMessage { Sender = "@me:hs", Body = "mine", Kind = MessageKind.Text, PendingTxn = "t9" });
        var news = st.Apply(Sync("s2", new JsonObject
        {
            ["!g:hs"] = new JsonObject
            {
                ["timeline"] = new JsonObject { ["events"] = new JsonArray(
                    Ev("m.room.message", "@me:hs", "$mine", 10, new { msgtype = "m.text", body = "mine" }, unsignedPart: new { transaction_id = "t9" }),
                    Ev("m.room.message", "@x:hs", "$x", 11, new { msgtype = "m.text", body = "yo" })) },
                ["unread_notifications"] = new JsonObject { ["notification_count"] = 1 },
            },
        }));
        Assert.Equal("Team", room.DisplayName("@me:hs"));
        Assert.Equal(2, room.Timeline.Count);
        Assert.Null(room.Timeline[0].PendingTxn);
        Assert.Equal("$mine", room.Timeline[0].EventId);
        Assert.Equal("yo", Assert.Single(news).Message.Body);
        // a muted room: messages but no unread count, so nothing to notify
        var quiet = st.Apply(Sync("s3", new JsonObject
        {
            ["!g:hs"] = new JsonObject { ["timeline"] = new JsonObject { ["events"] = new JsonArray(Ev("m.room.message", "@x:hs", "$y", 12, new { msgtype = "m.text", body = "shh" })) },
                ["unread_notifications"] = new JsonObject { ["notification_count"] = 1 } },
        }));
        Assert.Empty(quiet);
    }

    [Fact]
    public void Invites_name_who_invited_and_older_messages_go_in_front()
    {
        var st = new MatrixState("@me:hs");
        st.Apply(Sync("s1", new JsonObject
        {
            ["!c:hs"] = new JsonObject { ["timeline"] = new JsonObject { ["prev_batch"] = "p", ["events"] = new JsonArray(Ev("m.room.message", "@x:hs", "$3", 3, new { msgtype = "m.text", body = "three" })) } },
        }, invite: new JsonObject
        {
            ["!i:hs"] = new JsonObject { ["invite_state"] = new JsonObject { ["events"] = new JsonArray(
                Ev("m.room.member", "@carol:hs", "$i", 1, new { membership = "join", displayname = "Carol" }, "@carol:hs"),
                Ev("m.room.member", "@carol:hs", "$j", 2, new { membership = "invite", is_direct = true }, "@me:hs")) } },
        }));
        var inv = st.Rooms["!i:hs"];
        Assert.True(inv.Invite);
        Assert.True(inv.IsDirect);
        Assert.Equal("Carol", inv.DisplayName("@me:hs"));
        st.ApplyOlder("!c:hs", new JsonObject
        {
            ["end"] = "p0",
            ["chunk"] = new JsonArray(Ev("m.room.message", "@x:hs", "$2", 2, new { msgtype = "m.text", body = "two" }),
                                      Ev("m.room.message", "@x:hs", "$1", 1, new { msgtype = "m.image", body = "cat.png", url = "mxc://hs/abc" })),
        });
        var c = st.Rooms["!c:hs"];
        Assert.Equal(new[] { "cat.png", "two", "three" }, c.Timeline.Select(m => m.Body));
        Assert.Equal(MessageKind.Image, c.Timeline[0].Kind);
        Assert.Equal(("hs", "abc"), MatrixClient.Mxc(c.Timeline[0].Mxc));
        Assert.Equal("p0", c.PrevBatch);
    }
}
