using Webb;
using Xunit;

namespace Webb.Tests
{
    public class SseQueryParserTests
    {
        [Fact]
        public void EmptyQuery_ReturnsEmptyFilter()
        {
            var filter = SseQueryParser.Parse("");
            Assert.True(filter.Wants("anything"));
        }

        [Fact]
        public void NoEventsParam_ReturnsEmptyFilter()
        {
            var filter = SseQueryParser.Parse("?other=value");
            Assert.True(filter.Wants("lap_detected"));
        }

        [Fact]
        public void SinglePositiveEvent_BuildsIncludeFilter()
        {
            var filter = SseQueryParser.Parse("?events=lap_detected");
            Assert.True(filter.Wants("lap_detected"));
            Assert.False(filter.Wants("race_start"));
        }

        [Fact]
        public void MultiplePositiveEvents_AllIncluded()
        {
            var filter = SseQueryParser.Parse("?events=lap_detected,race_start");
            Assert.True(filter.Wants("lap_detected"));
            Assert.True(filter.Wants("race_start"));
            Assert.False(filter.Wants("race_end"));
        }

        [Fact]
        public void SingleNegativeEvent_BuildsExcludeFilter()
        {
            var filter = SseQueryParser.Parse("?events=-lap_detected");
            Assert.False(filter.Wants("lap_detected"));
            Assert.True(filter.Wants("race_start"));
            Assert.True(filter.Wants("race_end"));
        }

        [Fact]
        public void MultipleNegativeEvents_AllExcluded()
        {
            var filter = SseQueryParser.Parse("?events=-race_time_remaining,-split_detection");
            Assert.False(filter.Wants("race_time_remaining"));
            Assert.False(filter.Wants("split_detection"));
            Assert.True(filter.Wants("race_start"));
            Assert.True(filter.Wants("lap_detected"));
        }

        [Fact]
        public void MixedPositiveAndNegative_AppliesBothFilters()
        {
            var filter = SseQueryParser.Parse("?events=race_start,-race_changed");
            Assert.True(filter.Wants("race_start"));
            Assert.False(filter.Wants("race_changed"));
            Assert.False(filter.Wants("lap_detected")); // not in include list
        }

        [Fact]
        public void WhitespaceAroundNames_IsTrimmed()
        {
            var filter = SseQueryParser.Parse("?events= lap_detected , race_start ");
            Assert.True(filter.Wants("lap_detected"));
            Assert.True(filter.Wants("race_start"));
        }

        [Fact]
        public void EmptySegmentsFromTrailingComma_AreIgnored()
        {
            var filter = SseQueryParser.Parse("?events=lap_detected,");
            Assert.True(filter.Wants("lap_detected"));
        }

        [Fact]
        public void Parsing_IsCaseInsensitive()
        {
            var filter = SseQueryParser.Parse("?events=Lap_Detected");
            Assert.True(filter.Wants("lap_detected"));
            Assert.True(filter.Wants("LAP_DETECTED"));
        }
    }
}
