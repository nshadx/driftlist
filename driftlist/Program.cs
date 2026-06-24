using System.Diagnostics;

double[][] prob =
[
    [ 0.0, 0.6, 0.0, 0.1, 0.3 ],
    [ 0.5, 0.0, 0.0, 0.4, 0.1 ],
    [ 0.0, 0.1, 0.0, 0.6, 0.3 ],
    [ 0.0, 0.1, 0.6, 0.0, 0.3 ],
    [ 0.4, 0.1, 0.0, 0.5, 0.0 ],
];

Track a = new("phonk", "aggressive");
Track b = new("phonk", "dark");
Track c = new("rap", "calm");
Track d = new("rap", "sad");
Track e = new("rap", "aggressive");

Track[] lib = [a, b, c, d, e];

Console.WriteLine($"{a} -> {Transition(0)}");
Console.WriteLine($"{a} -> {Transition(0)}");
Console.WriteLine($"{a} -> {Transition(0)}");
Console.WriteLine($"{a} -> {Transition(0)}");
Console.WriteLine($"{a} -> {Transition(0)}");

Console.WriteLine("---");

Console.WriteLine($"{b} -> {Transition(1)}");
Console.WriteLine($"{b} -> {Transition(1)}");
Console.WriteLine($"{b} -> {Transition(1)}");
Console.WriteLine($"{b} -> {Transition(1)}");
Console.WriteLine($"{b} -> {Transition(1)}");

Console.WriteLine("---");

Console.WriteLine($"{e} -> {Transition(4)}");
Console.WriteLine($"{e} -> {Transition(4)}");
Console.WriteLine($"{e} -> {Transition(4)}");
Console.WriteLine($"{e} -> {Transition(4)}");
Console.WriteLine($"{e} -> {Transition(4)}");

Track Transition(int id)
{
    var j = prob[id];
    var p = Random.Shared.NextDouble();
    var c = 0d;
    for (var i = 0; i < j.Length; i++)
    {
        c += j[i];
        if (p < c)
        {
            return lib[i];
        }
    }

    throw new UnreachableException();
}

double SoftMax()

internal record Track(string Genre, string Mood);