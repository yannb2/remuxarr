using System.Text;
using Muxarr.Core.Utilities;

namespace Muxarr.Core.Config;

public class ProcessingConfig
{
    public int ScanIntervalMinutes { get; set; }
    public int ConversionTimeoutMinutes { get; set; } = 60;
    public bool QueueScannedFiles { get; set; }

    /// <summary>
    /// Folders the scan skips, one per line: a name ("Extras-old") matches anywhere in
    /// the path, a full path ("/movies/other/incoming") only that folder.
    /// </summary>
    public string ExcludedFolders { get; set; } = string.Empty;

    public List<string> GetExcludedFolders()
    {
        return PathFilter.ParseExcludedFolders(ExcludedFolders);
    }

    /// <summary>
    /// Fork: the folders outside the library that Remuxarr may read, one per line: the
    /// Copy subtitles page takes donor files from them, the Remux a file page picks the
    /// file to remux in them. They have to be mounted in the container (read-only is enough).
    /// </summary>
    public string DonorFolders { get; set; } = "/downloads\n/mnt/pc";

    public List<string> GetDonorFolders()
    {
        return DonorFolders.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .Select(line => line.Length > 1 ? line.TrimEnd('/') : line)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Fork: where the Remux a file page writes its results. The only folder outside the
    /// library Remuxarr writes to, so it has to be mounted read-write.
    /// </summary>
    public string CleanFolder { get; set; } = DefaultCleanFolder;

    public const string DefaultCleanFolder = "/downloads/import/clean";

    public string GetCleanFolder()
    {
        var folder = CleanFolder.Trim();
        return folder.Length == 0 ? DefaultCleanFolder : folder.Length > 1 ? folder.TrimEnd('/') : folder;
    }

    public bool PostProcessingEnabled { get; set; }
    public string PostProcessingCommand { get; set; } = string.Empty;

    private static readonly (string Placeholder, string Variable)[] ShellVariables =
    [
        ("{{filename}}", "MUXARR_FILENAME"),
        ("{{directory}}", "MUXARR_DIRECTORY"),
        ("{{file}}", "MUXARR_FILE"),
        ("{filename}", "MUXARR_FILENAME"),
        ("{directory}", "MUXARR_DIRECTORY"),
        ("{file}", "MUXARR_FILE")
    ];

    public static Dictionary<string, string> ShellEnvironment(string filePath)
    {
        return new Dictionary<string, string>
        {
            ["MUXARR_FILE"] = filePath,
            ["MUXARR_FILENAME"] = Path.GetFileNameWithoutExtension(filePath),
            ["MUXARR_DIRECTORY"] = Path.GetDirectoryName(filePath) ?? string.Empty
        };
    }

    // Placeholders become quoted references to ShellEnvironment's variables, so a file
    // name like "$(touch x).mkv" stays data instead of running as part of the command.
    public string BuildShellScript()
    {
        var command = PostProcessingCommand;
        var script = new StringBuilder(command.Length + 32);
        var quote = '\0';
        var outerQuotes = new Stack<char>();

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];

            if (quote != '\'' && c == '\\' && i + 1 < command.Length)
            {
                script.Append(c).Append(command[++i]);
                continue;
            }

            // A comment runs to the end of the line, so the apostrophe in "don't" is not a quote.
            if (quote == '\0' && c == '#' && (i == 0 || char.IsWhiteSpace(command[i - 1]) || command[i - 1] is ';' or '&' or '|'))
            {
                var end = command.IndexOf('\n', i);
                end = end < 0 ? command.Length : end;
                script.Append(command, i, end - i);
                i = end - 1;
                continue;
            }

            // Quoting starts over inside $( ), even within double quotes: "$(basename "{file}")".
            if (quote != '\'' && c == '$' && i + 1 < command.Length && command[i + 1] == '(')
            {
                outerQuotes.Push(quote);
                quote = '\0';
                script.Append("$(");
                i++;
                continue;
            }

            if (quote == '\0' && c == '(')
            {
                outerQuotes.Push('\0');
            }
            else if (quote == '\0' && c == ')' && outerQuotes.Count > 0)
            {
                quote = outerQuotes.Pop();
            }

            var match = ShellVariables.FirstOrDefault(v =>
                string.CompareOrdinal(command, i, v.Placeholder, 0, v.Placeholder.Length) == 0);
            if (match.Placeholder != null)
            {
                script.Append(quote switch
                {
                    '"' => $"${{{match.Variable}}}",
                    '\'' => $"'\"${{{match.Variable}}}\"'",
                    _ => $"\"${{{match.Variable}}}\""
                });
                i += match.Placeholder.Length - 1;
                continue;
            }

            if (c is '"' or '\'' && (quote == '\0' || quote == c))
            {
                quote = quote == c ? '\0' : c;
            }

            script.Append(c);
        }

        return script.ToString();
    }

    public string ResolveCommand(string filePath)
    {
        var environment = ShellEnvironment(filePath);
        return ShellVariables.Aggregate(PostProcessingCommand,
            (command, v) => command.Replace(v.Placeholder, environment[v.Variable]));
    }
}
