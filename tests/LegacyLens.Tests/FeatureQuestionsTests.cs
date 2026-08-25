using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// The questions a codebase's unknown column comes to.
///
/// M26 measured that the unit of decision is the feature, not the type: two
/// attributes that turn off request validation were 596 of 857 calls, answered
/// by one sentence. What was left came to forty-four types, and forty-four rows
/// is not forty-four decisions. `ScriptBundle`, `StyleBundle`,
/// `CssRewriteUrlTransform` and `IItemTransform` are one question about
/// bundling.
///
/// On nopCommerce 3.90 those forty-four come to six questions, and twenty-one
/// of the forty-four are third-party names the syntax attributed to the wrong
/// package. Printing forty-four rows hands somebody a list to look up; printing
/// six hands them the decisions.
/// </summary>
public class FeatureQuestionsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"features-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private Features Written(string json)
    {
        File.WriteAllText(_path, json);
        return Features.Load(_path);
    }

    private static IReadOnlyList<ApiUse> Using(params (string Name, int Uses)[] types) =>
        types.Select(t => new ApiUse(t.Name, t.Uses, 1)).ToList();

    private const string Catalogue = """
        {
          "Old.Package": [
            {
              "name": "Bundling",
              "types": ["ScriptBundle", "StyleBundle", "IItemTransform"],
              "was": "It bundled things.",
              "now": "Nothing does.",
              "options": ["Build them outside.", "Serve them unbundled."]
            },
            {
              "name": "Display modes",
              "types": ["IDisplayMode"],
              "was": "It picked a view per device.",
              "now": "Removed.",
              "options": ["Serve one responsive view."]
            }
          ]
        }
        """;

    [Fact]
    public void Scattered_types_come_back_as_one_question()
    {
        var asked = Written(Catalogue).Ask("Old.Package",
            Using(("ScriptBundle", 3), ("StyleBundle", 2), ("IItemTransform", 1)));

        var one = Assert.Single(asked);

        Assert.Equal("Bundling", one.Feature.Name);
        Assert.Equal(6, one.Uses);
        Assert.Equal(3, one.Through.Count);
    }

    [Fact]
    public void A_feature_this_codebase_never_touched_is_not_a_question_it_has()
    {
        // The catalogue describes a framework. A report describes one codebase,
        // and listing what it does not use would be padding the decisions with
        // decisions nobody has to make.
        var asked = Written(Catalogue).Ask("Old.Package", Using(("ScriptBundle", 1)));

        Assert.Equal("Bundling", Assert.Single(asked).Feature.Name);
    }

    [Fact]
    public void A_type_no_feature_covers_is_left_out_rather_than_given_one_of_its_own()
    {
        // The gap is real and burying it would be the whole problem. On
        // nopCommerce twenty-one of the forty-four are third-party names the
        // syntax attributed to the wrong package, and inventing a feature for
        // `SqlConnection` would make that invisible.
        var asked = Written(Catalogue).Ask("Old.Package",
            Using(("ScriptBundle", 1), ("SqlConnection", 40)));

        var one = Assert.Single(asked);

        Assert.DoesNotContain(one.Through, t => t.Name == "SqlConnection");
    }

    [Fact]
    public void The_heaviest_question_comes_first()
    {
        var asked = Written(Catalogue).Ask("Old.Package",
            Using(("IDisplayMode", 2), ("ScriptBundle", 30)));

        Assert.Equal("Bundling", asked[0].Feature.Name);
    }

    [Fact]
    public void Every_feature_says_what_it_was_what_replaced_it_and_what_to_choose_between()
    {
        // Read off the file that ships. A feature with no options is a heading
        // over nothing, and one with no "now" tells somebody their code is
        // broken without saying what the world looks like instead.
        var features = Features.Load();

        Assert.True(features.Count > 0, $"the catalogue was not found: {features.Source}");

        foreach (var feature in features.For("Microsoft.AspNet.Mvc"))
        {
            Assert.False(string.IsNullOrWhiteSpace(feature.Was), $"{feature.Name}: no was");
            Assert.False(string.IsNullOrWhiteSpace(feature.Now), $"{feature.Name}: no now");
            Assert.NotEmpty(feature.Types);
            Assert.True(feature.Options.Count >= 2,
                $"{feature.Name}: a single option is a recommendation wearing a list");
        }
    }

    [Fact]
    public void Bundling_belongs_to_the_package_that_defines_it()
    {
        // It lived under Microsoft.AspNet.Mvc while the surface counted
        // ScriptBundle as MVC's work. M28 stopped doing that, correctly, and
        // the question stopped being asked anywhere at all because
        // Microsoft.AspNet.Web.Optimization was not in the catalogue.
        var features = Features.Load();

        Assert.Empty(features.For("Microsoft.AspNet.Mvc")
            .Where(f => f.Name.Contains("Bundling", StringComparison.Ordinal)));

        var bundling = Assert.Single(features.For("Microsoft.AspNet.Web.Optimization"));

        Assert.Contains("ScriptBundle", bundling.Types);
        Assert.True(bundling.Options.Count >= 2);
    }

    [Fact]
    public void A_catalogue_that_is_not_there_says_so_rather_than_reading_as_empty()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json");

        var features = Features.Load(missing);

        Assert.Equal(0, features.Count);
        Assert.Contains(missing, features.Source);
    }

    [Fact]
    public void And_one_that_will_not_parse_says_that_too()
    {
        var features = Written("{ not json");

        Assert.Equal(0, features.Count);
        Assert.Contains("could not be read", features.Source);
    }
}
