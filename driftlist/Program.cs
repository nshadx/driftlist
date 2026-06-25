using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibVLCSharp.Shared;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;

/*
 * Temperature parameter.
 * Controls how strongly cosine similarity differences are amplified before softmax.
 * Lower value → sharper distribution → more deterministic transitions.
 * Higher value → flatter distribution → more random transitions.
 */
const double temp = 0.2;

/*
 * Top-p threshold.
 * Filters out low-probability tracks before sampling.
 * Only tracks whose cumulative probability (sorted descending) fits within this threshold are considered.
 */
const double topP = 0.3;

/*
 * EMA alpha parameter.
 * Controls how much weight the next track's embedding has over the accumulated mood vector.
 * Higher alpha → mood shifts quickly toward the new track.
 * Lower alpha → history dominates, mood changes slowly.
 */
const double alpha = 0.7;

/*
 * Mood reset threshold.
 * If cosine similarity between the effective mood vector and the next track drops below this value,
 * the mood history is discarded and reset to the new track's embedding.
 * This handles cases where the user abruptly switches genre.
 */
const double threshold = 0.5;

var json = File.ReadAllText(args[0]);
var musicDir = args[1];

var entries = JsonSerializer.Deserialize<List<EmbeddingEntry>>(json, new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
}) ?? throw new InvalidOperationException("Cannot deserialize embeddings.json");

var lib = entries.Select(e => new Track(e.File)).OrderBy(t => t.Name).ToArray();
var vec = entries.OrderBy(e => e.File).Select(e => e.Embedding).ToArray();
var played = new HashSet<int>();

// Accumulated mood vector. Encodes the weighted history of recently played tracks.
// Reset to the selected track's embedding at the start of each session.
double[] effective;

var libVLC = new LibVLC();
var mediaPlayer = new MediaPlayer(libVLC);

void Play(string filename)
{
    var nameWithoutExt = Path.GetFileNameWithoutExtension(filename);
    var path = Directory.GetFiles(musicDir)
        .First(f => Path.GetFileNameWithoutExtension(f) == nameWithoutExt);

    var media = new Media(libVLC, path);
    mediaPlayer.Media = media;
    mediaPlayer.Play();
}

while (true)
{
    var selected = AnsiConsole.Prompt(
        new SelectionPrompt<Track>()
            .Title("[green]Select starting track:[/]")
            .PageSize(15)
            .UseConverter(t => t.Name)
            .AddChoices(lib)
    );

    var currentIndex = Array.IndexOf(lib, selected);

    // Session start: reset mood vector to the selected track's embedding.
    effective = (double[])vec[currentIndex].Clone();

    AnsiConsole.MarkupLine($"\n[bold yellow]▶ {selected.Name}[/]");
    Play(selected.Name);

    while (true)
    {
        AnsiConsole.Markup("[grey]Enter — next, M — pick manually, S — back to menu, X — exit[/] ");
        var key = Console.ReadKey(intercept: true);
        Console.WriteLine();

        if (key.Key == ConsoleKey.S)
        {
            mediaPlayer.Stop();
            played.Clear();
            break;
        }

        if (key.Key == ConsoleKey.X)
        {
            mediaPlayer.Stop();
            mediaPlayer.Dispose();
            libVLC.Dispose();
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            var next = Transition(currentIndex);
            if (next == null)
            {
                mediaPlayer.Stop();
                AnsiConsole.MarkupLine("\n[grey]All tracks have been played.[/]");
                played.Clear();
                break;
            }

            currentIndex = UpdateEffective(next);

            AnsiConsole.MarkupLine($"[bold yellow]▶ {next.Name}[/]");
            Play(next.Name);
        }

        if (key.Key == ConsoleKey.M)
        {
            var manual = AnsiConsole.Prompt(
                new SelectionPrompt<Track>()
                    .Title("[green]Select track:[/]")
                    .PageSize(15)
                    .UseConverter(t => t.Name)
                    .AddChoices(lib)
            );

            currentIndex = Array.IndexOf(lib, manual);
            played.Add(currentIndex);
            AnsiConsole.MarkupLine($"[bold yellow]▶ {manual.Name}[/]");
            Play(manual.Name);
        }
    }

    Console.WriteLine();
}

/*
 * Updates the effective mood vector using EMA (Exponential Moving Average).
 *
 * If the new track is too dissimilar from the current mood (sim < threshold),
 * the history is discarded and effective is reset to the new track's embedding.
 *
 * Otherwise, the EMA formula is applied element-wise:
 *   effective[j] = alpha * next[j] + (1 - alpha) * effective[j]
 *
 * Because the formula is recurrent, the influence of each past track
 * decays exponentially — older tracks contribute less and less over time.
 */
