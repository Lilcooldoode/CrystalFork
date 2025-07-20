using System.Collections.Concurrent;
using System.Net.Sockets;
using C = ClientPackets;
using S = ServerPackets;

public class GameClient
{
    private readonly Config _config;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly ConcurrentQueue<Packet> _sendQueue = new();
    private readonly byte[] _buffer = new byte[1024 * 8];
    private byte[] _rawData = Array.Empty<byte>();
    private int? _selectedIndex;

    public GameClient(Config config)
    {
        _config = config;
    }

    public async Task ConnectAsync()
    {
        _client = new TcpClient();
        await _client.ConnectAsync(_config.ServerIP, _config.ServerPort);
        _stream = _client.GetStream();
        Console.WriteLine("Connected to server");
        _ = Task.Run(ReceiveLoop);
    }

    public async Task LoginAsync()
    {
        if (_stream == null) return;
        // send client version (empty hash for simplicity)
        var ver = new C.ClientVersion { VersionHash = Array.Empty<byte>() };
        await SendAsync(ver);

        // send login details
        var login = new C.Login { AccountID = _config.AccountID, Password = _config.Password };
        await SendAsync(login);

        // StartGame will be sent once LoginSuccess is received
    }

    public async Task RunAsync()
    {
        if (_stream == null) return;
        var rnd = new Random();
        while (true)
        {
            var dir = (MirDirection)rnd.Next(0, 8);
            var walk = new C.Walk { Direction = dir };
            await SendAsync(walk);
            await Task.Delay(1000);
        }
    }

    private async Task SendAsync(Packet p)
    {
        if (_stream == null) return;
        var data = p.GetPacketBytes().ToArray();
        await _stream.WriteAsync(data, 0, data.Length);
    }

    private async Task ReceiveLoop()
    {
        if (_stream == null) return;
        try
        {
            while (true)
            {
                int count = await _stream.ReadAsync(_buffer, 0, _buffer.Length);
                if (count == 0) break;
                var tmp = new byte[_rawData.Length + count];
                Buffer.BlockCopy(_rawData, 0, tmp, 0, _rawData.Length);
                Buffer.BlockCopy(_buffer, 0, tmp, _rawData.Length, count);
                _rawData = tmp;

                Packet? p;
                while ((p = Packet.ReceivePacket(_rawData, out _rawData)) != null)
                {
                    HandlePacket(p);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Receive error: {ex.Message}");
        }
    }

    private void HandlePacket(Packet p)
    {
        switch (p)
        {
            case S.LoginSuccess ls:
                var match = ls.Characters.FirstOrDefault(c => c.Name.Equals(_config.CharacterName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    Console.WriteLine($"Character '{_config.CharacterName}' not found.");
                }
                else
                {
                    _selectedIndex = match.Index;
                    Console.WriteLine($"Selected character '{match.Name}' (Index {match.Index})");
                    var start = new C.StartGame { CharacterIndex = match.Index };
                    _ = SendAsync(start);
                }
                break;
            case S.StartGame sg:
                var reason = sg.Result switch
                {
                    0 => "Disabled",
                    1 => "Not logged in",
                    2 => "Character not found",
                    3 => "Start Game Error",
                    4 => "Success",
                    _ => "Unknown"
                };
                Console.WriteLine($"StartGame Result: {sg.Result} ({reason})");
                break;
            case S.StartGameBanned ban:
                Console.WriteLine($"StartGame Banned: {ban.Reason} until {ban.ExpiryDate}");
                break;
            case S.StartGameDelay delay:
                Console.WriteLine($"StartGame delayed for {delay.Milliseconds} ms");
                break;
            default:
                Console.WriteLine($"Received packet {p.GetType().Name}");
                break;
        }
    }
}
