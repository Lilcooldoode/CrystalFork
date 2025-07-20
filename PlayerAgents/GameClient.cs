using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Drawing;
using C = ClientPackets;
using S = ServerPackets;
using Shared;

public class GameClient
{
    private readonly Config _config;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly ConcurrentQueue<Packet> _sendQueue = new();
    private readonly byte[] _buffer = new byte[1024 * 8];
    private byte[] _rawData = Array.Empty<byte>();
    private int? _selectedIndex;
    private readonly Random _random = new();
    private MirClass? _playerClass;
    private readonly TaskCompletionSource<MirClass> _classTcs = new();
    private Point _currentLocation = Point.Empty;
    private string _playerName = string.Empty;

    public MirClass? PlayerClass => _playerClass;
    public Task<MirClass> WaitForClassAsync() => _classTcs.Task;

    public GameClient(Config config)
    {
        _config = config;
    }

    private Task RandomStartupDelayAsync() => Task.Delay(_random.Next(1000, 3000));

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
        Console.WriteLine("Logging in...");
        // send client version (empty hash for simplicity)
        var ver = new C.ClientVersion { VersionHash = Array.Empty<byte>() };
        await RandomStartupDelayAsync();
        await SendAsync(ver);

        // send login details
        var login = new C.Login { AccountID = _config.AccountID, Password = _config.Password };
        await RandomStartupDelayAsync();
        await SendAsync(login);

        // StartGame will be sent once LoginSuccess is received
    }

    private async Task CreateAccountAsync()
    {
        if (_stream == null) return;
        Console.WriteLine($"Creating account '{_config.AccountID}'...");
        var acc = new C.NewAccount
        {
            AccountID = _config.AccountID,
            Password = _config.Password,
            BirthDate = DateTime.Today,
            UserName = _config.AccountID,
            SecretQuestion = string.Empty,
            SecretAnswer = string.Empty,
            EMailAddress = string.Empty
        };
        await RandomStartupDelayAsync();
        await SendAsync(acc);
    }

    private async Task CreateCharacterAsync()
    {
        if (_stream == null) return;
        Console.WriteLine($"Creating character '{_config.CharacterName}'...");
        var chr = new C.NewCharacter
        {
            Name = _config.CharacterName,
            Gender = (MirGender)_random.Next(Enum.GetValues<MirGender>().Length),
            Class = (MirClass)_random.Next(Enum.GetValues<MirClass>().Length)
        };
        await RandomStartupDelayAsync();
        await SendAsync(chr);
    }

    public async Task WalkAsync(MirDirection direction)
    {
        if (_stream == null) return;
        var target = Functions.PointMove(_currentLocation, direction, 1);
        Console.WriteLine($"I am walking to {target.X}, {target.Y}");
        var walk = new C.Walk { Direction = direction };
        await SendAsync(walk);
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
            case S.Login l:
                if (l.Result == 3)
                {
                    Console.WriteLine("Account not found, creating...");
                    _ = CreateAccountAsync();
                }
                else if (l.Result != 4)
                {
                    Console.WriteLine($"Login failed: {l.Result}");
                }
                else
                {
                    Console.WriteLine("Wrong password");
                }
                break;
            case S.NewAccount na:
                if (na.Result == 8)
                {
                    Console.WriteLine("Account created");
                    _ = LoginAsync();
                }
                else
                {
                    Console.WriteLine($"Account creation failed: {na.Result}");
                }
                break;
            case S.LoginSuccess ls:
                var match = ls.Characters.FirstOrDefault(c => c.Name.Equals(_config.CharacterName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    Console.WriteLine($"Character '{_config.CharacterName}' not found, creating...");
                    _ = CreateCharacterAsync();
                }
                else
                {
                    _selectedIndex = match.Index;
                    Console.WriteLine($"Selected character '{match.Name}' (Index {match.Index})");
                    var start = new C.StartGame { CharacterIndex = match.Index };
                    _ = Task.Run(async () => { await RandomStartupDelayAsync(); await SendAsync(start); });
                }
                break;
            case S.NewCharacterSuccess ncs:
                Console.WriteLine("Character created");
                _selectedIndex = ncs.CharInfo.Index;
                var startNew = new C.StartGame { CharacterIndex = ncs.CharInfo.Index };
                _ = Task.Run(async () => { await RandomStartupDelayAsync(); await SendAsync(startNew); });
                break;
            case S.NewCharacter nc:
                Console.WriteLine($"Character creation failed: {nc.Result}");
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
            case S.UserInformation info:
                _playerClass = info.Class;
                _playerName = info.Name;
                _currentLocation = info.Location;
                Console.WriteLine($"Logged in as {_playerName}");
                Console.WriteLine($"I am currently at location {_currentLocation.X}, {_currentLocation.Y}");
                _classTcs.TrySetResult(info.Class);
                break;
            case S.UserLocation loc:
                _currentLocation = loc.Location;
                break;
            default:
                // ignore unhandled packets
                break;
        }
    }
}
