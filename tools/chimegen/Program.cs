using System;
using System.IO;

const int Rate = 44100;

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        dir = dir.Parent;

    return dir?.FullName
        ?? throw new DirectoryNotFoundException("no .git above " + AppContext.BaseDirectory);
}

var root = RepoRoot();
var outDir = Path.Combine(root, "DeserokUtils", "Features", "Earshot", "Sounds");
Directory.CreateDirectory(outDir);

static double Voice(double t, double freq, double decay, int timbre = 0)
{

    double[] mult = { 1.0, 2.0, 3.01, 4.2, 5.4 };
    double[][] amps = {
        new[] { 1.0, 0.42, 0.18, 0.09, 0.05 },
        new[] { 1.0, 0.20, 0.06, 0.02, 0.01 },
        new[] { 1.0, 0.08, 0.02, 0.00, 0.00 },
    };
    double[] amp = amps[timbre];
    double sum = 0;
    for (int i = 0; i < mult.Length; i++)
        sum += amp[i] * Math.Sin(2 * Math.PI * freq * mult[i] * t) * Math.Exp(-decay * (1 + i * 0.55) * t);
    return sum;
}

static void Write(string path, (double freq, double start, double len, double gain)[] notes, double decay,
    int timbre = 0)
{
    double total = 0;
    foreach (var n in notes) total = Math.Max(total, n.start + n.len);
    total += 0.25;

    int count = (int)(total * Rate);
    var samples = new double[count];

    foreach (var n in notes)
    {
        int begin = (int)(n.start * Rate);
        int end = Math.Min(count, begin + (int)((n.len + 0.25) * Rate));
        for (int i = begin; i < end; i++)
        {
            double t = (i - begin) / (double)Rate;
            double attack = Math.Min(1.0, t / 0.004);
            samples[i] += Voice(t, n.freq, decay, timbre) * n.gain * attack;
        }
    }

    double peak = 0;
    foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
    double norm = peak > 0 ? 0.89 / peak : 1.0;

    using var fs = new FileStream(path, FileMode.Create);
    using var w = new BinaryWriter(fs);
    int dataBytes = count * 2;
    w.Write("RIFF"u8.ToArray()); w.Write(36 + dataBytes); w.Write("WAVE"u8.ToArray());
    w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
    w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
    w.Write("data"u8.ToArray()); w.Write(dataBytes);
    foreach (var s in samples) w.Write((short)Math.Clamp(s * norm * 32767, -32768, 32767));

    Console.WriteLine($"  {Path.GetFileName(path),-22} {total:0.00}s  {new FileInfo(path).Length / 1024.0:0.0} KB");
}

double E5 = 659.26, E6 = 1318.51, A6 = 1760.00;

Console.WriteLine("earshot:");

Write(Path.Combine(outDir, "earshot-bell.wav"), [(A6, 0.00, 0.32, 1.0)], 8.0, timbre: 0);

Write(Path.Combine(outDir, "earshot-soft.wav"), [(E6, 0.00, 0.40, 1.0)], 6.0, timbre: 1);

var foodDir = Path.Combine(root, "DeserokUtils", "Features", "Food", "Sounds");
Directory.CreateDirectory(foodDir);

Console.WriteLine("food:");
Write(Path.Combine(foodDir, "food-boop.wav"),
    [(E5, 0.00, 0.10, 1.0), (E5, 0.16, 0.10, 1.0), (E5, 0.32, 0.14, 1.0)], 16.0, timbre: 1);