int UpdateEffective(Track next)
{
    var nextIndex = Array.IndexOf(lib, next);
    var sim = CosineSimilarity(effective, vec[nextIndex]);
    if (sim < threshold)
        effective = (double[])vec[nextIndex].Clone();
    else
        for (var j = 0; j < effective.Length; j++)
            effective[j] = alpha * vec[nextIndex][j] + (1 - alpha) * effective[j];
    return nextIndex;
}

/*
 * Samples the next track using the current effective mood vector.
 *
 * Steps:
 *   1. Build a dynamic transition row from the effective vector via cosine similarity.
 *   2. Zero out already-played tracks (probability of revisiting = 0).
 *   3. Apply top-p filtering: keep only the most probable tracks up to the threshold.
 *   4. Renormalize so probabilities sum to 1.
 *   5. Sample using a cryptographically random number mapped to [0, 1).
 */
Track? Transition(int id)
{
    played.Add(id);

    var row = GetProbRow(effective, id);

    for (var i = 0; i < row.Length; i++)
        if (played.Contains(i))
            row[i] = 0;

    var sortedRow = row
        .Select((x, i) => (i, x))
        .OrderByDescending(x => x.x)
        .ToArray();
    var cumulative = 0d;

    for (var i = 0; i < sortedRow.Length; i++)
    {
        cumulative += sortedRow[i].x;
        if (cumulative > topP)
            for (var j = i; j < sortedRow.Length; j++)
                row[sortedRow[j].i] = 0;
    }

    NormalizeProb(row);

    var bytes = RandomNumberGenerator.GetBytes(8);
    var ul = BitConverter.ToUInt64(bytes, 0) >> 11;
    var p = ul / (double)(1UL << 53);

    // Segment sampling: partition [0, 1] by probability weights and find where p lands.
    cumulative = 0;
    for (var i = 0; i < row.Length; i++)
    {
        cumulative += row[i];
        if (p < cumulative)
            return lib[i];
    }

    return null;
}

/*
 * Builds a single transition probability row from a source embedding.
 *
 *   1. Compute cosine similarity between the source vector and every track in the library.
 *   2. Divide by temperature before softmax — since softmax grows exponentially,
 *      even small changes in input produce large differences in output probabilities.
 *      Lower temp → sharper contrast between similar and dissimilar tracks.
 *   3. Apply softmax to convert scores into a valid probability distribution.
 *   4. Zero out the current track (a track cannot transition to itself).
 *   5. Renormalize.
 */
double[] GetProbRow(double[] vec1, int excludeIndex = -1)
{
    var row = new double[vec.Length];
    for (var i = 0; i < row.Length; i++)
        row[i] = CosineSimilarity(vec1, vec[i]);
    for (var i = 0; i < row.Length; i++)
        row[i] /= temp;

    row = SoftMax(row);

    if (excludeIndex != -1)
        row[excludeIndex] = 0;

    NormalizeProb(row);

    return row;
}

/*
 * Normalizes an array so that its elements sum to 1.
 * Divides each element by the total sum.
 * Note: minor floating-point drift in the sum is acceptable.
 */
void NormalizeProb(double[] arr)
{
    var s = arr.Sum();
    for (var i = 0; i < arr.Length; i++)
        arr[i] /= s;
}

/*
 * Softmax function. Converts raw scores into a probability distribution.
 * Uses the exponential function — growth is nonlinear, which amplifies
 * differences between high and low scores.
 */
double[] SoftMax(double[] arr)
{
    var s = arr.Sum(Math.Exp);
    return arr.Select(x => Math.Exp(x) / s).ToArray();
}

/*
 * Cosine similarity between two vectors.
 * Measures the cosine of the angle between them in n-dimensional space.
 * Result ranges from -1 (opposite directions) to 1 (identical direction).
 *
 * When two vectors are collinear (point in the same direction),
 * the angle between them is 0° and cos(0°) = 1.
 * When perpendicular — 90°, cos(90°) = 0.
 * When opposite — 180°, cos(180°) = -1.
 *
 * Tracks with similar audio characteristics will have embeddings
 * that point in similar directions, yielding a similarity score close to 1.
 */
double CosineSimilarity(double[] a, double[] b)
{
    var scalar = 0d;
    for (var i = 0; i < a.Length; i++)
        scalar += a[i] * b[i];
    return scalar / (GetLength(a) * GetLength(b));
}

/*
 * Euclidean length (L2 norm) of a vector in n-dimensional space.
 * Used as the denominator in cosine similarity to normalize direction.
 */
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