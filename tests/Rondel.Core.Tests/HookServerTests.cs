using System.Net;
using System.Text;
using System.Text.Json;
using Rondel.Core.Activity;

namespace Rondel.Core.Tests;

/// <summary>The local server agents' hooks report to, and the hooks file that points them at it.</summary>
public sealed class HookServerTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "rondel-tests-" + Guid.NewGuid().ToString("N"));
    readonly AgentActivity _activity = new();
    readonly HttpClient _http = new();

    public void Dispose()
    {
        _http.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void TheHooksFilePostsEveryFollowedEventWithTheTokenAndTheSessionsId()
    {
        string json = HookServer.Build(51234, "0123456789abcdef0123456789abcdef");
        using var document = JsonDocument.Parse(json);
        var hooks = document.RootElement.GetProperty("hooks");

        Assert.Equal(
            ["UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest", "Notification", "Stop", "StopFailure", "SessionEnd"],
            hooks.EnumerateObject().Select(p => p.Name));
        Assert.Equal("AskUserQuestion", hooks.GetProperty("PreToolUse")[0].GetProperty("matcher").GetString());
        var hook = hooks.GetProperty("Stop")[0].GetProperty("hooks")[0];
        Assert.Equal("http", hook.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:51234/hook", hook.GetProperty("url").GetString());
        Assert.Equal("$RONDEL_SESSION", hook.GetProperty("headers").GetProperty(HookServer.SessionHeader).GetString());
        Assert.Equal(["RONDEL_SESSION"], hook.GetProperty("allowedEnvVars").EnumerateArray().Select(e => e.GetString()));

        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, HookServer.FileName);
        File.WriteAllText(path, json);
        Assert.Equal((51234, "0123456789abcdef0123456789abcdef"), HookServer.Read(path));
    }

    [Theory]
    [InlineData("POST /hook HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Rondel-Token: secret-token-1234\r\nX-Rondel-Session: 0123456789ab\r\nContent-Length: 12", 200)]
    [InlineData("POST /hook?x=1 HTTP/1.1\r\nx-rondel-token: secret-token-1234\r\nx-rondel-session: 0123456789ab\r\ncontent-length: 12", 200)]
    [InlineData("POST /hook HTTP/1.1\r\nX-Rondel-Token: wrong-token-12345\r\nX-Rondel-Session: 0123456789ab\r\nContent-Length: 12", 401)]
    [InlineData("POST /hook HTTP/1.1\r\nX-Rondel-Session: 0123456789ab\r\nContent-Length: 12", 401)]
    [InlineData("OPTIONS /hook HTTP/1.1\r\nOrigin: https://example.com", 405)] // a browser asking first
    [InlineData("POST /other HTTP/1.1\r\nX-Rondel-Token: secret-token-1234", 404)]
    [InlineData("POST /hook HTTP/1.1\r\nX-Rondel-Token: secret-token-1234\r\nX-Rondel-Session: \r\nContent-Length: 12", 400)] // started elsewhere
    [InlineData("POST /hook HTTP/1.1\r\nX-Rondel-Token: secret-token-1234\r\nX-Rondel-Session: ../x\r\nContent-Length: 12", 400)]
    [InlineData("POST /hook HTTP/1.1\r\nX-Rondel-Token: secret-token-1234\r\nX-Rondel-Session: 0123456789ab\r\nTransfer-Encoding: chunked", 411)]
    [InlineData("POST /hook HTTP/1.1\r\nX-Rondel-Token: secret-token-1234\r\nX-Rondel-Session: 0123456789ab\r\nContent-Length: 99999999", 413)]
    [InlineData("garbage", 400)]
    public void OnlyRequestsWithTheTokenAndASessionAreAccepted(string head, int status)
    {
        var result = HookServer.CheckHead(head, "secret-token-1234"u8);

        Assert.Equal(status, result.Status);
        if (status == 200)
        {
            Assert.Equal("0123456789ab", result.Session);
            Assert.Equal(12, result.ContentLength);
        }
    }

    [Fact]
    public async Task AnEventPostedLikeAHookChangesTheAgentAndARestartKeepsThePort()
    {
        int port;
        using (var server = new HookServer(_activity))
        {
            Assert.True(server.Start(_directory));
            port = server.Port;
            Assert.Equal(Path.Combine(_directory, HookServer.FileName), _activity.HooksFile);
            string token = HookServer.Read(_activity.HooksFile!).Token!;
            var agent = _activity.Start(Path.Combine(_directory, "weather-api"), "Claude Code", working: true);
            var changed = new TaskCompletionSource<AgentChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            _activity.Changed += change => changed.TrySetResult(change);

            using var refused = await Post(port, "not-the-token-000", agent.Id, """{ "hook_event_name": "Stop" }""");
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

            using var accepted = await Post(port, token, agent.Id, """{ "hook_event_name": "PermissionRequest", "tool_name": "Bash", "session_id": "s1" }""");
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var change = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(AgentState.Waiting, change.Agent.State);
            Assert.Equal("Bash", change.Agent.Tool);
        }
        Assert.Null(_activity.HooksFile);

        // Agents started before the restart still post to the port in their hooks file.
        using var again = new HookServer(_activity);
        Assert.True(again.Start(_directory));
        Assert.Equal(port, again.Port);
    }

    Task<HttpResponseMessage> Post(int port, string token, string session, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/hook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(HookServer.TokenHeader, token);
        request.Headers.Add(HookServer.SessionHeader, session);
        return _http.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
