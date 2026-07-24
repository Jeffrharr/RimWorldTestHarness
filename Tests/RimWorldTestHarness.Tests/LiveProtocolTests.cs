using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

// The live channel is parsed by two independent implementations (this C# listener and the Python
// client), so the contract has to survive a serialize -> deserialize round-trip losslessly and has
// to tolerate the relaxed casing a foreign client might emit. These tests pin both.
[TestFixture]
public class LiveProtocolTests
{
    [Test]
    public void LiveRequest_RoundTrips()
    {
        LiveRequest req = new()
        {
            Id = "abc-123",
            Seq = 42,
            Action = "SetTime",
            Args = { ["hour"] = "13.5" },
            TimeoutMs = 15000,
        };

        LiveRequest? back = LiveProtocol.Deserialize<LiveRequest>(LiveProtocol.Serialize(req));

        Assert.That(back, Is.Not.Null);
        Assert.That(back!.Id, Is.EqualTo("abc-123"));
        Assert.That(back.Seq, Is.EqualTo(42));
        Assert.That(back.Action, Is.EqualTo("SetTime"));
        Assert.That(back.Args["hour"], Is.EqualTo("13.5"));
        Assert.That(back.TimeoutMs, Is.EqualTo(15000));
    }

    [Test]
    public void LiveResponse_RoundTrips_IncludingErrorNull()
    {
        LiveResponse resp = new()
        {
            Id = "abc-123",
            Seq = 42,
            Ok = true,
            Action = "Probe",
            Result = { ["value"] = "0.42" },
            Error = null,
            TicksGame = 123456,
        };

        LiveResponse? back = LiveProtocol.Deserialize<LiveResponse>(LiveProtocol.Serialize(resp));

        Assert.That(back, Is.Not.Null);
        Assert.That(back!.Ok, Is.True);
        Assert.That(back.Result["value"], Is.EqualTo("0.42"));
        Assert.That(back.Error, Is.Null);
        Assert.That(back.TicksGame, Is.EqualTo(123456));
    }

    [Test]
    public void LiveResponse_FailureCarriesError()
    {
        LiveResponse resp = new() { Id = "x", Ok = false, Error = "No probe registered named 'nope'" };

        LiveResponse? back = LiveProtocol.Deserialize<LiveResponse>(LiveProtocol.Serialize(resp));

        Assert.That(back!.Ok, Is.False);
        Assert.That(back.Error, Is.EqualTo("No probe registered named 'nope'"));
    }

    [Test]
    public void LiveStatus_RoundTrips()
    {
        LiveStatus status = new()
        {
            HarnessLoaded = true,
            MapLoaded = true,
            TicksGame = 999,
            DayOfYear = 12,
            HourFloat = 13.5f,
            TimeSpeed = "Paused",
            InFlight = "abc-123",
            ProcessedSeq = 41,
            UpdatedAtTick = 1000,
        };

        LiveStatus? back = LiveProtocol.Deserialize<LiveStatus>(LiveProtocol.Serialize(status));

        Assert.That(back, Is.Not.Null);
        Assert.That(back!.MapLoaded, Is.True);
        Assert.That(back.HourFloat, Is.EqualTo(13.5f));
        Assert.That(back.TimeSpeed, Is.EqualTo("Paused"));
        Assert.That(back.InFlight, Is.EqualTo("abc-123"));
        Assert.That(back.ProcessedSeq, Is.EqualTo(41));
    }

    [Test]
    public void Catalog_RoundTrips_ActionsProbesAndGame()
    {
        Catalog catalog = new()
        {
            Actions =
            {
                new CatalogAction
                {
                    Name = "Screenshot",
                    Category = "RimWorldTestHarness",
                    Source = "harness",
                    ActionType = "",
                    Available = true,
                    Invokable = true,
                    Args = { new CatalogArg { Key = "fileName", Type = "string", Desc = "output PNG name" } },
                },
                new CatalogAction
                {
                    Name = "Change weather...",
                    Category = "General",
                    Source = "native",
                    ActionType = "Action",
                    Available = true,
                    Invokable = true,
                },
            },
            Probes = { new CatalogProbe { Name = "shadow_lean", Desc = "shadow lean at map center", Unit = "ratio" } },
            Game = new CatalogGame
            {
                Version = "1.6.4",
                LoadedMods = { "Core", "joof.celestiallighting" },
                MapName = "Colony",
                Tick = 123456,
            },
        };

        Catalog? back = LiveProtocol.Deserialize<Catalog>(LiveProtocol.Serialize(catalog));

        Assert.That(back, Is.Not.Null);
        Assert.That(back!.Actions, Has.Count.EqualTo(2));
        Assert.That(back.Actions[0].Name, Is.EqualTo("Screenshot"));
        Assert.That(back.Actions[0].Args[0].Key, Is.EqualTo("fileName"));
        Assert.That(back.Actions[1].Source, Is.EqualTo("native"));
        Assert.That(back.Probes[0].Name, Is.EqualTo("shadow_lean"));
        Assert.That(back.Game.LoadedMods, Does.Contain("joof.celestiallighting"));
    }

    [Test]
    public void Deserialize_IsCaseInsensitive_ForForeignClients()
    {
        // A Python client emitting camelCase keys must still parse — the reader is case-insensitive.
        const string camel = """
        { "id": "z", "seq": 7, "action": "FastForward", "args": { "ticks": "2500" }, "timeoutMs": 30000 }
        """;

        LiveRequest? back = LiveProtocol.Deserialize<LiveRequest>(camel);

        Assert.That(back, Is.Not.Null);
        Assert.That(back!.Id, Is.EqualTo("z"));
        Assert.That(back.Seq, Is.EqualTo(7));
        Assert.That(back.Action, Is.EqualTo("FastForward"));
        Assert.That(back.Args["ticks"], Is.EqualTo("2500"));
        Assert.That(back.TimeoutMs, Is.EqualTo(30000));
    }
}
