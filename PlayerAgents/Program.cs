using System.Text.Json;
using System.IO;
using ClientPackets;
using Shared;

public class Config
{
    public string ServerIP { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 7000;
    public string AccountID { get; set; } = "";
    public string Password { get; set; } = "";
    public string CharacterName { get; set; } = string.Empty;
}

internal class Program
{
    private static void WaitForExit()
    {
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey(true);
    }

    private static async Task Main(string[] args)
    {
        try
        {
            var configPath = "config.json";
            var index = 0;

            if (index < args.Length && !args[index].StartsWith("-"))
            {
                configPath = args[index];
                index++;
            }

            if (!File.Exists(configPath))
            {
                Console.WriteLine($"Config file '{configPath}' not found.");
                WaitForExit();
                return;
            }

            var config = JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(configPath)) ?? new Config();

            for (; index < args.Length; index++)
            {
                var arg = args[index];

                switch (arg)
                {
                    case "--account":
                    case "-a":
                        if (index + 1 < args.Length) config.AccountID = args[++index];
                        break;
                    case "--password":
                    case "-p":
                        if (index + 1 < args.Length) config.Password = args[++index];
                        break;
                    case "--character":
                    case "-c":
                        if (index + 1 < args.Length) config.CharacterName = args[++index];
                        break;
                    default:
                        if (arg.StartsWith("--account="))
                            config.AccountID = arg.Substring("--account=".Length);
                        else if (arg.StartsWith("--password="))
                            config.Password = arg.Substring("--password=".Length);
                        else if (arg.StartsWith("--character="))
                            config.CharacterName = arg.Substring("--character=".Length);
                        else
                            Console.WriteLine($"Unknown argument '{arg}'");
                        break;
                }
            }

            var npcFile = Path.Combine(AppContext.BaseDirectory, "npc_memory.json");
            var npcMemory = new NpcMemoryBank(npcFile);

            var moveFile = Path.Combine(AppContext.BaseDirectory, "movement_memory.json");
            var movementMemory = new MapMovementMemoryBank(moveFile);

            var client = new GameClient(config, npcMemory, movementMemory);
            await client.ConnectAsync();
            await client.LoginAsync();

            var playerClass = await client.WaitForClassAsync();
            BaseAI ai = playerClass switch
            {
                MirClass.Warrior => new WarriorAI(client),
                MirClass.Wizard => new WizardAI(client),
                MirClass.Taoist => new TaoistAI(client),
                MirClass.Assassin => new AssassinAI(client),
                MirClass.Archer => new ArcherAI(client),
                _ => new BaseAI(client)
            };

            await ai.RunAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex}");
            WaitForExit();
        }
    }
}
