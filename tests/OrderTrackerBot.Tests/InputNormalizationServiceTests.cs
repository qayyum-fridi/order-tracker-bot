using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public class InputNormalizationServiceTests
{
    [Theory]
    [InlineData("Rs. 5,000/-", "5000")]
    [InlineData("Rs 1,250", "1250")]
    [InlineData("₨ 300/-", "300")]
    [InlineData("1,23 rupay", "1,23 rupay")]
    [InlineData("order 12 ka 2,500 ka bill", "order 12 ka 2500 ka bill")]
    public void Currency_and_grouped_amounts_become_plain_digits(string raw, string expected) =>
        Assert.Equal(expected, InputNormalizationService.Normalize(raw).Text);

    [Theory]
    [InlineData("umm mark 3 shipped", "mark 3 shipped")]
    [InlineData("hmm, Ali ka order 2 uh", "Ali ka order 2")]
    public void Voice_fillers_are_removed_from_transcripts(string raw, string expected) =>
        Assert.Equal(expected, InputNormalizationService.Normalize(raw, InputModality.Voice).Text);

    [Fact]
    public void Typed_text_keeps_fillers_because_they_are_seller_words() =>
        Assert.Equal("umm mark 3 shipped", InputNormalizationService.Normalize("umm mark 3 shipped").Text);

    [Fact]
    public void Filler_inside_a_word_is_never_removed() =>
        Assert.Equal("humara order 4", InputNormalizationService.Normalize("humara order 4", InputModality.Voice).Text);

    [Fact]
    public void Original_text_is_kept_unchanged_next_to_normalized_text()
    {
        var result = InputNormalizationService.Normalize("Rs. 5,000/-", InputModality.Voice);

        Assert.Equal("Rs. 5,000/-", result.Original);
        Assert.Equal("5000", result.Text);
        Assert.Equal(InputModality.Voice, result.Modality);
    }

    [Fact]
    public void Language_is_urdu_for_urdu_script_and_roman_otherwise()
    {
        Assert.Equal("ur", InputNormalizationService.Normalize("آرڈر 5 شپ کریں").Language);
        Assert.Equal("roman", InputNormalizationService.Normalize("mark 5 shipped").Language);
    }

    [Fact]
    public void Missing_input_normalizes_to_empty_text() =>
        Assert.Equal(string.Empty, InputNormalizationService.Normalize(null).Text);
}
