namespace Emerald.Core;

/// <summary>
/// Where a recording's files go and what they are called.
///
/// <paramref name="Folder"/> is the per-recording folder, stamped with the moment Record was
/// pressed. <paramref name="Stem"/> is the filename ffmpeg is handed, with its <c>%d</c> intact
/// and <b>no extension</b> — the two outputs write the same names with different ones, so the
/// extension belongs to the output rather than to the naming.
/// </summary>
public readonly record struct RecordingNames(string Folder, string Stem, long Start)
{
    /// <summary>The pattern for one output, extension and all.</summary>
    public string PatternFor(string extension) => $"{Stem}.{extension}";

    /// <summary>What the first file will be called, for a log line or a label.</summary>
    public string FirstFile(string extension) =>
        $"{Stem.Replace("%d", Start.ToString())}.{extension}";
}

/// <summary>
/// Where a recording's segments go and what they are numbered.
///
/// <b>Why a counter and not a timestamp in the filename.</b> ffmpeg's segment muxer will give a
/// name either a timestamp or a sequence number, never both: under <c>-strftime 1</c> the
/// pattern goes to strftime, where <c>%d</c> is the day of the month — so a pattern asking for
/// both writes every segment of a recording to one filename and keeps only the last. Verified,
/// not assumed. Emerald therefore formats the stamp itself, into the folder name and as a
/// literal in the filename, and hands ffmpeg only the <c>%d</c>.
///
/// <b>The number restarts at 1 for every recording.</b> Each recording has a folder of its own
/// stamped with the moment it rolled, and that stamp is in the filename too — so a number only
/// has to be unique inside its folder, and starting again at one is what makes a file easy to
/// place by eye. Nothing is carried between recordings, which also means there is no counter to
/// be left behind by a crash, or thrown backwards when the storage limit prunes the oldest
/// recording away.
/// </summary>
public static class RecordingSequence
{
    /// <summary>How the per-recording folder is named. Sorts chronologically as text.</summary>
    public const string FolderFormat = "yyyy-MM-dd_HH-mm-ss";

    /// <summary>The number the first segment of a recording takes.</summary>
    public const int FirstNumber = 1;

    /// <summary>
    /// What a recording's files are called when the operator has not named them.
    ///
    /// One constant rather than the five literals this used to be, in five files, which is how
    /// it came to be changed in some of them and not the others.
    /// </summary>
    public const string DefaultPrefix = "media";

    /// <summary>
    /// What the default used to be, so a settings file still holding it can be told from a title
    /// the operator actually chose.
    ///
    /// It was written into the settings the first time the deck opened, so it is not a choice —
    /// and leaving it would have every machine that has ever run Emerald keep writing files under
    /// the old name. A title anyone typed deliberately is left exactly as it is.
    /// </summary>
    public const string FormerDefaultPrefix = "capture";

    /// <summary>The prefix to use for a saved or typed title, falling back to the default.</summary>
    public static string PrefixOr(string? title)
    {
        string trimmed = (title ?? "").Trim();

        return trimmed.Length == 0 ||
               trimmed.Equals(FormerDefaultPrefix, StringComparison.OrdinalIgnoreCase)
            ? DefaultPrefix
            : trimmed;
    }

    /// <summary>
    /// The folder and the filename pattern for a recording about to roll.
    ///
    /// Shaped as <c>prefix_MMddyyHHmm_N</c>, e.g. <c>media_0930262209_1</c>. The date and time
    /// are the moment the recording rolled rather than the segment's own, so they are the same on
    /// every file of a recording and can be written as a literal — which is what makes a sequence
    /// number possible at all.
    ///
    /// The number is unpadded. Nothing here sorts by name — segments are ordered by the number
    /// itself, so 2 and 10 fall the right way round. File Explorer does sort by name, and there
    /// 10 comes before 2.
    /// </summary>
    public static RecordingNames Next(DateTime startedAt, string namePrefix)
    {
        string folder = startedAt.ToString(FolderFormat);
        string stem = $"{PrefixOr(namePrefix)}_{startedAt:MMddyy}{startedAt:HHmm}_%d";

        return new RecordingNames(folder, stem, FirstNumber);
    }

    /// <summary>
    /// The number out of a filename written under this scheme, or null when the name is not one
    /// of ours.
    ///
    /// Reads the last underscore-separated run of digits, so it holds for
    /// <c>media_0930262209_12</c> and for a prefix that itself contains digits or underscores —
    /// which an operator's own recording title very well might.
    /// </summary>
    public static long? NumberIn(string fileName)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(fileName);

        int at = stem.LastIndexOf('_');
        if (at < 0 || at == stem.Length - 1) return null;

        string tail = stem[(at + 1)..];

        return tail.Length > 0 && tail.All(char.IsAsciiDigit) && long.TryParse(tail, out long n)
            ? n
            : null;
    }

    /// <summary>
    /// The moment a per-recording folder was made, from its name — or null for a folder that is
    /// not one of ours.
    /// </summary>
    public static DateTime? FolderStartedAt(string folderName) =>
        DateTime.TryParseExact(folderName, FolderFormat, null,
                               System.Globalization.DateTimeStyles.None, out DateTime when)
            ? when
            : null;
}
