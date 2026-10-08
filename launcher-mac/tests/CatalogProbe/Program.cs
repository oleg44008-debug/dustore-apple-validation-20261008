using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

string assemblyPath = Path.GetFullPath(args[0]), reportPath = Path.GetFullPath(args[1]);
string folder = Path.GetDirectoryName(assemblyPath)!;
AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(folder, name.Name + ".dll"))
    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, name.Name + ".dll")) : null;
Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
Type vmType = assembly.GetType("DustoreLauncherV.Mac.ViewModels.MainViewModel", true)!;
string profile = Path.Combine(Path.GetDirectoryName(reportPath)!, "catalog-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(profile);
string game = Path.Combine(profile, "Fixture.app"); Directory.CreateDirectory(game);
var entries = Enumerable.Range(0, 2000).Select(i => new { id = Guid.NewGuid(), name = $"Game {i:0000}", sourcePath = game,
    addedUtc = DateTimeOffset.UtcNow.AddSeconds(-i) }).ToArray();
File.WriteAllText(Path.Combine(profile, "library.json"), JsonSerializer.Serialize(entries));
Environment.SetEnvironmentVariable("DUSTOREV_PROFILE_DIRECTORY", profile);
object vm = Activator.CreateInstance(vmType)!;
var initialize = Stopwatch.StartNew(); await (Task)vmType.GetMethod("InitializeAsync")!.Invoke(vm, null)!;
initialize.Stop();
var search = vmType.GetProperty("Search")!;
var games = vmType.GetProperty("Games")!;
object selected = vmType.GetProperty("SelectedGame")!.GetValue(vm)!;
var times = new List<double>();
for (int i = 0; i < 60; i++)
{
    var watch = Stopwatch.StartNew(); search.SetValue(vm, i % 2 == 0 ? "Game 0" : ""); watch.Stop();
    times.Add(watch.Elapsed.TotalMilliseconds);
}
var ordered = times.Order().ToArray();
bool identityPreserved = ReferenceEquals(selected, vmType.GetProperty("SelectedGame")!.GetValue(vm));
var shelf = vmType.GetProperty("ShelfItems")!.GetValue(vm)!;
var result = new { status = "Pass", mode = "headless-viewmodel-overhead", version = assembly.GetName().Version?.ToString(),
    catalogEntries = 2000, trials = times.Count, initializationMilliseconds = initialize.Elapsed.TotalMilliseconds,
    synchronousSearchMeanMilliseconds = times.Average(), synchronousSearchP95Milliseconds = ordered[(int)Math.Ceiling(times.Count * .95) - 1],
    synchronousSearchMaxMilliseconds = times.Max(), selectedItemIdentityPreserved = identityPreserved,
    shelfItemCount = (int)shelf.GetType().GetProperty("Count")!.GetValue(shelf)!,
    managedBytes = GC.GetTotalMemory(true), macOSRuntimeVerified = false, includesRendering = false, verifiedAtUtc = DateTimeOffset.UtcNow };
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(result));
if (vm is IDisposable disposable) disposable.Dispose();
