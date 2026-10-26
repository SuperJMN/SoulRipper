using SoulRipper.Hints;

namespace SoulRipper.Tests;

public class TitlesTests
{
    [Fact]
    public void No_intro_and_rip_names_normalize_alike()
    {
        const string rip = "Legend%20of%20Zelda,%20The%20(1986-02-21)(Nintendo)%5bNES%5d.7z";

        Assert.Contains(Titles.Normalize("Legend of Zelda, The (USA) (Rev A)"), Titles.RipTitles(rip).Select(Titles.Normalize));
    }

    [Fact]
    public void Alternative_titles_in_brackets_are_candidates()
    {
        const string rip = "Super%20Mario%20Land%202%20-%206%20Golden%20Coins%20%5bSuper%20Mario%20Land%202%20-%206%20Tsu%20no%20Kinka%5d%20(1992)%5bGB%5d.7z";

        Assert.Contains("Super Mario Land 2 - 6 Tsu no Kinka", Titles.RipTitles(rip));
    }

    [Fact]
    public void Display_name_drops_tags_and_moves_articles()
    {
        Assert.Equal("The Legend of Zelda", Titles.DisplayName("Legend of Zelda, The (USA) (Rev A)"));
    }

    [Fact]
    public void Similar_titles_score_high_and_different_ones_low()
    {
        double Score(string a, string b) => SequenceMatcher.Ratio(Titles.Normalize(a).ToCharArray(), Titles.Normalize(b).ToCharArray());

        Assert.True(Score("Super Mario Land (World)", "Super Mario Land") >= 0.9);
        Assert.True(Score("Super Mario Land (World)", "Super Mario Land 2 - 6 Golden Coins") < 0.9);
    }
}
