using BotNexus.Domain.Primitives;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Notifications;
using BotNexus.Gateway.Notifications.Push;

namespace BotNexus.Gateway.Tests.Notifications.Push;

/// <summary>
/// Pins how the gateway talks to APNs, and what it does when Apple pushes back.
/// </summary>
/// <remarks>
/// None of this can be verified against Apple without an Apple Developer account, a registered
/// bundle id and a real device token. What CAN be verified without those - and is, here - is every
/// decision the gateway makes on its own: the request it builds, the token it signs, which
/// refusals are permanent, and that an unconfigured gateway does nothing at all. The unverifiable
/// remainder is whether Apple likes it.
/// </remarks>
public sealed class ApnsSenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "botnexus-apns", Guid.NewGuid().ToString("N"));

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero));

    private string DbPath => Path.Combine(_dir, "apns.sqlite");
    private string KeyPath => Path.Combine(_dir, "AuthKey.p8");

    private const string DeviceToken = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

    public ApnsSenderTests()
    {
        Directory.CreateDirectory(_dir);

        // A real P-256 key in the .p8 format Apple issues, so the signing path runs for real.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(KeyPath, new string(PemEncoding.Write("PRIVATE KEY", key.ExportPkcs8PrivateKey())));
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolFor(DbPath);
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private static readonly Notification Sample = new()
    {
        Id = "n1",
        Kind = NotificationKind.AgentRunFailed,
        Severity = NotificationSeverity.Error,
        Title = "Agent 'assistant' run failed",
        Body = "The provider returned 529.",
        Link = "conversation/c1",
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private ApnsOptions Configured() => new()
    {
        TeamId = "TEAM123456",
        KeyId = "KEY1234567",
        BundleId = "com.example.botnexus",
        PrivateKeyPath = KeyPath,
    };

    private IApnsDeviceStore Store() => new SqliteApnsDeviceStore(DbPath, timeProvider: _time);

    private ApnsSender Sender(
        StubApns apns,
        IApnsDeviceStore store,
        ApnsOptions? options = null,
        int? waiting = null,
        PendingQuestion? question = null)
    {
        var resolved = options ?? Configured();

        return new ApnsSender(
            new HttpClient(apns),
            store,
            resolved,
            new ApnsTokenProvider(resolved, _time),
            waiting is { } count ? new StubWaitingCount(count) : null,
            question is null ? null : new StubQuestionLookup(question));
    }

    private async Task<IApnsDeviceStore> StoreWithDevice(string environment = ApnsEnvironment.Production)
    {
        var store = Store();
        await store.SaveAsync(new ApnsDevice { DeviceToken = DeviceToken, Environment = environment });

        return store;
    }

    // A gateway without an Apple Developer account is the ordinary case. It must cost nothing and
    // say nothing, not fail per notification.
    [Fact]
    public async Task Does_nothing_at_all_when_apns_is_not_configured()
    {
        var apns = new StubApns();
        var store = await StoreWithDevice();

        var delivered = await Sender(apns, store, new ApnsOptions()).SendAsync(Sample);

        Assert.Equal(0, delivered);
        Assert.Empty(apns.Requests);
    }

    [Fact]
    public async Task Sends_nothing_when_no_device_has_registered()
    {
        var apns = new StubApns();

        Assert.Equal(0, await Sender(apns, Store()).SendAsync(Sample));
        Assert.Empty(apns.Requests);
    }

    [Fact]
    public async Task Addresses_the_device_with_the_headers_apns_requires()
    {
        var apns = new StubApns();

        var delivered = await Sender(apns, await StoreWithDevice()).SendAsync(Sample);

        Assert.Equal(1, delivered);
        var request = Assert.Single(apns.Requests);

        Assert.Equal($"https://api.push.apple.com/3/device/{DeviceToken}", request.Url);
        Assert.Equal("com.example.botnexus", request.Topic);
        Assert.Equal("alert", request.PushType);
        Assert.Equal("10", request.Priority);
        Assert.StartsWith("bearer ", request.Authorization);

        // HTTP/2 only. Apple refuses the connection rather than negotiating down, and the failure
        // is a transport error with nothing useful in it.
        Assert.Equal(HttpVersion.Version20, request.Version);
    }

    // The two environments are not interchangeable, and sending to the wrong host is refused in a
    // way that reads like a bad token rather than a wrong address.
    [Fact]
    public async Task Sends_a_sandbox_token_to_the_sandbox_host()
    {
        var apns = new StubApns();

        await Sender(apns, await StoreWithDevice(ApnsEnvironment.Sandbox)).SendAsync(Sample);

        Assert.StartsWith("https://api.sandbox.push.apple.com/", apns.Requests[0].Url);
    }

    [Fact]
    public async Task Sends_the_notification_as_an_aps_alert()
    {
        var apns = new StubApns();

        await Sender(apns, await StoreWithDevice()).SendAsync(Sample);

        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(apns.Requests[0].Body));
        var root = payload.RootElement;
        var alert = root.GetProperty("aps").GetProperty("alert");

        Assert.Equal("Agent 'assistant' run failed", alert.GetProperty("title").GetString());
        Assert.Equal("The provider returned 529.", alert.GetProperty("body").GetString());
        Assert.Equal("n1", root.GetProperty("id").GetString());
        Assert.Equal("AgentRunFailed", root.GetProperty("kind").GetString());
        Assert.Equal("conversation/c1", root.GetProperty("link").GetString());
    }

    // An oversize payload is REJECTED, not truncated by Apple - the notification simply never
    // arrives. A long provider error is a realistic way to reach 4KB.
    [Fact]
    public void Trims_an_oversized_body_rather_than_losing_the_notification()
    {
        var huge = Sample with { Body = new string('x', 8000) };

        var payload = ApnsSender.BuildPayload(huge);

        Assert.True(payload.Length <= 4096, $"payload was {payload.Length} bytes");

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(payload));

        // The title survives: it is the part written to be read on a lock screen.
        Assert.Equal(
            huge.Title,
            document.RootElement.GetProperty("aps").GetProperty("alert").GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, "Unregistered")]
    [InlineData(HttpStatusCode.BadRequest, "BadDeviceToken")]
    public async Task Forgets_a_device_apple_says_is_gone(HttpStatusCode status, string reason)
    {
        var apns = new StubApns { Status = status, Reason = reason };
        var store = await StoreWithDevice();

        await Sender(apns, store).SendAsync(Sample);

        Assert.Empty(await store.ListAsync());
    }

    // A credential fault affects every device, so it must not look like one device's problem - and
    // must never delete a registration that is perfectly good.
    [Theory]
    [InlineData("InvalidProviderToken")]
    [InlineData("ExpiredProviderToken")]
    [InlineData("TopicDisallowed")]
    public async Task Keeps_every_device_when_the_gateway_credentials_are_wrong(string reason)
    {
        var apns = new StubApns { Status = HttpStatusCode.Forbidden, Reason = reason };
        var store = await StoreWithDevice();

        await Sender(apns, store).SendAsync(Sample);

        Assert.Single(await store.ListAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "TooManyRequests")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable")]
    public async Task Keeps_a_device_through_a_transient_refusal(HttpStatusCode status, string reason)
    {
        var apns = new StubApns { Status = status, Reason = reason };
        var store = await StoreWithDevice();

        Assert.Equal(0, await Sender(apns, store).SendAsync(Sample));
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task Records_delivery()
    {
        var store = await StoreWithDevice();

        await Sender(new StubApns(), store).SendAsync(Sample);

        Assert.NotNull(Assert.Single(await store.ListAsync()).LastSuccessAtUtc);
    }

    // #168: a phone's level decides what it is sent. Off sends nothing at all.
    [Fact]
    public async Task Sends_nothing_to_a_device_that_turned_notifications_off()
    {
        var apns = new StubApns();
        var store = await StoreWithDevice();
        await store.SetLevelAsync(DeviceToken, ApnsNotificationLevel.Off);

        Assert.Equal(0, await Sender(apns, store).SendAsync(Sample));
        Assert.Empty(apns.Requests);
    }

    // A phone at the default must not be woken for a reply it did not ask to hear about.
    [Fact]
    public async Task Holds_back_a_finished_reply_from_a_device_at_the_default_level()
    {
        var apns = new StubApns();
        var completed = Sample with { Kind = NotificationKind.AgentRunCompleted, Severity = NotificationSeverity.Info };

        Assert.Equal(0, await Sender(apns, await StoreWithDevice()).SendAsync(completed));
        Assert.Empty(apns.Requests);
    }

    // thread-id groups a conversation's notifications together on the lock screen, and the ids let
    // an app open the conversation without first looking up which agent it belongs to.
    [Fact]
    public async Task Groups_by_conversation_and_names_the_agent_and_conversation()
    {
        var apns = new StubApns();
        var about = Sample with { AgentId = "assistant", ConversationId = "c1" };

        await Sender(apns, await StoreWithDevice()).SendAsync(about);

        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(apns.Requests[0].Body));
        var root = payload.RootElement;

        Assert.Equal("c1", root.GetProperty("aps").GetProperty("thread-id").GetString());
        Assert.Equal("assistant", root.GetProperty("agentId").GetString());
        Assert.Equal("c1", root.GetProperty("conversationId").GetString());
    }

    // The number on the icon is the one thing a phone shows without being opened. It counts what is
    // waiting on the person, not what this particular push is about.
    [Fact]
    public async Task Carries_the_waiting_count_as_the_badge()
    {
        var apns = new StubApns();

        await Sender(apns, await StoreWithDevice(), waiting: 3).SendAsync(Sample);

        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(apns.Requests[0].Body));
        Assert.Equal(3, payload.RootElement.GetProperty("aps").GetProperty("badge").GetInt32());
    }

    // Zero is a number worth sending: it is what takes the badge off the icon once the last
    // question has been answered.
    [Fact]
    public async Task Sends_a_zero_badge_when_nothing_is_waiting()
    {
        var apns = new StubApns();

        await Sender(apns, await StoreWithDevice(), waiting: 0).SendAsync(Sample);

        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(apns.Requests[0].Body));
        Assert.Equal(0, payload.RootElement.GetProperty("aps").GetProperty("badge").GetInt32());
    }

    // A gateway with no way to count leaves the badge alone rather than guessing at zero, which
    // would clear a count the phone had rightly been showing.
    [Fact]
    public async Task Leaves_the_badge_out_when_there_is_nothing_to_count_with()
    {
        var apns = new StubApns();

        await Sender(apns, await StoreWithDevice()).SendAsync(Sample);

        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(apns.Requests[0].Body));
        Assert.False(payload.RootElement.GetProperty("aps").TryGetProperty("badge", out _));
    }

    /// <summary>Stands in for the waiting count, which is a database read in the real thing.</summary>
    private sealed class StubWaitingCount(int count) : IWaitingConversationCount
    {
        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(count);
    }

    // Telegram puts the answers under the question as buttons. A phone can do the same, but only if
    // the push carries them.
    [Fact]
    public async Task A_waiting_question_carries_its_answers()
    {
        var apns = new StubApns();
        var waiting = Sample with
        {
            Kind = NotificationKind.AgentWaitingForInput,
            Severity = NotificationSeverity.Warning,
            ConversationId = "c1",
        };

        await Sender(
            apns,
            await StoreWithDevice(),
            question: new PendingQuestion(
                "req-1",
                [new PendingQuestionChoice("now", "Deploy now"), new PendingQuestionChoice("wait", "Wait")],
                AllowFreeForm: true,
                AllowMultiple: false))
            .SendAsync(waiting);

        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(apns.Requests[0].Body));
        var root = payload.RootElement;

        Assert.Equal("req-1", root.GetProperty("requestId").GetString());
        Assert.True(root.GetProperty("allowFreeForm").GetBoolean());
        Assert.False(root.GetProperty("allowMultiple").GetBoolean());

        var choices = root.GetProperty("choices");
        Assert.Equal(2, choices.GetArrayLength());
        Assert.Equal("now", choices[0].GetProperty("value").GetString());
        Assert.Equal("Deploy now", choices[0].GetProperty("label").GetString());
    }

    // A reply or a failure has nothing to answer, and must not cost a database read to discover it.
    [Fact]
    public async Task A_notification_that_is_not_a_question_asks_for_nothing()
    {
        var apns = new StubApns();
        var lookup = new StubQuestionLookup(new PendingQuestion("req-1", [], true, false));

        await new ApnsSender(
            new HttpClient(apns),
            await StoreWithDevice(),
            Configured(),
            new ApnsTokenProvider(Configured(), _time),
            waiting: null,
            questions: lookup)
            .SendAsync(Sample);

        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(apns.Requests[0].Body));
        Assert.False(payload.RootElement.TryGetProperty("requestId", out _));
        Assert.Equal(0, lookup.Calls);
    }

    // An open-ended question has no buttons; the phone offers a text field instead, and needs to be
    // told that is allowed.
    [Fact]
    public async Task A_question_with_no_choices_still_says_it_can_be_typed()
    {
        var apns = new StubApns();
        var waiting = Sample with
        {
            Kind = NotificationKind.AgentWaitingForInput,
            ConversationId = "c1",
        };

        await Sender(
            apns,
            await StoreWithDevice(),
            question: new PendingQuestion("req-1", [], AllowFreeForm: true, AllowMultiple: false))
            .SendAsync(waiting);

        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(apns.Requests[0].Body));
        var root = payload.RootElement;

        Assert.Equal("req-1", root.GetProperty("requestId").GetString());
        Assert.Equal(0, root.GetProperty("choices").GetArrayLength());
        Assert.True(root.GetProperty("allowFreeForm").GetBoolean());
    }

    // The question was answered in the portal a moment ago: the push still goes, without buttons
    // that would resolve nothing.
    [Fact]
    public async Task A_question_that_is_no_longer_pending_sends_the_notification_anyway()
    {
        var apns = new StubApns();
        var waiting = Sample with
        {
            Kind = NotificationKind.AgentWaitingForInput,
            ConversationId = "c1",
        };

        var delivered = await Sender(apns, await StoreWithDevice()).SendAsync(waiting);

        Assert.Equal(1, delivered);
        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(apns.Requests[0].Body));
        Assert.False(payload.RootElement.TryGetProperty("requestId", out _));
    }

    // iOS attaches buttons by matching the push's category against one the app registered before the
    // notification arrived. Sending the kind here would mean a question that carries its answers and
    // shows none of them.
    [Fact]
    public async Task A_question_names_the_category_its_answers_need()
    {
        var apns = new StubApns();
        var waiting = Sample with { Kind = NotificationKind.AgentWaitingForInput, ConversationId = "c1" };

        await Sender(
            apns,
            await StoreWithDevice(),
            question: new PendingQuestion(
                "req-1",
                [new PendingQuestionChoice("now", "Deploy now"), new PendingQuestionChoice("wait", "Wait")],
                AllowFreeForm: true,
                AllowMultiple: false))
            .SendAsync(waiting);

        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(apns.Requests[0].Body));
        Assert.Equal(
            "botnexus.question.2.reply",
            payload.RootElement.GetProperty("aps").GetProperty("category").GetString());
    }

    // Too many answers to draw: the notification offers none and opening the conversation shows them
    // all, which is what Telegram does at its own limit.
    [Fact]
    public async Task A_question_with_too_many_answers_offers_none()
    {
        var apns = new StubApns();
        var waiting = Sample with { Kind = NotificationKind.AgentWaitingForInput, ConversationId = "c1" };
        var many = Enumerable.Range(1, 9)
            .Select(index => new PendingQuestionChoice($"v{index}", $"Choice {index}"))
            .ToArray();

        await Sender(
            apns,
            await StoreWithDevice(),
            question: new PendingQuestion("req-1", many, AllowFreeForm: false, AllowMultiple: false))
            .SendAsync(waiting);

        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(apns.Requests[0].Body));
        Assert.Equal(
            "botnexus.question.0",
            payload.RootElement.GetProperty("aps").GetProperty("category").GetString());
    }

    // A reply or a failure has nothing to answer, and keeps naming its kind so a client can route it.
    [Fact]
    public async Task Anything_that_is_not_a_question_still_names_its_kind()
    {
        var apns = new StubApns();

        await Sender(apns, await StoreWithDevice()).SendAsync(Sample);

        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(apns.Requests[0].Body));
        Assert.Equal(
            NotificationKind.AgentRunFailed.ToString(),
            payload.RootElement.GetProperty("aps").GetProperty("category").GetString());
    }

    /// <summary>Stands in for the pending-question lookup, which is a database read in the real thing.</summary>
    private sealed class StubQuestionLookup(PendingQuestion question) : IPendingQuestionLookup
    {
        public int Calls { get; private set; }

        public Task<PendingQuestion?> FindAsync(ConversationId conversationId, CancellationToken ct = default)
        {
            Calls++;

            return Task.FromResult<PendingQuestion?>(question);
        }
    }

    /// <summary>Stands in for APNs and records what it was sent.</summary>
    private sealed class StubApns : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string? Reason { get; set; }

        public List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new Captured
            {
                Url = request.RequestUri!.ToString(),
                Version = request.Version,
                Authorization = Header(request, "authorization"),
                Topic = Header(request, "apns-topic"),
                PushType = Header(request, "apns-push-type"),
                Priority = Header(request, "apns-priority"),
                CollapseId = Header(request, "apns-collapse-id"),
                Body = request.Content is null
                    ? []
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken),
            });

            return new HttpResponseMessage(Status)
            {
                Content = Reason is null
                    ? new StringContent("")
                    : new StringContent($"{{\"reason\":\"{Reason}\"}}"),
            };
        }

        private static string Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? string.Join("", values) : string.Empty;

        internal sealed class Captured
        {
            public required string Url { get; init; }
            public required Version Version { get; init; }
            public required string Authorization { get; init; }
            public required string Topic { get; init; }
            public required string PushType { get; init; }
            public required string Priority { get; init; }
            public required string CollapseId { get; init; }
            public required byte[] Body { get; init; }
        }
    }
}
