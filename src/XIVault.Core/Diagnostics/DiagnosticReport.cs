using System.Globalization;
using System.Text;

namespace XIVault.Core.Diagnostics;

public enum DiagnosticStatus
{
    Healthy,
    Warning,
    Error,
}

public enum DiagnosticArea
{
    XivLauncher,
    Dalamud,
    BackupDestination,
    Scheduling,
}

public sealed record DiagnosticCheck(string Label, DiagnosticStatus Status, string Detail);

public sealed record DiagnosticGroup(DiagnosticArea Area, string Title, IReadOnlyList<DiagnosticCheck> Checks)
{
    public DiagnosticStatus Status => Checks.Count == 0 ? DiagnosticStatus.Healthy : Checks.Max(check => check.Status);
}

public sealed record DiagnosticReport(DateTime GeneratedAtUtc, string XivaultVersion, string OsVersion, IReadOnlyList<DiagnosticGroup> Groups)
{
    public IEnumerable<DiagnosticCheck> AllChecks => Groups.SelectMany(group => group.Checks);

    public int Passed => AllChecks.Count(check => check.Status == DiagnosticStatus.Healthy);

    public int Warnings => AllChecks.Count(check => check.Status == DiagnosticStatus.Warning);

    public int Errors => AllChecks.Count(check => check.Status == DiagnosticStatus.Error);

    public DiagnosticStatus Overall => Groups.Count == 0 ? DiagnosticStatus.Healthy : Groups.Max(group => group.Status);

    /// <summary>"13 checks passed · 1 suggestion".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { Formatting.Count(Passed, "check") + " passed" };
            if (Warnings > 0)
            {
                parts.Add(Formatting.Count(Warnings, "suggestion"));
            }

            if (Errors > 0)
            {
                parts.Add(Formatting.Count(Errors, "problem"));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Plain text for pasting into an issue. Holds paths, versions and results only.</summary>
    public string ToText()
    {
        var text = new StringBuilder()
            .AppendLine("XIVault diagnostic report")
            .Append(CultureInfo.InvariantCulture, $"Generated {GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC · XIVault {XivaultVersion} · {OsVersion}")
            .AppendLine()
            .AppendLine();
        foreach (var group in Groups)
        {
            text.Append(CultureInfo.InvariantCulture, $"{group.Title} [{Label(group.Status)}]").AppendLine();
            foreach (var check in group.Checks)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {Marker(check.Status)} {check.Label}");
                if (check.Detail.Length > 0)
                {
                    text.Append(" — ").Append(check.Detail);
                }

                text.AppendLine();
            }

            text.AppendLine();
        }

        return text
            .AppendLine(Summary)
            .AppendLine("Paths, versions and results only. Plugin configuration contents are never included.")
            .ToString();
    }

    public static string Label(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Healthy => "Healthy",
        DiagnosticStatus.Warning => "Suggestion",
        _ => "Problem",
    };

    private static string Marker(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Healthy => "[ok]  ",
        DiagnosticStatus.Warning => "[warn]",
        _ => "[fail]",
    };
}
