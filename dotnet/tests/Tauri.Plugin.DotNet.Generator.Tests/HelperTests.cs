using Tauri.Plugin.DotNet.Generator;

namespace Tauri.Plugin.DotNet.Generator.Tests;

public class HelperTests
{
    #region ToCamelCase

    [Theory]
    [InlineData("Name", "name")]
    [InlineData("name", "name")]
    [InlineData("", "")]
    [InlineData("A", "a")]
    [InlineData("ABC", "aBC")]
    [InlineData("GetPerson", "getPerson")]
    public void ToCamelCase_ConvertsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, StringHelpers.ToCamelCase(input));
    }

    #endregion

    #region ToPascalCase

    [Theory]
    [InlineData("progress", "Progress")]
    [InlineData("task_completed", "TaskCompleted")]
    [InlineData("task-completed", "TaskCompleted")]
    [InlineData("", "")]
    [InlineData("Already", "Already")]
    [InlineData("a", "A")]
    [InlineData("multi_word_event", "MultiWordEvent")]
    [InlineData("kebab-case-event", "KebabCaseEvent")]
    public void ToPascalCase_ConvertsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, StringHelpers.ToPascalCase(input));
    }

    #endregion

    #region FormatEnumValue

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(42, "42")]
    [InlineData(-1, "-1")]
    public void FormatEnumValue_NumericValue_ReturnsNumber(int value, string expected)
    {
        Assert.Equal(expected, StringHelpers.FormatEnumValue(value));
    }

    [Fact]
    public void FormatEnumValue_StringValue_ReturnsQuoted()
    {
        Assert.Equal("\"hello\"", StringHelpers.FormatEnumValue("hello"));
    }

    [Fact]
    public void FormatEnumValue_Null_ReturnsZero()
    {
        Assert.Equal("0", StringHelpers.FormatEnumValue(null));
    }

    #endregion
}
