using System.Diagnostics;

double[] vecA = [1.0, 1.0]; // phonk, aggressive
double[] vecB = [1.0, 0.6]; // phonk, dark
double[] vecC = [0.2, 0.1]; // rap, calm
double[] vecD = [0.2, 0.2]; // rap, sad
double[] vecE = [0.2, 1.0]; // rap, aggressive

double[][] vec =
[
    vecA,
    vecB,
    vecC,
    vecD,
    vecE,
];

var prob = GetProb(vec);

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

double[][] GetProb(double[][] vec)
{
    var prob = new double[vec.Length][];
    
    for (var i = 0; i < prob.Length; i++)
    {
        prob[i] = new double[vec.Length];
        
        for (var j = 0; j < prob[i].Length; j++)
        {
            prob[i][j] = CosineSimilarity(vec[i], vec[j]);
        }
    }
    
    for (var i = 0; i < prob.Length; i++)
    {
        prob[i] = SoftMax(prob[i]);
    }
    
    for (var i = 0; i < prob.Length; i++)
    {
        prob[i][i] = 0;
        var s = prob[i].Sum();
        for (var j = 0; j < prob[i].Length; j++)
        {
            prob[i][j] /= s;
        }
    }
    
    return prob;
}

double[] SoftMax(double[] arr)
{
    var s = arr.Sum(Math.Exp);
    return arr.Select(x => Math.Exp(x) / s).ToArray();
}

double CosineSimilarity(double[] a, double[] b)
{
    // |a| = |b|
    var scalar = 0d;
    for (var i = 0; i < a.Length; i++)
    {
        scalar += a[i] * b[i];
    }
    
    return scalar / (GetLength(a) * GetLength(b));
}

double GetLength(double[] vec)
{
    var sum = 0d;
    for (var i = 0; i < vec.Length; i++)
    {
        sum += Math.Pow(vec[i], 2);
    }
    
    return Math.Sqrt(sum);
}

internal record Track(string Genre, string Mood);