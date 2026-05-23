using System;
using System.Collections.Generic;
using Webb;
using Xunit;

namespace Webb.Tests
{
    public class SseEventFilterTests
    {
        // --- Wants() ---

        [Fact]
        public void EmptyFilter_AllowsAllEvents()
        {
            var filter = Filter(include: [], exclude: []);
            Assert.True(filter.Wants("lap_detected"));
            Assert.True(filter.Wants("race_start"));
            Assert.True(filter.Wants("anything"));
        }

        [Fact]
        public void IncludeFilter_AllowsOnlyListedEvents()
        {
            var filter = Filter(include: ["lap_detected", "race_start"], exclude: []);
            Assert.True(filter.Wants("lap_detected"));
            Assert.True(filter.Wants("race_start"));
            Assert.False(filter.Wants("race_end"));
            Assert.False(filter.Wants("split_detection"));
        }

        [Fact]
        public void ExcludeFilter_BlocksListedEvents()
        {
            var filter = Filter(include: [], exclude: ["lap_detected"]);
            Assert.False(filter.Wants("lap_detected"));
            Assert.True(filter.Wants("race_start"));
            Assert.True(filter.Wants("race_end"));
        }

        [Fact]
        public void ExcludeFilter_MultipleExclusions()
        {
            var filter = Filter(include: [], exclude: ["race_time_remaining", "split_detection"]);
            Assert.False(filter.Wants("race_time_remaining"));
            Assert.False(filter.Wants("split_detection"));
            Assert.True(filter.Wants("lap_detected"));
            Assert.True(filter.Wants("race_start"));
        }

        [Fact]
        public void ExcludeTakesPrecedenceOverInclude()
        {
            var filter = Filter(include: ["lap_detected", "race_start"], exclude: ["lap_detected"]);
            Assert.False(filter.Wants("lap_detected"));
            Assert.True(filter.Wants("race_start"));
        }

        [Fact]
        public void Filter_IsCaseInsensitive()
        {
            var filter = Filter(include: ["Lap_Detected"], exclude: []);
            Assert.True(filter.Wants("lap_detected"));
            Assert.True(filter.Wants("LAP_DETECTED"));
            Assert.True(filter.Wants("Lap_Detected"));
        }

        // --- Description ---

        [Fact]
        public void Description_EmptyFilter_ReturnsAllEvents()
        {
            var filter = Filter(include: [], exclude: []);
            Assert.Equal("all events", filter.Description);
        }

        [Fact]
        public void Description_IncludeOnly_ListsEvents()
        {
            var filter = Filter(include: ["lap_detected"], exclude: []);
            Assert.Contains("lap_detected", filter.Description);
            Assert.DoesNotContain("except", filter.Description);
        }

        [Fact]
        public void Description_ExcludeOnly_StartsWithAllExcept()
        {
            var filter = Filter(include: [], exclude: ["lap_detected"]);
            Assert.StartsWith("all except", filter.Description);
            Assert.Contains("lap_detected", filter.Description);
        }

        [Fact]
        public void Description_BothSets_ContainsExcept()
        {
            var filter = Filter(include: ["race_start"], exclude: ["race_changed"]);
            Assert.Contains("race_start", filter.Description);
            Assert.Contains("except", filter.Description);
            Assert.Contains("race_changed", filter.Description);
        }

        private static SseEventFilter Filter(string[] include, string[] exclude) =>
            new SseEventFilter(
                new HashSet<string>(include, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase));
    }
}
