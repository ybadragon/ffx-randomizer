using FfxRandomizer.Core;
using FfxRandomizer.Core.Game;
using FfxRandomizer.Core.Randomization;
using FfxRandomizer.Core.Storage;

// Developer tool for checking the core against a real install without the window.
//   ffxr status
//   ffxr apply [--seed <text>] <option-id>...     e.g. ffxr apply tidus
//   ffxr restore

var game = GameInstall.FindSteamInstall();
if (game is null)
{
    Console.Error.WriteLine("Couldn't find the Steam copy of FINAL FANTASY X/X-2 HD Remaster.");
    return 2;
}

var service = new RandomizerService(game, AppPaths.Default);
string command = args.Length > 0 ? args[0] : "status";

try
{
    switch (command)
    {
        case "status":
            Console.WriteLine($"Game:   {game.Directory} (build {game.BuildId})");
            Console.WriteLine($"Status: {service.Patcher.GetStatus()}");
            var applied = service.AppliedEntry();
            if (applied is not null) Console.WriteLine($"Run:    seed {applied.Seed}, {applied.CreatedAt:g}");
            return 0;

        case "apply":
            var settings = RandomizerSettings.Defaults();
            var seed = Seed.NewRandom();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--seed" && i + 1 < args.Length) seed = new Seed(args[++i]);
                else settings.Toggles[args[i]] = true;
            }
            var outcome = service.Run(settings, seed, preset: null, new Progress<RunStep>(s => Console.WriteLine($"  {s}")));
            Console.WriteLine($"Applied seed {outcome.Entry.Seed}: {outcome.Entry.FileCount} files.");
            if (outcome.Entry.SpoilerPath is not null) Console.WriteLine($"Spoiler log: {outcome.Entry.SpoilerPath}");
            return 0;

        case "restore":
            Console.WriteLine(service.Patcher.Restore() ? "Original game restored." : "The game wasn't randomized; nothing to restore.");
            return 0;

        default:
            Console.Error.WriteLine($"Unknown command {command}. Use status, apply or restore.");
            return 2;
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"ERROR: {e.Message}");
    if (e.InnerException is not null) Console.Error.WriteLine($"  {e.InnerException.Message}");
    return 1;
}
