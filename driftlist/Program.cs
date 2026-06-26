using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibVLCSharp.Shared;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;

/*
 * EMA alpha parameter (maximum).
 * Controls how much weight the next track's embedding has over the accumulated mood vector.
 * Higher alpha → mood shifts quickly toward the new track.
 * Lower alpha → history dominates, mood changes slowly.
 */
const double alphaMax = 0.3;

/*
 * EMA time constant (seconds).
 * Controls how quickly a track reaches its full weight in the mood vector.
 * After tau seconds, a track contributes ~63% of its maximum influence.
 */
const double tau = 10.0;

/*
 * Mood reset threshold.
 * If cosine similarity between the effective mood vector and the next track drops below this value,
 * the mood history is discarded and reset to the new track's embedding.
 * This handles cases where the user abruptly switches genre.
 */
const double threshold = 0.5;

var json = File.ReadAllText(args[0]);
var musicDir = args[1];
var logFile = args.Length > 2 ? args[2] : "transitions.jsonl";
var ucbFile = Path.Combine(Path.GetDirectoryName(args[0])!, "ucb.json");

var entries = JsonSerializer.Deserialize<List<EmbeddingEntry>>(json, new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
}) ?? throw new InvalidOperationException("Cannot deserialize embeddings.json");

var lib = entries.Select(e => new Track(e.File)).OrderBy(t => t.Name).ToArray();
var vec = entries.OrderBy(e => e.File).Select(e => e.Embedding).ToArray();
var played = new HashSet<int>();
double[] effective;
DateTime trackStarted;

// Active UCB parameters for the current session (set by SelectHandle before StartSession)
double temp;
double topP;

var libVLC = new LibVLC();
var mediaPlayer = new MediaPlayer(libVLC);

// ── UCB init ──────────────────────────────────────────────────────────────────

var ucb = LoadUcb();

// ── Entry point ───────────────────────────────────────────────────────────────

while (true)
{
    var handle = SelectHandle(ucb);
    temp = handle.PreferTemp;
    topP = handle.PreferTopP;

    var sessionRewards = new List<double>();
    var currentIndex = SelectStartingTrack();
    StartSession(currentIndex);

    while (true)
    {
        AnsiConsole.Markup("[grey]Enter — next, M — pick manually, S — new session, X — exit[/] ");
        var key = Console.ReadKey(intercept: true);
        Console.WriteLine();

        if (key.Key == ConsoleKey.X)
        {
            FinalizeSession(handle, sessionRewards);
            Quit();
            return;
        }

        if (key.Key == ConsoleKey.S)
        {
            FinalizeSession(handle, sessionRewards);
            EndSession();
            break;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            var (nextIndex, elapsed) = HandleNext(currentIndex);
            if (elapsed > 0) sessionRewards.Add(elapsed);
            currentIndex = nextIndex;
        }

        if (key.Key == ConsoleKey.M) currentIndex = HandleManual();
    }

    Console.WriteLine();
}

// ── UCB ───────────────────────────────────────────────────────────────────────

