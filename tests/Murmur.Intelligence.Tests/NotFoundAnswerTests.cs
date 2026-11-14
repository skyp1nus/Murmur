using Murmur.Intelligence.Rag;

namespace Murmur.Intelligence.Tests;

public sealed class NotFoundAnswerTests
{
    [Theory]
    [InlineData("У записах цього немає.")]
    [InlineData("Про це в записі нічого не згадується.")]
    [InlineData("Я не знайшов такої інформації.")]
    [InlineData("Інформація про зарплату відсутня.")]
    [InlineData("This is not mentioned in the recording.")]
    [InlineData("I couldn't find anything about it.")]
    [InlineData("The recording doesn’t say.")]
    [InlineData("The excerpts DO NOT CONTAIN that.")]
    public void IsNotFound_RecognizesUkrainianAndEnglish(string text) => Assert.True(NotFoundAnswer.IsNotFound(text));

    [Theory]
    [InlineData("Бюджет затвердили: 1,2 мільйона гривень [07:42].")]
    [InlineData("The team picked Redis [16:27].")]
    [InlineData("")]
    public void IsNotFound_IgnoresRealAnswers(string text) => Assert.False(NotFoundAnswer.IsNotFound(text));
}
