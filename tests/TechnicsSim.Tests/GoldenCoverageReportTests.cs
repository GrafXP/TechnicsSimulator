using System.Text.Json;
using TechnicsSim.LDraw;
using TechnicsSim.LDraw.Reporting;

namespace TechnicsSim.Tests;

/// <summary>
/// Diffs the coverage report for each supplied MPD against a committed golden file.
///
/// The comparison excludes the source-provenance block, which holds absolute paths and the
/// library hash, so a diff means the model was interpreted differently rather than that the
/// checkout lives elsewhere. The committed file still records which library and shadow
/// revision produced it, for the human reading it later.
///
/// Regenerate deliberately with TECHNICSSIM_UPDATE_GOLDEN=1; a baseline should never move as
/// an invisible side effect of an unrelated change.
/// </summary>
public sealed class GoldenCoverageReportTests
{
    private const string UpdateEnvironmentVariable = "TECHNICSSIM_UPDATE_GOLDEN";

    private static string GoldenDirectory => Path.Combine(
        TestEnvironment.RepositoryRoot, "tests", "TechnicsSim.Tests", "Golden");

    public static TheoryData<string> Models => new()
    {
        "8275-1.mpd",
        "42055-1.mpd",
        "42100-1.mpd",
        "42121-1.mpd",
    };

    [ShadowTheory]
    [MemberData(nameof(Models))]
    public void MatchesTheCommittedGoldenReport(string fileName)
    {
        var model = ModelLoader.Load(
            Path.Combine(TestEnvironment.ModelsDirectory, fileName),
            [TestEnvironment.Library!]);

        var report = CoverageReportBuilder.Build(
            model, TestEnvironment.Shadow!, TestEnvironment.LibraryInfo, TestEnvironment.ShadowInfo);

        var goldenPath = Path.Combine(GoldenDirectory, $"{Path.GetFileNameWithoutExtension(fileName)}.json");

        if (Environment.GetEnvironmentVariable(UpdateEnvironmentVariable) == "1")
        {
            Directory.CreateDirectory(GoldenDirectory);
            File.WriteAllText(
                goldenPath,
                CoverageReportBuilder.ToJson(
                    report.WithNormalizedPaths(TestEnvironment.RepositoryRoot)));
            return;
        }

        Assert.True(
            File.Exists(goldenPath),
            $"Missing golden report '{goldenPath}'. Generate it with {UpdateEnvironmentVariable}=1.");

        var expected = JsonSerializer.Deserialize<CoverageReport>(
            File.ReadAllText(goldenPath), CoverageReportBuilder.JsonOptions)!;

        var expectedJson = CoverageReportBuilder.ToJson(expected.WithoutSourceProvenance());
        var actualJson = CoverageReportBuilder.ToJson(report.WithoutSourceProvenance());

        if (expectedJson != actualJson)
        {
            // Name both library revisions: the usual innocent cause of a diff is a different
            // parts library, not a regression in the code under test.
            Assert.Fail(
                $"Coverage report for {fileName} differs from the golden baseline."
                + $"{Environment.NewLine}  golden library : {expected.Sources.OfficialLibrary?.UpdateTag ?? "(none)"}"
                + $"{Environment.NewLine}  golden shadow  : {expected.Sources.ShadowLibrary?.Commit ?? "(none)"}"
                + $"{Environment.NewLine}  actual library : {report.Sources.OfficialLibrary?.UpdateTag ?? "(none)"}"
                + $"{Environment.NewLine}  actual shadow  : {report.Sources.ShadowLibrary?.Commit ?? "(none)"}"
                + $"{Environment.NewLine}{Environment.NewLine}{FirstDifference(expectedJson, actualJson)}");
        }
    }

    /// <summary>Reports the first differing line so failures are readable at a glance.</summary>
    private static string FirstDifference(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');

        for (var i = 0; i < Math.Min(expectedLines.Length, actualLines.Length); i++)
        {
            if (expectedLines[i] != actualLines[i])
            {
                return $"  first difference at line {i + 1}:"
                    + $"{Environment.NewLine}    expected: {expectedLines[i].Trim()}"
                    + $"{Environment.NewLine}    actual  : {actualLines[i].Trim()}";
            }
        }

        return $"  line counts differ: expected {expectedLines.Length}, actual {actualLines.Length}";
    }
}
