using System.Reflection;
using LegacyLens.Characterization;

namespace LegacyLens.Tests;

/// <summary>
/// A method taking anything but a primitive used to produce no test at all,
/// because there was no value to call it with. These cover the composite
/// values that close that gap, and the one property that makes them usable:
/// what gets written into the test file has to rebuild the object the method
/// was actually called with, or the suite pins a behaviour nobody exercised.
/// </summary>
public class ValuesTests
{
    [Fact]
    public void A_plain_data_type_now_has_values()
    {
        Assert.NotEmpty(Values.For(typeof(Address)));
    }

    [Fact]
    public void The_literal_is_an_object_initialiser()
    {
        var literal = Values.Literal(Values.For(typeof(Address))[0]);

        Assert.StartsWith("new global::LegacyLens.Tests.Address {", literal);
        Assert.Contains("City = ", literal);
        Assert.Contains("Number = ", literal);
    }

    /// <summary>
    /// The property that matters. The written form is read back from the same
    /// object, so every assignment in the literal has to carry the value the
    /// instance actually holds.
    /// </summary>
    [Fact]
    public void The_literal_carries_the_values_the_object_holds()
    {
        foreach (var value in Values.For(typeof(Address)))
        {
            var address = Assert.IsType<Address>(value);
            var literal = Values.Literal(address);

            Assert.Contains($"City = {Values.Literal(address.City)}", literal);
            Assert.Contains($"Number = {Values.Literal(address.Number)}", literal);
        }
    }

    [Fact]
    public void Variants_differ_from_one_another()
    {
        var written = Values.For(typeof(Address)).Select(Values.Literal).Distinct().ToList();

        Assert.True(written.Count > 1, "every variant produced the same object");
    }

    [Fact]
    public void A_nested_data_type_is_built_one_level_down()
    {
        var literal = Values.Literal(Values.For(typeof(Recipient))[0]);

        Assert.Contains("Home = new global::LegacyLens.Tests.Address {", literal);
    }

    [Fact]
    public void Framework_types_are_refused()
    {
        // Reflection reports a parameterless constructor on plenty of framework
        // types whose meaning has nothing to do with the one this would invent.
        Assert.Empty(Values.For(typeof(System.Text.StringBuilder)));
        Assert.Empty(Values.For(typeof(Stream)));
    }

    [Fact]
    public void A_type_without_a_parameterless_constructor_is_refused()
    {
        Assert.Empty(Values.For(typeof(NeedsArguments)));
    }

    [Fact]
    public void A_type_with_nothing_settable_is_refused()
    {
        Assert.Empty(Values.For(typeof(ReadOnlyThing)));
    }

    [Fact]
    public void Cases_are_produced_for_a_composite_parameter()
    {
        Assert.NotEmpty(Values.Cases([typeof(Address)], limit: 3));
    }

    /// <summary>
    /// End to end, through the real generator: the values are built, the method
    /// is invoked with them, the literal is written into a suite, and the suite
    /// is compiled and run before anything is kept. A file coming out the far
    /// side is the whole chain working.
    /// </summary>
    [Fact]
    public void The_generated_suite_compiles_and_runs()
    {
        var run = new Characterizer { CasesPerMethod = 3, Types = t => t == typeof(LabelMaker) }
            .Run(typeof(ValuesTests).Assembly);

        var why = string.Join("; ", run.Skipped.Select(s => $"{s.Member}={s.Reason}"));
        Assert.True(run.Files.Count == 1, $"files={run.Files.Count} skipped=[{why}]");
        var file = run.Files[0];
        Assert.True(file.Cases > 0, "no case survived being compiled and run");
        Assert.Contains("new global::LegacyLens.Tests.Address {", file.Source);
    }
}

public class Address
{
    public string City { get; set; } = string.Empty;
    public int Number { get; set; }
}

public class Recipient
{
    public string Name { get; set; } = string.Empty;
    public Address Home { get; set; } = new();
}

public class NeedsArguments
{
    public NeedsArguments(int required) => Required = required;

    public int Required { get; set; }
}

public class ReadOnlyThing
{
    public int Fixed => 1;
}

/// <summary>Takes a composite, which used to mean no test at all.</summary>
public class LabelMaker
{
    public string Label(Address address) => $"{address.Number} {address.City}";
}
