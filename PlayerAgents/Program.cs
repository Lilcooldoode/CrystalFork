using System.Text.Json;
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
    private static async Task Main(string[] args)
    {
        var configPath = args.Length > 0 ? args[0] : "config.json";
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"Config file '{configPath}' not found.");
            return;
        }

        var config = JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(configPath));
        if (config == null)
        {
            Console.WriteLine("Failed to read config.");
            return;
        }

        var client = new GameClient(config);
        await client.ConnectAsync();
        await client.LoginAsync();
        await client.RunAsync();
    }
}
