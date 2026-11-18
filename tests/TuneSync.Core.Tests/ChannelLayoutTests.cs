using TuneSync.Core.Audio;
using TuneSync.Core.Models;

namespace TuneSync.Core.Tests;

public class ChannelLayoutTests
{
    private const int Rate = 48000;

    /// <summary>The subwoofer channel's level (dB) for a steady sine in both left and right.</summary>
    private static double SubLevelDb(double crossoverHz, double toneHz, out float[] output)
    {
        var frames = Rate * 2;
        var stereo = new float[frames * 2];
        for (var i = 0; i < frames; i++)
            stereo[2 * i] = stereo[2 * i + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * toneHz * i / Rate));
        output = new float[frames * 6];
        new ChannelLayout(SpeakerLayout.Surround51, Rate, crossoverHz).Expand(stereo, output);
        // the second half only: the filter has settled
        double peak = 0;
        for (var i = frames / 2; i < frames; i++) peak = Math.Max(peak, Math.Abs(output[i * 6 + 3]));
        return 20 * Math.Log10(peak / 0.5);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(150)]
    public void Subwoofer_IsDown6dBAtTheCrossover(int crossover)
    {
        Assert.InRange(SubLevelDb(crossover, crossover, out _), -7, -5);
        Assert.InRange(SubLevelDb(crossover, crossover / 4.0, out _), -1, 0.01);  // well below: nearly all of it
        Assert.True(SubLevelDb(crossover, crossover * 4.0, out _) < -20);           // two octaves above: mostly gone
    }

    [Fact]
    public void LowerCrossover_SendsLessToTheSubwoofer()
    {
        Assert.True(SubLevelDb(60, 100, out _) < SubLevelDb(150, 100, out _) - 6);
    }

    [Fact]
    public void Fronts_StayAsTheyAre_WhateverTheCrossover()
    {
        SubLevelDb(60, 200, out var a);
        SubLevelDb(150, 200, out var b);
        for (var i = 0; i < a.Length; i += 6)
        {
            Assert.Equal(a[i], b[i]);
            Assert.Equal(a[i + 1], b[i + 1]);
        }
    }

    [Theory]
    [InlineData(0, OutputSettings.DefaultSubCrossoverHz)]
    [InlineData(1000, OutputSettings.DefaultSubCrossoverHz)]
    [InlineData(80, 80)]
    public void Normalize_KeepsTheCrossoverInRange(int set, int expected)
    {
        var output = new OutputSettings { SubCrossoverHz = set };
        output.Normalize();
        Assert.Equal(expected, output.SubCrossoverHz);
    }
}
