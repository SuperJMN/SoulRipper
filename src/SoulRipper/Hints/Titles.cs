using System.Text.RegularExpressions;

namespace SoulRipper.Hints;

// Game titles as No-Intro and the rip sites write them.
internal static class Titles
{
    // Comparable form of a title: no tags, articles, punctuation or case.
    public static string Normalize(string title)
    {
        title = Regex.Replace(title, @"\([^)]*\)|\[[^\]]*\]", " ");
        title = Regex.Replace(title.Trim(), @"^(.*), (The|A|An)\b", "$2 $1");
        title = Regex.Replace(title.ToLowerInvariant(), @"\b(the|a|an)\b", " ");
        return Regex.Replace(title, "[^a-z0-9]+", "");
    }

    // Titles a rip set's file name stands for: main title and [alternative] ones.
    public static List<string> RipTitles(string fileName)
    {
        var stem = Uri.UnescapeDataString(fileName);
        var dot = stem.LastIndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        var titles = new List<string> { Regex.Split(stem, @"\s*[\[(]")[0] };
        titles.AddRange(Regex.Matches(stem, @"\[([^\]]+)\]").Select(m => m.Groups[1].Value));
        return titles;
    }

    // "Legend of Zelda, The (USA) (Rev A)" -> "The Legend of Zelda".
    public static string DisplayName(string noIntroName)
    {
        var title = Regex.Replace(noIntroName, @"\s*[\[(].*$", "").Trim();
        return Regex.Replace(title, "^(.*), (The|A|An)$", "$2 $1");
    }
}
