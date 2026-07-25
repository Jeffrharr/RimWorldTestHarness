using System.Collections.Generic;
using System.IO;

namespace RimWorldTestHarness.Shared;

// Parses a suite list file: the selection mechanism for "run these scenarios in one game load".
//
// A plain newline-delimited list of scenario paths was chosen over a delimited env var or a JSON
// suite format for three reasons:
//
//   * Paths can contain ':' and spaces, so a PATH-style delimited env var needs quoting rules that
//     a hand-written list file doesn't.
//   * It is trivially generated (Runner/run_test.sh writes one from its CLI args) AND trivially
//     hand-authored, so "run all scenarios" and "run my three flaky ones" use the same mechanism.
//   * The generated list lands in the run's report folder, so a run's artifacts record exactly which
//     scenarios it was asked to cover — a suite whose membership can't be reconstructed afterwards
//     is a suite whose green result means less than it looks like.
//
// Deliberately NOT a glob: the harness never expands one itself, because "all scenarios" silently
// growing when someone drops a file in Scenarios/ is a surprise, and the shell already globs.
public static class SuiteList
{
    private const char CommentMarker = '#';

    // Relative entries resolve against baseDir (the list file's own directory), so a checked-in
    // suite file next to the scenarios it names stays portable between checkouts and worktrees.
    // Pure string work — nothing here touches the filesystem, so it is fully offline-testable.
    public static List<string> Parse(string text, string baseDir, List<string> errors)
    {
        List<string> paths = new List<string>();
        string[] lines = text.Replace("\r\n", "\n").Split('\n');

        for (int i = 0; i < lines.Length; i++)
            AddEntry(lines[i], i + 1, baseDir, paths, errors);

        // An empty suite must fail rather than vacuously succeed. This is the same hazard as
        // ReportComparer.AllPass over an empty check list: "nothing was verified" reads identically
        // to "everything passed" unless something says otherwise.
        if (paths.Count == 0)
            errors.Add("suite list is empty — no scenarios to run");

        return paths;
    }

    private static void AddEntry(string rawLine, int lineNumber, string baseDir,
                                 List<string> paths, List<string> errors)
    {
        string entry = StripComment(rawLine).Trim();
        if (entry.Length == 0)
            return;

        string resolved = Resolve(entry, baseDir);

        // A duplicate would run the same scenario twice under the same name, so its two reports (and
        // its two sets of screenshots, which share one filename prefix) would be indistinguishable.
        if (paths.Contains(resolved))
        {
            errors.Add($"suite list line {lineNumber}: '{entry}' is listed more than once");
            return;
        }

        paths.Add(resolved);
    }

    // Only a WHOLE-LINE comment is honoured. A trailing '#' is not treated as a comment because '#'
    // is legal in a filename, and silently truncating a path at one would look like a missing file.
    private static string StripComment(string line) =>
        line.TrimStart().StartsWith(CommentMarker.ToString()) ? "" : line;

    private static string Resolve(string entry, string baseDir) =>
        Path.IsPathRooted(entry) || baseDir.Length == 0 ? entry : Path.Combine(baseDir, entry);
}
