using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

[TestFixture]
public class ScenarioSpecLoaderTests
{
    [Test]
    public void LoadFromJson_ParsesNameSaveFileAndSteps()
    {
        const string json = """
        {
          "name": "shadow_lean_at_equinox",
          "saveFile": "fixtures/minimal_colony.rws",
          "steps": [
            { "type": "SetTile", "args": { "latitude": "55" } },
            { "type": "Probe", "args": { "probeName": "shadow_lean", "expectedValue": "0", "tolerance": "0.01" } }
          ]
        }
        """;

        ScenarioSpec spec = ScenarioSpecLoader.LoadFromJson(json);

        Assert.That(spec.Name, Is.EqualTo("shadow_lean_at_equinox"));
        Assert.That(spec.SaveFile, Is.EqualTo("fixtures/minimal_colony.rws"));
        Assert.That(spec.Steps, Has.Count.EqualTo(2));
        Assert.That(spec.Steps[0].Type, Is.EqualTo("SetTile"));
        Assert.That(spec.Steps[0].Args["latitude"], Is.EqualTo("55"));
        Assert.That(spec.Steps[1].Args["probeName"], Is.EqualTo("shadow_lean"));
    }

    [Test]
    public void LoadFromJson_EmptyObject_ReturnsSpecWithDefaults()
    {
        ScenarioSpec spec = ScenarioSpecLoader.LoadFromJson("{}");

        Assert.That(spec.Name, Is.EqualTo(""));
        Assert.That(spec.Steps, Is.Empty);
        Assert.That(spec.RequiredMods, Is.Empty);
    }

    // The loader is the single choke point where composite steps are desugared, so by the time a
    // driver sees a spec there are only primitive steps left.
    [Test]
    public void LoadFromJson_ExpandsTimelapseIntoPrimitiveSteps()
    {
        const string json = """
        {
          "name": "daycycle",
          "steps": [
            { "type": "Timelapse", "args": { "toHour": "3", "fileNamePrefix": "dc", "settleFrames": "0" } }
          ]
        }
        """;

        ScenarioSpec spec = ScenarioSpecLoader.LoadFromJson(json);

        Assert.That(spec.LoadErrors, Is.Empty);
        Assert.That(spec.Steps.Any(s => s.Type == "Timelapse"), Is.False);
        Assert.That(spec.Steps.Select(s => s.Type),
            Is.EqualTo(new[] { "SetTime", "Screenshot", "SetTime", "Screenshot", "SetTime", "Screenshot" }));
        Assert.That(spec.Steps[1].Args["fileName"], Is.EqualTo("dc_0000.png"));
    }

    [Test]
    public void LoadFromJson_InvalidTimelapse_SurfacesLoadErrorAndKeepsStep()
    {
        const string json = """
        {
          "steps": [ { "type": "Timelapse", "args": { "stepHours": "0" } } ]
        }
        """;

        ScenarioSpec spec = ScenarioSpecLoader.LoadFromJson(json);

        Assert.That(spec.LoadErrors, Has.Count.EqualTo(1));
        Assert.That(spec.Steps, Has.Count.EqualTo(1));
        Assert.That(spec.Steps[0].Type, Is.EqualTo("Timelapse"));
    }

    [Test]
    public void LoadFromJson_ParsesRequiredMods()
    {
        const string json = """
        {
          "name": "with_dubs_skylights",
          "requiredMods": { "Dubwise.DubsSkylights": "833899765" }
        }
        """;

        ScenarioSpec spec = ScenarioSpecLoader.LoadFromJson(json);

        Assert.That(spec.RequiredMods, Has.Count.EqualTo(1));
        Assert.That(spec.RequiredMods["Dubwise.DubsSkylights"], Is.EqualTo("833899765"));
    }
}