UcbState LoadUcb()
{
    if (File.Exists(ucbFile))
    {
        var loaded = JsonSerializer.Deserialize<UcbState>(File.ReadAllText(ucbFile),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (loaded != null) return loaded;
    }

    double[] temps = [0.05, 0.1, 0.2];
    double[] topPs = [0.3, 0.4, 0.5];
    var arms = temps
        .SelectMany(t => topPs.Select(p => new Handle(t, p, 0.0, 0)))
        .ToArray();
    return new UcbState(arms, 0);
}

void SaveUcb() =>
    File.WriteAllText(ucbFile, JsonSerializer.Serialize(ucb, new JsonSerializerOptions { WriteIndented = true }));

void FinalizeSession(Handle handle, List<double> rewards)
{
    if (rewards.Count == 0) return;
    var reward = rewards.Average();
    ucb = UpdateHandle(ucb, handle, reward);
    SaveUcb();
}

/*
 * Selects the arm with the highest UCB score:
 *   score_j = MeanReward_j + sqrt(2 * ln(TotalPulls) / TimesChosen_j)
 *
 * If an arm has never been chosen (TimesChosen == 0), it gets infinite priority (initialization phase).
 */
Handle SelectHandle(UcbState state)
{
    var unvisited = state.Handles.FirstOrDefault(h => h.TimesChosen == 0);
    if (unvisited != null)
        return unvisited;
    return state.Handles.MaxBy(x => x.MeanReward + Math.Sqrt(2 * Math.Log(state.TotalPulls) / x.TimesChosen))!;
}

/*
 * Updates the selected arm after a session ends.
 * reward = mean listen time across all transitions in the session.
 * MeanReward updated incrementally: x̄ = x̄ + (reward - x̄) / n_j
 */
UcbState UpdateHandle(UcbState state, Handle handle, double reward)
{
    state = state with { TotalPulls = state.TotalPulls + 1 };
    var newN = handle.TimesChosen + 1;
    state.Handles[Array.IndexOf(state.Handles, handle)] = handle with
    {
        MeanReward = handle.MeanReward + (reward - handle.MeanReward) / newN,
        TimesChosen = newN
    };
    return state;
}

// ── Session control ───────────────────────────────────────────────────────────

int SelectStartingTrack()
{
    return Array.IndexOf(lib, AnsiConsole.Prompt(
        new SelectionPrompt<Track>()
            .Title($"[green]Select starting track:[/] [grey](temp={temp}, topP={topP})[/]")
            .PageSize(15)
            .UseConverter(t => t.Name)
            .AddChoices(lib)
    ));
}

void StartSession(int index)
{
    played.Clear();
    ResetEffective(index);
    PlayTrack(index);
    trackStarted = DateTime.UtcNow;
    AnsiConsole.MarkupLine($"\n[bold yellow]▶ {lib[index].Name}[/]");
}

void EndSession()
{
    mediaPlayer.Stop();
    played.Clear();
}

void Quit()
{
    mediaPlayer.Stop();
    mediaPlayer.Dispose();
    libVLC.Dispose();
}

// ── Playback handlers ─────────────────────────────────────────────────────────

(int nextIndex, double elapsed) HandleNext(int currentIndex)
{
    var elapsed = (DateTime.UtcNow - trackStarted).TotalSeconds;
    var next = Transition(currentIndex);

    if (next == null)
    {
        if (played.Count == lib.Length)
        {
            AnsiConsole.MarkupLine("\n[grey]All tracks have been played.[/]");
            played.Clear();
            return (currentIndex, elapsed);
        }

        ResetEffective(currentIndex);
        AnsiConsole.MarkupLine("\n[grey]Mood reset, retrying...[/]");
        next = Transition(currentIndex);

        if (next == null)
        {
            AnsiConsole.MarkupLine("\n[grey]No candidates found.[/]");
            return (currentIndex, 0);
        }
    }

    var nextIndex = UpdateEffective(next, elapsed);
    LogTransition(lib[currentIndex], next, elapsed, nextIndex, manual: false);
    trackStarted = DateTime.UtcNow;
    AnsiConsole.MarkupLine($"[bold yellow]▶ {next.Name}[/]");
    PlayTrack(nextIndex);
    return (nextIndex, elapsed);
}

int HandleManual()
{
    var elapsed = (DateTime.UtcNow - trackStarted).TotalSeconds;

    var manual = AnsiConsole.Prompt(
        new SelectionPrompt<Track>()
            .Title("[green]Select track:[/]")
            .PageSize(15)
            .UseConverter(t => t.Name)
            .AddChoices(lib)
    );

    var index = Array.IndexOf(lib, manual);
    LogTransition(lib[index], manual, elapsed, index, manual: true);
    played.Add(index);
    UpdateEffective(manual, elapsed);
    trackStarted = DateTime.UtcNow;
    AnsiConsole.MarkupLine($"[bold yellow]▶ {manual.Name}[/]");
    PlayTrack(index);
    return index;
}

void PlayTrack(int index)
{
    var nameWithoutExt = Path.GetFileNameWithoutExtension(lib[index].Name);
    var path = Directory.GetFiles(musicDir)
        .First(f => Path.GetFileNameWithoutExtension(f) == nameWithoutExt);

    var media = new Media(libVLC, path);
    mediaPlayer.Media = media;
    mediaPlayer.Play();
}

// ── Logging ───────────────────────────────────────────────────────────────────

void LogTransition(Track from, Track to, double listenedSec, int toIndex, bool manual)
{
    var sim = CosineSimilarity(effective, vec[toIndex]);
    var record = new
    {
        from = from.Name,
        to = to.Name,
        listened_sec = Math.Round(listenedSec, 2),
        effective_sim = Math.Round(sim, 4),
        manual,
        temp,
        topP,
        ts = DateTime.UtcNow
    };
    File.AppendAllText(logFile, JsonSerializer.Serialize(record) + "\n");
}

// ── Effective mood vector ─────────────────────────────────────────────────────

void ResetEffective(int index)
{
    effective = (double[])vec[index].Clone();
}

int UpdateEffective(Track next, double listenedSeconds)
{
    var nextIndex = Array.IndexOf(lib, next);
    var sim = CosineSimilarity(effective, vec[nextIndex]);

    if (sim < threshold)
    {
        effective = (double[])vec[nextIndex].Clone();
    }
    else
    {
        // alpha(t) = alphaMax * (1 - e^(-t / tau))
        // Saturation function: alpha grows from 0 toward alphaMax as listen time increases.
        // At t = tau, alpha reaches ~63% of alphaMax. Approaches alphaMax asymptotically — never reaches it exactly.
        var alpha = alphaMax * (1.0 - Math.Exp(-listenedSeconds / tau));
        for (var j = 0; j < effective.Length; j++)
            effective[j] = alpha * vec[nextIndex][j] + (1 - alpha) * effective[j];
    }

    return nextIndex;
}

// ── Markov transition ─────────────────────────────────────────────────────────

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

    if (row.Sum() == 0) return null;

    NormalizeProb(row);

    var prob = GetRandomDouble();

    cumulative = 0;
    for (var i = 0; i < row.Length; i++)
    {
        cumulative += row[i];
        if (prob < cumulative)
            return lib[i];
    }

    return null;
}

double GetRandomDouble()
{
    var bytes = RandomNumberGenerator.GetBytes(8);
    var ul = BitConverter.ToUInt64(bytes, 0) >> 11;
    var d = ul / (double)(1UL << 53);
    return d;
}

// ── Math ──────────────────────────────────────────────────────────────────────

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

double GetLength(double[] v)
{
    var sum = 0d;
    for (var i = 0; i < v.Length; i++)
        sum += Math.Pow(v[i], 2);
    return Math.Sqrt(sum);
}

// ── Types ─────────────────────────────────────────────────────────────────────

internal record Track(string Name);

internal class EmbeddingEntry
{
    public string File { get; set; } = "";
    public double[] Embedding { get; set; } = [];
}

internal record Handle(double PreferTemp, double PreferTopP, double MeanReward, int TimesChosen);
internal record UcbState(Handle[] Handles, int TotalPulls);