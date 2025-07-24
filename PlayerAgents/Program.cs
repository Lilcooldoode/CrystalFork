using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using ClientPackets;
using Shared;
using System.Linq;
using System.Runtime.InteropServices;

public sealed class AgentConfig
{
    public string AccountID { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
}

public sealed class Config
{
    public string ServerIP { get; init; } = "127.0.0.1";
    public int ServerPort { get; init; } = 7000;

    public int PlayerCount { get; set; }
    public int ConcurrentLogins { get; set; } = 50;

    // Single agent fields for backwards compatibility
    public string AccountID { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;

    public List<AgentConfig>? Agents { get; set; }
}

internal class Program
{
    private const int STD_OUTPUT_HANDLE = -11;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CONSOLE_FONT_INFO_EX
    {
        public uint cbSize;
        public uint nFont;
        public COORD dwFontSize;
        public int FontFamily;
        public int FontWeight;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCurrentConsoleFontEx(IntPtr consoleOutput, bool maximumWindow, ref CONSOLE_FONT_INFO_EX consoleCurrentFontEx);

    private static void SetConsoleFontSize(short size)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            var info = new CONSOLE_FONT_INFO_EX
            {
                cbSize = (uint)Marshal.SizeOf<CONSOLE_FONT_INFO_EX>(),
                FaceName = "Consolas",
                FontFamily = 0,
                FontWeight = 400,
                dwFontSize = new COORD { X = 0, Y = size }
            };

            SetCurrentConsoleFontEx(handle, false, ref info);
        }
        catch
        {
            // ignored
        }
    }

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

            var expRateFile = Path.Combine(AppContext.BaseDirectory, "exp_rate_memory.json");
            var expRateMemory = new MapExpRateMemoryBank(expRateFile);

            var navManager = new NavDataManager();

            List<AgentConfig> agentConfigs;
            if (config.PlayerCount > 0)
            {
                agentConfigs = Enumerable.Range(1, config.PlayerCount)
                    .Select(i => new AgentConfig
                    {
                        AccountID = $"acc{i:D3}",
                        Password = $"pass{i:D3}",
                        CharacterName = $"hero{i}"
                    }).ToList();
            }
            else if (config.Agents != null && config.Agents.Count > 0)
            {
                agentConfigs = config.Agents;
            }
            else
            {
                agentConfigs = new List<AgentConfig>
                {
                    new AgentConfig
                    {
                        AccountID = config.AccountID,
                        Password = config.Password,
                        CharacterName = config.CharacterName
                    }
                };
            }

            SetConsoleFontSize(5);

            IAgentLogger logger = agentConfigs.Count > 1
                ? new SummaryAgentLogger() as IAgentLogger
                : new NullAgentLogger();

            if (agentConfigs.Count > 1)
                Console.SetOut(TextWriter.Null);

            foreach (var agent in agentConfigs)
            {
                logger.RegisterAgent(string.IsNullOrEmpty(agent.CharacterName) ? agent.AccountID : agent.CharacterName);
            }

            var semaphore = new SemaphoreSlim(config.ConcurrentLogins > 0 ? config.ConcurrentLogins : 50);
            var tasks = new List<Task>();
            foreach (var agent in agentConfigs)
            {
                await semaphore.WaitAsync();

                var agentConfig = new Config
                {
                    ServerIP = config.ServerIP,
                    ServerPort = config.ServerPort,
                    AccountID = agent.AccountID,
                    Password = agent.Password,
                    CharacterName = agent.CharacterName
                };

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await RunAgentAsync(agentConfig, npcMemory, movementMemory, expRateMemory, navManager, logger, semaphore);
                    }
                    catch
                    {
                        semaphore.Release();
                        throw;
                    }
                }));
            }

            await Task.WhenAll(tasks);
            if (logger is IDisposable d)
                d.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex}");
            WaitForExit();
        }
    }

    private static async Task RunAgentAsync(
        Config config,
        NpcMemoryBank npcMemory,
        MapMovementMemoryBank movementMemory,
        MapExpRateMemoryBank expRateMemory,
        NavDataManager navManager,
        IAgentLogger logger,
        SemaphoreSlim? loginLimiter = null)
    {
        var client = new GameClient(config, npcMemory, movementMemory, expRateMemory, navManager, logger);
        client.UpdateAction("connecting");
        await client.ConnectAsync();
        await client.LoginAsync();
        loginLimiter?.Release();

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

        client.UpdateAction("running AI");
        await ai.RunAsync();
    }
}
