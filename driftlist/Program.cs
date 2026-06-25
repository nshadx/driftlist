using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;

const double temp = 0.2;
const double topP = 0.3;

var json = File.ReadAllText(args[0]);
var musicDir = args[1];

var entries = JsonSerializer.Deserialize<List<EmbeddingEntry>>(json, new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
}) ?? throw new InvalidOperationException("Cannot deserialize embeddings.json");

var lib = entries.Select(e => new Track(e.File)).OrderBy(t => t.Name).ToArray();
var vec = entries.OrderBy(e => e.File).Select(e => e.Embedding).ToArray();
var prob = GetProb(vec);
var played = new HashSet<int>();

WaveOutEvent? output = null;
AudioFileReader? reader = null;

void Play(string filename)
{
    output?.Stop();
    output?.Dispose();
    reader?.Dispose();

    var path = Path.Combine(musicDir, filename);
    reader = new AudioFileReader(path);
    output = new WaveOutEvent();
    output.Init(reader);
    output.Play();
}

while (true)
{
    var selected = AnsiConsole.Prompt(
        new SelectionPrompt<Track>()
            .Title("[green]Выбери стартовый трек:[/]")
            .PageSize(15)
            .UseConverter(t => t.Name)
            .AddChoices(lib)
    );

    var currentIndex = Array.IndexOf(lib, selected);
    AnsiConsole.MarkupLine($"\n[bold yellow]▶ {selected.Name}[/]");
    Play(selected.Name);

    while (true)
    {
        AnsiConsole.Markup("[grey]Enter — следующий, Q — сменить стартовый, Esc — выход[/] ");
        var key = Console.ReadKey(intercept: true);
        Console.WriteLine();

        if (key.Key == ConsoleKey.Escape)
        {
            output?.Stop();
            output?.Dispose();
            reader?.Dispose();
            return;
        }

        if (key.Key == ConsoleKey.Q)
        {
            output?.Stop();
            break;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            var next = Transition(currentIndex);
            if (next == null)
            {
                output?.Stop();
                AnsiConsole.MarkupLine("\n[grey]Все треки прослушаны.[/]");
                played.Clear();
                break;
            }
            currentIndex = Array.IndexOf(lib, next);
            AnsiConsole.MarkupLine($"[bold yellow]▶ {next.Name}[/]");
            Play(next.Name);
        }
    }

    Console.WriteLine();
}

Track? Transition(int id)
{
    played.Add(id);
    var row = (double[])prob[id].Clone();

    for (var i = 0; i < row.Length; i++)
    {
        if (played.Contains(i))
            row[i] = 0;
    }
    
    var sort = row
        .Select((x, i) => (i, x))
        .OrderByDescending(x => x.x)
        .ToArray();
    var c = 0d;
    
    for (var i = 0; i < sort.Length; i++)
    {
        c += sort[i].x;
        
        if (c > topP)
            for (var j = i; j < sort.Length; j++)
                row[sort[j].i] = 0;
    }
    
    NormalizeProb(row);
    
    // Step 1: fill an array with 8 random bytes
    var bytes = RandomNumberGenerator.GetBytes(8); 
    // Step 2: bit-shift 11 and 53 based on double's mantissa bits
    var ul = BitConverter.ToUInt64(bytes, 0) / (1 << 11);
    var p = ul / (double)(1UL << 53);

    c = 0;
    for (var i = 0; i < row.Length; i++)
    {
        c += row[i];
        if (p < c)
            return lib[i];
    }
    
    return null;
}

double[][] GetProb(double[][] vec)
{
    var prob = new double[vec.Length][];
    for (var i = 0; i < prob.Length; i++)
    {
        prob[i] = new double[vec.Length];
        for (var j = 0; j < prob[i].Length; j++)
            prob[i][j] = CosineSimilarity(vec[i], vec[j]);
    }
    for (var i = 0; i < prob.Length; i++)
    {
        for (var j = 0; j < prob[i].Length; j++)
            prob[i][j] /= temp;
        prob[i] = SoftMax(prob[i]);
    }
    for (var i = 0; i < prob.Length; i++)
    {
        prob[i][i] = 0;
        NormalizeProb(prob[i]);
    }
    return prob;
}

void NormalizeProb(double[] arr)
{
    var s = arr.Sum();
    for (var i = 0; i < arr.Length; i++)
        arr[i] /= s;
}

double[] SoftMax(double[] arr)
{
    var s = arr.Sum(Math.Exp);
    return arr.Select(x => Math.Exp(x) / s).ToArray();
}

double CosineSimilarity(double[] a, double[] b)
{
    var scalar = 0d;
    for (var i = 0; i < a.Length; i++)
        scalar += a[i] * b[i];
    return scalar / (GetLength(a) * GetLength(b));
}

double GetLength(double[] vec)
{
    var sum = 0d;
    for (var i = 0; i < vec.Length; i++)
        sum += Math.Pow(vec[i], 2);
    return Math.Sqrt(sum);
}

internal record Track(string Name);

internal class EmbeddingEntry
{
    public string File { get; set; } = "";
    public double[] Embedding { get; set; } = [];
}