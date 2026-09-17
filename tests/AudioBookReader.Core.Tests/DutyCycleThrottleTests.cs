using System.Diagnostics;
using AudioBookReader.Core.Alignment;

namespace AudioBookReader.Core.Tests;

public class DutyCycleThrottleTests
{
    /// <summary>A clock the test advances by hand, so no test actually waits.</summary>
    private sealed class Clock
    {
        private long _ticks;

        public List<TimeSpan> Rests { get; } = [];

        public long Now() => _ticks;

        public void Advance(TimeSpan by) => _ticks += (long)(by.TotalSeconds * Stopwatch.Frequency);

        public Task Delay(TimeSpan duration, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Rests.Add(duration);
            Advance(duration);
            return Task.CompletedTask;
        }
    }

    private static (Clock Clock, DutyCycleThrottle Throttle) Build(float duty)
    {
        var clock = new Clock();
        return (clock, new DutyCycleThrottle(() => duty, clock.Delay, clock.Now));
    }

    [Fact]
    public async Task DoesNotRestOnTheFirstCallBecauseNoWorkHasHappenedYet()
    {
        var (clock, throttle) = Build(0.5f);

        await throttle.ThrottleAsync();

        Assert.Empty(clock.Rests);
    }

    [Fact]
    public async Task RestsAsLongAsItWorkedAtFiftyPercent()
    {
        var (clock, throttle) = Build(0.5f);

        await throttle.ThrottleAsync();      // start of work
        clock.Advance(TimeSpan.FromSeconds(4));
        await throttle.ThrottleAsync();

        Assert.Equal([TimeSpan.FromSeconds(4)], clock.Rests);
    }

    [Fact]
    public async Task RestsThreeTimesAsLongAtTwentyFivePercent()
    {
        var (clock, throttle) = Build(0.25f);

        await throttle.ThrottleAsync();
        clock.Advance(TimeSpan.FromSeconds(4));
        await throttle.ThrottleAsync();

        Assert.Equal([TimeSpan.FromSeconds(12)], clock.Rests);
    }

    [Fact]
    public async Task NeverRestsAtFullDutyCycle()
    {
        var (clock, throttle) = Build(1f);

        await throttle.ThrottleAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        await throttle.ThrottleAsync();

        Assert.Empty(clock.Rests);
    }

    [Fact]
    public async Task DoesNotCountItsOwnRestAsWork()
    {
        var (clock, throttle) = Build(0.5f);

        await throttle.ThrottleAsync();
        clock.Advance(TimeSpan.FromSeconds(4));
        await throttle.ThrottleAsync();      // rests 4s, advancing the clock
        clock.Advance(TimeSpan.FromSeconds(2));
        await throttle.ThrottleAsync();

        // The second rest reflects only the 2 seconds of work, not the 4 spent resting.
        Assert.Equal([TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2)], clock.Rests);
    }

    [Fact]
    public async Task CapsASingleRestSoAStallDoesNotPauseForMinutes()
    {
        var clock = new Clock();
        var throttle = new DutyCycleThrottle(() => 0.25f, clock.Delay, clock.Now)
        {
            MaximumRest = TimeSpan.FromSeconds(30),
        };

        await throttle.ThrottleAsync();
        clock.Advance(TimeSpan.FromMinutes(5));   // the process was suspended mid-probe
        await throttle.ThrottleAsync();

        Assert.Equal([TimeSpan.FromSeconds(30)], clock.Rests);
    }

    [Fact]
    public async Task FollowsADutyCycleThatChangesBetweenCalls()
    {
        // Stands in for thermal backoff lowering the budget while a run is in progress.
        var duty = 1f;
        var clock = new Clock();
        var throttle = new DutyCycleThrottle(() => duty, clock.Delay, clock.Now);

        await throttle.ThrottleAsync();
        clock.Advance(TimeSpan.FromSeconds(4));
        await throttle.ThrottleAsync();
        Assert.Empty(clock.Rests);

        duty = 0.5f;
        clock.Advance(TimeSpan.FromSeconds(4));
        await throttle.ThrottleAsync();

        Assert.Equal([TimeSpan.FromSeconds(4)], clock.Rests);
    }

    [Fact]
    public async Task StopsWhenCancelled()
    {
        var (clock, throttle) = Build(0.25f);
        using var cancellation = new CancellationTokenSource();

        await throttle.ThrottleAsync();
        clock.Advance(TimeSpan.FromSeconds(4));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => throttle.ThrottleAsync(cancellation.Token));
    }
}

public class CpuBudgetTests
{
    private const long TenHoursMs = 10 * 60 * 60 * 1_000L;

    // Doubled from the figures the presets were first documented with: coverage is
    // ProbeDurationMs / ProbeIntervalMs, and halving the spacing from 60 s to 30 s halves that
    // fraction's denominator, which doubles the audio actually transcribed and everything
    // downstream of it in equal measure.
    [Theory]
    [InlineData("eco", 400)]
    [InlineData("balanced", 100)]
    [InlineData("turbo", 25)]
    public void EveryPresetFinishesWellInsideTheTimeItTakesToListenToTheBook(string preset, double expectedMinutes)
    {
        var budget = CpuBudget.Presets.Single(p => p.Id == preset);

        var estimate = budget.EstimateAlignmentTime(TenHoursMs);

        Assert.InRange(
            estimate,
            TimeSpan.FromMinutes(expectedMinutes * 0.95),
            TimeSpan.FromMinutes(expectedMinutes * 1.05));

        // The actual promise, not a number that happened to hold when coverage was half of this:
        // every preset finishes before the book itself would be heard start to finish. Eco no
        // longer finishes with hours to spare the way it did at 60 s spacing — at 30 s it takes
        // close to seven hours against ten of audio — so this still passes, but the comfortable
        // margin the presets table describes is now closer to true for eco alone than the other
        // two, which is worth knowing rather than papering over with a laxer bound.
        Assert.True(estimate < TimeSpan.FromMilliseconds(TenHoursMs));
    }

    [Fact]
    public void FasterPresetsEstimateShorterRuns()
    {
        var eco = CpuBudget.Eco.EstimateAlignmentTime(TenHoursMs);
        var balanced = CpuBudget.Balanced.EstimateAlignmentTime(TenHoursMs);
        var turbo = CpuBudget.Turbo.EstimateAlignmentTime(TenHoursMs);

        Assert.True(eco > balanced);
        Assert.True(balanced > turbo);
    }

    [Fact]
    public void DenserProbingCostsProportionallyMore()
    {
        var sparse = CpuBudget.Balanced.EstimateAlignmentTime(TenHoursMs);
        var dense = CpuBudget.Balanced.EstimateAlignmentTime(TenHoursMs, AlignmentSettings.Refinement);

        // The ratio is coverage, not a fixed number: Refinement transcribes effectively all of the
        // audio, and the default's own coverage is ProbeDurationMs / ProbeIntervalMs — halving the
        // spacing halves that ratio along with it, which is exactly what happened when the default
        // moved from 60 s to 30 s and this stopped clearing "more than five times".
        var defaultCoverage = new AlignmentSettings().ProbeDurationMs / (double)new AlignmentSettings().ProbeIntervalMs;
        Assert.True(dense > sparse * (1 / defaultCoverage) * 0.9);
    }

    [Fact]
    public void OnlyTurboCompetesWithTheForeground()
    {
        Assert.True(CpuBudget.Eco.BackgroundPriority);
        Assert.True(CpuBudget.Balanced.BackgroundPriority);
        Assert.False(CpuBudget.Turbo.BackgroundPriority);
    }
}
