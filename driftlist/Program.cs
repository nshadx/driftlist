using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibVLCSharp.Shared;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;

// параметр температуры. отвечает за то, насколько сильно доверять схожести треков
const double temp = 0.2;
// параметр отсева маловероятных треков
const double topP = 0.3;
// параметр, отвечающий за то, насколько сильно доверять настроению из следующего трека. следующий трек может быть выбран вручную, потому может корректировать общее настроение
const double alpha = 0.7;
// порог, отвечающий за сброс настроения, если треки слишком отличаются друг от друга (т.е. если юзер резко сменил жанр, это означает, что предыдущие настроения - бессмысленны)
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
// корректирующий вектор настроения
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
            .Title("[green]Выбери стартовый трек:[/]")
            .PageSize(15)
            .UseConverter(t => t.Name)
            .AddChoices(lib)
    );

    var currentIndex = Array.IndexOf(lib, selected);
    // корректирующий вектор настроения сбрасывается после остановки сессии - т.е. выбор изначального трека
    effective = vec[currentIndex];

    AnsiConsole.MarkupLine($"\n[bold yellow]▶ {selected.Name}[/]");
    Play(selected.Name);

    while (true)
    {
        AnsiConsole.Markup("[grey]Enter — следующий, Q — сменить стартовый, Esc — выход[/] ");
        var key = Console.ReadKey(intercept: true);
        Console.WriteLine();

        if (key.Key == ConsoleKey.Escape)
        {
            mediaPlayer.Stop();
            mediaPlayer.Dispose();
            libVLC.Dispose();
            return;
        }

        if (key.Key == ConsoleKey.Q)
        {
            mediaPlayer.Stop();
            played.Clear();
            break;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            var next = Transition(currentIndex);
            if (next == null)
            {
                mediaPlayer.Stop();
                AnsiConsole.MarkupLine("\n[grey]Все треки прослушаны.[/]");
                played.Clear();
                break;
            }

            currentIndex = UpdateEffective(next);
            
            AnsiConsole.MarkupLine($"[bold yellow]▶ {next.Name}[/]");
            Play(next.Name);
        }
    }

    Console.WriteLine();
}

// функция обновления корректирующего вектора. работает по принципу EMA (экспоненциальное скользящее среднее) - откуда брать предпочтительнее настроение - из нового трека, или сохранять старое.
int UpdateEffective(Track next)
{
    var nextIndex = Array.IndexOf(lib, next);
    var sim = CosineSimilarity(effective, vec[nextIndex]);
    if (sim < threshold)
        effective = (double[])vec[nextIndex].Clone();
    else
        for (var j = 0; j < effective.Length; j++)
            // рекуррентная формула EMA, alpha и (1 - alpha) взвешивают друг друга, а поскольку формула рекуррентна, итоговый вес следующего трека будет меняться нелинейно
            effective[j] = alpha * vec[nextIndex][j] + (1 - alpha) * effective[j];
    return nextIndex;
}

Track? Transition(int id)
{
    played.Add(id);

    // строим динамическу строку переходов, вместо статической матрицы переходов
    var row = GetProbRow(effective);

    for (var i = 0; i < row.Length; i++)
    {
        // если когда-то трек был уже проигран, нам не следует его ставить следующим за конечное число шагов, вероятность перейти в такой трек - нулевая
        if (played.Contains(i))
            row[i] = 0;
    }

    var sortedRow = row
        .Select((x, i) => (i, x))
        .OrderByDescending(x => x.x)
        .ToArray();
    var cumulative = 0d;

    // отсев по медиане
    for (var i = 0; i < sortedRow.Length; i++)
    {
        cumulative += sortedRow[i].x;

        if (cumulative > topP)
            for (var j = i; j < sortedRow.Length; j++)
                row[sortedRow[j].i] = 0;
    }

    // некоторые вероятности к этому моменту могли быть занулены. следует пересчитать вероятности, чтобы их сумма давала 1
    NormalizeProb(row);

    var bytes = RandomNumberGenerator.GetBytes(8);
    var ul = BitConverter.ToUInt64(bytes, 0) >> 11;
    var p = ul / (double)(1UL << 53);

    // выбор случайного трека, путем разбиения отрезка на вероятности
    cumulative = 0;
    for (var i = 0; i < row.Length; i++)
    {
        cumulative += row[i];
        if (p < cumulative)
            return lib[i];
    }

    return null;
}

//получение динамической строки матрицы переходов. работает так: берем вектор и сравниваем похожесть по косинусной похожести. заполняем полученные коэффициенты. затем, эти коэффициенты корректируем температурой, отдаляя схожесть или приближая ее ПЕРЕД софтмакс, поскольку эта функция растет нелинейно, то полученный вклад от температуры будет значительно влиять на итоговую вероятность
//зануляем "главную диагональ", если составить квадратную матрицу по алгоритму получения ее строки. следующий трек никогда не может быть текущим.
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

// считаем общую сумму, затем долю каждого, перезаписываем значения, получаем общую сумму 1 (не учитываем дрейфы неточности у дабла)
void NormalizeProb(double[] arr)
{
    var s = arr.Sum();
    for (var i = 0; i < arr.Length; i++)
        arr[i] /= s;
}

// аналогичная сумма, только по экспоненте, что дает НЕЛИНЕЙНЫЙ рост
double[] SoftMax(double[] arr)
{
    var s = arr.Sum(Math.Exp);
    return arr.Select(x => Math.Exp(x) / s).ToArray();
}

// алгоритм косинусной схожести, понятия не имею что это.
double CosineSimilarity(double[] a, double[] b)
{
    var scalar = 0d;
    for (var i = 0; i < a.Length; i++)
        scalar += a[i] * b[i];
    return scalar / (GetLength(a) * GetLength(b));
}

// получение длины вектора в н мерном пространстве
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